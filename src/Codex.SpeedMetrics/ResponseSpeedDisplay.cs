namespace Codex.SpeedMetrics;

/// <summary>Harness-style completed-response throughput. No wall-clock decay, token
/// estimation, packet counting or arithmetic averaging of response rates.</summary>
public sealed class ResponseSpeedDisplay
{
    private readonly Dictionary<string, (string? SegmentKey, double Rate)> _last = new(StringComparer.Ordinal);
    public (double? Rate, bool Held) Read(SpeedReading reading)
    {
        // Never carry a previous model/route segment's held speed across a model or route switch.
        if (reading.SegmentPending)
        {
            _last.Remove(reading.ThreadId);
            return (null, false);
        }

        // Both numerator and denominator cover exactly the same completed responses.
        if (reading.TimedResponses > 0 && reading.TimedOutputTokens >= 0 && reading.TimedMilliseconds > 0)
        {
            var rate = reading.TimedOutputTokens * 1000d / reading.TimedMilliseconds;
            if (double.IsFinite(rate))
            {
                _last[reading.ThreadId] = (reading.SegmentKey, rate);
                if (_last.Count > 32) _last.Remove(_last.Keys.First(key => key != reading.ThreadId));
                return (rate, false);
            }
        }

        if (_last.TryGetValue(reading.ThreadId, out var previous))
        {
            // Only retain the previous rate while still in the exact same model/route segment
            // (or while segment metadata is not yet loaded on a live-only tick).
            if (reading.SegmentKey is null || string.Equals(previous.SegmentKey, reading.SegmentKey, StringComparison.Ordinal))
            {
                return (previous.Rate, true);
            }
            _last.Remove(reading.ThreadId);
        }
        return (null, false);
    }
}
