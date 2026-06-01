namespace DellFanControl.Services;

/// <summary>
/// Interface for IPMI service implementations
/// Uses the shared status objects for temperatures and fans.
/// </summary>
public interface IIPMIService : IDisposable
{
    Task<TemperatureStatus> GetTemperaturesAsync(CancellationToken cancellationToken = default);
    Task<FanStatus> GetFanStatusAsync(CancellationToken cancellationToken = default);
    Task<bool> SetFanSpeedAsync(int percentage, CancellationToken cancellationToken = default);
    Task<bool> RestoreDynamicControlAsync(CancellationToken cancellationToken = default);
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
}

public record TemperatureReading
{
    public string Name { get; init; } = string.Empty;
    public double Value { get; init; }
    public string Unit { get; init; } = string.Empty;
}

public record FanReading
{
    public string Name { get; init; } = string.Empty;
    public int SpeedRPM { get; init; }
}