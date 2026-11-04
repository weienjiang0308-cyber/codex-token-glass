namespace Codex.SpeedMetrics;

public sealed record ThreadApiUsage(string Thread, DateOnly LastDate, UsageTotals Usage);
public sealed record DailyThreadApiUsage(DateOnly Date, string Thread, UsageTotals Usage);
public static class UsageBreakdown
{
    public static IReadOnlyList<ThreadApiUsage> ByThread(IEnumerable<UsageEntry> entries) => entries
        .DistinctBy(e=>e.Id).GroupBy(e=>e.Thread)
        .Select(g=>new ThreadApiUsage(g.Key,DateOnly.FromDateTime(g.Max(e=>e.At).UtcDateTime),UsageTotals.Sum(g)))
        .OrderByDescending(x=>x.Usage.KnownApiUsd).ThenBy(x=>x.Thread,StringComparer.Ordinal).ToArray();
    public static IReadOnlyList<DailyThreadApiUsage> ByDayAndThread(IEnumerable<UsageEntry> entries) => entries
        .DistinctBy(e=>e.Id).GroupBy(e=>(DateOnly.FromDateTime(e.At.UtcDateTime),e.Thread))
        .Select(g=>new DailyThreadApiUsage(g.Key.Item1,g.Key.Thread,UsageTotals.Sum(g)))
        .OrderByDescending(x=>x.Date).ThenByDescending(x=>x.Usage.KnownApiUsd).ToArray();
}
