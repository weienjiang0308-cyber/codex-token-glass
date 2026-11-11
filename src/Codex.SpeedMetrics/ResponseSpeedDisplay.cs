namespace Codex.SpeedMetrics;

/// <summary>Harness-style completed-response throughput. No wall-clock decay, token
/// estimation, packet counting or arithmetic averaging of response rates.</summary>
public sealed class ResponseSpeedDisplay
{
    private readonly Dictionary<string, double> _last = new(StringComparer.Ordinal);
    public (double? Rate, bool Held) Read(SpeedReading reading)
    {
        // Both numerator and denominator cover exactly the same completed responses.
        if (reading.TimedResponses > 0 && reading.TimedOutputTokens >= 0 && reading.TimedMilliseconds > 0)
        {
            var rate = reading.TimedOutputTokens * 1000d / reading.TimedMilliseconds;
            if (double.IsFinite(rate))
            {
                _last[reading.ThreadId] = rate;
                if (_last.Count > 32) _last.Remove(_last.Keys.First(key => key != reading.ThreadId));
                return (rate, false);
            }
        }
        return _last.TryGetValue(reading.ThreadId, out var previous) ? (previous, true) : (null, false);
    }
}
