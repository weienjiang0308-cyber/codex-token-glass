using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;

namespace CodexTokenMetrics;

internal static class DesktopIpcTests
{
    private const string A = "11111111-1111-7111-8111-111111111111";
    private const string B = "22222222-2222-7222-8222-222222222222";
    private const string C = "33333333-3333-7333-8333-333333333333";
    internal static object Following(string thread, bool following = true, string client = "desktop-a", string host = "local", int version = 1) =>
        new { type = "broadcast", method = "thread-stream-following-changed", sourceClientId = client, version,
            @params = new { conversationId = thread, hostId = host, following } };
    internal static object Ack(string requestId) => new { type = "response", method = "initialize", requestId, resultType = "success", result = new { clientId = "observer" } };

    internal static void Run(Action<bool, string> check, string home)
    {
        long time = 1000;
        var state = new DesktopRouteState(() => time);
        void Feed(object value) => state.Accept(JsonSerializer.SerializeToUtf8Bytes(value, Program.Json));
        state.Begin("init"); Feed(Following(A)); time += 400;
        check(state.Read().ThreadId is null, "IPC never accepts a subscription before initialization");
        Feed(Ack("init")); Feed(Following(A));
        check(state.Read().ThreadId is null, "IPC bootstrap waits for the subscription burst to settle");
        time += 400; check(state.Read().ThreadId == A, "IPC selects one observed local conversation");
        Feed(Following(A)); time += 400; check(state.Read().LocalCount == 1, "duplicate following broadcasts are idempotent");
        Feed(Following(A, client: "desktop-b")); time += 400;
        check(state.Read().LocalCount == 1 && state.Read().ThreadId == A, "two clients following the same conversation are deduplicated");
        Feed(Following(B)); time += 400;
        check(state.Read().ThreadId is null && state.Read().LocalCount == 2, "multiple conversations never choose the latest event as foreground");
        Feed(Following(A, false)); Feed(Following(A, false, "desktop-b")); time += 400;
        check(state.Read().ThreadId == B, "unfollow removes only the matching subscription");
        Feed(Following(A, false)); check(state.Read().ThreadId == B, "late unfollow cannot clear the replacement conversation");
        Feed(Following(C, host: "ssh-demo")); time += 400;
        check(state.Read().RemoteCount == 1 && state.Read().ThreadId is null, "remote subscriptions cannot be mistaken for a local foreground thread");
        Feed(new { type = "broadcast", method = "client-status-changed", @params = new { clientId = "desktop-a", status = "disconnected" } });
        time += 400; check(state.Read().LocalCount == 0 && state.Read().RemoteCount == 0, "client disconnect clears all its subscriptions");
        Feed(Following(A)); Feed(new { type = "broadcast", method = "ipc-connection-reset", @params = new { } }); time += 400;
        check(state.Read().ThreadId is null, "router reset cannot retain a stale selected conversation");
        Feed(Following(A, version: 99)); time += 400;
        check(state.Read().ErrorCode == "unsupported-following-version" && state.Read().ThreadId is null, "unknown channel versions invalidate auto selection");
        state.Disconnect("test"); check(!state.Read().Connected && state.Read().LocalCount == 0, "transport disconnect removes route state");
        state.Begin("next"); Feed(Ack("next")); Feed(Following(A, client: "observer")); time += 400;
        check(state.Read().LocalCount == 0, "observer does not follow itself");
        var reply = state.Accept(JsonSerializer.SerializeToUtf8Bytes(new { type = "client-discovery-request", requestId = "discovery", request = new { method = "tool" } }));
        using var response = JsonDocument.Parse(JsonSerializer.Serialize(reply, Program.Json));
        check(!response.RootElement.GetProperty("response").GetProperty("canHandle").GetBoolean(), "observer explicitly declines request discovery");
        Feed(Following("../../invalid")); time += 400;
        check(state.Read().ThreadId is null, "malformed conversation identifiers do not reach file lookup");
        var observed = new DesktopRoute(true, true, 1, 0, A, null);
        var monitor = new MetricsMonitor(home, null, () => observed);
        check(monitor.Read().SelectionMode == "desktopObserved" && monitor.Read().SessionId == A, "auto mode resolves the broadcast ID in the selected profile");
        observed = new(true, true, 2, 0, A, null);
        var ambiguous = monitor.Read();
        check(ambiguous.SelectionMode == "desktopAmbiguous" && ambiguous.InputTokens is null && ambiguous.Speed is null,
            "ambiguous auto mode clears metrics even if a provider supplies an inconsistent ID");
        observed = DesktopRoute.Offline;
        check(monitor.Read().SelectionMode == "desktopDisconnected" && monitor.Read().TotalTokens is null, "disconnected auto mode does not silently fall back to recent activity");
        check(new MetricsMonitor(home, A, () => observed).Read().SelectionMode == "pinned", "manual pin is independent of desktop connectivity");
        observed = new(true, true, 0, 1, null, null);
        check(monitor.Read().SelectionMode == "desktopRemote" && monitor.Read().InputTokens is null, "remote-only route never reads a local fallback");
        observed = new(true, true, 1, 0, C, null);
        check(monitor.Read().InputTokens is null, "missing observed rollout never displays another session's usage");
        var archivedAgeLog = Path.Combine(home, "sessions", "database-selected.jsonl");
        File.SetLastWriteTimeUtc(archivedAgeLog, DateTime.UnixEpoch);
        for (var i = 0; i < 41; i++)
        {
            var id = Guid.NewGuid().ToString();
            File.WriteAllText(Path.Combine(home, "sessions", $"recent-{i}.jsonl"),
                JsonSerializer.Serialize(new { type = "session_meta", payload = new { id } }) + "\n");
        }
        observed = new(true, true, 2, 0, null, null, [A, B]);
        var candidates = new MetricsMonitor(home, null, () => observed).Read();
        using (var choices = JsonDocument.Parse(JsonSerializer.Serialize(candidates, Program.Json)))
            check(choices.RootElement.GetProperty("sessions").EnumerateArray().Any(x =>
                x.GetProperty("id").GetString() == A && x.GetProperty("title").GetString() == "Synthetic database title"),
                "older Desktop candidates remain in the chooser beyond the recent-file limit");
        Frames(check).GetAwaiter().GetResult();
        Transport(check).GetAwaiter().GetResult();
        check(DesktopIpcClient.Endpoints(Path.Combine(home, "custom-profile")).Count == 1, "custom profiles do not use a global legacy endpoint");
    }

