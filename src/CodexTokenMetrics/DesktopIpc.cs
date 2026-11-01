using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CodexTokenMetrics;

internal sealed record DesktopRoute(bool Connected, bool Settled, int LocalCount, int RemoteCount, string? ThreadId, string? ErrorCode, string[]? LocalThreads = null)
{
    public static readonly DesktopRoute Offline = new(false, false, 0, 0, null, null);
}

/// <summary>Observed subscriptions, not a claim about OS window focus.</summary>
internal sealed class DesktopRouteState(Func<long>? clock = null)
{
    private readonly object _sync = new();
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private readonly HashSet<(string Client, string Host, string Thread)> _following = [];
    private string? _requestId, _clientId, _error;
    private bool _connected;
    private long _changed;
    internal const int SettleMs = 300;

    public void Begin(string requestId)
    {
        lock (_sync) { _requestId = requestId; _clientId = null; _connected = false; _following.Clear(); _error = null; _changed = _clock(); }
    }
    public void Disconnect(string? error)
    {
        lock (_sync) { _connected = false; _following.Clear(); _clientId = null; _error = error; _changed = _clock(); }
    }
    public DesktopRoute Read()
    {
        lock (_sync)
        {
            var local = _following.Where(x => x.Host == "local").Select(x => x.Thread).Distinct().Order(StringComparer.Ordinal).ToArray();
            var remote = _following.Where(x => x.Host != "local").Select(x => (x.Host, x.Thread)).Distinct().Count();
            var settled = _connected && _error is null && _clock() - _changed >= SettleMs;
            return new(_connected, settled, local.Length, remote,
                settled && local.Length == 1 && remote == 0 ? local[0] : null, _error, local);
        }
    }

    internal static string? Text(JsonElement element, string key) => element.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 256 } text ? text : null;

    // The observer never offers to handle tools/requests. A negative discovery
    // answer prevents it from delaying another client's request routing.
    public object? Accept(ReadOnlyMemory<byte> payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        var type = Text(root, "type"); var requestId = Text(root, "requestId");
        if (type == "client-discovery-request" && requestId is not null)
            return new { type = "client-discovery-response", requestId, response = new { canHandle = false } };
        if (type == "request" && requestId is not null)
            return new { type = "response", requestId, resultType = "error", error = "no-handler-for-request" };
        lock (_sync)
        {
            if (type == "response" && requestId == _requestId && Text(root, "method") == "initialize")
            {
                if (Text(root, "resultType") == "success" && root.TryGetProperty("result", out var result)
                    && result.ValueKind == JsonValueKind.Object && Text(result, "clientId") is string id)
                { _clientId = id; _connected = true; _error = null; _changed = _clock(); }
                else { _connected = false; _error = "initialize-rejected"; }
                return null;
            }
            if (!_connected || type != "broadcast" || !root.TryGetProperty("params", out var parameters)
                || parameters.ValueKind != JsonValueKind.Object) return null;
            var method = Text(root, "method");
            if (method == "ipc-connection-reset") { _following.Clear(); _changed = _clock(); return null; }
            if (method == "client-status-changed" && Text(parameters, "status") == "disconnected"
                && Text(parameters, "clientId") is string disconnected)
            {
                if (_following.RemoveWhere(x => x.Client == disconnected) > 0) _changed = _clock();
                return null;
            }
            if (method != "thread-stream-following-changed") return null;
            // This channel is version 1 in the inspected Desktop build. Version 0
            // has the same fields on older clients; unknown versions invalidate
            // the selection instead of silently holding a stale current thread.
            if (root.TryGetProperty("version", out var version)
                && (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number is < 0 or > 1))
            { _following.Clear(); _error = "unsupported-following-version"; _changed = _clock(); return null; }
            var client = Text(root, "sourceClientId"); var host = Text(parameters, "hostId");
            var thread = Text(parameters, "conversationId");
            if (client is null || client == _clientId || host is null || thread is not { Length: 36 }
                || !Guid.TryParseExact(thread, "D", out var threadGuid) || !parameters.TryGetProperty("following", out var following)
                || following.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            var key = (client, host, threadGuid.ToString("D"));
            var changed = following.GetBoolean() ? _following.Add(key) : _following.Remove(key);
            if (changed) _changed = _clock();
        }
        return null;
    }
}

