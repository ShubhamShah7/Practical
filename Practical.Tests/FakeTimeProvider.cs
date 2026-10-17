namespace Practical.Tests;

/// <summary>
/// Minimal TimeProvider override that lets tests control the clock.
/// </summary>
public sealed class FakeTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan delta) => UtcNow += delta;
}
