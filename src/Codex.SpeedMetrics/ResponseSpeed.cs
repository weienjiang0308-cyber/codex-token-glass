using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Codex.SpeedMetrics;

public static class ResponseTimingRules
{
    public const long MaxValidResponseMs = 3_600_000;
    public const long MinPeakDurationMs = 500;
    public const long BufferedBurstMinTokens = 32;
    public const long BufferedBurstMaxDurationMs = 120;
    public const double BufferedBurstMaxTps = 3000d;
    public const long GatewayBurstMinTotalMs = 1500;
    public const double GatewayBurstMaxDecodeShare = 0.04d;
    public const long GatewayBurstHardShortMs = 80;

    public static bool IsBufferedBurst(long outputTokens, long elapsedMs) =>
        elapsedMs <= 0
        || (outputTokens >= BufferedBurstMinTokens
            && (elapsedMs < BufferedBurstMaxDurationMs || 1000d * outputTokens / elapsedMs > BufferedBurstMaxTps));

    public static bool IsGatewayBufferedBurst(long outputTokens, long decodeMs, long totalMs)
    {
        if (decodeMs <= 0) return true;
        if (outputTokens < BufferedBurstMinTokens) return false;
        if (decodeMs < GatewayBurstHardShortMs) return true;
        if (totalMs >= GatewayBurstMinTotalMs && decodeMs < totalMs * GatewayBurstMaxDecodeShare) return true;
        return false;
    }

    public static bool IsGatewayBufferedBurst(long outputTokens, long decodeMs, long totalMs, long? flowMs)
    {
        if (IsGatewayBufferedBurst(outputTokens, decodeMs, totalMs)) return true;
        if (flowMs is long flow && flow > 0 && outputTokens >= BufferedBurstMinTokens)
        {
            if (flow < GatewayBurstHardShortMs) return true;
            if (1000d * outputTokens / flow > BufferedBurstMaxTps) return true;
        }
        return false;
    }
}

public sealed record ModelSegmentSummary(
    int Index,
    string Model,
    string? ServedModel,
    string? Route,
    long InputTokens,
    long CachedInputTokens,
    long CacheKnownInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    int CacheKnownResponses,
    int CacheUnknownResponses,
    int CompletedResponses,
    int TimedResponses,
    long TimedOutputTokens,
    long TimedMilliseconds,
    double? LastResponseTps,
    double? PeakResponseTps,
    int GatewayMatchedResponses,
    int HiddenReasoningResponses,
    int BufferedBurstResponses,
    int SourceConflictResponses)
{
    public long TotalTokens => InputTokens + OutputTokens;
    public string Key => $"seg:{Index}:{Route ?? "unknown"}:{ServedModel ?? Model}";
    public double? CacheHitPercent => CacheKnownInputTokens > 0 ? 100d * CachedInputTokens / CacheKnownInputTokens : null;
    public double? AverageTps => TimedMilliseconds > 0 ? 1000d * TimedOutputTokens / TimedMilliseconds : null;
}

/// <summary>
/// Independent metrics. Average uses official per-response output (reasoning and tool arguments
/// included), timed from the first observed model-output item through final response usage.
/// Live is ONLY a two-second, o200k_base estimate of newly received visible text.
/// Neither metric is a claim about the server's internal decoder clock.
/// </summary>
public sealed record SpeedReading(
    string ThreadId, string? TurnId, bool Streaming,
    double? AverageTps, long TimedOutputTokens, long TimedMilliseconds,
    int TimedResponses, int CompletedResponses,
    double? LastResponseTps, long LastResponseOutputTokens, long LastResponseMilliseconds,
    double? PeakResponseTps, double? LiveEstimateTps, bool LiveConnected,
    long? LastOutputAtMs, bool PartialHistory,
    string? LiveError = null, long LiveFrames = 0, long LiveTextUpdates = 0, bool ToolRunning = false, string? Activity = null,
    int GatewayMatchedResponses = 0, int HiddenReasoningResponses = 0, int BufferedBurstResponses = 0,
    double? AverageTtftMs = null, double? LastTtftMs = null, double? EndToEndAverageTps = null, string TimingSource = "rollout",
    string? SegmentKey = null, int SegmentIndex = 0, string? CurrentModel = null, string? CurrentRoute = null,
    bool SegmentPending = false, bool HasSegmentUsage = false,
    long SegmentTotalTokens = 0, long SegmentInputTokens = 0, long SegmentCachedInputTokens = 0,
    long SegmentCacheKnownInputTokens = 0, long SegmentOutputTokens = 0, long SegmentReasoningOutputTokens = 0,
    int SegmentCacheKnownResponses = 0, int SegmentCacheUnknownResponses = 0,
    long SessionTotalTokens = 0, int SourceConflictCount = 0)
{
    public string AverageCoverage => $"{TimedResponses}/{CompletedResponses}";
    public double? SegmentCacheHitPercent => SegmentCacheKnownInputTokens > 0
        ? 100d * SegmentCachedInputTokens / SegmentCacheKnownInputTokens
        : null;
}

public sealed class ResponseSpeedAnalyzer
{
    private sealed class SegmentAccumulator
    {
        public int Index { get; }
        public string Model { get; set; }
        public string? ServedModel { get; set; }
        public string? Route { get; set; }
        public long InputTokens { get; set; }
        public long CachedInputTokens { get; set; }
        public long CacheKnownInputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long ReasoningOutputTokens { get; set; }
        public int CacheKnownResponses { get; set; }
        public int CacheUnknownResponses { get; set; }
        public int CompletedResponses { get; set; }
        public int TimedResponses { get; set; }
        public long TimedTokens { get; set; }
        public long TimedMilliseconds { get; set; }
        public long LastValidTokens { get; set; }
        public long LastValidMilliseconds { get; set; }
        public double? PeakTps { get; set; }
        public int GatewayMatchedResponses { get; set; }
        public int HiddenReasoningResponses { get; set; }
        public int BufferedBurstResponses { get; set; }
        public long EndToEndTokens { get; set; }
        public long EndToEndMilliseconds { get; set; }
        public long TtftMilliseconds { get; set; }
        public int TtftCount { get; set; }
        public double? LastTtftMs { get; set; }
        public int SourceConflictResponses { get; set; }

        public SegmentAccumulator(int index, string model, string? servedModel = null, string? route = null)
        {
            Index = index;
            Model = model;
            ServedModel = servedModel;
            Route = route;
        }

        public long TotalTokens => InputTokens + OutputTokens;
        public string Key => $"seg:{Index}:{Route ?? "unknown"}:{ServedModel ?? Model}";
        public double? LastValidTps => LastValidMilliseconds > 0 ? 1000d * LastValidTokens / LastValidMilliseconds : null;
        public double? AverageTps => TimedMilliseconds > 0 ? 1000d * TimedTokens / TimedMilliseconds : null;
        public double? AverageTtftMs => TtftCount > 0 ? (double)TtftMilliseconds / TtftCount : null;
        public double? EndToEndTps => EndToEndMilliseconds > 0 ? 1000d * EndToEndTokens / EndToEndMilliseconds : null;
        public string TimingSource => GatewayMatchedResponses <= 0
            ? "rollout"
            : GatewayMatchedResponses >= CompletedResponses ? "gateway" : "hybrid";

