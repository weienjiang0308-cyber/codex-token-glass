using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Codex.SpeedMetrics;

public sealed record UsageEntry(string Id, string Thread, string? Turn, DateTimeOffset At,
    string Model, long Input, long Cached, long CacheWrite, long Output, long Reasoning, bool Legacy = false);

public sealed record UsageTotals(long Input, long Cached, long Output, long Reasoning,
    decimal KnownApiUsd, int Responses, int UnpricedResponses)
{
    public long Total => Input + Output;
    public double? CachePercent => Input > 0 ? 100d * Cached / Input : null;
    public static UsageTotals Sum(IEnumerable<UsageEntry> entries)
    {
        long input = 0, cached = 0, output = 0, reasoning = 0;
        decimal cost = 0; int count = 0, unknown = 0;
        var prices = ApiEquivalent.Prices;
        foreach (var e in entries)
        {
            input += e.Input; cached += e.Cached; output += e.Output; reasoning += e.Reasoning; count++;
            var value = ApiEquivalent.Price(e, prices);
            if (value is decimal n) cost += n; else unknown++;
        }
        return new(input, cached, output, reasoning, cost, count, unknown);
    }
}

/// <summary>2026-11-01 published Standard API equivalent, USD. Never a subscription bill.
/// Unknown models and cache-write TTL are deliberately unpriced. Per-request long context matters.</summary>
public static class ApiEquivalent
{
    public const string Source = "https://developers.openai.com/api/docs/pricing";
    private static IReadOnlyDictionary<string, ModelPrice> _prices = new Dictionary<string, ModelPrice>
    {
        ["gpt-6.1-sol"] = ModelPrice.Baseline("gpt-6.1-sol",2m,.10m,10m),
        ["gpt-6.1-terra"] = ModelPrice.Baseline("gpt-6.1-terra",1m,.05m,5m),
        ["gpt-6-astra"] = ModelPrice.Baseline("gpt-6-astra",10m,1m,50m),
        ["gpt-6-sol"] = ModelPrice.Baseline("gpt-6-sol",2m,.20m,10m)
    };
    public static IReadOnlyDictionary<string, ModelPrice> Prices => System.Threading.Volatile.Read(ref _prices);
    public static void SetPrices(IEnumerable<ModelPrice> prices) => System.Threading.Volatile.Write(ref _prices,
        prices.ToDictionary(p=>p.Model,StringComparer.Ordinal));
    public static decimal? Price(UsageEntry e) => Price(e,Prices);
    public static decimal? Price(UsageEntry e, IReadOnlyDictionary<string,ModelPrice> prices)
    {
        if (e.CacheWrite > 0) return null; // rollout does not identify 30m versus 12h cache write
        if (!prices.TryGetValue(e.Model,out var rate)) return null;
        if (rate.Cached is null && e.Cached > 0) return null;
        var input=rate.Input;var cached=rate.Cached??0;var output=rate.Output;
        if (rate.LongThreshold is long threshold && e.Input>threshold)
        {
            if(rate.LongInputMultiplier is null || rate.LongOutputMultiplier is null || (e.Cached>0&&rate.LongCachedMultiplier is null))return null;
            input*=rate.LongInputMultiplier.Value;output*=rate.LongOutputMultiplier.Value;cached*=rate.LongCachedMultiplier??1;
        }
        return ((e.Input - e.Cached) * input + e.Cached * cached + e.Output * output) / 1_000_000m;
    }
}

public sealed record DailyUsage(DateOnly Date, long Tokens, string Source);
public static class UsageMerge
{
    // Daily buckets have no timezone metadata. UTC alignment is an explicit provisional convention.
    // A historical official bucket REPLACES its local day, never adds to it.
    public static List<DailyUsage> Combine(IReadOnlyDictionary<DateOnly, long> official,
        IEnumerable<UsageEntry> entries, DateOnly today)
    {
        var local = entries.GroupBy(e => DateOnly.FromDateTime(e.At.UtcDateTime))
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Input + e.Output));
        return official.Keys.Union(local.Keys).Where(d => d <= today).OrderBy(d => d).Select(d =>
        {
            var hasOfficial = official.TryGetValue(d, out var remote);
            var here = local.GetValueOrDefault(d);
            return d < today && hasOfficial ? new DailyUsage(d, remote, "官方")
                : hasOfficial ? new DailyUsage(d, Math.Max(remote, here), "当日下界（取较大值，不相加）")
                : new DailyUsage(d, here, "本机暂计");
        }).ToList();
    }
    public static DateOnly Monday(DateOnly date) => date.AddDays(-((int)date.DayOfWeek + 6) % 7);
}

