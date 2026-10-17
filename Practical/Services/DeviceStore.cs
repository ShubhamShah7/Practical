using System.Collections.Concurrent;
using Practical.Models;

namespace Practical.Services;

public sealed class DeviceStore(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, DeviceWindow> _windows = new();

    public void Ingest(string deviceId, long epochSecond, double value)
    {
        var window = _windows.GetOrAdd(deviceId, static _ => new DeviceWindow());
        window.Add(epochSecond, value);
    }

    public AggregateResult? Query(string deviceId)
    {
        if (!_windows.TryGetValue(deviceId, out var window))
            return null;

        var nowEpochSecond = clock.GetUtcNow().ToUnixTimeSeconds();
        return window.Query(deviceId, nowEpochSecond);
    }
}