    private static async Task Frames(Action<bool, string> check)
    {
        var first = IpcFrames.Encode(Following(A)); var second = IpcFrames.Encode(Following(B));
        using var fragments = new FragmentedStream([.. first, .. second]);
        using var a = JsonDocument.Parse((await IpcFrames.Read(fragments, default))!);
        using var b = JsonDocument.Parse((await IpcFrames.Read(fragments, default))!);
        check(a.RootElement.GetProperty("params").GetProperty("conversationId").GetString() == A
            && b.RootElement.GetProperty("params").GetProperty("conversationId").GetString() == B,
            "length-prefixed transport handles fragmented headers and coalesced frames");
        check(await IpcFrames.Read(fragments, default) is null, "clean frame EOF is recognized");
        var large = new byte[IpcFrames.MaximumJsonBytes + 1 + 4 + first.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(large, IpcFrames.MaximumJsonBytes + 1);
        first.CopyTo(large, IpcFrames.MaximumJsonBytes + 5);
        using var body = new MemoryStream(large);
        check((await IpcFrames.Read(body, default))!.Length == 0 && (await IpcFrames.Read(body, default))!.Length > 0,
            "large unrelated history is drained without losing the next metadata frame");
        foreach (var bytes in new[] { new byte[4], new byte[] { 1, 0 }, new byte[] { 3, 0, 0, 0, 123 } })
        {
            var rejected = false;
            try { using var bad = new MemoryStream(bytes); await IpcFrames.Read(bad, default); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { rejected = true; }
            check(rejected, "invalid or truncated frame is rejected");
        }
    }

    private static async Task Transport(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ctg-ipc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var endpoint = Path.Combine(temp, "s");
        try
        {
            using var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            server.Bind(new UnixDomainSocketEndPoint(endpoint)); server.Listen(2);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(endpoint, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await using var client = new DesktopIpcClient(() => [endpoint]); client.Start();
            using (var peer = await server.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(5)))
            using (var stream = new NetworkStream(peer, false))
            {
                using var init = JsonDocument.Parse((await IpcFrames.Read(stream, default).WaitAsync(TimeSpan.FromSeconds(5)))!);
                var requestId = init.RootElement.GetProperty("requestId").GetString()!;
                check(init.RootElement.GetProperty("method").GetString() == "initialize", "real Unix socket sends the observer handshake only");
                var frames = IpcFrames.Encode(Ack(requestId)).Concat(IpcFrames.Encode(Following(A))).ToArray();
                for (var i = 0; i < frames.Length; i += 3) await stream.WriteAsync(frames.AsMemory(i, Math.Min(3, frames.Length - i)));
                await Until(() => client.State.Read().ThreadId == A);
                check(client.State.Read().Connected, "real Unix socket receives fragmented replay and validates the local peer");
            }
            await Until(() => !client.State.Read().Connected);
            check(client.State.Read().ThreadId is null, "real socket EOF immediately clears the route");
            using var peer2 = await server.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var stream2 = new NetworkStream(peer2, false);
            using var init2 = JsonDocument.Parse((await IpcFrames.Read(stream2, default).WaitAsync(TimeSpan.FromSeconds(5)))!);
            await stream2.WriteAsync(IpcFrames.Encode(Ack(init2.RootElement.GetProperty("requestId").GetString()!)));
            await stream2.WriteAsync(IpcFrames.Encode(Following(B)));
            await Until(() => client.State.Read().ThreadId == B);
            check(client.State.Read().LocalCount == 1, "reconnect does not restore subscriptions from the previous connection");
            await stream2.WriteAsync(new byte[4]);
            await Until(() => !client.State.Read().Connected);
            check(client.State.Read().ThreadId is null, "invalid wire frame disconnects and clears the selected conversation");
            using var peer3 = await server.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var stream3 = new NetworkStream(peer3, false);
            using var init3 = JsonDocument.Parse((await IpcFrames.Read(stream3, default).WaitAsync(TimeSpan.FromSeconds(5)))!);
            await stream3.WriteAsync(IpcFrames.Encode(Ack(init3.RootElement.GetProperty("requestId").GetString()!)));
            await stream3.WriteAsync(IpcFrames.Encode(Following(C)));
            await Until(() => client.State.Read().ThreadId == C);
            check(client.State.Read().LocalCount == 1, "invalid frame recovery reconnects without stale subscriptions");
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            check(!client.State.Read().Connected, "disposing cancels a blocked socket read without orphaning the monitor");
        }
        finally
        {
            var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(temp).StartsWith(root, StringComparison.Ordinal) || !Path.GetFileName(temp).StartsWith("ctg-ipc-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing IPC test cleanup outside its generated directory");
            Directory.Delete(temp, true);
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(15, stop.Token);
    }
    private sealed class FragmentedStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
    }
}
