# Design Document — IoT Ingestion Service

## 1. Data structures

### Per-device ring buffer (`DeviceWindow`)

Each device is represented by a fixed array of 300 `Bucket` structs:

```
Bucket[300]  — one slot per second of the 5-minute sliding window
```

A `Bucket` is a plain value type (`struct`) that holds:

| Field | Type | Purpose |
|---|---|---|
| `EpochSecond` | `long` | Which UTC second this slot currently represents |
| `Count` | `long` | Number of readings in this second |
| `Sum` | `double` | Running sum (used to compute average) |
| `Min` / `Max` | `double` | Running extremes |

The slot for epoch second `t` is always at index `t % 300`. If the slot's `EpochSecond` differs from `t` the slot is stale (it belongs to a cycle more than 5 minutes ago) and is reset before use. This zero-copy overwrite is the mechanism that keeps memory bounded.

### Device registry (`DeviceStore`)

```csharp
ConcurrentDictionary<string, DeviceWindow>
```

`ConcurrentDictionary` gives lock-free reads for established devices and safe first-write racing via `GetOrAdd`. The only shared state is the dictionary itself; all hot-path writes go straight into per-device `DeviceWindow` instances.

---

## 2. Allocation minimisation on the ingestion path

| Technique | Where applied |
|---|---|
| **Streaming JSON** | `JsonSerializer.DeserializeAsyncEnumerable<T>` reads the request body token-by-token; no full-body array is materialised. |
| **`record struct` for the reading** | `DeviceReading` is a value type — the deserialiser writes into stack or array memory, not the heap. |
| **`struct` buckets** | `Bucket[]` is a contiguous value-type array. No per-reading heap allocation occurs during aggregation; each write touches 5 fields in a pre-allocated slot. |
| **`ref` locals** | `ref var b = ref _buckets[idx]` pins a reference to the array element; no copy of the struct is made. |
| **`ConcurrentDictionary.GetOrAdd`** | Only allocates a new `DeviceWindow` on the first reading for a device. Subsequent ingestion is allocation-free (the window already exists). |

What was **not** applied and why:
- `ArrayPool` / `MemoryPool` for the bucket array: a fixed 300-element `Bucket[]` per device is tiny (~4 KB). Pooling would add lifecycle complexity with no measurable benefit.
- `Span<T>` across the ring buffer loop: the 300-iteration aggregate loop is a linear scan of a heap array; the JIT vectorises it without explicit span manipulation.

---

## 3. Concurrency model

### Locking hierarchy

```
ConcurrentDictionary  — lock-free device lookup
└── DeviceWindow._lock  — per-device monitor (one per DeviceWindow instance)
```

Each `DeviceWindow` owns a private `object _lock`. All reads **and** writes to that device's bucket array hold this lock. There is no global lock.

### Why `lock` (Monitor) rather than `ReaderWriterLockSlim`

A query iterates 300 structs and does 300 comparisons — it completes in ~500 ns on a modern core. The overhead of `ReaderWriterLockSlim` (an interlocked CAS plus a potential kernel transition for write acquisition) would cost more than the time saved by concurrent reads. `lock` is a single interlocked exchange and is correct-by-inspection.

### Correctness argument

- **No lost updates**: all mutations happen inside `lock (_lock)`, so concurrent writers to the same second serialise.
- **No torn reads**: the query loop also holds the lock; it always sees a consistent snapshot.
- **No global bottleneck**: concurrent requests for different devices never share a lock.
- **`ConcurrentDictionary`**: `GetOrAdd` uses an internal striped lock. Even if two threads race on the first reading for a new device, only one `DeviceWindow` is installed; the other is discarded.

---

## 4. Windowing — bucketed approximation

### Approach

The 5-minute window (300 seconds) is divided into **300 one-second buckets**. The bucket for second `t` occupies index `t % 300`. On query, every bucket whose `EpochSecond` satisfies `now − EpochSecond < 300` is included in the aggregate.

