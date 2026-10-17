using BenchmarkDotNet.Attributes;
using Practical.Services;

[MemoryDiagnoser]
[ShortRunJob]
public class IngestionBenchmarks
{
    private DeviceStore _store = null!;
    private long _epochSecond;

    [GlobalSetup]
    public void Setup()
    {
        _store = new DeviceStore(TimeProvider.System);
        _epochSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    [Benchmark]
    [Arguments(1_000)]
    [Arguments(10_000)]
    [Arguments(50_000)]
    public void IngestBatch_SingleDevice(int count)
    {
        for (var i = 0; i < count; i++)
            _store.Ingest("device-bench", _epochSecond, i * 1.0);
    }

    [Benchmark]
    [Arguments(1_000)]
    [Arguments(10_000)]
    public void IngestBatch_MultiDevice(int count)
    {
        for (var i = 0; i < count; i++)
            _store.Ingest($"device-{i % 100}", _epochSecond, i * 1.0);
    }

    [Benchmark]
    public void IngestBatch_Concurrent_10Threads_1000Each()
    {
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1_000; i++)
                _store.Ingest("device-concurrent", _epochSecond, i * 1.0);
        }));
        Task.WhenAll(tasks).GetAwaiter().GetResult();
    }

    [Benchmark]
    public void Query_SingleDevice()
    {
        _store.Query("device-bench");
    }

    [Benchmark]
    public void Query_UnknownDevice()
    {
        _store.Query("device-unknown-xyz");
    }
}
