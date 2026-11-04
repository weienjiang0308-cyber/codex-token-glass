namespace Codex.SpeedMetrics;
public static class ResponseSpeedAggregate
{
    public static double? Average(IEnumerable<SpeedReading?> readings)
    {
        long tokens=0, milliseconds=0;
        foreach(var reading in readings)
            if(reading is { TimedMilliseconds: > 0 })
            {tokens+=reading.TimedOutputTokens;milliseconds+=reading.TimedMilliseconds;}
        return milliseconds>0?1000d*tokens/milliseconds:null;
    }
}
