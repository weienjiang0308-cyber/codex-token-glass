using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Codex.SpeedMetrics;

public sealed record UsageEntry(string Id, string Thread, string? Turn, DateTimeOffset At,
    string Model, long Input, long Cached, long CacheWrite, long Output, long Reasoning, bool Legacy = false,
    bool CacheKnown = true, string? Route = null, string? ServedModel = null, string? UsageSource = null,
    bool HasSourceConflict = false, long? GatewayInput = null, long? GatewayCached = null, long? GatewayOutput = null,
    int SegmentIndex = 0, bool RequestCountKnown = true)
{
    public string DisplayModel => !string.IsNullOrWhiteSpace(ServedModel) ? ServedModel : Model;
    public string RouteAndModelKey => string.IsNullOrWhiteSpace(Route) ? DisplayModel : $"{DisplayModel} · {Route}";
}

public sealed record UsageConflict(string ResponseId, string Thread, string Reason,
    long PrimaryInput, long PrimaryCached, long PrimaryOutput,
    long? SecondaryInput, long? SecondaryCached, long? SecondaryOutput);

public sealed record UsageTotals(long Input, long Cached, long Output, long Reasoning,
    decimal KnownApiUsd, int Responses, int UnpricedResponses,
    long CacheKnownInput = -1, int CacheKnownResponses = 0, int CacheUnknownResponses = 0,
    int ConflictResponses = 0, bool RequestCountExact = true)
{
    public long Total => Input + Output;
    public long EffectiveCacheInputDenominator => CacheKnownInput >= 0 ? CacheKnownInput : Input;
    public double? CachePercent => EffectiveCacheInputDenominator > 0 ? 100d * Cached / EffectiveCacheInputDenominator : null;
    public static UsageTotals Sum(IEnumerable<UsageEntry> entries)
    {
        long input = 0, cached = 0, cacheKnownInput = 0, output = 0, reasoning = 0;
        decimal cost = 0; int count = 0, unknown = 0, cacheKnown = 0, cacheUnknown = 0, conflicts = 0;
        var requestCountExact = true;
        var prices = ApiEquivalent.Prices;
        foreach (var e in entries)
        {
            input += e.Input;
            if (e.CacheKnown)
            {
                cached += e.Cached;
                cacheKnownInput += e.Input;
                cacheKnown++;
            }
            else
            {
                cacheUnknown++;
            }
            output += e.Output; reasoning += e.Reasoning; count++;
            if (e.HasSourceConflict) conflicts++;
            if (!e.RequestCountKnown) requestCountExact = false;
            var value = ApiEquivalent.Price(e, prices);
            if (value is decimal n) cost += n; else unknown++;
        }
        return new(input, cached, output, reasoning, cost, count, unknown,
            cacheKnownInput, cacheKnown, cacheUnknown, conflicts, requestCountExact);
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
    private readonly IGatewayTimingLookup? _gatewayLookup;
    public UsageLedger(IGatewayTimingLookup? gatewayLookup = null) => _gatewayLookup = gatewayLookup;
    public int Errors { get; private set; }
    public int LegacyFiles { get; private set; }
    public IReadOnlyList<UsageEntry> Entries { get; private set; } = Array.Empty<UsageEntry>();
    public IReadOnlyList<UsageConflict> Conflicts { get; private set; } = Array.Empty<UsageConflict>();
    public void Refresh(string sessionsRoot)
    {
        Errors = 0;
        _gatewayLookup?.Refresh();
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
        var conflicts = new List<UsageConflict>();
        var merged = new List<UsageEntry>();
        var rawAll = _files.Values.SelectMany(f => f.AllEntries);

        foreach (var group in rawAll.GroupBy(e => e.Id, StringComparer.Ordinal))
        {
            var items = group.ToList();
            if (items.Count == 1)
            {
                merged.Add(EnrichWithGateway(items[0], conflicts));
                continue;
            }

            var first = items[0];
            var allEquivalent = items.All(item => CountsEqual(first, item));
            if (allEquivalent)
            {
                // Genuine fork copy or mirrored log: deduplicate to one authoritative entry.
                var best = items
                    .OrderByDescending(e => !string.Equals(e.Model, "unknown", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(e => !string.IsNullOrWhiteSpace(e.Turn))
                    .ThenBy(e => e.At)
                    .First();
                merged.Add(EnrichWithGateway(best, conflicts));
                continue;
            }

            // Same response_id carried different counts: distinguish cross-thread ID collisions
            // from same-thread count conflicts instead of silently picking the first row.
            foreach (var threadGroup in items.GroupBy(e => e.Thread, StringComparer.Ordinal))
            {
                var threadItems = threadGroup.ToList();
                var primary = threadItems[0];
                var sameThreadDivergent = threadItems.Any(item => !CountsEqual(primary, item));
                var crossThreadCollision = items.Select(i => i.Thread).Distinct(StringComparer.Ordinal).Count() > 1;
                if (sameThreadDivergent)
                {
                    var other = threadItems.First(item => !CountsEqual(primary, item));
                    conflicts.Add(new UsageConflict(primary.Id, primary.Thread, "same-thread-id-count-conflict",
                        primary.Input, primary.Cached, primary.Output, other.Input, other.Cached, other.Output));
                }
                else if (crossThreadCollision)
                {
                    var other = items.First(item => !string.Equals(item.Thread, primary.Thread, StringComparison.Ordinal));
                    conflicts.Add(new UsageConflict(primary.Id, primary.Thread, "cross-thread-id-collision",
                        primary.Input, primary.Cached, primary.Output, other.Input, other.Cached, other.Output));
                }
                var enriched = EnrichWithGateway(primary with { HasSourceConflict = true }, conflicts);
                merged.Add(enriched);
            }
        }

        AssignSegments(merged);
        Conflicts = conflicts;
        Entries = merged.OrderBy(e => e.At).ToArray();
    }

    private UsageEntry EnrichWithGateway(UsageEntry entry, List<UsageConflict> conflicts)
    {
        if (entry.Legacy || _gatewayLookup is null || string.IsNullOrWhiteSpace(entry.Id))
        {
            return entry;
        }

        if (_gatewayLookup.TryGet(entry.Id, out var gw))
        {
            var route = gw.EffectiveRoute;
            var served = gw.EffectiveServedModel;
            var gwInput = gw.FullInputTokens;
            var gwCached = gw.CacheUnknown ? null : gw.CacheReadTokens;
            var gwOutput = gw.OutputTokens;

            var divergent = (gwInput is long gi && gi != entry.Input)
                || (gwOutput is long go && go != entry.Output)
                || (!gw.CacheUnknown && entry.CacheKnown && gwCached is long gc && gc != entry.Cached);

            if (divergent)
            {
                conflicts.Add(new UsageConflict(entry.Id, entry.Thread, "codex-gateway-count-divergence",
                    entry.Input, entry.Cached, entry.Output, gwInput, gwCached, gwOutput));
            }

            return entry with
            {
                Route = route ?? entry.Route,
                ServedModel = served ?? entry.ServedModel,
                UsageSource = "codex+gateway",
                HasSourceConflict = entry.HasSourceConflict || divergent,
                GatewayInput = divergent ? gwInput : entry.GatewayInput,
                GatewayCached = divergent ? gwCached : entry.GatewayCached,
                GatewayOutput = divergent ? gwOutput : entry.GatewayOutput
            };
        }

        if (_gatewayLookup.TryGetInspect(entry.Id, out var inspected) && inspected.IsAmbiguous)
        {
            conflicts.Add(new UsageConflict(entry.Id, entry.Thread, "gateway-ambiguous-response-id",
                entry.Input, entry.Cached, entry.Output, inspected.FullInputTokens, inspected.CacheReadTokens, inspected.OutputTokens));
            return entry with { HasSourceConflict = true };
        }

        return entry;
    }

    private static bool CountsEqual(UsageEntry left, UsageEntry right) =>
        left.Input == right.Input
        && left.Cached == right.Cached
        && left.CacheKnown == right.CacheKnown
        && left.CacheWrite == right.CacheWrite
        && left.Output == right.Output
        && left.Reasoning == right.Reasoning;

    private static void AssignSegments(List<UsageEntry> entries)
    {
        var indicesByThread = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
        {
            var thread = entries[i].Thread;
            if (!indicesByThread.TryGetValue(thread, out var list))
            {
                list = new List<int>();
                indicesByThread[thread] = list;
            }
            list.Add(i);
        }

        foreach (var list in indicesByThread.Values)
        {
            list.Sort((a, b) => entries[a].At.CompareTo(entries[b].At));
            var segIndex = 0;
            string? segModel = null;
            string? segRoute = null;
            foreach (var idx in list)
            {
                var e = entries[idx];
                var effectiveModel = e.DisplayModel;
                var effectiveRoute = e.Route;
                if (segIndex == 0)
                {
                    segIndex = 1;
                    segModel = effectiveModel;
                    segRoute = effectiveRoute;
                }
                else
                {
                    var modelChanged = !string.Equals(segModel, effectiveModel, StringComparison.OrdinalIgnoreCase);
                    var routeChanged = !string.IsNullOrWhiteSpace(segRoute)
                        && !string.IsNullOrWhiteSpace(effectiveRoute)
                        && !string.Equals(segRoute, effectiveRoute, StringComparison.OrdinalIgnoreCase);
                    if (modelChanged || routeChanged)
                    {
                        segIndex++;
                        segModel = effectiveModel;
                        segRoute = effectiveRoute;
                    }
                    else if (string.IsNullOrWhiteSpace(segRoute) && !string.IsNullOrWhiteSpace(effectiveRoute))
                    {
                        segRoute = effectiveRoute;
                    }
                }
                entries[idx] = e with { SegmentIndex = segIndex };
            }
        }
    }

    private sealed class UsageFile
    {
        private readonly string _path;
        private long _offset, _lineNumber;
        private readonly StringBuilder _pending = new();
        private Decoder _decoder = Encoding.UTF8.GetDecoder();
        private string _thread = "", _model = "unknown";
        private string? _turn;
        private readonly Dictionary<string, string> _turnModels = new(StringComparer.Ordinal);
        private bool _hasUnboundTurnContext;
        private long _priorInput, _priorCached, _priorOutput, _priorReasoning;
        private bool _priorCacheKnown = true;
        private string? _priorFingerprint;
        private long _modernCoveredInput, _modernCoveredOutput;
        private string? _lastModernUsageFingerprint;
        public bool Modern { get; private set; }
        public int Errors { get; private set; }
        public List<UsageEntry> Records { get; } = new();
        public List<UsageEntry> Legacy { get; } = new();
        public List<UsageEntry> AllEntries { get; } = new();
        public UsageFile(string path) { _path = path; }
        public void Read()
        {
            using var f = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (f.Length < _offset)
            {
                _offset = _lineNumber = 0; _pending.Clear(); _decoder = Encoding.UTF8.GetDecoder();
                Records.Clear(); Legacy.Clear(); AllEntries.Clear(); Modern = false; Errors = 0;
                _thread = ""; _model = "unknown"; _turn = null; _turnModels.Clear(); _hasUnboundTurnContext = false;
                _priorFingerprint = null; _lastModernUsageFingerprint = null;
                _priorInput = _priorCached = _priorOutput = _priorReasoning = 0;
                _priorCacheKnown = true; _modernCoveredInput = _modernCoveredOutput = 0;
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
                var type = S(r, "type");
                if (type == "session_meta") { _thread = S(p, "id") ?? _thread; return; }
                if (type == "turn_context")
                {
                    var ctxModel = S(p, "model");
                    var ctxTurn = S(p, "turn_id");
                    if (!string.IsNullOrWhiteSpace(ctxModel)) _model = ctxModel;
                    else _model = "unknown";
                    if (!string.IsNullOrWhiteSpace(ctxTurn))
                    {
                        _turn = ctxTurn;
                        _turnModels[ctxTurn] = _model;
                        _hasUnboundTurnContext = false;
                    }
                    else
                    {
                        _hasUnboundTurnContext = true;
                    }
                    return;
                }
                if (type == "event_msg" && S(p, "type") == "task_started") { _turn = S(p, "turn_id"); return; }
                if (!DateTimeOffset.TryParse(S(r, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) { Errors++; return; }
                if (type == "token_usage_record" && p.TryGetProperty("usage", out var u))
                {
                    var id = S(p, "response_id");
                    if (string.IsNullOrEmpty(id)) { Errors++; return; }
                    if (!TryParseUsageObject(u, out var parsed)) { Errors++; return; }
                    Modern = true;
                    var turnId = S(p, "turn_id") ?? _turn;
                    var resolvedModel = ResolveModelForTurn(S(p, "model"), turnId);
                    var entry = new UsageEntry(
                        id,
                        S(p, "thread_id") ?? _thread,
                        turnId,
                        at,
                        resolvedModel,
                        parsed.Input,
                        parsed.Cached,
                        parsed.CacheWrite,
                        parsed.Output,
                        parsed.Reasoning,
                        Legacy: false,
                        CacheKnown: parsed.CacheKnown,
                        UsageSource: "codex-record");
                    Records.Add(entry);
                    AllEntries.Add(entry);
                    _lastModernUsageFingerprint = UsageFingerprint(parsed.Input, parsed.Cached, parsed.Output);

                    if (p.TryGetProperty("thread_token_usage", out var threadTotal)
                        && threadTotal.ValueKind == JsonValueKind.Object
                        && TryParseUsageObject(threadTotal, out var modernCumulative))
                    {
                        _priorInput = modernCumulative.Input;
                        _priorCached = modernCumulative.Cached;
                        _priorOutput = modernCumulative.Output;
                        _priorReasoning = modernCumulative.Reasoning;
                        _priorCacheKnown = modernCumulative.CacheKnown;
                        _modernCoveredInput = Math.Max(_modernCoveredInput, modernCumulative.Input);
                        _modernCoveredOutput = Math.Max(_modernCoveredOutput, modernCumulative.Output);
                        _priorFingerprint = threadTotal.GetRawText();
                    }
                    else
                    {
                        _priorInput += parsed.Input;
                        if (parsed.CacheKnown) _priorCached += parsed.Cached;
                        else _priorCacheKnown = false;
                        _priorOutput += parsed.Output;
                        _priorReasoning += parsed.Reasoning;
                        _modernCoveredInput = Math.Max(_modernCoveredInput, _priorInput);
                        _modernCoveredOutput = Math.Max(_modernCoveredOutput, _priorOutput);
                    }
                }
                else if (type == "event_msg" && S(p, "type") == "token_count" &&
                    p.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object &&
                    info.TryGetProperty("total_token_usage", out var total) && total.ValueKind == JsonValueKind.Object)
                {
                    var fingerprint = total.GetRawText(); if (fingerprint == _priorFingerprint) return; _priorFingerprint = fingerprint;
                    if (!TryParseUsageObject(total, out var parsedTotal)) { Errors++; return; }

                    // If a modern record already covered this exact or higher cumulative level,
                    // this token_count is a redundant mirror rather than an independent request.
                    if (Modern && parsedTotal.Input <= _modernCoveredInput && parsedTotal.Output <= _modernCoveredOutput)
                    {
                        return;
                    }

                    ParsedUsage? parsedLast = null;
                    if (info.TryGetProperty("last_token_usage", out var lastEl)
                        && lastEl.ValueKind == JsonValueKind.Object
                        && TryParseUsageObject(lastEl, out var lastParsed))
                    {
                        parsedLast = lastParsed;
                        if (Modern && _lastModernUsageFingerprint is not null
                            && UsageFingerprint(lastParsed.Input, lastParsed.Cached, lastParsed.Output) == _lastModernUsageFingerprint
                            && parsedTotal.Input == _priorInput && parsedTotal.Output == _priorOutput)
                        {
                            return;
                        }
                    }

                    // Cumulative regressions after compaction reset the baseline; never charge the old prefix again.
                    if (parsedTotal.Input >= _priorInput && parsedTotal.Output >= _priorOutput)
                    {
                        var deltaInput = parsedTotal.Input - _priorInput;
                        var deltaOutput = parsedTotal.Output - _priorOutput;
                        if (deltaInput > 0 || deltaOutput > 0)
                        {
                            var cacheKnown = parsedTotal.CacheKnown && _priorCacheKnown;
                            var deltaCached = cacheKnown
                                ? Math.Clamp(parsedTotal.Cached - _priorCached, 0, deltaInput)
                                : 0;
                            var deltaReasoning = Math.Max(0, parsedTotal.Reasoning - _priorReasoning);
                            var requestCountKnown = parsedLast is { } lp
                                && lp.Input == deltaInput
                                && lp.Output == deltaOutput;
                            var legacyModel = ResolveModelForTurn(null, _turn);
                            var legacyEntry = new UsageEntry(
                                "legacy:" + _thread + ":" + fingerprint,
                                _thread,
                                _turn,
                                at,
                                legacyModel,
                                deltaInput,
                                deltaCached,
                                0,
                                deltaOutput,
                                deltaReasoning,
                                Legacy: true,
                                CacheKnown: cacheKnown,
                                UsageSource: "codex-legacy",
                                RequestCountKnown: requestCountKnown);
                            Legacy.Add(legacyEntry);
                            AllEntries.Add(legacyEntry);
                        }
                    }
                    _priorInput = parsedTotal.Input;
                    _priorCached = parsedTotal.Cached;
                    _priorOutput = parsedTotal.Output;
                    _priorReasoning = parsedTotal.Reasoning;
                    _priorCacheKnown = parsedTotal.CacheKnown;
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { Errors++; }
        }

        private string ResolveModelForTurn(string? explicitModel, string? turnId)
        {
            if (!string.IsNullOrWhiteSpace(explicitModel)) return explicitModel;
            if (!string.IsNullOrWhiteSpace(turnId))
            {
                if (_turnModels.TryGetValue(turnId, out var mapped)) return mapped;
                // Do not borrow a different turn's model when turn_id is known to belong to another turn.
                if (!_hasUnboundTurnContext && _turnModels.Count > 0 && !string.Equals(turnId, _turn, StringComparison.Ordinal))
                {
                    return "unknown";
                }
            }
            return _model;
        }

        private static string UsageFingerprint(long input, long cached, long output) =>
            string.Create(CultureInfo.InvariantCulture, $"{input}:{cached}:{output}");

        internal readonly record struct ParsedUsage(long Input, long Cached, bool CacheKnown, long CacheWrite, long Output, long Reasoning);

        internal static bool TryParseUsageObject(JsonElement usage, out ParsedUsage parsed)
        {
            parsed = default;
            if (usage.ValueKind != JsonValueKind.Object) return false;

            // Support standard Codex/Responses fields and OpenAI Chat Completions fallback names.
            var hasInput = TryReadOptionalStrict(usage, "input_tokens", out var inputOpt)
                && (inputOpt is not null || (TryReadOptionalStrict(usage, "prompt_tokens", out inputOpt)));
            var hasOutput = TryReadOptionalStrict(usage, "output_tokens", out var outputOpt)
                && (outputOpt is not null || (TryReadOptionalStrict(usage, "completion_tokens", out outputOpt)));
            if (!hasInput || !hasOutput || inputOpt is not long rawInput || outputOpt is not long output)
            {
                return false;
            }

            var cacheUnknownFlag = usage.TryGetProperty("cache_unknown", out var unkEl) && unkEl.ValueKind == JsonValueKind.True;
            if (!TryReadOptionalStrict(usage, "cached_input_tokens", out var cachedOpt)) return false;
            if (!TryReadOptionalStrict(usage, "cache_write_input_tokens", out var cacheWriteOpt)) return false;
            if (!TryReadOptionalStrict(usage, "reasoning_output_tokens", out var reasoningOpt)) return false;

            var anthropicStyle = false;
            long? anthropicRead = null;
            long? anthropicCreate = null;
            if (cachedOpt is null)
            {
                if (TryReadNestedOptionalStrict(usage, "input_tokens_details", "cached_tokens", out var nestedCached)
                    && nestedCached is long nc)
                {
                    cachedOpt = nc;
                }
                else if (TryReadNestedOptionalStrict(usage, "prompt_tokens_details", "cached_tokens", out var promptCached)
                    && promptCached is long pc)
                {
                    cachedOpt = pc;
                }
                else if (usage.TryGetProperty("cache_read_input_tokens", out _) || usage.TryGetProperty("cache_creation_input_tokens", out _))
                {
                    if (!TryReadOptionalStrict(usage, "cache_read_input_tokens", out anthropicRead)) return false;
                    if (!TryReadOptionalStrict(usage, "cache_creation_input_tokens", out anthropicCreate)) return false;
                    anthropicStyle = true;
                }
            }

            long fullInput;
            long cached;
            bool cacheKnown;
            long cacheWrite;
            if (anthropicStyle)
            {
                var read = anthropicRead ?? 0;
                var create = anthropicCreate ?? 0;
                fullInput = rawInput + read + create;
                cached = read;
                cacheWrite = create;
                cacheKnown = !cacheUnknownFlag && anthropicRead is not null;
            }
            else
            {
                fullInput = rawInput;
                cacheWrite = cacheWriteOpt ?? 0;
                cacheKnown = !cacheUnknownFlag && cachedOpt is not null;
                cached = cacheKnown ? cachedOpt!.Value : 0;
                if (cacheKnown && cached > fullInput) return false;
            }

            var reasoning = reasoningOpt ?? 0;
            parsed = new ParsedUsage(fullInput, cached, cacheKnown, cacheWrite, output, reasoning);
            return true;
        }

        private static bool TryReadOptionalStrict(JsonElement obj, string property, out long? value)
        {
            value = null;
            if (!obj.TryGetProperty(property, out var el) || el.ValueKind == JsonValueKind.Null)
            {
                return true;
            }
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var parsed) && parsed >= 0)
            {
                value = parsed;
                return true;
            }
            return false;
        }

        private static bool TryReadNestedOptionalStrict(JsonElement obj, string parent, string child, out long? value)
        {
            value = null;
            if (!obj.TryGetProperty(parent, out var p) || p.ValueKind != JsonValueKind.Object)
            {
                return true;
            }
            return TryReadOptionalStrict(p, child, out value);
        }
    }
}
