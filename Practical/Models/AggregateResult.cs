namespace Practical.Models;

public record AggregateResult(
    string DeviceId,
    long Count,
    double Min,
    double Max,
    double Average);