        public ModelSegmentSummary ToSummary() => new(
            Index, Model, ServedModel, Route,
            InputTokens, CachedInputTokens, CacheKnownInputTokens, OutputTokens, ReasoningOutputTokens,
            CacheKnownResponses, CacheUnknownResponses,
            CompletedResponses, TimedResponses, TimedTokens, TimedMilliseconds,
            LastValidTps, PeakTps,
            GatewayMatchedResponses, HiddenReasoningResponses, BufferedBurstResponses, SourceConflictResponses);
    }

    private readonly record struct SeenResponseCounts(long Input, long Cached, bool CacheKnown, long Output, long Reasoning);
    private readonly record struct PendingLegacySnapshot(SegmentAccumulator Segment, long At, long Input, long Cached, bool CacheKnown, long Output, long Reasoning, bool WasTimed, long TimedMs);

    private readonly Dictionary<string, SeenResponseCounts> _responses = new(StringComparer.Ordinal);
    private readonly HashSet<string> _earlierTurns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _turnModels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SegmentAccumulator> _turnSegments = new(StringComparer.Ordinal);
    private readonly List<SegmentAccumulator> _segments = new();
    private readonly Queue<string> _unmatchedResponseIds = new();
    private readonly IGatewayTimingLookup? _gatewayLookup;
    private SegmentAccumulator? _currentSegment;
    private string? _currentContextModel;
    private bool _hasUnboundTurnContext;
    private long? _firstOutput;
    private bool _sawReasoningItem;
    private long _responseBoundary;
    private long _timedTokens, _timedMs;
    private int _timedResponses, _responsesInTurn;
    private bool _sawModernUsage;
    private readonly bool _preferModern;
    private long _allOutputTokens;
    private long _priorLegacyInput, _priorLegacyCached, _priorLegacyOutput, _priorLegacyReasoning;
    private bool _priorLegacyCacheKnown = true;
    private long _modernCoveredInput, _modernCoveredOutput;
    private PendingLegacySnapshot? _lastLegacySample;
    public long SessionInputTokens { get; private set; }
    public long SessionCachedInputTokens { get; private set; }
    public long SessionCacheKnownInputTokens { get; private set; }
    public long SessionOutputTokens { get; private set; }
    public long SessionReasoningOutputTokens { get; private set; }
    public long SessionTotalTokens => SessionInputTokens + SessionOutputTokens;
    public int SourceConflictCount { get; private set; }
    public IReadOnlyList<ModelSegmentSummary> Segments => _segments.Select(s => s.ToSummary()).ToArray();
    public ModelSegmentSummary? CurrentSegment => _currentSegment?.ToSummary();
    public bool HasMultipleSegments => _segments.Count > 1;
    public bool IsSegmentPending => _currentSegment is { CompletedResponses: 0 } && (_segments.Count > 1 || _currentSegment.Model != "unknown");
    public long SessionTimedTokens { get; private set; }
    public long SessionTimedMilliseconds { get; private set; }
    public int SessionTimedResponses { get; private set; }
    public int SessionCompletedResponses { get; private set; }
    public int SessionGatewayMatchedResponses { get; private set; }
    public int SessionHiddenReasoningResponses { get; private set; }
    public int SessionBufferedBurstResponses { get; private set; }
    public long SessionEndToEndTokens { get; private set; }
    public long SessionEndToEndMilliseconds { get; private set; }
    public long SessionTtftMilliseconds { get; private set; }
    public int SessionTtftCount { get; private set; }
    public double? LastTtftMs { get; private set; }
    public double? AverageTtftMs => SessionTtftCount > 0 ? (double)SessionTtftMilliseconds / SessionTtftCount : null;
    public double? SessionEndToEndTps => SessionEndToEndMilliseconds > 0 ? 1000d * SessionEndToEndTokens / SessionEndToEndMilliseconds : null;
    public double? SessionPeakTps { get; private set; }
    public long LastValidTokens { get; private set; }
    public long LastValidMilliseconds { get; private set; }
    public double? LastValidTps => LastValidMilliseconds > 0 ? 1000d * LastValidTokens / LastValidMilliseconds : null;
    public string TimingSource => SessionGatewayMatchedResponses <= 0
        ? "rollout"
        : SessionGatewayMatchedResponses >= SessionCompletedResponses ? "gateway" : "hybrid";
    private string? _lastLegacyFingerprint;
    public string? TurnId { get; private set; }
    public bool Streaming { get; private set; }
    public bool PartialHistory { get; set; }
    public long AllOutputTokens => _allOutputTokens;
    public ResponseSpeedAnalyzer(bool preferModern = true, IGatewayTimingLookup? gatewayLookup = null)
    {
        _preferModern = preferModern;
        _gatewayLookup = gatewayLookup;
    }
    public long LastTokens { get; private set; }
    public long LastMilliseconds { get; private set; }
    public double? PeakTps { get; private set; }
    public double? AverageTps => _timedMs > 0 ? 1000d * _timedTokens / _timedMs : null;
    public long TimedTokens => _timedTokens;
    public long TimedMilliseconds => _timedMs;
    public int TimedResponses => _timedResponses;
    public int CompletedResponses => _responsesInTurn;
    public double? LastTps => LastMilliseconds > 0 ? 1000d * LastTokens / LastMilliseconds : null;

    internal bool HasNewlyMatchedGatewayResponse(IGatewayTimingLookup lookup)
    {
        foreach (var responseId in _unmatchedResponseIds)
        {
            if (lookup.TryGet(responseId, out _)) return true;
        }
        return false;
    }

    private void BeginTurn(string? turnId, long at, bool completeStart)
    {
        if (turnId is not null && turnId == TurnId) return;
        if (TurnId is not null) _earlierTurns.Add(TurnId);
        if (turnId is not null) _earlierTurns.Remove(turnId);
        TurnId = turnId;
        Streaming = true;
        _firstOutput = null;
        _sawReasoningItem = false;
        _responseBoundary = at;
        _timedTokens = _timedMs = 0;
        _allOutputTokens = 0;
        _timedResponses = _responsesInTurn = 0;
        LastTokens = LastMilliseconds = 0;
        PeakTps = null;
        PartialHistory = !completeStart;
    }

