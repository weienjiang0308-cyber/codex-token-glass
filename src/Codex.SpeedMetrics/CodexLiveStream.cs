using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codex.SpeedMetrics;

/// <summary>Visible-text tokenizer, deliberately independent of the number of transport chunks.</summary>
public sealed class VisibleTextWindow
{
    public static Func<string, int>? TokenCounter { private get; set; }
    public static Func<string, int>? StablePrefixLength { private get; set; }
    private readonly Dictionary<string, (string Text, int Count, bool Complete)> _texts = new(StringComparer.Ordinal);
    public const int SampleMilliseconds = 250;
    public const int WindowMilliseconds = 2000;
    private readonly SortedDictionary<long, long> _samples = new();
    public long ObservedTokens { get; private set; }
    public long? LastOutputAtMs { get; private set; }
    public bool HasObservedNewText { get; private set; }
    public static int Count(string text) => (TokenCounter ?? throw new InvalidOperationException("Tokenizer not initialized by host"))(text);
    public void Clear()
    {
        _texts.Clear(); _samples.Clear(); LastOutputAtMs = null; HasObservedNewText = false; ObservedTokens = 0;
    }
    public void Observe(string id, string text, long monotonicMs, bool baseline = false, bool complete = true)
    {
        if (text.Length > 262_144) return; // Bound work; never replace overflow with chunk counts.
        if (_texts.TryGetValue(id, out var before) && before.Text == text && before.Complete == complete) return;
        var length = complete || baseline ? text.Length : (StablePrefixLength ?? throw new InvalidOperationException("Stable tokenizer not initialized"))(text);
        var count = Count(text[..length]);
        var delta = _texts.TryGetValue(id, out before) && text.StartsWith(before.Text, StringComparison.Ordinal)
            ? Math.Max(0, count - before.Count) : before.Text is null ? count : 0;
        var committed = before.Text is not null && text.StartsWith(before.Text, StringComparison.Ordinal) ? Math.Max(count, before.Count) : count;
        _texts[id] = (text, committed, complete);
        if (!baseline && delta != 0)
        {
            var bucket = monotonicMs / SampleMilliseconds;
            _samples[bucket] = _samples.GetValueOrDefault(bucket) + delta;
            ObservedTokens += delta;
            Prune(bucket);
            LastOutputAtMs = monotonicMs;
            if (delta > 0) HasObservedNewText = true;
        }
    }
    private void Prune(long bucket)
    {
        foreach (var key in _samples.Keys.TakeWhile(k => k < bucket - WindowMilliseconds / SampleMilliseconds - 6).ToArray())
            _samples.Remove(key);
    }
    public double? Read(long monotonicMs)
    {
        // Use eight complete 250 ms bins. A tiny packet interval is never a denominator.
        var bucket = monotonicMs / SampleMilliseconds;
        Prune(bucket);
        var tokens = _samples.Where(x => x.Key >= bucket - 8 && x.Key < bucket).Sum(x => x.Value);
        return HasObservedNewText ? Math.Max(0, tokens) / (WindowMilliseconds / 1000d) : null;
    }

    public double? ReadDisplayRate(long monotonicMs)
    {
        // A displayed second is the mean of its four 250 ms measurements, not one
        // arbitrary point sample. Summing these unheld readings preserves token area.
        var end = monotonicMs / 1000 * 4;
        Prune(end);
        double sum = 0;
        for (var bucket = end - 3; bucket <= end; bucket++)
            sum += Math.Max(0, _samples.Where(x => x.Key >= bucket - 8 && x.Key < bucket).Sum(x => x.Value)) / 2d;
        return HasObservedNewText ? sum / 4d : null;
    }

}

/// <summary>
/// Codex's existing per-user IPC, read-only follower subscription. Keeps only the latest turn's
/// model text. Old snapshots, reconnects and duplicate revisions never become new live tokens.
/// </summary>
public sealed class CodexLiveStream : IDisposable
{
    private const int MaximumFrame = 64 * 1024 * 1024;
    private sealed class FollowState
    {
        public long Revision = -1;
        public string[] Prefix = Array.Empty<string>();
        public JsonObject? Turn;
        public string? TurnId;
        public long Started;
        public VisibleTextWindow Window = new();
    }
    private readonly object _gate = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly SemaphoreSlim _subscriptions = new(1, 1);
    private readonly HashSet<string> _followed = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, FollowState> _states = new(StringComparer.Ordinal);
    private HashSet<string> _desired = new(StringComparer.Ordinal);
    private NamedPipeClientStream? _pipe;
    private string? _clientId;
    private Task? _runner;
    private bool _connected;
    public string? LastError { get; private set; }
    public long FramesReceived { get; private set; }
    public long TextUpdates { get; private set; }
    internal static long Now => Environment.TickCount64;

