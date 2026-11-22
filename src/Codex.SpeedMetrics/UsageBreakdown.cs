namespace Codex.SpeedMetrics;

public sealed record ThreadApiUsage(string Thread, DateOnly LastDate, UsageTotals Usage);
public sealed record DailyThreadApiUsage(DateOnly Date, string Thread, UsageTotals Usage);
public static class UsageBreakdown
{
    private static IEnumerable<UsageEntry> DeduplicateSafe(IEnumerable<UsageEntry> entries)
    {
        var seen = new Dictionary<string, UsageEntry>(StringComparer.Ordinal);
        var seenByThread = new HashSet<(string Thread, string Id)>();
        foreach (var e in entries)
        {
            if (!seen.TryGetValue(e.Id, out var first))
            {
                seen[e.Id] = e;
                seenByThread.Add((e.Thread, e.Id));
                yield return e;
                continue;
            }
            var sameCounts = first.Input == e.Input && first.Cached == e.Cached
                && first.CacheKnown == e.CacheKnown && first.Output == e.Output;
            if (sameCounts) continue; // Genuine fork copy.
            if (seenByThread.Add((e.Thread, e.Id))) yield return e;
        }
    }

    public static IReadOnlyList<ThreadApiUsage> ByThread(IEnumerable<UsageEntry> entries) => DeduplicateSafe(entries)
        .GroupBy(e=>e.Thread)
        .Select(g=>new ThreadApiUsage(g.Key,DateOnly.FromDateTime(g.Max(e=>e.At).UtcDateTime),UsageTotals.Sum(g)))
        .OrderByDescending(x=>x.Usage.KnownApiUsd).ThenBy(x=>x.Thread,StringComparer.Ordinal).ToArray();
    public static IReadOnlyList<DailyThreadApiUsage> ByDayAndThread(IEnumerable<UsageEntry> entries) => DeduplicateSafe(entries)
        .GroupBy(e=>(DateOnly.FromDateTime(e.At.UtcDateTime),e.Thread))
        .Select(g=>new DailyThreadApiUsage(g.Key.Item1,g.Key.Thread,UsageTotals.Sum(g)))
        .OrderByDescending(x=>x.Date).ThenByDescending(x=>x.Usage.KnownApiUsd).ToArray();
}