    public void ObserveLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            ObserveRecord(doc.RootElement);
        }
        catch (JsonException) { /* A malformed record cannot invent a rate. */ }
    }

    public void ObserveRecord(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty("payload", out var p)
            || p.ValueKind != JsonValueKind.Object) return;
        var at = Timestamp(record);
        var recordType = Text(record, "type");
        var eventType = Text(p, "type");
        if (recordType == "turn_context")
        {
            ObserveTurnContext(p);
            return;
        }
        if (recordType == "event_msg" && eventType == "task_started")
        {
            BeginTurn(Text(p, "turn_id"), at, true);
            return;
        }
        if (recordType == "event_msg" && eventType == "turn_aborted")
        {
            Streaming = false;
            _firstOutput = null;
            _sawReasoningItem = false;
            return;
        }
        if (recordType == "event_msg" && eventType == "task_complete")
        {
            Streaming = false;
            return;
        }
        if (recordType == "event_msg" && eventType == "item_completed" &&
            p.TryGetProperty("item", out var item))
        {
            var kind = Text(item, "type");
            if (kind is not ("Reasoning" or "AgentMessage" or "reasoning" or "agentMessage")) return;
            var itemTurn = Text(p, "turn_id");
            if (itemTurn is not null && TurnId is not null && itemTurn != TurnId) return;
            var start = Number(p, "started_at_ms");
            var stop = Number(p, "completed_at_ms");
            // Do not infer a first token from a completion-only tool call or a delayed old item.
            if (start > 0 && stop >= start && start >= _responseBoundary)
            {
                _firstOutput = _firstOutput is null ? start : Math.Min(_firstOutput.Value, start);
                if (kind is "Reasoning" or "reasoning") _sawReasoningItem = true;
            }
            return;
        }
        if (recordType == "token_usage_record" && p.TryGetProperty("usage", out var usage))
        {
            var rawResponseId = Text(p, "response_id");
            var parsedOutput = NonnegativeNumber(usage, "output_tokens") ?? NonnegativeNumber(usage, "completion_tokens");
            var parsedInput = TryReadInputAndCache(usage, out var fullInput, out var cachedInput, out var cacheKnown, out var cacheValid);
            if (!cacheValid) return;
            var parsedReasoning = NonnegativeNumber(usage, "reasoning_output_tokens") ?? 0;
            var inputVal = parsedInput ? fullInput : 0;
            var outputVal = parsedOutput ?? 0;
            var id = string.IsNullOrEmpty(rawResponseId) ? "record:" + at + ":" + usage.GetRawText() : rawResponseId;
            var counts = new SeenResponseCounts(inputVal, cachedInput, cacheKnown, outputVal, parsedReasoning);
            if (_responses.TryGetValue(id, out var prior))
            {
                if (prior != counts)
                {
                    SourceConflictCount++;
                    if (_currentSegment is not null) _currentSegment.SourceConflictResponses++;
                }
                return;
            }
            _responses[id] = counts;
            _sawModernUsage = true;

            // If a legacy token_count line for the exact same response arrived immediately before
            // this token_usage_record (mirror order), roll back that provisional legacy entry so
            // the single response is never counted twice.
            if (_lastLegacySample is { } legacyMirror
                && legacyMirror.At == at
                && legacyMirror.Input == inputVal
                && legacyMirror.Output == outputVal)
            {
                RollbackLegacySample(legacyMirror);
                _lastLegacySample = null;
            }

            var turn = Text(p, "turn_id");
            var explicitModel = Text(p, "model");
            var resolvedModel = ResolveModelForTurn(explicitModel, turn);
            var gateway = ResolveGatewayForResponse(rawResponseId, parsedInput ? inputVal : (long?)null, cachedInput, cacheKnown, parsedOutput, out var gatewayAmbiguous, out var gatewayConflict);
            if (gatewayAmbiguous || gatewayConflict)
            {
                SourceConflictCount++;
            }

            // Advance cumulative legacy coverage watermark so mirrored token_count records are ignored
            // while genuine legacy-only spans before or after modern records remain intact.
            if (p.TryGetProperty("thread_token_usage", out var threadTotal)
                && threadTotal.ValueKind == JsonValueKind.Object
                && TryReadInputAndCache(threadTotal, out var ttIn, out var ttCached, out var ttCacheKnown, out var ttValid)
                && ttValid
                && NonnegativeNumber(threadTotal, "output_tokens") is long ttOut)
            {
                _priorLegacyInput = ttIn;
                _priorLegacyCached = ttCached;
                _priorLegacyCacheKnown = ttCacheKnown;
                _priorLegacyOutput = ttOut;
                _priorLegacyReasoning = NonnegativeNumber(threadTotal, "reasoning_output_tokens") ?? 0;
                _modernCoveredInput = Math.Max(_modernCoveredInput, ttIn);
                _modernCoveredOutput = Math.Max(_modernCoveredOutput, ttOut);
            }
            else
            {
                _priorLegacyInput += inputVal;
                if (cacheKnown) _priorLegacyCached += cachedInput;
                else _priorLegacyCacheKnown = false;
                _priorLegacyOutput += outputVal;
                _priorLegacyReasoning += parsedReasoning;
                _modernCoveredInput = Math.Max(_modernCoveredInput, _priorLegacyInput);
                _modernCoveredOutput = Math.Max(_modernCoveredOutput, _priorLegacyOutput);
            }

            // A late record of an earlier turn belongs to its original segment and session total.
            // It must not rewind the current turn or pollute the active model segment.
            var belongsToEarlierSegment = turn is not null
                && _currentSegment is not null
                && _turnSegments.TryGetValue(turn, out var boundSegment)
                && boundSegment.Index < _currentSegment.Index;
            if ((turn is not null && turn != TurnId && _earlierTurns.Contains(turn)) || belongsToEarlierSegment)
            {
                var targetOldSegment = ResolveLateTurnSegment(turn, resolvedModel, gateway);
                if (parsedOutput is long lateOut)
                {
                    SessionCompletedResponses++;
                    targetOldSegment.CompletedResponses++;
                    RecordUsageTokens(targetOldSegment, inputVal, cachedInput, cacheKnown, lateOut, parsedReasoning, gatewayAmbiguous || gatewayConflict);
                }
                return;
            }

            if (turn != TurnId) BeginTurn(turn, _responseBoundary, false);
            var activeSegment = EnsureActiveSegmentForResponse(resolvedModel, gateway, turn);
            if (parsedOutput is not null)
            {
                RecordUsageTokens(activeSegment, inputVal, cachedInput, cacheKnown, outputVal, parsedReasoning, gatewayAmbiguous || gatewayConflict);
            }
            CompleteResponse(activeSegment, parsedOutput, NonnegativeNumber(usage, "reasoning_output_tokens"), rawResponseId, gateway, at);
            return;
        }
        // Legacy token_count: skip redundant mirrors already covered by modern records,
        // but keep genuine independent legacy intervals in mixed-carrier logs.
        if (recordType == "event_msg" && eventType == "token_count" &&
            p.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            var hasTotal = info.TryGetProperty("total_token_usage", out var total) && total.ValueKind == JsonValueKind.Object;
            var hasLast = info.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object;
            if (!hasTotal && !hasLast) return;

            var fingerprint = (hasTotal ? total.GetRawText() : "") + "|" + (hasLast ? last.GetRawText() : "");
            if (fingerprint == _lastLegacyFingerprint) return;
            _lastLegacyFingerprint = fingerprint;

            if (hasTotal)
            {
                var hasTotalIn = TryReadInputAndCache(total, out var totIn, out var totCached, out var totCacheKnown, out var totValid);
                var totOut = NonnegativeNumber(total, "output_tokens");
                if (!totValid || totOut is not long totalOutput) return;
                var totalInput = hasTotalIn ? totIn : 0;
                var totalReasoning = NonnegativeNumber(total, "reasoning_output_tokens") ?? 0;

                if (_sawModernUsage && totalInput <= _modernCoveredInput && totalOutput <= _modernCoveredOutput)
                {
                    return;
                }

                if (totalInput >= _priorLegacyInput && totalOutput >= _priorLegacyOutput)
                {
                    var deltaInput = totalInput - _priorLegacyInput;
                    var deltaOutput = totalOutput - _priorLegacyOutput;
                    if (deltaInput > 0 || deltaOutput > 0)
                    {
                        var cacheKnown = hasTotalIn && totCacheKnown && _priorLegacyCacheKnown;
                        var deltaCached = cacheKnown ? Math.Clamp(totCached - _priorLegacyCached, 0, deltaInput) : 0;
                        var deltaReasoning = Math.Max(0, totalReasoning - _priorLegacyReasoning);
                        var seg = EnsureActiveSegmentForResponse(ResolveModelForTurn(null, TurnId), null, TurnId);
                        RecordUsageTokens(seg, deltaInput, deltaCached, cacheKnown, deltaOutput, deltaReasoning, false);
                        var beforeTimed = seg.TimedResponses;
                        var beforeMs = seg.TimedMilliseconds;
                        var lastOut = hasLast ? NonnegativeNumber(last, "output_tokens") : null;
                        var lastReas = hasLast ? NonnegativeNumber(last, "reasoning_output_tokens") : null;
                        CompleteResponse(seg, lastOut ?? deltaOutput, lastReas ?? deltaReasoning, null, null, at);
                        var wasTimed = seg.TimedResponses > beforeTimed;
                        _lastLegacySample = new PendingLegacySnapshot(seg, at, deltaInput, deltaCached, cacheKnown, deltaOutput, deltaReasoning, wasTimed, wasTimed ? seg.TimedMilliseconds - beforeMs : 0);
                    }
                }
                _priorLegacyInput = totalInput;
                _priorLegacyCached = hasTotalIn ? totCached : 0;
                _priorLegacyCacheKnown = hasTotalIn && totCacheKnown;
                _priorLegacyOutput = totalOutput;
                _priorLegacyReasoning = totalReasoning;
                return;
            }

            if (hasLast && !_preferModern && !_sawModernUsage)
            {
                var hasLastIn = TryReadInputAndCache(last, out var lIn, out var lCached, out var lCacheKnown, out var lValid);
                var lastOut = NonnegativeNumber(last, "output_tokens");
                if (!lValid || lastOut is not long outVal) return;
                var lastReas = NonnegativeNumber(last, "reasoning_output_tokens");
                var seg = EnsureActiveSegmentForResponse(ResolveModelForTurn(null, TurnId), null, TurnId);
                RecordUsageTokens(seg, hasLastIn ? lIn : 0, hasLastIn && lCacheKnown ? lCached : 0, hasLastIn && lCacheKnown, outVal, lastReas ?? 0, false);
                CompleteResponse(seg, outVal, lastReas, null, null, at);
            }
        }
    }

    private void ObserveTurnContext(JsonElement payload)
    {
        var model = Text(payload, "model");
        var turnId = Text(payload, "turn_id");
        var normalizedModel = string.IsNullOrWhiteSpace(model) ? "unknown" : model.Trim();
        _currentContextModel = normalizedModel;
        if (!string.IsNullOrWhiteSpace(turnId))
        {
            _turnModels[turnId] = normalizedModel;
            _hasUnboundTurnContext = false;
        }
        else
        {
            _hasUnboundTurnContext = true;
        }

        if (_currentSegment is null)
        {
            _currentSegment = new SegmentAccumulator(1, normalizedModel);
            _segments.Add(_currentSegment);
        }
        else if (!string.Equals(_currentSegment.Model, normalizedModel, StringComparison.OrdinalIgnoreCase))
        {
            if (_currentSegment.CompletedResponses == 0)
            {
                _currentSegment.Model = normalizedModel;
                _currentSegment.ServedModel = null;
                _currentSegment.Route = null;
            }
            else
            {
                _currentSegment = new SegmentAccumulator(_segments.Count + 1, normalizedModel);
                _segments.Add(_currentSegment);
            }
        }

        if (!string.IsNullOrWhiteSpace(turnId))
        {
            _turnSegments[turnId] = _currentSegment;
        }
    }

    private string ResolveModelForTurn(string? explicitModel, string? turnId)
    {
        if (!string.IsNullOrWhiteSpace(explicitModel)) return explicitModel.Trim();
        if (!string.IsNullOrWhiteSpace(turnId))
        {
            if (_turnModels.TryGetValue(turnId, out var mapped)) return mapped;
            if (!_hasUnboundTurnContext && _turnModels.Count > 0 && !string.Equals(turnId, TurnId, StringComparison.Ordinal))
            {
                return "unknown";
            }
        }
        return _currentContextModel ?? "unknown";
    }

    private SegmentAccumulator ResolveLateTurnSegment(string turnId, string resolvedModel, GatewayResponseTiming? gateway)
    {
        if (_turnSegments.TryGetValue(turnId, out var existing)) return existing;
        var match = _segments.FirstOrDefault(s =>
            string.Equals(s.Model, resolvedModel, StringComparison.OrdinalIgnoreCase)
            && (gateway?.EffectiveRoute is null || s.Route is null || string.Equals(s.Route, gateway.Value.EffectiveRoute, StringComparison.OrdinalIgnoreCase)));
        if (match is not null)
        {
            _turnSegments[turnId] = match;
            return match;
        }
        var fallback = new SegmentAccumulator(_segments.Count + 1, resolvedModel, gateway?.EffectiveServedModel, gateway?.EffectiveRoute);
        _segments.Add(fallback);
        _turnSegments[turnId] = fallback;
        return fallback;
    }

    private SegmentAccumulator EnsureActiveSegmentForResponse(string resolvedModel, GatewayResponseTiming? gateway, string? turnId)
    {
        var gwRoute = gateway?.EffectiveRoute;
        var gwServed = gateway?.EffectiveServedModel;
        if (_currentSegment is null)
        {
            _currentSegment = new SegmentAccumulator(1, resolvedModel, gwServed, gwRoute);
            _segments.Add(_currentSegment);
        }
        else
        {
            var modelChanged = !string.Equals(resolvedModel, "unknown", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(_currentSegment.Model, "unknown", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(_currentSegment.Model, resolvedModel, StringComparison.OrdinalIgnoreCase);
            var servedChanged = !string.IsNullOrWhiteSpace(_currentSegment.ServedModel)
                && !string.IsNullOrWhiteSpace(gwServed)
                && !string.Equals(_currentSegment.ServedModel, gwServed, StringComparison.OrdinalIgnoreCase);
            var routeChanged = !string.IsNullOrWhiteSpace(_currentSegment.Route)
                && !string.IsNullOrWhiteSpace(gwRoute)
                && !string.Equals(_currentSegment.Route, gwRoute, StringComparison.OrdinalIgnoreCase);

            if (modelChanged || servedChanged || routeChanged)
            {
                if (_currentSegment.CompletedResponses == 0)
                {
                    _currentSegment.Model = resolvedModel;
                    _currentSegment.ServedModel = gwServed;
                    _currentSegment.Route = gwRoute;
                }
                else
                {
                    _currentSegment = new SegmentAccumulator(_segments.Count + 1, resolvedModel, gwServed, gwRoute);
                    _segments.Add(_currentSegment);
                }
            }
            else
            {
                if (string.Equals(_currentSegment.Model, "unknown", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(resolvedModel, "unknown", StringComparison.OrdinalIgnoreCase))
                {
                    _currentSegment.Model = resolvedModel;
                }
                if (string.IsNullOrWhiteSpace(_currentSegment.ServedModel) && !string.IsNullOrWhiteSpace(gwServed))
                {
                    _currentSegment.ServedModel = gwServed;
                }
                if (string.IsNullOrWhiteSpace(_currentSegment.Route) && !string.IsNullOrWhiteSpace(gwRoute))
                {
                    _currentSegment.Route = gwRoute;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(turnId))
        {
            _turnSegments[turnId] = _currentSegment;
        }
        return _currentSegment;
    }

    private GatewayResponseTiming? ResolveGatewayForResponse(
        string? responseId,
        long? codexInput,
        long codexCached,
        bool codexCacheKnown,
        long? codexOutput,
        out bool ambiguous,
        out bool conflict)
    {
        ambiguous = false;
        conflict = false;
        if (_gatewayLookup is null || string.IsNullOrEmpty(responseId)) return null;

        if (_gatewayLookup.TryGet(responseId, out var gw))
        {
            if (gw.Status != 0 && gw.Status != 200) return null;
            if ((codexInput is long ci && gw.FullInputTokens is long gi && ci != gi)
                || (codexOutput is long co && gw.OutputTokens is long go && co != go)
                || (codexCacheKnown && !gw.CacheUnknown && gw.CacheReadTokens is long gc && gc != codexCached))
            {
                conflict = true;
            }
            return gw;
        }

        if (_gatewayLookup.TryGetInspect(responseId, out var inspected) && inspected.IsAmbiguous)
        {
            ambiguous = true;
        }
        return null;
    }

    private void RecordUsageTokens(SegmentAccumulator segment, long input, long cached, bool cacheKnown, long output, long reasoning, bool conflict)
    {
        SessionInputTokens += input;
        SessionOutputTokens += output;
        SessionReasoningOutputTokens += reasoning;
        segment.InputTokens += input;
        segment.OutputTokens += output;
        segment.ReasoningOutputTokens += reasoning;
        if (cacheKnown)
        {
            SessionCachedInputTokens += cached;
            SessionCacheKnownInputTokens += input;
            segment.CachedInputTokens += cached;
            segment.CacheKnownInputTokens += input;
            segment.CacheKnownResponses++;
        }
        else
        {
            segment.CacheUnknownResponses++;
        }
        if (conflict)
        {
            segment.SourceConflictResponses++;
        }
    }

    private void RollbackLegacySample(PendingLegacySnapshot sample)
    {
        var seg = sample.Segment;
        SessionInputTokens = Math.Max(0, SessionInputTokens - sample.Input);
        SessionOutputTokens = Math.Max(0, SessionOutputTokens - sample.Output);
        SessionReasoningOutputTokens = Math.Max(0, SessionReasoningOutputTokens - sample.Reasoning);
        seg.InputTokens = Math.Max(0, seg.InputTokens - sample.Input);
        seg.OutputTokens = Math.Max(0, seg.OutputTokens - sample.Output);
        seg.ReasoningOutputTokens = Math.Max(0, seg.ReasoningOutputTokens - sample.Reasoning);
        if (sample.CacheKnown)
        {
            SessionCachedInputTokens = Math.Max(0, SessionCachedInputTokens - sample.Cached);
            SessionCacheKnownInputTokens = Math.Max(0, SessionCacheKnownInputTokens - sample.Input);
            seg.CachedInputTokens = Math.Max(0, seg.CachedInputTokens - sample.Cached);
            seg.CacheKnownInputTokens = Math.Max(0, seg.CacheKnownInputTokens - sample.Input);
            seg.CacheKnownResponses = Math.Max(0, seg.CacheKnownResponses - 1);
        }
        else
        {
            seg.CacheUnknownResponses = Math.Max(0, seg.CacheUnknownResponses - 1);
        }
        SessionCompletedResponses = Math.Max(0, SessionCompletedResponses - 1);
        seg.CompletedResponses = Math.Max(0, seg.CompletedResponses - 1);
        _responsesInTurn = Math.Max(0, _responsesInTurn - 1);
        _allOutputTokens = Math.Max(0, _allOutputTokens - sample.Output);
        if (sample.WasTimed)
        {
            SessionTimedTokens = Math.Max(0, SessionTimedTokens - sample.Output);
            SessionTimedMilliseconds = Math.Max(0, SessionTimedMilliseconds - sample.TimedMs);
            SessionTimedResponses = Math.Max(0, SessionTimedResponses - 1);
            seg.TimedTokens = Math.Max(0, seg.TimedTokens - sample.Output);
            seg.TimedMilliseconds = Math.Max(0, seg.TimedMilliseconds - sample.TimedMs);
            seg.TimedResponses = Math.Max(0, seg.TimedResponses - 1);
            _timedTokens = Math.Max(0, _timedTokens - sample.Output);
            _timedMs = Math.Max(0, _timedMs - sample.TimedMs);
            _timedResponses = Math.Max(0, _timedResponses - 1);
        }
    }

    private static bool TryReadInputAndCache(JsonElement usage, out long fullInput, out long cached, out bool cacheKnown, out bool valid)
    {
        fullInput = 0;
        cached = 0;
        cacheKnown = false;
        valid = true;
        if (usage.ValueKind != JsonValueKind.Object) { valid = false; return false; }

        if (!TryOptionalNonnegative(usage, "input_tokens", out var inputOpt)) { valid = false; return false; }
        if (inputOpt is null && !TryOptionalNonnegative(usage, "prompt_tokens", out inputOpt)) { valid = false; return false; }
        if (inputOpt is not long rawInput) return false;

        var cacheUnknownFlag = usage.TryGetProperty("cache_unknown", out var unk) && unk.ValueKind == JsonValueKind.True;
        if (!TryOptionalNonnegative(usage, "cached_input_tokens", out var cachedOpt)) { valid = false; return false; }

        if (cachedOpt is null)
        {
            if (TryNestedOptionalNonnegative(usage, "input_tokens_details", "cached_tokens", out var ic) && ic is long icVal)
            {
                cachedOpt = icVal;
            }
            else if (TryNestedOptionalNonnegative(usage, "prompt_tokens_details", "cached_tokens", out var pc) && pc is long pcVal)
            {
                cachedOpt = pcVal;
            }
            else if (usage.TryGetProperty("cache_read_input_tokens", out _) || usage.TryGetProperty("cache_creation_input_tokens", out _))
            {
                if (!TryOptionalNonnegative(usage, "cache_read_input_tokens", out var ar)
                    || !TryOptionalNonnegative(usage, "cache_creation_input_tokens", out var ac))
                {
                    valid = false;
                    return false;
                }
                fullInput = rawInput + (ar ?? 0) + (ac ?? 0);
                cacheKnown = !cacheUnknownFlag && ar is not null;
                cached = cacheKnown ? ar!.Value : 0;
                return true;
            }
        }

        fullInput = rawInput;
        cacheKnown = !cacheUnknownFlag && cachedOpt is not null;
        cached = cacheKnown ? cachedOpt!.Value : 0;
        if (cacheKnown && cached > fullInput) { valid = false; return false; }
        return true;
    }

    private static bool TryOptionalNonnegative(JsonElement obj, string property, out long? value)
    {
        value = null;
        if (!obj.TryGetProperty(property, out var el) || el.ValueKind == JsonValueKind.Null) return true;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var parsed) && parsed >= 0)
        {
            value = parsed;
            return true;
        }
        return false;
    }

    private static bool TryNestedOptionalNonnegative(JsonElement obj, string parent, string child, out long? value)
    {
        value = null;
        if (!obj.TryGetProperty(parent, out var p) || p.ValueKind != JsonValueKind.Object) return true;
        return TryOptionalNonnegative(p, child, out value);
    }

    private void CompleteResponse(SegmentAccumulator segment, long? reportedTokens, long? reportedReasoningTokens, string? responseId, GatewayResponseTiming? gatewayOpt, long at)
    {
        if (reportedTokens is long outputTokens)
        {
            _responsesInTurn++;
            SessionCompletedResponses++;
            segment.CompletedResponses++;
            _allOutputTokens += outputTokens;
            LastTokens = outputTokens;
            LastMilliseconds = 0;

            var matchedGateway = false;
            var resolvedByGateway = false;
            if (gatewayOpt is GatewayResponseTiming gateway
                && (gateway.Status == 0 || gateway.Status == 200)
                && gateway.TotalMs > 0)
            {
                matchedGateway = true;
                SessionGatewayMatchedResponses++;
                segment.GatewayMatchedResponses++;
                SessionEndToEndTokens += outputTokens;
                SessionEndToEndMilliseconds += gateway.TotalMs;
                segment.EndToEndTokens += outputTokens;
                segment.EndToEndMilliseconds += gateway.TotalMs;

                var effectiveTtft = ResolveGatewayTtft(gateway);
                if (effectiveTtft > 0)
                {
                    LastTtftMs = effectiveTtft;
                    SessionTtftMilliseconds += effectiveTtft;
                    SessionTtftCount++;
                    segment.LastTtftMs = effectiveTtft;
                    segment.TtftMilliseconds += effectiveTtft;
                    segment.TtftCount++;
                }

                var effectiveReasoning = gateway.ReasoningTokens ?? reportedReasoningTokens ?? 0;
                if (effectiveReasoning > 0 && !(gateway.FirstReasoningMs is > 0) && !_sawReasoningItem)
                {
                    SessionHiddenReasoningResponses++;
                    segment.HiddenReasoningResponses++;
                    resolvedByGateway = true;
                }
                else if (effectiveTtft > 0 && gateway.TotalMs > effectiveTtft
                    && gateway.TotalMs - effectiveTtft <= ResponseTimingRules.MaxValidResponseMs)
                {
                    var decodeMs = gateway.TotalMs - effectiveTtft;
                    if (ResponseTimingRules.IsGatewayBufferedBurst(outputTokens, decodeMs, gateway.TotalMs, gateway.FlowMs))
                    {
                        SessionBufferedBurstResponses++;
                        segment.BufferedBurstResponses++;
                    }
                    else
                    {
                        RecordTimedSample(segment, outputTokens, decodeMs);
                    }
                    resolvedByGateway = true;
                }
            }

            if (!matchedGateway && _gatewayLookup is not null && !string.IsNullOrEmpty(responseId))
            {
                _unmatchedResponseIds.Enqueue(responseId);
                while (_unmatchedResponseIds.Count > 64) _unmatchedResponseIds.Dequeue();
            }

            if (!resolvedByGateway && _firstOutput is long start && at > start && at - start <= ResponseTimingRules.MaxValidResponseMs)
            {
                var elapsed = at - start;
                if (reportedReasoningTokens is > 0 && !_sawReasoningItem)
                {
                    SessionHiddenReasoningResponses++;
                    segment.HiddenReasoningResponses++;
                }
                else if (ResponseTimingRules.IsBufferedBurst(outputTokens, elapsed))
                {
                    SessionBufferedBurstResponses++;
                    segment.BufferedBurstResponses++;
                }
                else
                {
                    RecordTimedSample(segment, outputTokens, elapsed);
                }
            }
        }
        _firstOutput = null;
        _sawReasoningItem = false;
        _responseBoundary = Math.Max(_responseBoundary, at);
    }

    private static long ResolveGatewayTtft(GatewayResponseTiming gateway)
    {
        if (gateway.FirstReasoningMs is > 0 && gateway.TtftMs is > 0)
            return Math.Min(gateway.FirstReasoningMs.Value, gateway.TtftMs.Value);
        if (gateway.TtftMs is > 0) return gateway.TtftMs.Value;
        if (gateway.FirstReasoningMs is > 0) return gateway.FirstReasoningMs.Value;
        return 0;
    }

    private void RecordTimedSample(SegmentAccumulator segment, long outputTokens, long elapsedMs)
    {
        _timedTokens += outputTokens;
        _timedMs += elapsedMs;
        _timedResponses++;
        SessionTimedTokens += outputTokens;
        SessionTimedMilliseconds += elapsedMs;
        SessionTimedResponses++;
        segment.TimedTokens += outputTokens;
        segment.TimedMilliseconds += elapsedMs;
        segment.TimedResponses++;
        LastMilliseconds = elapsedMs;
        LastValidTokens = outputTokens;
        LastValidMilliseconds = elapsedMs;
        segment.LastValidTokens = outputTokens;
        segment.LastValidMilliseconds = elapsedMs;
        if (elapsedMs >= ResponseTimingRules.MinPeakDurationMs)
        {
            var sampleTps = 1000d * outputTokens / elapsedMs;
            PeakTps = Math.Max(PeakTps ?? 0, sampleTps);
            SessionPeakTps = Math.Max(SessionPeakTps ?? 0, sampleTps);
            segment.PeakTps = Math.Max(segment.PeakTps ?? 0, sampleTps);
        }
    }

    internal (string? SegmentKey, int SegmentIndex, string? Model, string? Route, bool Pending, bool HasSegmentUsage,
        long TimedTokens, long TimedMs, int TimedResponses, int CompletedResponses,
        double? LastTps, long LastTokens, long LastMs, double? PeakTps,
        int GatewayMatched, int HiddenReasoning, int BufferedBurst,
        double? AvgTtftMs, double? LastTtftMs, double? EndToEndTps, string TimingSource,
        long SegTotal, long SegInput, long SegCached, long SegCacheKnownInput, long SegOutput, long SegReasoning,
        int SegCacheKnownResp, int SegCacheUnknownResp, long SessTotal, int Conflicts) CaptureActiveSegment()
    {
        var seg = _currentSegment;
        if (seg is null)
        {
            return (null, 0, null, null, false, false,
                SessionTimedTokens, SessionTimedMilliseconds, SessionTimedResponses, SessionCompletedResponses,
                LastValidTps, LastValidTokens, LastValidMilliseconds, SessionPeakTps,
                SessionGatewayMatchedResponses, SessionHiddenReasoningResponses, SessionBufferedBurstResponses,
                AverageTtftMs, LastTtftMs, SessionEndToEndTps, TimingSource,
                SessionTotalTokens, SessionInputTokens, SessionCachedInputTokens, SessionCacheKnownInputTokens,
                SessionOutputTokens, SessionReasoningOutputTokens, 0, 0, SessionTotalTokens, SourceConflictCount);
        }

        var pending = seg.CompletedResponses == 0 && (_segments.Count > 1 || !string.Equals(seg.Model, "unknown", StringComparison.OrdinalIgnoreCase));
        return (seg.Key, seg.Index, seg.ServedModel ?? seg.Model, seg.Route, pending, true,
            seg.TimedTokens, seg.TimedMilliseconds, seg.TimedResponses, seg.CompletedResponses,
            seg.LastValidTps, seg.LastValidTokens, seg.LastValidMilliseconds, seg.PeakTps,
            seg.GatewayMatchedResponses, seg.HiddenReasoningResponses, seg.BufferedBurstResponses,
            seg.AverageTtftMs, seg.LastTtftMs, seg.EndToEndTps, seg.TimingSource,
            seg.TotalTokens, seg.InputTokens, seg.CachedInputTokens, seg.CacheKnownInputTokens,
            seg.OutputTokens, seg.ReasoningOutputTokens, seg.CacheKnownResponses, seg.CacheUnknownResponses,
            SessionTotalTokens, SourceConflictCount);
    }

    internal static string? Text(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) &&
        v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    internal static long Number(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
    private static long? NonnegativeNumber(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n >= 0 ? n : null;
    internal static long Timestamp(JsonElement e) =>
        DateTimeOffset.TryParse(Text(e, "timestamp"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var t) ? t.ToUnixTimeMilliseconds() : 0;
}

internal sealed class IncrementalSpeedFile
{
    internal sealed record Snapshot(string? TurnId, bool Streaming, long Tokens, long Milliseconds,
        int Timed, int Completed, double? LastTps, long LastTokens, long LastMilliseconds, double? Peak, bool Partial,
        int GatewayMatched, int HiddenReasoning, int BufferedBurst, double? AverageTtftMs, double? LastTtftMs,
        double? EndToEndTps, string TimingSource,
        string? SegmentKey, int SegmentIndex, string? CurrentModel, string? CurrentRoute,
        bool SegmentPending, bool HasSegmentUsage,
        long SegmentTotalTokens, long SegmentInputTokens, long SegmentCachedInputTokens,
        long SegmentCacheKnownInputTokens, long SegmentOutputTokens, long SegmentReasoningOutputTokens,
        int SegmentCacheKnownResponses, int SegmentCacheUnknownResponses,
        long SessionTotalTokens, int SourceConflictCount);
    private readonly IGatewayTimingLookup? _gatewayLookup;
    private Snapshot? _snapshot;
    private int _reading;
    private const int MaximumLine = 8 * 1024 * 1024;
    private long _offset;
    private long _gatewayVersion;
    private readonly StringBuilder _pending = new();
    private Decoder _decoder = Encoding.UTF8.GetDecoder();
    private bool _initialized;
    private DateTime _lastWriteUtc;
    private DateTime _createdUtc;
    public ResponseSpeedAnalyzer Analyzer { get; private set; }
    public string Path { get; }
    public IncrementalSpeedFile(string path, IGatewayTimingLookup? gatewayLookup = null)
    {
        Path = path;
        _gatewayLookup = gatewayLookup;
        Analyzer = new ResponseSpeedAnalyzer(preferModern: true, gatewayLookup: gatewayLookup);
    }

    public Snapshot? ReadInBackground()
    {
        if (Interlocked.CompareExchange(ref _reading, 1, 0) == 0)
            _ = Task.Run(() =>
            {
                try
                {
                    ReadNew();
                    Volatile.Write(ref _snapshot, Capture());
                }
                finally { Interlocked.Exchange(ref _reading, 0); }
            });
        return Volatile.Read(ref _snapshot);
    }
    public Snapshot Capture()
    {
        var a = Analyzer;
        var seg = a.CaptureActiveSegment();
        return new(a.TurnId, a.Streaming, seg.TimedTokens, seg.TimedMs,
            seg.TimedResponses, seg.CompletedResponses, seg.LastTps, seg.LastTokens, seg.LastMs,
            seg.PeakTps, a.PartialHistory, seg.GatewayMatched, seg.HiddenReasoning,
            seg.BufferedBurst, seg.AvgTtftMs, seg.LastTtftMs, seg.EndToEndTps, seg.TimingSource,
            seg.SegmentKey, seg.SegmentIndex, seg.Model, seg.Route,
            seg.Pending, seg.HasSegmentUsage,
            seg.SegTotal, seg.SegInput, seg.SegCached, seg.SegCacheKnownInput, seg.SegOutput, seg.SegReasoning,
            seg.SegCacheKnownResp, seg.SegCacheUnknownResp, seg.SessTotal, seg.Conflicts);
    }

    public void ReadNew()
    {
        try
        {
            _gatewayLookup?.Refresh();
            var currentGatewayVersion = _gatewayLookup?.Version ?? 0;
            var gatewayUpdatedMatches = _initialized && _gatewayLookup is not null
                && currentGatewayVersion != _gatewayVersion
                && Analyzer.HasNewlyMatchedGatewayResponse(_gatewayLookup);
            if (_gatewayLookup is not null) _gatewayVersion = currentGatewayVersion;

            using var f = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var written = File.GetLastWriteTimeUtc(Path);
            var created = File.GetCreationTimeUtc(Path);
            var sameLengthRewrite = _initialized && f.Length == _offset && written != _lastWriteUtc;
            if (_initialized && !gatewayUpdatedMatches && f.Length == _offset && written == _lastWriteUtc && created == _createdUtc) return;
            if (!_initialized || gatewayUpdatedMatches || f.Length < _offset || sameLengthRewrite || created != _createdUtc)
            {
                _initialized = true;
                Analyzer = new ResponseSpeedAnalyzer(ContainsModernUsage(f), _gatewayLookup);
                _pending.Clear();
                _decoder = Encoding.UTF8.GetDecoder();
                // Whole-session statistics are rebuilt in the background, then incrementally
                // maintained. Never publish a current-turn subset as a session-wide result.
                _offset = 0;
            }
            _lastWriteUtc = written; _createdUtc = created;
            f.Position = _offset;
            var bytes = new byte[64 * 1024];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            int read;
            while ((read = f.Read(bytes, 0, bytes.Length)) > 0)
            {
                _offset += read;
                var count = _decoder.GetChars(bytes, 0, read, chars, 0, false);
                for (var i = 0; i < count; i++)
                {
                    if (chars[i] == '\n')
                    {
                        if (_pending.Length <= MaximumLine) Analyzer.ObserveLine(_pending.ToString());
                        _pending.Clear();
                        if (_pending.Capacity > 1024 * 1024) _pending.Capacity = 64 * 1024;
                    }
                    else if (_pending.Length <= MaximumLine) _pending.Append(chars[i]);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // A whole-session replay must choose its usage carrier from the whole file, not
    // the last 8 MiB. Inspect bounded line prefixes; giant tool bodies are never allocated.
    private static bool ContainsModernUsage(FileStream stream)
    {
        stream.Position = 0;
        var buffer = new byte[64 * 1024]; var prefix = new byte[8192]; int count = 0, read;
        bool IsModern()
        {
            try
            {
                var json = new Utf8JsonReader(prefix.AsSpan(0, count), isFinalBlock: false, state: default);
                while (json.Read())
                    if (json.TokenType == JsonTokenType.PropertyName && json.CurrentDepth == 1 && json.ValueTextEquals("type"))
                        return json.Read() && json.TokenType == JsonTokenType.String && json.ValueTextEquals("token_usage_record");
            }
            catch (JsonException) { }
            return false;
        }
        while ((read = stream.Read(buffer)) > 0)
            for (var i = 0; i < read; i++)
                if (buffer[i] == 10) { if (IsModern()) return true; count = 0; }
                else if (count < prefix.Length) prefix[count++] = buffer[i];
        return count > 0 && IsModern();
    }


}

public sealed class SpeedObserver : IDisposable
{
    private readonly Dictionary<string, IncrementalSpeedFile> _files = new(StringComparer.Ordinal);
    private readonly CodexLiveStream _live = new(measureText: false);
    private readonly bool _enableLive;
    private readonly IGatewayTimingLookup? _gatewayLookup;
    public SpeedObserver(bool enableLive = true, IGatewayTimingLookup? gatewayLookup = null)
    {
        _enableLive = enableLive;
        _gatewayLookup = gatewayLookup ?? (enableLive ? MagpieGatewayTimingStore.TryCreateDefault() : null);
    }
    public LiveUsageTotals? ReadUsage(string threadId) => _live.ReadUsage(threadId);
    public void Watch(IEnumerable<string> threads)
    {
        if (!_enableLive) return;
        _live.Start();
        _live.SetThreads(threads);
    }
    public SpeedReading Read(string threadId, string path)
    {
        if (!_files.TryGetValue(threadId, out var file) || file.Path != path)
        {
            _files[threadId] = file = new IncrementalSpeedFile(path, _gatewayLookup);
            if (_files.Count > 16) _files.Remove(_files.Keys.First(key => key != threadId));
        }
        IncrementalSpeedFile.Snapshot? a;
        if (_enableLive) a = file.ReadInBackground();
        else { file.ReadNew(); a = file.Capture(); }
        var live = _live.Read(threadId);
        // Live IPC is the authority for current activity; delayed log writes must not mask it.
        var average = a is { Milliseconds: > 0 } ? a.Tokens * 1000d / a.Milliseconds : (double?)null;
        return new SpeedReading(threadId, live.Connected ? live.TurnId : a?.TurnId,
            live.Connected ? live.Activity is not ("ready" or "connecting") : a?.Streaming == true, average, a?.Tokens ?? 0,
            a?.Milliseconds ?? 0, a?.Timed ?? 0, a?.Completed ?? 0, a?.LastTps, a?.LastTokens ?? 0,
            a?.LastMilliseconds ?? 0, a?.Peak, null, live.Connected,
            live.LastOutputAtMs, a?.Partial ?? true, _live.LastError, _live.FramesReceived, _live.TextUpdates, live.ToolRunning, live.Activity,
            a?.GatewayMatched ?? 0,
            a?.HiddenReasoning ?? 0,
            a?.BufferedBurst ?? 0,
            a?.AverageTtftMs,
            a?.LastTtftMs,
            a?.EndToEndTps,
            a?.TimingSource ?? "rollout",
            a?.SegmentKey,
            a?.SegmentIndex ?? 0,
            a?.CurrentModel,
            a?.CurrentRoute,
            a?.SegmentPending ?? false,
            a?.HasSegmentUsage ?? false,
            a?.SegmentTotalTokens ?? 0,
            a?.SegmentInputTokens ?? 0,
            a?.SegmentCachedInputTokens ?? 0,
            a?.SegmentCacheKnownInputTokens ?? 0,
            a?.SegmentOutputTokens ?? 0,
            a?.SegmentReasoningOutputTokens ?? 0,
            a?.SegmentCacheKnownResponses ?? 0,
            a?.SegmentCacheUnknownResponses ?? 0,
            a?.SessionTotalTokens ?? 0,
            a?.SourceConflictCount ?? 0);
    }
    public SpeedReading ReadLive(string threadId)
    {
        var live = _live.Read(threadId);
        return new SpeedReading(threadId, live.TurnId, live.Activity is not ("ready" or "connecting"),
            null, 0, 0, 0, 0, null, 0, 0, null, live.Tps, live.Connected, live.LastOutputAtMs, true,
            _live.LastError, _live.FramesReceived, _live.TextUpdates, live.ToolRunning, live.Activity);
    }
    public void Dispose() => _live.Dispose();
}