internal static class IpcFrames
{
    internal const int MaximumJsonBytes = 1024 * 1024;
    internal const uint MaximumWireBytes = 256 * 1024 * 1024;
    public static byte[] Encode(object message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Program.Json);
        var frame = new byte[body.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length); body.CopyTo(frame, 4);
        return frame;
    }
    public static async Task<byte[]?> Read(Stream stream, CancellationToken stop)
    {
        var header = new byte[4];
        var read = await stream.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false, stop);
        if (read == 0) return null;
        if (read != 4) throw new EndOfStreamException();
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (size is 0 or > MaximumWireBytes) throw new InvalidDataException("invalid-frame-size");
        if (size > MaximumJsonBytes)
        {
            // Legitimate unrelated broadcasts can contain large history snapshots.
            // Drain them in bounded chunks without parsing or retaining their text.
            var scratch = new byte[64 * 1024]; var remaining = size;
            while (remaining > 0)
            {
                var take = (int)Math.Min(remaining, (uint)scratch.Length);
                await stream.ReadExactlyAsync(scratch.AsMemory(0, take), stop); remaining -= (uint)take;
            }
            return [];
        }
        var payload = new byte[(int)size]; await stream.ReadExactlyAsync(payload, stop); return payload;
    }
}

internal sealed class DesktopIpcClient : IAsyncDisposable
{
    private readonly Func<IReadOnlyList<string>> _endpoints;
    private readonly CancellationTokenSource _stop = new();
    private Task? _runner;
    private int _disposed;
    public DesktopRouteState State { get; } = new();
    public DesktopIpcClient(Func<IReadOnlyList<string>> endpoints) => _endpoints = endpoints;
    public void Start() { ObjectDisposedException.ThrowIf(_disposed != 0, this); if (_runner is null) _runner = Run(); }

    internal static IReadOnlyList<string> Endpoints(string home)
    {
        var primary = Path.Combine(home, "ipc", "ipc.sock");
        if (!OperatingSystem.IsMacOS()) return [primary];
        var defaultHome = Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        if (Path.GetFullPath(home).TrimEnd('/') != Path.GetFullPath(defaultHome).TrimEnd('/')) return [primary];
        var uid = geteuid();
        return [primary, Path.Combine(Path.GetTempPath(), "codex-ipc", uid == 0 ? "ipc.sock" : $"ipc-{uid}.sock")];
    }

    private async Task Run()
    {
        var stop = _stop.Token;
        while (!stop.IsCancellationRequested)
        {
            var connected = false;
            foreach (var endpoint in _endpoints())
            {
                if (stop.IsCancellationRequested) break;
                try
                {
                    ValidatePath(endpoint);
                    using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    using (var connect = CancellationTokenSource.CreateLinkedTokenSource(stop))
                    { connect.CancelAfter(TimeSpan.FromSeconds(2)); await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), connect.Token); }
                    ValidatePeer(socket);
                    using var stream = new NetworkStream(socket, ownsSocket: false);
                    var requestId = Guid.NewGuid().ToString(); State.Begin(requestId);
                    await stream.WriteAsync(IpcFrames.Encode(new { type = "request", requestId,
                        sourceClientId = "codex-token-glass-observer", version = 0, method = "initialize",
                        @params = new { clientType = "codex-token-glass-observer" } }), stop);
                    // The router acknowledges initialize; the other Desktop clients
                    // replay following metadata on client-status-changed/connected.
                    connected = true;
                    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    handshake.CancelAfter(TimeSpan.FromSeconds(3));
                    while (!stop.IsCancellationRequested)
                    {
                        var payload = await IpcFrames.Read(stream, State.Read().Connected ? stop : handshake.Token);
                        if (payload is null) break;
                        if (payload.Length == 0) continue;
                        if (State.Accept(payload) is { } reply) await stream.WriteAsync(IpcFrames.Encode(reply), stop);
                        if (State.Read().Connected) handshake.CancelAfter(Timeout.Infinite);
                    }
                    State.Disconnect(null);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or UnauthorizedAccessException
                    or OperationCanceledException or ArgumentException or JsonException)
                { State.Disconnect(ex is UnauthorizedAccessException ? "untrusted-local-peer" : "connection-unavailable"); }
                // A live endpoint was found. Retry it after a disconnect rather than
                // combining subscriptions from a second, older router.
                if (connected) break;
            }
            try { await Task.Delay(1000, stop); } catch (OperationCanceledException) { break; }
        }
        State.Disconnect(null);
    }

    private static void ValidatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An IPC endpoint must be an absolute local path.");
        if (!OperatingSystem.IsMacOS()) return;
        var file = new FileInfo(path); var parent = file.Directory;
        if (file.LinkTarget is not null || parent is null || parent.LinkTarget is not null)
            throw new UnauthorizedAccessException();
        var writable = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        if ((File.GetUnixFileMode(path) & writable) != 0 || (File.GetUnixFileMode(parent.FullName) & writable) != 0)
            throw new UnauthorizedAccessException();
    }
    private static void ValidatePeer(Socket socket)
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (getpeereid(socket.Handle.ToInt32(), out var uid, out _) != 0 || uid != geteuid())
            throw new UnauthorizedAccessException();
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); if (_runner is not null) await _runner; _stop.Dispose();
    }
    [DllImport("/usr/lib/libSystem.B.dylib")] private static extern uint geteuid();
    [DllImport("/usr/lib/libSystem.B.dylib")] private static extern int getpeereid(int fd, out uint uid, out uint gid);
}
