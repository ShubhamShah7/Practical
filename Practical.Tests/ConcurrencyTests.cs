using Practical.Services;
using Xunit;

namespace Practical.Tests;

public class ConcurrencyTests
{
    [Fact]
    public async Task Concurrent_ingestion_same_device_no_lost_updates()
    {
        var clock = new FakeTimeProvider();
        var store = new DeviceStore(clock);
        var epoch = clock.UtcNow.ToUnixTimeSeconds();

        const int threads = 10;
        const int perThread = 1_000;

        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < perThread; i++)
                store.Ingest("device-shared", epoch, 1.0);
        }));

        await Task.WhenAll(tasks);

        var result = store.Query("device-shared");
        Assert.NotNull(result);
        Assert.Equal(threads * perThread, result!.Count);
    }

    [Fact]
    public async Task Concurrent_ingestion_different_devices_correct_isolation()
    {
        var clock = new FakeTimeProvider();
        var store = new DeviceStore(clock);
        var epoch = clock.UtcNow.ToUnixTimeSeconds();

        const int deviceCount = 20;
        const int perDevice = 500;

        var tasks = Enumerable.Range(0, deviceCount).Select(i => Task.Run(() =>
        {
            for (var j = 0; j < perDevice; j++)
                store.Ingest($"device-{i}", epoch, i * 1.0);
        }));

        await Task.WhenAll(tasks);

        for (var i = 0; i < deviceCount; i++)
        {
            var result = store.Query($"device-{i}");
            Assert.NotNull(result);
            Assert.Equal(perDevice, result!.Count);
            Assert.Equal(i * 1.0, result.Min);
            Assert.Equal(i * 1.0, result.Max);
        }
    }

    [Fact]
    public async Task Concurrent_reads_and_writes_no_exceptions()
    {
        var clock = new FakeTimeProvider();
        var store = new DeviceStore(clock);
        var epoch = clock.UtcNow.ToUnixTimeSeconds();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        // Writers
        var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                store.Ingest("concurrent-device", epoch, 42.0);
                await Task.Yield();
            }
        }));

        // Readers — must not throw
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            var exceptions = new List<Exception>();
            while (!cts.Token.IsCancellationRequested)
            {
                try { store.Query("concurrent-device"); }
                catch (Exception ex) { exceptions.Add(ex); }
                await Task.Yield();
            }
            return exceptions;
        }));

        await Task.WhenAll(writers);
        var readerResults = await Task.WhenAll(readers);

        Assert.All(readerResults, exList => Assert.Empty(exList));
    }

    [Fact]
    public async Task High_contention_sum_is_accurate()
    {
        var clock = new FakeTimeProvider();
        var store = new DeviceStore(clock);
        var epoch = clock.UtcNow.ToUnixTimeSeconds();

        const int threads = 8;
        const int perThread = 2_000;
        const double value = 3.14;

        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < perThread; i++)
                store.Ingest("hotspot", epoch, value);
        }));

        await Task.WhenAll(tasks);

        var result = store.Query("hotspot");
        Assert.NotNull(result);
        Assert.Equal(threads * perThread, result!.Count);
        // Average of identical values must equal that value
        Assert.Equal(value, result.Average, precision: 10);
    }
}
