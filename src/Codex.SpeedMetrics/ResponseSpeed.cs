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
    string? LiveError = null, long LiveFrames = 0, long LiveTextUpdates = 0)
{
    public string AverageCoverage => $"{TimedResponses}/{CompletedResponses}";
}

public sealed class ResponseSpeedAnalyzer
{
    private readonly HashSet<string> _responses = new(StringComparer.Ordinal);
    private long? _firstOutput;
    private long _responseBoundary;
    private long _timedTokens, _timedMs;
    private int _timedResponses, _responsesInTurn;
    private bool _sawModernUsage;
    private readonly bool _preferModern;
    private long _allOutputTokens;
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
        if (!record.TryGetProperty("payload", out var p)) return;
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
            if (turn != TurnId) BeginTurn(turn, _responseBoundary, false);
            CompleteResponse(Number(usage, "output_tokens"), at);
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
            CompleteResponse(Number(last, "output_tokens"), at);
        }
    }

    private void CompleteResponse(long outputTokens, long at)
    {
        if (outputTokens > 0)
        {
            _responsesInTurn++;
            _allOutputTokens += outputTokens;
            LastTokens = outputTokens;
            LastMilliseconds = 0;
            if (_firstOutput is long start && at > start && at - start <= 3_600_000)
            {
                var elapsed = at - start;
                _timedTokens += outputTokens;
                _timedMs += elapsed;
                _timedResponses++;
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
    internal static long Timestamp(JsonElement e) =>
        DateTimeOffset.TryParse(Text(e, "timestamp"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var t) ? t.ToUnixTimeMilliseconds() : 0;
}

internal sealed class IncrementalSpeedFile
{
    private const long TailLimit = 8 * 1024 * 1024;
    private const int MaximumLine = 8 * 1024 * 1024;
    private long _offset;
    private readonly StringBuilder _pending = new();
    private Decoder _decoder = Encoding.UTF8.GetDecoder();
    private bool _initialized;
    public ResponseSpeedAnalyzer Analyzer { get; private set; } = new();
    public string Path { get; }
    public IncrementalSpeedFile(string path) => Path = path;

    public void ReadNew()
    {
        try
        {
            using var f = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!_initialized || f.Length < _offset)
            {
                _initialized = true;
                f.Position = Math.Max(0, f.Length - TailLimit);
                var probe = new byte[(int)(f.Length - f.Position)];
                var bytesRead = f.Read(probe, 0, probe.Length);
                Analyzer = new ResponseSpeedAnalyzer(Encoding.UTF8.GetString(probe, 0, bytesRead).Contains("\"token_usage_record\"", StringComparison.Ordinal));
                _pending.Clear();
                _decoder = Encoding.UTF8.GetDecoder();
                var turnStart = FindLastTurnStart(f);
                _offset = turnStart ?? Math.Max(0, f.Length - TailLimit);
                if (turnStart is null && _offset > 0)
                {
                    Analyzer.PartialHistory = true;
                    f.Position = _offset;
                    int b;
                    do { b = f.ReadByte(); _offset++; } while (b >= 0 && b != 10);
                }
            }
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

    // Locate the current turn with fixed-size buffers. A restart must not silently drop earlier
    // responses in a long turn merely because tool output pushed them out of the 8 MB tail.
    private static long? FindLastTurnStart(FileStream file)
    {
        file.Position=0;var end=file.Length;var buffer=new byte[64*1024];var prefix=new byte[8192];
        int count=0;bool overflow=false;long at=0,lineStart=0;long? last=null;
        while(at<end)
        {
            var read=file.Read(buffer,0,(int)Math.Min(buffer.Length,end-at));if(read==0)break;
            for(int i=0;i<read;i++)
            {
                var b=buffer[i];at++;
                if(b==10)
                {
                    if(!overflow && count>0 && Encoding.UTF8.GetString(prefix,0,count).Contains("\"task_started\"",StringComparison.Ordinal))
                    {
                        try
                        {
                            using var doc=JsonDocument.Parse(prefix.AsMemory(0,count));var r=doc.RootElement;
                            if(ResponseSpeedAnalyzer.Text(r,"type")=="event_msg" && r.TryGetProperty("payload",out var p)
                                && ResponseSpeedAnalyzer.Text(p,"type")=="task_started")last=lineStart;
                        }
                        catch(JsonException) { }
                    }
                    count=0;overflow=false;lineStart=at;
                }
                else if(count<prefix.Length)prefix[count++]=b;
                else overflow=true;
            }
        }
        return last;
    }
}

public sealed class SpeedObserver : IDisposable
{
    private readonly Dictionary<string, IncrementalSpeedFile> _files = new(StringComparer.Ordinal);
    private readonly CodexLiveStream _live = new();
    private readonly bool _enableLive;
    public SpeedObserver(bool enableLive = true) => _enableLive = enableLive;
    public void Watch(IEnumerable<string> threads)
    {
        if (!_enableLive) return;
        _live.Start();
        _live.SetThreads(threads);
    }
    public SpeedReading Read(string threadId, string path)
    {
        if (!_files.TryGetValue(threadId, out var file) || file.Path != path)
            _files[threadId] = file = new IncrementalSpeedFile(path);
        file.ReadNew();
        var a = file.Analyzer;
        var live = _live.Read(threadId);
        if (live.TurnId != a.TurnId) live = (null, live.Connected, null, live.TurnId);
        return new SpeedReading(threadId, a.TurnId, a.Streaming, a.AverageTps, a.TimedTokens,
            a.TimedMilliseconds, a.TimedResponses, a.CompletedResponses, a.LastTps, a.LastTokens,
            a.LastMilliseconds, a.PeakTps, live.Tps, live.Connected,
            live.LastOutputAtMs, a.PartialHistory, _live.LastError, _live.FramesReceived, _live.TextUpdates);
    }
    public void Dispose() => _live.Dispose();
}
