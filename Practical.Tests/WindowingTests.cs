using Practical.Services;
using Xunit;

namespace Practical.Tests;

public class WindowingTests
{
    private static (DeviceStore store, FakeTimeProvider clock) MakeStore()
    {
        var clock = new FakeTimeProvider();
        return (new DeviceStore(clock), clock);
    }

    private static long EpochOf(DateTimeOffset dt) => dt.ToUnixTimeSeconds();

    [Fact]
    public void Reading_within_window_is_counted()
    {
        var (store, clock) = MakeStore();
        store.Ingest("d1", EpochOf(clock.UtcNow), 10.0);

        var result = store.Query("d1");
        Assert.NotNull(result);
        Assert.Equal(1, result!.Count);
        Assert.Equal(10.0, result.Min);
        Assert.Equal(10.0, result.Max);
        Assert.Equal(10.0, result.Average);
    }

    [Fact]
    public void Reading_just_outside_window_is_excluded()
    {
        var (store, clock) = MakeStore();
        // Ingest at t=0, then advance 300 seconds (exactly at boundary = outside)
        var readingTime = clock.UtcNow;
        store.Ingest("d1", EpochOf(readingTime), 42.0);

        clock.Advance(TimeSpan.FromSeconds(300));

        // nowEpochSecond - readingEpoch == 300, which is NOT < 300, so excluded
        var result = store.Query("d1");
        Assert.Null(result);
    }

    [Fact]
    public void Reading_just_inside_window_is_included()
    {
        var (store, clock) = MakeStore();
        var readingTime = clock.UtcNow;
        store.Ingest("d1", EpochOf(readingTime), 42.0);

        clock.Advance(TimeSpan.FromSeconds(299));

        // nowEpochSecond - readingEpoch == 299, which is < 300 → included
        var result = store.Query("d1");
        Assert.NotNull(result);
        Assert.Equal(1, result!.Count);
    }

    [Fact]
    public void Multiple_readings_aggregate_correctly()
    {
        var (store, clock) = MakeStore();
        var epoch = EpochOf(clock.UtcNow);
        store.Ingest("d1", epoch, 10.0);
        store.Ingest("d1", epoch, 20.0);
        store.Ingest("d1", epoch, 30.0);

        var result = store.Query("d1");
        Assert.NotNull(result);
        Assert.Equal(3, result!.Count);
        Assert.Equal(10.0, result.Min);
        Assert.Equal(30.0, result.Max);
        Assert.Equal(20.0, result.Average);
    }

    [Fact]
    public void Readings_across_multiple_seconds_aggregate_correctly()
    {
        var (store, clock) = MakeStore();
        var base_ = clock.UtcNow;
        store.Ingest("d1", EpochOf(base_), 5.0);
        store.Ingest("d1", EpochOf(base_.AddSeconds(10)), 15.0);
        store.Ingest("d1", EpochOf(base_.AddSeconds(20)), 10.0);

        var result = store.Query("d1");
        Assert.NotNull(result);
        Assert.Equal(3, result!.Count);
        Assert.Equal(5.0, result.Min);
        Assert.Equal(15.0, result.Max);
        Assert.InRange(result.Average, 9.99, 10.01);
    }

    [Fact]
    public void Unknown_device_returns_null()
    {
        var (store, _) = MakeStore();
        var result = store.Query("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public void Device_with_all_expired_readings_returns_null()
    {
        var (store, clock) = MakeStore();
        store.Ingest("d1", EpochOf(clock.UtcNow), 99.0);

        clock.Advance(TimeSpan.FromMinutes(6)); // well past window

        var result = store.Query("d1");
        Assert.Null(result);
    }

    [Fact]
    public void Out_of_order_readings_within_window_are_handled()
    {
        var (store, clock) = MakeStore();
        var now = clock.UtcNow;
        // "Late" reading for 30 seconds ago arrives after a newer reading
        store.Ingest("d1", EpochOf(now), 100.0);
        store.Ingest("d1", EpochOf(now.AddSeconds(-30)), 50.0); // out-of-order but within window

        var result = store.Query("d1");
        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Equal(50.0, result.Min);
        Assert.Equal(100.0, result.Max);
    }

    [Fact]
    public void Mixed_in_window_and_expired_readings()
    {
        var (store, clock) = MakeStore();
        var old = clock.UtcNow;

        // Ingest at t=0 (will expire after 5 min)
        store.Ingest("d1", EpochOf(old), 1000.0);

        // Advance 4 minutes, ingest fresh reading
        clock.Advance(TimeSpan.FromMinutes(4));
        var fresh = clock.UtcNow;
        store.Ingest("d1", EpochOf(fresh), 5.0);

        // Advance another 90 seconds — old reading (now 330s old) is expired; fresh (90s old) is still live
        clock.Advance(TimeSpan.FromSeconds(90));

        var result = store.Query("d1");
        Assert.NotNull(result);
        Assert.Equal(1, result!.Count);
        Assert.Equal(5.0, result.Min);
        Assert.Equal(5.0, result.Max);
    }

    [Fact]
    public void Different_devices_are_isolated()
    {
        var (store, clock) = MakeStore();
        var epoch = EpochOf(clock.UtcNow);
        store.Ingest("alpha", epoch, 1.0);
        store.Ingest("beta", epoch, 2.0);

        var a = store.Query("alpha");
        var b = store.Query("beta");

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(1, a!.Count);
        Assert.Equal(1, b!.Count);
        Assert.Equal(1.0, a.Average);
        Assert.Equal(2.0, b.Average);
    }
}
