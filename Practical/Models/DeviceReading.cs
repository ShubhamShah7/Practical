namespace Practical.Models;

public record struct DeviceReading(string DeviceId, DateTime Timestamp, double Value);
