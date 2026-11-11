using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Codex.SpeedMetrics;

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
    string? LiveError = null, long LiveFrames = 0, long LiveTextUpdates = 0, bool ToolRunning = false, string? Activity = null)
{
    public string AverageCoverage => $"{TimedResponses}/{CompletedResponses}";
}

public sealed class ResponseSpeedAnalyzer
{
    private readonly HashSet<string> _responses = new(StringComparer.Ordinal);
    private readonly HashSet<string> _earlierTurns = new(StringComparer.Ordinal);
    private long? _firstOutput;
    private long _responseBoundary;
    private long _timedTokens, _timedMs;
    private int _timedResponses, _responsesInTurn;
    private bool _sawModernUsage;
    private readonly bool _preferModern;
    private long _allOutputTokens;
    public long SessionTimedTokens { get; private set; }
    public long SessionTimedMilliseconds { get; private set; }
    public int SessionTimedResponses { get; private set; }
    public int SessionCompletedResponses { get; private set; }
    private string? _lastLegacyFingerprint;
    public string? TurnId { get; private set; }
    public bool Streaming { get; private set; }
    public bool PartialHistory { get; set; }
    public long AllOutputTokens => _allOutputTokens;
    public ResponseSpeedAnalyzer(bool preferModern = true) => _preferModern = preferModern;
    public long LastTokens { get; private set; }
    public long LastMilliseconds { get; private set; }
    public double? PeakTps { get; private set; }
    public double? AverageTps => _timedMs > 0 ? 1000d * _timedTokens / _timedMs : null;
    public long TimedTokens => _timedTokens;
    public long TimedMilliseconds => _timedMs;
    public int TimedResponses => _timedResponses;
    public int CompletedResponses => _responsesInTurn;
    public double? LastTps => LastMilliseconds > 0 ? 1000d * LastTokens / LastMilliseconds : null;

    private void BeginTurn(string? turnId, long at, bool completeStart)
    {
        if (turnId is not null && turnId == TurnId) return;
        if (TurnId is not null) _earlierTurns.Add(TurnId);
        if (turnId is not null) _earlierTurns.Remove(turnId);
        TurnId = turnId;
        Streaming = true;
        _firstOutput = null;
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
        if (recordType == "event_msg" && eventType == "task_started")
        {
            BeginTurn(Text(p, "turn_id"), at, true);
            return;
        }
        if (recordType == "event_msg" && eventType == "turn_aborted")
        {
            Streaming = false;
            _firstOutput = null;
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
                _firstOutput = _firstOutput is null ? start : Math.Min(_firstOutput.Value, start);
            return;
        }
        if (recordType == "token_usage_record" && p.TryGetProperty("usage", out var usage))
        {
            _sawModernUsage = true;
            var id = Text(p, "response_id");
            if (string.IsNullOrEmpty(id)) id = "record:" + at + ":" + usage.GetRawText();
            if (!_responses.Add(id)) return;
            var turn = Text(p, "turn_id");
            // A late record of an earlier turn is a completed, untimed response. It must not
            // rewind the current turn, or that turn's next items would be rejected as foreign.
            if (turn is not null && turn != TurnId && _earlierTurns.Contains(turn))
            {
                if (NonnegativeNumber(usage, "output_tokens") is not null) SessionCompletedResponses++;
                return;
            }
            if (turn != TurnId) BeginTurn(turn, _responseBoundary, false);
            CompleteResponse(NonnegativeNumber(usage, "output_tokens"), at);
            return;
        }
        // Older logs have only token_count. Modern token_count mirrors MUST NOT be counted again.
        if (recordType == "event_msg" && eventType == "token_count" && !_preferModern && !_sawModernUsage &&
            p.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object &&
            info.TryGetProperty("last_token_usage", out var last))
        {
            var fingerprint = (info.TryGetProperty("total_token_usage", out var total) ? total.GetRawText() : "")
                + "|" + last.GetRawText();
            if (fingerprint == _lastLegacyFingerprint) return;
            _lastLegacyFingerprint = fingerprint;
            CompleteResponse(NonnegativeNumber(last, "output_tokens"), at);
        }
    }

    private void CompleteResponse(long? reportedTokens, long at)
    {
        if (reportedTokens is long outputTokens)
        {
            _responsesInTurn++;
            SessionCompletedResponses++;
            _allOutputTokens += outputTokens;
            LastTokens = outputTokens;
            LastMilliseconds = 0;
            if (_firstOutput is long start && at > start && at - start <= 3_600_000)
            {
                var elapsed = at - start;
                _timedTokens += outputTokens;
                _timedMs += elapsed;
                _timedResponses++;
                SessionTimedTokens += outputTokens;
                SessionTimedMilliseconds += elapsed;
                SessionTimedResponses++;
                LastMilliseconds = elapsed;
                if (elapsed >= 500)
                    PeakTps = Math.Max(PeakTps ?? 0, 1000d * outputTokens / elapsed);
            }
        }
        _firstOutput = null;
        _responseBoundary = Math.Max(_responseBoundary, at);
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
        int Timed, int Completed, double? LastTps, long LastTokens, long LastMilliseconds, double? Peak, bool Partial);
    private Snapshot? _snapshot;
    private int _reading;
    private const int MaximumLine = 8 * 1024 * 1024;
    private long _offset;
    private readonly StringBuilder _pending = new();
    private Decoder _decoder = Encoding.UTF8.GetDecoder();
    private bool _initialized;
    private DateTime _lastWriteUtc;
    private DateTime _createdUtc;
    public ResponseSpeedAnalyzer Analyzer { get; private set; } = new();
    public string Path { get; }
    public IncrementalSpeedFile(string path) => Path = path;

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
        return new(a.TurnId, a.Streaming, a.SessionTimedTokens, a.SessionTimedMilliseconds,
            a.SessionTimedResponses, a.SessionCompletedResponses, a.LastTps, a.LastTokens, a.LastMilliseconds, a.PeakTps, a.PartialHistory);
    }

    public void ReadNew()
    {
        try
        {
            using var f = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var written = File.GetLastWriteTimeUtc(Path);
            var created = File.GetCreationTimeUtc(Path);
            var sameLengthRewrite = _initialized && f.Length == _offset && written != _lastWriteUtc;
            if (_initialized && f.Length == _offset && written == _lastWriteUtc && created == _createdUtc) return;
            if (!_initialized || f.Length < _offset || sameLengthRewrite || created != _createdUtc)
            {
                _initialized = true;
                Analyzer = new ResponseSpeedAnalyzer(ContainsModernUsage(f));
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
    public SpeedObserver(bool enableLive = true) => _enableLive = enableLive;
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
            _files[threadId] = file = new IncrementalSpeedFile(path);
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
            live.LastOutputAtMs, a?.Partial ?? true, _live.LastError, _live.FramesReceived, _live.TextUpdates, live.ToolRunning, live.Activity);
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
