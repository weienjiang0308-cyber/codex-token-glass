using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Codex.SpeedMetrics;

namespace CodexStatusbar;

internal sealed record StatisticsSnapshot(IReadOnlyList<UsageEntry> Entries,
    IReadOnlyDictionary<DateOnly, long> OfficialDays, DateTimeOffset? OfficialFetched,
    DateTimeOffset LocalFetched, string? Error, int ReadErrors, int LegacyFiles, string PriceStatus = "价格待核对", IReadOnlyDictionary<string,string>? ThreadNames = null, IReadOnlyList<MonthlyApiValue>? Monthly = null);

internal sealed class UsageStatisticsService : IDisposable
{
    private readonly UsageLedger _ledger = new();
    private readonly OfficialPriceUpdater _prices = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly string _sessions;
    private readonly string _cache;
    private DateTimeOffset _nextOfficial;
    private int _refreshRequested;
    private volatile StatisticsSnapshot? _current;
    public StatisticsSnapshot? Current { get => _current; private set => _current = value; }
    public UsageStatisticsService(string sessions, bool background = true)
    {
        _sessions = sessions;
        _cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexStatusbar", "statistics-official.json");
        if (background) _ = Task.Run(Loop);
    }
    public void RequestRefresh() => Interlocked.Exchange(ref _refreshRequested, 1);
    private async Task Loop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await RefreshAsync(_stop.Token);
                await Task.Delay(TimeSpan.FromSeconds(10), _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    public async Task RefreshAsync(CancellationToken token)
    {
        var force = Interlocked.Exchange(ref _refreshRequested, 0) == 1;
        var days = Current?.OfficialDays ?? new Dictionary<DateOnly, long>();
        var fetched = Current?.OfficialFetched;
        var error = Current?.Error;
        if (Current is null && File.Exists(_cache))
        {
            try
            {
                using var cached = JsonDocument.Parse(await File.ReadAllTextAsync(_cache, token));
                days = ReadDays(cached.RootElement.GetProperty("result"));
                fetched = cached.RootElement.GetProperty("fetched").GetDateTimeOffset();
                error = "正在核对官方缓存…";
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { }
        }
        // Full history once, then appended bytes only. Independent of selected foreground conversation.
        try { _ledger.Refresh(_sessions); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = "本地日志暂不可读"; }
        var names=_ledger.Entries.Select(e=>e.Thread).Distinct().ToDictionary(id=>id,
            id=>StateDatabase.TryReadDisplayThread(StateDatabase.ResolvePathForSessionsRoot(_sessions),id)?.Title ?? id);
        Current = new(_ledger.Entries, days, fetched, DateTimeOffset.UtcNow, error, _ledger.Errors, _ledger.LegacyFiles, _prices.Status, names, Current?.Monthly);
        await _prices.Refresh(_ledger.Entries.Select(e => e.Model), force, token);
        IReadOnlyList<MonthlyApiValue>? monthly=Current?.Monthly;
        try
        {
            monthly=MonthlyApiArchive.Save(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexStatusbar","monthly-api-equivalent.json"),_ledger.Entries,DateOnly.FromDateTime(DateTime.UtcNow));
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException){error="月度汇总暂未写入本机";}
        Current = Current! with { PriceStatus = _prices.Status, Monthly=monthly, Error=error };
        if (DateTimeOffset.UtcNow >= _nextOfficial || force)
        {
            _nextOfficial = DateTimeOffset.UtcNow.AddMinutes(10);
            try
            {
                var result = await ReadOfficialAsync(token);
                days = ReadDays(result); fetched = DateTimeOffset.UtcNow; error = null;
                Directory.CreateDirectory(Path.GetDirectoryName(_cache)!);
                var pending = _cache + ".tmp";
                await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(new { fetched, result }), token);
                File.Move(pending, _cache, true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                // Do not log protocol bodies or credentials. Cached official buckets remain explicitly dated.
                error = "官方更新失败（" + ex.GetType().Name + "）；保留上次成功数据";
                _nextOfficial = DateTimeOffset.UtcNow.AddMinutes(2);
            }
            Current = new(_ledger.Entries, days, fetched, DateTimeOffset.UtcNow, error, _ledger.Errors, _ledger.LegacyFiles, _prices.Status, names, monthly);
        }
    }
    internal static Dictionary<DateOnly, long> ReadDays(JsonElement result)
    {
        if (!result.TryGetProperty("dailyUsageBuckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Official daily buckets unavailable");
        var days = new Dictionary<DateOnly, long>();
        foreach (var b in buckets.EnumerateArray())
            if (DateOnly.TryParseExact(b.GetProperty("startDate").GetString(), "yyyy-MM-dd", out var date) &&
                b.GetProperty("tokens").TryGetInt64(out var tokens) && tokens >= 0) days[date] = tokens;
        return days;
    }
    private static string FindCodex()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var p = Path.Combine(dir.Trim('"'), "codex.exe"); if (File.Exists(p)) return p;
        }
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(root))
        {
            var p = Directory.EnumerateFiles(root, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (p is not null) return p;
        }
        throw new FileNotFoundException("Codex CLI not found");
    }
    internal static async Task<JsonElement> ReadOfficialAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        using var process = new Process { StartInfo = new ProcessStartInfo(FindCodex()) {
            Arguments = "app-server --stdio", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false)
        }};
        process.Start();
        _ = process.StandardError.ReadToEndAsync(timeout.Token);
        async Task<JsonElement> Rpc(int id, string method, object args)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = args }));
            await process.StandardInput.FlushAsync(timeout.Token);
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is string line)
            {
                using var doc = JsonDocument.Parse(line); var r = doc.RootElement;
                if (!r.TryGetProperty("id", out var n) || !n.TryGetInt32(out var reply) || reply != id) continue;
                if (r.TryGetProperty("error", out _)) throw new InvalidOperationException("Codex usage RPC failed");
                return r.GetProperty("result").Clone();
            }
            throw new EndOfStreamException();
        }
        try
        {
            await Rpc(1, "initialize", new { clientInfo = new { name = "codex-statusbar-statistics", version = "1" }, capabilities = new { experimentalApi = true } });
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}");
            return await Rpc(2, "account/usage/read", new { });
        }
        finally
        {
            // Only our short-lived read-only helper is closed; the desktop app-server is never touched.
            process.StandardInput.Close();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }
    public void Dispose() => _stop.Cancel();
}
