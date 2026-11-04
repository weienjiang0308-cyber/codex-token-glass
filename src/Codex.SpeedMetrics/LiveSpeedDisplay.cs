namespace Codex.SpeedMetrics;

/// <summary>Presentation-only hold; raw telemetry stays a rolling two-second measurement.
/// Window expiry without new output must not decay the last active reading toward zero.</summary>
public sealed class LiveSpeedDisplay
{
    private string? _thread, _turn;
    private long? _lastOutput;
    private double? _lastRate;
    public (double? Rate, bool Held) Read(SpeedReading reading, long monotonicMs)
    {
        if (_thread != reading.ThreadId || _turn != reading.TurnId || !reading.LiveConnected)
        {
            _thread = reading.ThreadId; _turn = reading.TurnId; _lastOutput = null; _lastRate = null;
        }
        if (reading.LiveConnected && reading.LastOutputAtMs is long at && at != _lastOutput &&
            reading.LiveEstimateTps is > 0 && double.IsFinite(reading.LiveEstimateTps.Value))
        { _lastOutput = at; _lastRate = reading.LiveEstimateTps; }
        return (_lastRate, _lastRate.HasValue && (!reading.Streaming || monotonicMs - (_lastOutput ?? monotonicMs) > 1000));
    }
}