    public void Start() => _runner ??= Task.Run(Run);
    public void SetThreads(IEnumerable<string> ids)
    {
        HashSet<string> next = new(ids.Where(x => Guid.TryParse(x, out _)).Distinct().Take(4), StringComparer.Ordinal);
        List<string> added, removed;
        lock (_gate)
        {
            if (_desired.SetEquals(next)) return;
            added = next.Except(_desired).ToList();
            removed = _desired.Except(next).ToList();
            _desired = next;
            foreach (var id in removed) _states.Remove(id);
            foreach (var id in added) _states[id] = new FollowState();
        }
        _ = ReconcileSubscriptions();
    }

    private async Task ReconcileSubscriptions()
    {
        await _subscriptions.WaitAsync();
        try
        {
            if (_clientId is null) return;
            HashSet<string> desired;
            lock (_gate) desired = new(_desired, StringComparer.Ordinal);
            foreach (var id in _followed.Except(desired).ToArray())
            { await Follow(id, false); _followed.Remove(id); }
            foreach (var id in desired.Except(_followed).ToArray())
            { await Follow(id, true); _followed.Add(id); }
        }
        finally { _subscriptions.Release(); }
    }
    public (double? Tps, bool Connected, long? LastOutputAtMs, string? TurnId) Read(string thread)
    {
        lock (_gate)
        {
            if (!_connected || !_states.TryGetValue(thread, out var state)) return (null, false, null, null);
            return (state.Window.ReadDisplayRate(Now), true, state.Window.LastOutputAtMs, state.TurnId);
        }
    }