/// <summary>Incremental read-only index. All parsing runs on a worker; no file is rewritten.
/// Explicit response IDs deduplicate forked logs and mirrored event_msg carriers globally.</summary>
public sealed class UsageLedger
{
    private readonly Dictionary<string, UsageFile> _files = new(StringComparer.OrdinalIgnoreCase);
    public int Errors { get; private set; }
    public int LegacyFiles { get; private set; }
    public IReadOnlyList<UsageEntry> Entries { get; private set; } = Array.Empty<UsageEntry>();
    public void Refresh(string sessionsRoot)
    {
        Errors = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { sessionsRoot, Path.Combine(Path.GetDirectoryName(sessionsRoot)!, "archived_sessions") })
        {
            if (!Directory.Exists(root)) continue;
            try { foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)) paths.Add(path); }
            catch (IOException) { Errors++; }
            catch (UnauthorizedAccessException) { Errors++; }
        }
        foreach (var gone in _files.Keys.Except(paths).ToArray()) _files.Remove(gone);
        foreach (var path in paths)
        {
            if (!_files.TryGetValue(path, out var file)) _files[path] = file = new UsageFile(path);
            try { file.Read(); } catch (IOException) { Errors++; } catch (UnauthorizedAccessException) { Errors++; }
        }
        Errors += _files.Values.Sum(f => f.Errors);
        LegacyFiles = _files.Values.Count(f => !f.Modern && f.Legacy.Count > 0);
        Entries = _files.Values.SelectMany(f => f.Modern ? f.Records : f.Legacy)
            .GroupBy(e => e.Id, StringComparer.Ordinal).Select(g => g.First()).OrderBy(e => e.At).ToArray();
    }

    private sealed class UsageFile
    {
        private readonly string _path;
        private long _offset, _lineNumber;
        private readonly StringBuilder _pending = new();
        private Decoder _decoder = Encoding.UTF8.GetDecoder();
        private string _thread = "", _model = "unknown";
        private string? _turn;
        private long _priorInput, _priorCached, _priorOutput, _priorReasoning;
        private string? _priorFingerprint;
        public bool Modern { get; private set; }
        public int Errors { get; private set; }
        public List<UsageEntry> Records { get; } = new();
        public List<UsageEntry> Legacy { get; } = new();
        public UsageFile(string path) { _path = path; }
        public void Read()
        {
            using var f = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (f.Length < _offset)
            {
                _offset = _lineNumber = 0; _pending.Clear(); _decoder = Encoding.UTF8.GetDecoder();
                Records.Clear(); Legacy.Clear(); Modern = false; Errors = 0;
                _thread = ""; _model = "unknown"; _turn = null; _priorFingerprint = null;
                _priorInput = _priorCached = _priorOutput = _priorReasoning = 0;
            }
            f.Position = _offset;
            var bytes = new byte[64 * 1024]; var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            // Freeze the endpoint so a busy conversation cannot keep the scanner in this file forever.
            var end = f.Length;
            while (_offset < end)
            {
                var n = f.Read(bytes, 0, (int)Math.Min(bytes.Length, end - _offset)); if (n == 0) break;
                _offset += n; var nc = _decoder.GetChars(bytes, 0, n, chars, 0, false);
                for (int i = 0; i < nc; i++)
                {
                    if (chars[i] == '\n')
                    {
                        _lineNumber++;
                        if (_pending.Length <= 8 * 1024 * 1024) Parse(_pending.ToString());
                        else Errors++;
                        _pending.Clear();
                        if (_pending.Capacity > 64 * 1024) _pending.Capacity = 64 * 1024;
                    }
                    else if (_pending.Length <= 8 * 1024 * 1024) _pending.Append(chars[i]);
                }
            }
        }
        private void Parse(string line)
        {
            if (!line.Contains("token_usage_record", StringComparison.Ordinal) &&
                !line.Contains("token_count", StringComparison.Ordinal) && !line.Contains("turn_context", StringComparison.Ordinal) &&
                !line.Contains("session_meta", StringComparison.Ordinal) && !line.Contains("task_started", StringComparison.Ordinal)) return;
            try
            {
                using var doc = JsonDocument.Parse(line); var r = doc.RootElement;
                // A non-object root or payload is a foreign record, not a reason to stop all statistics.
                if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("payload", out var p)
                    || p.ValueKind != JsonValueKind.Object) return;
                string? S(JsonElement e, string k) => ResponseSpeedAnalyzer.Text(e, k);
                long N(JsonElement e, string k) => ResponseSpeedAnalyzer.Number(e, k);
                var type = S(r, "type");
                if (type == "session_meta") { _thread = S(p, "id") ?? _thread; return; }
                if (type == "turn_context") { _model = S(p, "model") ?? "unknown"; _turn = S(p, "turn_id") ?? _turn; return; }
                if (type == "event_msg" && S(p, "type") == "task_started") { _turn = S(p, "turn_id"); return; }
                if (!DateTimeOffset.TryParse(S(r, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) { Errors++; return; }
                if (type == "token_usage_record" && p.TryGetProperty("usage", out var u))
                {
                    Modern = true;
                    var id = S(p, "response_id");
                    if (string.IsNullOrEmpty(id)) { Errors++; return; }
                    var input = N(u, "input_tokens"); var cached = N(u, "cached_input_tokens"); var output = N(u, "output_tokens");
                    if (input < 0 || cached < 0 || cached > input || output < 0) { Errors++; return; }
                    Records.Add(new(id, S(p, "thread_id") ?? _thread, S(p, "turn_id") ?? _turn, at,
                        S(p, "model") ?? _model, input, cached, N(u, "cache_write_input_tokens"), output, N(u, "reasoning_output_tokens")));
                }
                else if (!Modern && type == "event_msg" && S(p, "type") == "token_count" &&
                    p.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object &&
                    info.TryGetProperty("total_token_usage", out var total) && total.ValueKind == JsonValueKind.Object)
                {
                    var fingerprint = total.GetRawText(); if (fingerprint == _priorFingerprint) return; _priorFingerprint = fingerprint;
                    var input = N(total, "input_tokens"); var cached = N(total, "cached_input_tokens");
                    var output = N(total, "output_tokens"); var reasoning = N(total, "reasoning_output_tokens");
                    // Cumulative regressions after compaction reset the baseline; never charge the old prefix again.
                    if (input >= _priorInput && output >= _priorOutput)
                        Legacy.Add(new("legacy:" + _thread + ":" + fingerprint, _thread, _turn, at, "unknown",
                            input - _priorInput, Math.Clamp(cached - _priorCached, 0, input - _priorInput), 0,
                            output - _priorOutput, Math.Max(0, reasoning - _priorReasoning), true));
                    _priorInput = input; _priorCached = cached; _priorOutput = output; _priorReasoning = reasoning;
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { Errors++; }
        }
    }
}