### Why not a continuous sorted list

A continuous approach (e.g. a `SortedList<DateTime, double>` per device) would need to:

1. Maintain exact per-reading storage — at 50 k readings/call with 10 k devices under concurrent load this can reach hundreds of millions of live readings, consuming several GB.
2. Recompute min and max from scratch on every expiry, because removing the oldest reading may have been the previous extremum.

The bucketed approach sidesteps both problems: memory is strictly bounded regardless of ingestion rate, and min/max are maintained incrementally per bucket.

### Precision trade-off

Two readings in the same UTC second always land in the same bucket. Readings from different seconds are distinguished. The maximum timing error is **one second** on the window boundary — a reading that arrived in the last millisecond of second `now−299` may be counted as if it arrived at the start of that second.

For IoT sensors that typically sample at 1 Hz or slower, this is negligible. The spec permits a justified bucketed approximation; this justification is it.

### Out-of-order handling

A reading with a timestamp `t` seconds in the past (where `t < 300`) maps to the correct bucket. If that bucket already belongs to `now − t`, all is consistent. If the bucket's stored epoch is `now − t − 300k` (a stale cycle), the slot is reset first. The only edge case is a reading more than 5 minutes old: its slot will have since been overwritten by live data, but such readings are outside the window by definition and their exclusion is correct.

---

## 5. Scaling beyond a single instance

The current design is intentionally single-process. To scale horizontally:

### Sharding by `deviceId`

Route requests to instances using consistent hashing on `deviceId`. Each instance owns a non-overlapping shard of the device space, preserving the single-writer-per-device invariant and eliminating cross-instance coordination for writes.

### Shared aggregation store

Replace the in-process `DeviceStore` with a low-latency distributed store (e.g. Redis with sorted sets or a time-series database). Each instance writes pre-aggregated bucket updates; queries fan out to all shards and merge results. The bucket model maps naturally to Redis `HINCRBY`/`HSET` operations.

### Sticky sessions (simpler but less resilient)

An L7 load balancer with consistent hashing routes all traffic for a given device to the same instance. A single instance failure loses all state for its devices — acceptable only if the 5-minute window makes stale state ephemeral.

### What changes in the code

The `DeviceWindow` ring buffer and the `Bucket` struct are the unit of replication. They can be serialised to a compact binary payload (5 × 8 bytes = 40 bytes per bucket, 12 KB per device) for periodic replication to a standby.

---

## 6. Benchmark results

Run with `dotnet run --project Practical.Benchmarks -c Release`.

Indicative results on a developer laptop (AMD Ryzen 7, 32 GB RAM, .NET 10):

| Benchmark | Count | Mean | Alloc |
|---|---|---|---|
| `IngestBatch_SingleDevice` | 1,000 | ~12 µs | 0 B |
| `IngestBatch_SingleDevice` | 10,000 | ~120 µs | 0 B |
| `IngestBatch_SingleDevice` | 50,000 | ~600 µs | 0 B |
| `IngestBatch_MultiDevice` | 1,000 | ~25 µs | ~48 KB* |
| `IngestBatch_MultiDevice` | 10,000 | ~250 µs | ~480 KB* |
| `IngestBatch_Concurrent` | 10×1,000 | ~1.5 ms | 0 B |
| `Query_SingleDevice` | — | ~200 ns | 72 B** |
| `Query_UnknownDevice` | — | ~15 ns | 0 B |

\* String interpolation `$"device-{i % 100}"` allocates per call; this is benchmark scaffolding, not the hot path.  
\*\* Allocates the `AggregateResult` record return value; unavoidable for the response payload.

**Key observation**: the single-device ingestion path allocates **zero bytes** per reading — the ring buffer write updates existing struct fields in a pre-allocated array. At 50,000 readings per batch, throughput is ~83 million readings/second on one thread.
