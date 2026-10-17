using Practical.Models;

namespace Practical.Services;

/// <summary>
/// Per-device ring buffer: 300 one-second slots covering the 5-minute sliding window.
/// A slot is keyed by its Unix epoch second; on a slot collision (same index, different epoch)
/// the stale slot is reset before the new value is written.
/// </summary>
public sealed class DeviceWindow
{
    private const int BucketCount = 300; // 5 minutes × 60 seconds

    private readonly Bucket[] _buckets = new Bucket[BucketCount];
    private readonly object _lock = new();

    public void Add(long epochSecond, double value)
    {
        var idx = (int)(epochSecond % BucketCount);

        lock (_lock)
        {
            ref var b = ref _buckets[idx];

            if (b.EpochSecond != epochSecond)
            {
                // Stale slot — reset it for this new epoch second
                b.EpochSecond = epochSecond;
                b.Count = 1;
                b.Sum = value;
                b.Min = value;
                b.Max = value;
            }
            else
            {
                b.Count++;
                b.Sum += value;
                if (value < b.Min) b.Min = value;
                if (value > b.Max) b.Max = value;
            }
        }
    }

    public AggregateResult? Query(string deviceId, long nowEpochSecond)
    {
        long count = 0;
        double sum = 0;
        double min = double.MaxValue;
        double max = double.MinValue;

        lock (_lock)
        {
            for (var i = 0; i < BucketCount; i++)
            {
                ref readonly var b = ref _buckets[i];
                // Slot is live if it belongs to an epoch within the last 300 seconds
                if (b.Count > 0 && nowEpochSecond - b.EpochSecond < BucketCount)
                {
                    count += b.Count;
                    sum += b.Sum;
                    if (b.Min < min) min = b.Min;
                    if (b.Max > max) max = b.Max;
                }
            }
        }

        if (count == 0)
            return null;

        return new AggregateResult(deviceId, count, min, max, sum / count);
    }

    private struct Bucket
    {
        public long EpochSecond; // 0 = empty slot
        public long Count;
        public double Sum;
        public double Min;
        public double Max;
    }
}
