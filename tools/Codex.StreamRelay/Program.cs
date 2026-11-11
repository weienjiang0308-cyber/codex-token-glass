using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Text;

// Codex JSONL is UTF-8 regardless of the Windows console/OEM code page.
var utf8 = new UTF8Encoding(false);
Console.InputEncoding = utf8;
Console.OutputEncoding = utf8;

// Opt-in diagnostic relay. No credential or response-body logging.
var real = Environment.GetEnvironmentVariable("CODEX_RAW_REAL_EXE");
if (string.IsNullOrWhiteSpace(real) || !Path.IsPathFullyQualified(real) || !File.Exists(real)
    || string.Equals(Path.GetFullPath(real), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
{ Console.Error.WriteLine("CODEX_RAW_REAL_EXE must identify the original Codex executable."); return 2; }
var observe = args.Contains("app-server") && !args.Any(a => a is "--help" or "generate-json-schema" or "generate-ts" or "daemon" or "proxy");
var start = new ProcessStartInfo(real) { UseShellExecute = false, RedirectStandardInput = true,
    RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
    StandardInputEncoding = utf8, StandardOutputEncoding = utf8, StandardErrorEncoding = utf8 };
foreach (var arg in args) start.ArgumentList.Add(arg);
var telemetryEndpoint = Environment.GetEnvironmentVariable("CODEX_RAW_OTEL_ENDPOINT");
if (observe && telemetryEndpoint is not null)
{
    if (!Uri.TryCreate(telemetryEndpoint, UriKind.Absolute, out var local) || local.Scheme != "http" || local.Host != "127.0.0.1")
    { Console.Error.WriteLine("Diagnostic telemetry must target HTTP loopback."); return 2; }
    foreach (var setting in new[] {
        "otel.exporter={otlp-http={endpoint=" + System.Text.Json.JsonSerializer.Serialize(telemetryEndpoint) + ",protocol=\"json\",headers={}}}",
        "otel.log_sse_deltas=true", "otel.log_user_prompt=false", "otel.log_agent_responses=false",
        "otel.log_guardian_assessments=false", "otel.tool_result.max_bytes=0" })
    { start.ArgumentList.Add("-c"); start.ArgumentList.Add(setting); }
    start.Environment.TryGetValue("NO_PROXY", out var noProxy);
    start.Environment["NO_PROXY"] = string.Join(',', new[] { noProxy, "127.0.0.1" }.Where(x => !string.IsNullOrEmpty(x)));
}
start.Environment["CODEX_CLI_PATH"] = real;
using var child = Process.Start(start) ?? throw new IOException("Could not start the original Codex executable.");
var errorPump = child.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
if (!observe)
{
    var input = Task.Run(async () =>
    {
        try { await Console.OpenStandardInput().CopyToAsync(child.StandardInput.BaseStream); }
        catch (IOException) { }
        finally { child.StandardInput.Close(); }
    });
    var output = child.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
    await child.WaitForExitAsync(); await Task.WhenAll(output, errorPump);
    return child.ExitCode;
}
var directory = Environment.GetEnvironmentVariable("CODEX_RAW_TRACE_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTokenGlass", "stream-observer");
var tracePath = Path.Combine(directory, $"relay-{Environment.ProcessId}.jsonl");
var queue = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
long dropped = 0;
void Record(JsonObject record)
{
    record["monotonicTicks"] = Stopwatch.GetTimestamp();
    if (!queue.Writer.TryWrite(record)) Interlocked.Increment(ref dropped);
}
var writer = Task.Run(async () =>
{
    StreamWriter? file = null;
    try { Directory.CreateDirectory(directory); file = new StreamWriter(tracePath, append: false); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    { Console.Error.WriteLine("Stream observer trace unavailable; protocol forwarding continues."); }
    try
    {
        await foreach (var record in queue.Reader.ReadAllAsync())
        {
            if (file is null) continue;
            try { await file.WriteLineAsync(record.ToJsonString()); await file.FlushAsync(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { try { file.Dispose(); } catch (IOException) { } file = null; }
        }
    }
    finally { try { file?.Dispose(); } catch (IOException) { } }
});
Record(new() { ["kind"] = "start", ["frequency"] = Stopwatch.Frequency, ["utc"] = DateTimeOffset.UtcNow.ToString("O") });
var hidden = new HashSet<string>(StringComparer.Ordinal);
var first = new Dictionary<string, long>(StringComparer.Ordinal);
var completed = new HashSet<string>(StringComparer.Ordinal);
var inputPump = Task.Run(async () =>
{
    try
    {
        while (await Console.In.ReadLineAsync() is { } line)
        {
            try
            {
                if (JsonNode.Parse(line) is JsonObject request && request["method"]?.GetValue<string>() is { } method)
                {
                    if (method == "initialize" && request["params"] is JsonObject parameters)
                    {
                        var capabilities = parameters["capabilities"] as JsonObject;
                        if (capabilities is null) parameters["capabilities"] = capabilities = new JsonObject();
                        capabilities["experimentalApi"] = true;
                        if (capabilities["optOutNotificationMethods"] is JsonArray excluded)
                            for (var i = excluded.Count - 1; i >= 0; i--)
                                if (excluded[i]?.GetValue<string>() is "rawResponse/completed" or "rawResponseItem/completed")
                                { lock (hidden) hidden.Add(excluded[i]!.GetValue<string>()); excluded.RemoveAt(i); }
                        line = request.ToJsonString();
                        Record(new() { ["kind"] = "initialize-observation-enabled" });
                    }
                    else if (method == "thread/start" && request["params"] is JsonObject thread)
                    {
                        thread["experimentalRawEvents"] = true;
                        line = request.ToJsonString();
                        Record(new() { ["kind"] = "new-thread-raw-events-enabled" });
                    }
                    else if (method == "thread/resume") Record(new() { ["kind"] = "resume-raw-support-unverified" });
                    else if (method == "turn/start" && line.Contains("For a token timing diagnostic,", StringComparison.Ordinal))
                        Record(new() { ["kind"] = "diagnostic-turn-request", ["threadId"] = request["params"]?["threadId"]?.GetValue<string>() });
                }
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException)
            { /* Observation must never prevent forwarding an unsupported message shape. */ }
            await child.StandardInput.WriteLineAsync(line);
            await child.StandardInput.FlushAsync();
        }
        child.StandardInput.Close();
    }
    catch (IOException) { }
});
while (await child.StandardOutput.ReadLineAsync() is { } line)
{
    var received = Stopwatch.GetTimestamp();
    var suppress = false;
    try
    {
        if (JsonNode.Parse(line) is JsonObject message && message["method"]?.GetValue<string>() is { } method)
        {
            lock (hidden) suppress = hidden.Contains(method) && message["id"] is null;
            var p = message["params"] as JsonObject;
            var thread = p?["threadId"]?.GetValue<string>(); var turn = p?["turnId"]?.GetValue<string>();
            var key = thread + ":" + turn;
            if (thread is not null && turn is not null && method is "item/agentMessage/delta" or "item/reasoning/textDelta" or "item/reasoning/summaryTextDelta")
            {
                if (p?["delta"] is JsonValue delta && delta.TryGetValue<string>(out var text) && text.Length > 0 && !first.ContainsKey(key))
                { first[key] = received; Record(new() { ["kind"] = "first-exposed-delta", ["threadId"] = thread, ["turnId"] = turn, ["receivedTicks"] = received, ["method"] = method }); }
            }
            if (method == "rawResponse/completed")
            {
                var responseId = p?["responseId"]?.GetValue<string>();
                // Ignore duplicate completion BEFORE consuming the next response's first delta.
                if (thread is null || turn is null || responseId is null || !completed.Add(thread + ":" + responseId))
                { if (!suppress) { await Console.Out.WriteLineAsync(line); await Console.Out.FlushAsync(); } continue; }
                var begin = first.Remove(key, out var at) ? (long?)at : null;
                // An absent first delta stays unknown, especially for tool-only/hidden output.
                Record(new() { ["kind"] = "response", ["threadId"] = thread, ["turnId"] = turn,
                    ["responseId"] = responseId, ["outputTokens"] = p?["usage"]?["outputTokens"]?.DeepClone(),
                    ["firstExposedTicks"] = begin, ["completedTicks"] = received,
                    ["durationMs"] = begin.HasValue ? (received - begin.Value) * 1000d / Stopwatch.Frequency : null });
            }
            if (method == "turn/completed" && thread is not null)
            {
                var ended = p?["turn"]?["id"]?.GetValue<string>() ?? turn;
                if (ended is not null) first.Remove(thread + ":" + ended);
            }
        }
    }
    catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
    if (!suppress) { await Console.Out.WriteLineAsync(line); await Console.Out.FlushAsync(); }
}
await child.WaitForExitAsync(); await errorPump;
Record(new() { ["kind"] = "end", ["droppedRecords"] = Interlocked.Read(ref dropped), ["exitCode"] = child.ExitCode });
queue.Writer.Complete(); await writer;
return child.ExitCode;