    private async Task Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", "codex-ipc", PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(2500, _stop.Token);
                _pipe = pipe;
                var request = Guid.NewGuid().ToString();
                await Send(new { type = "request", requestId = request, sourceClientId = "codex-speed-metrics",
                    version = 0, method = "initialize", @params = new { clientType = "codex-speed-metrics" } });
                while (!_stop.IsCancellationRequested)
                {
                    var prefix = new byte[4];
                    await ReadExactly(pipe, prefix, _stop.Token);
                    var length = BitConverter.ToInt32(prefix, 0);
                    if (length < 2 || length > MaximumFrame) throw new IOException("IPC frame exceeds supported bound");
                    var payload = new byte[length];
                    await ReadExactly(pipe, payload, _stop.Token);
                    using var doc = JsonDocument.Parse(payload);
                    var message = doc.RootElement;
                    if (ResponseSpeedAnalyzer.Text(message, "requestId") == request)
                    {
                        if (ResponseSpeedAnalyzer.Text(message, "resultType") != "success")
                            throw new IOException("IPC initialize rejected");
                        _clientId = message.GetProperty("result").GetProperty("clientId").GetString();
                        List<string> ids;
                        lock (_gate)
                        {
                            _connected = true; LastError = null;
                            foreach (var s in _states.Values) { s.Revision = -1; s.Window.Clear(); s.Turn = null; s.Started = 0; }
                            ids = _desired.ToList();
                        }
                        await _subscriptions.WaitAsync();
                        try { _followed.Clear(); }
                        finally { _subscriptions.Release(); }
                        await ReconcileSubscriptions();
                        continue;
                    }
                    ProcessMessage(message);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message +
                    (ex.InnerException is null ? "" : " -> " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
            }
            finally
            {
                _pipe = null; _clientId = null;
                lock (_gate) { _connected = false; foreach (var state in _states.Values) state.Window.Clear(); }
            }
            try { await Task.Delay(1500, _stop.Token); } catch (OperationCanceledException) { break; }
        }
    }

    private void ProcessMessage(JsonElement message)
    {
        if (ResponseSpeedAnalyzer.Text(message, "method") != "thread-stream-state-changed" ||
            !message.TryGetProperty("params", out var p) || ResponseSpeedAnalyzer.Text(p, "hostId") != "local") return;
        var thread = ResponseSpeedAnalyzer.Text(p, "conversationId");
        if (thread is null || !p.TryGetProperty("change", out var change)) return;
        lock (_gate)
        {
            if (!_states.TryGetValue(thread, out var state)) return;
            FramesReceived++;
            var kind = ResponseSpeedAnalyzer.Text(change, "type");
            if (kind == "snapshot")
            {
                state.Revision = ResponseSpeedAnalyzer.Number(change, "revision");
                state.Window.Clear(); state.Turn = null; state.Started = 0;
                if (change.TryGetProperty("conversationState", out var conversation)) ObserveSnapshot(state, conversation);
                ObserveTexts(state, true);
                return;
            }
            if (kind != "patches") return;
            var revision = ResponseSpeedAnalyzer.Number(change, "revision");
            if (revision <= state.Revision) return;
            if (ResponseSpeedAnalyzer.Number(change, "baseRevision") != state.Revision)
            {
                state.Revision = -1; state.Window.Clear(); state.Turn = null;
                _ = Task.Run(async () => { await Follow(thread, false); await Follow(thread, true); });
                return;
            }
            if (change.TryGetProperty("patches", out var patches))
                foreach (var patch in patches.EnumerateArray()) ApplyPatch(state, patch);
            state.Revision = revision;
            ObserveTexts(state, false);
        }
    }

    private static void ObserveSnapshot(FollowState s, JsonElement c)
    {
        if (c.TryGetProperty("turnHistory", out var th) && th.TryGetProperty("history", out var history) &&
            history.TryGetProperty("entitiesByKey", out var entities))
            foreach (var entry in entities.EnumerateObject())
                ConsiderTurn(s, entry.Value, new[] { "turnHistory", "history", "entitiesByKey", entry.Name });
        if (c.TryGetProperty("turns", out var turns) && turns.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var t in turns.EnumerateArray()) ConsiderTurn(s, t, new[] { "turns", (i++).ToString(CultureInfo.InvariantCulture) });
        }
    }
    private static void ConsiderTurn(FollowState s, JsonElement t, string[] prefix)
    {
        if (t.ValueKind != JsonValueKind.Object || !t.TryGetProperty("items", out var items)) return;
        var started = ResponseSpeedAnalyzer.Number(t, "turnStartedAtMs");
        if (started < s.Started) return;
        var id = ResponseSpeedAnalyzer.Text(t, "turnId");
        if (id != s.TurnId) s.Window.Clear();
        s.TurnId = id; s.Started = started; s.Prefix = prefix;
        var modelItems = new JsonArray();
        foreach (var item in items.EnumerateArray()) modelItems.Add(SafeItem(item));
        s.Turn = new JsonObject { ["turnId"] = id, ["status"] = ResponseSpeedAnalyzer.Text(t, "status"), ["items"] = modelItems,
            ["agentMessageCompletedAtMsById"] = t.TryGetProperty("agentMessageCompletedAtMsById", out var completed) ? JsonNode.Parse(completed.GetRawText()) : new JsonObject() };
    }
    private static JsonNode? SafeItem(JsonElement item)
    {
        var type = ResponseSpeedAnalyzer.Text(item, "type");
        if (type is not ("agentMessage" or "reasoning"))
            return new JsonObject { ["type"] = type, ["id"] = ResponseSpeedAnalyzer.Text(item, "id") };
        return JsonNode.Parse(item.GetRawText());
    }
    private static string[] PatchPath(JsonElement p)
    {
        if (!p.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return path.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.ToString()).ToArray();
    }
    private static void ApplyPatch(FollowState s, JsonElement p)
    {
        var path = PatchPath(p);
        var op = ResponseSpeedAnalyzer.Text(p, "op");
        var hasValue = p.TryGetProperty("value", out var value);
        if (path.Length == 4 && path.Take(3).SequenceEqual(new[] { "turnHistory", "history", "entitiesByKey" }) && hasValue)
        { ConsiderTurn(s, value, path); return; }
        if (path.Length == 2 && path[0] == "turns" && hasValue)
        { ConsiderTurn(s, value, path); return; }
        if (s.Turn is null || path.Length <= s.Prefix.Length || !path.Take(s.Prefix.Length).SequenceEqual(s.Prefix)) return;
        var relative = path.Skip(s.Prefix.Length).ToArray();
        if (relative[0] is not ("items" or "status" or "agentMessageCompletedAtMsById")) return;
        // Tool stdout, arguments and file bodies are not visible-model-token estimates.
        if (relative[0] == "items" && relative.Length >= 3 && int.TryParse(relative[1], out var itemIndex) &&
            s.Turn["items"] is JsonArray list && itemIndex >= 0 && itemIndex < list.Count &&
            list[itemIndex]?["type"]?.GetValue<string>() is not ("agentMessage" or "reasoning")) return;
        JsonNode? parent = s.Turn;
        for (var i = 0; i < relative.Length - 1; i++)
        {
            if (parent is JsonArray a && int.TryParse(relative[i], out var index)) parent = index >= 0 && index < a.Count ? a[index] : null;
            else parent = parent?[relative[i]];
            if (parent is null) return;
        }
        var last = relative[^1];
        JsonNode? node = hasValue ? (relative.Length == 2 && relative[0] == "items" ? SafeItem(value) : JsonNode.Parse(value.GetRawText())) : null;
        if (parent is JsonArray array && int.TryParse(last, out var n))
        {
            if (op == "remove") { if (n >= 0 && n < array.Count) array.RemoveAt(n); }
            else if (op == "add" && n >= 0 && n <= array.Count) array.Insert(n, node);
            else if (n >= 0 && n < array.Count) array[n] = node;
        }
        else if (parent is JsonObject obj)
        {
            if (op == "remove") obj.Remove(last); else obj[last] = node;
        }
    }
    private void ObserveTexts(FollowState s, bool baseline)
    {
        if (s.Turn?["items"] is not JsonArray items) return;
        foreach (var item in items)
        {
            if (item is null) continue;
            var type = item["type"]?.GetValue<string>();
            var id = item["id"]?.GetValue<string>();
            if (id is null || type is not ("agentMessage" or "reasoning")) continue;
            var text = type == "agentMessage" ? TextOf(item["text"]) : TextOf(item["summary"]);
            if (type == "reasoning" && text.Length == 0) text = TextOf(item["content"]);
            var status = s.Turn["status"]?.GetValue<string>();
            var complete = status is "completed" or "interrupted" or "failed"
                || PositiveTimestamp(item["completedAtMs"])
                || PositiveTimestamp(s.Turn["agentMessageCompletedAtMsById"]?[id]);
            s.Window.Observe(s.TurnId + ":" + id, text, Now, baseline, complete);
            if (!baseline) TextUpdates++;
        }
    }
    private static bool PositiveTimestamp(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var at) && at > 0;
    private static string TextOf(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<string>(out var text)) return text;
        if (node is JsonArray array) return string.Join("\n", array.Select(TextOf));
        if (node is JsonObject obj && obj.TryGetPropertyValue("text", out var t)) return TextOf(t);
        return "";
    }
    private Task Follow(string thread, bool following) => _clientId is null ? Task.CompletedTask :
        Send(new { type = "broadcast", method = "thread-stream-following-changed", sourceClientId = _clientId,
            version = 1, @params = new { hostId = "local", conversationId = thread, following, speedObserver = true } });
    private async Task Send(object message)
    {
        try
        {
            await _write.WaitAsync(_stop.Token);
            try
            {
                var pipe = _pipe;
                if (pipe is null || !pipe.IsConnected) return;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
                await pipe.WriteAsync(BitConverter.GetBytes(bytes.Length), _stop.Token);
                await pipe.WriteAsync(bytes, _stop.Token);
                await pipe.FlushAsync(_stop.Token);
            }
            finally { _write.Release(); }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    private static async Task ReadExactly(Stream s, byte[] buffer, CancellationToken stop)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(offset), stop);
            if (n == 0) throw new IOException("IPC disconnected");
            offset += n;
        }
    }
    public void Dispose()
    {
        _stop.Cancel(); _pipe?.Dispose();
        try { _runner?.Wait(1500); } catch (AggregateException) { }
    }
}
