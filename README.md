# High-Throughput IoT Ingestion Service

An HTTP API built with .NET 10 that ingests batches of IoT device sensor readings and maintains rolling 5-minute aggregates (count, min, max, average) per device.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Project structure

```
Practical.slnx
├── Practical/           — Web API (the service)
├── Practical.Tests/     — xUnit tests (windowing + concurrency)
└── Practical.Benchmarks/ — BenchmarkDotNet ingestion benchmarks
```

## Build

```bash
dotnet build Practical.slnx
```

## Run

```bash
dotnet run --project Practical
```

The API listens on `http://localhost:5168` by default (see `Practical/Properties/launchSettings.json`).

## API

### POST /readings — ingest a batch

Body: JSON array of up to 50,000 readings.

```json
[
  { "deviceId": "sensor-0042", "timestamp": "2026-10-08T09:00:01Z", "value": 21.5 },
  { "deviceId": "sensor-0042", "timestamp": "2026-10-08T09:00:03Z", "value": 22.1 }
]
```

Response `200 OK`:
```json
{ "accepted": 2 }
```

### GET /readings/{deviceId} — query 5-minute aggregates

```
GET /readings/sensor-0042
```

Response `200 OK`:
```json
{
  "deviceId": "sensor-0042",
  "count": 2,
  "min": 21.5,
  "max": 22.1,
  "average": 21.8
}
```

Response `404 Not Found` when the device is unknown or has no readings in the current window:
```json
{ "error": "No readings in window for device 'sensor-unknown'." }
```

## Test

```bash
dotnet test Practical.slnx
```

Runs 14 tests covering windowing boundary conditions and concurrent ingestion correctness.

## Benchmark

```bash
dotnet run --project Practical.Benchmarks -c Release
```

Results are written to `Practical.Benchmarks/BenchmarkDotNet.Artifacts/`. See `DESIGN.md` for a summary and interpretation.

## Assumptions

- **In-memory only**: all state is lost on restart, as specified.
- **UTC timestamps**: readings with a non-UTC `timestamp` are treated as UTC (the model uses `DateTime`; the controller converts via `DateTimeOffset` with zero offset).
- **Slightly out-of-order**: readings arriving up to ~5 minutes late land in the correct second bucket. Readings arriving more than 5 minutes late fall outside the live window and will never be visible on query.
- **No authentication or rate-limiting** — explicitly out of scope.
