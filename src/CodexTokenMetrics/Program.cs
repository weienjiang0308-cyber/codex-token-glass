using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexStatusbar;

namespace CodexTokenMetrics;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.Strict
    };

    static async Task<int> Main(string[] args)
    {
        // This resolver belongs only to the portable assembly. The existing Windows
        // project's SQLite P/Invokes and behavior remain byte-for-byte unchanged.
        NativeLibrary.SetDllImportResolver(typeof(StateDatabase).Assembly, (name, _, _) =>
            name == "winsqlite3.dll" && !OperatingSystem.IsWindows()
                ? NativeLibrary.Load(OperatingSystem.IsMacOS() ? "/usr/lib/libsqlite3.dylib" : "libsqlite3.so.0")
                : IntPtr.Zero);
        try
        {
            string? Value(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            if (args.Contains("--self-test")) return MetricsTests.Run(Value("--self-test") ?? "fixtures");
            var home = Value("--home") ?? Environment.GetEnvironmentVariable("CODEX_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            home = Path.GetFullPath(home);
            var forcedThread = Value("--thread");
            var useDesktop = OperatingSystem.IsMacOS() && forcedThread is null && !args.Contains("--recent-activity");
            await using var desktop = new DesktopIpcClient(() => DesktopIpcClient.Endpoints(home));
            if (useDesktop) desktop.Start();
            var monitor = new MetricsMonitor(home, forcedThread, useDesktop ? desktop.State.Read : null);
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            do
            {
                Console.WriteLine(JsonSerializer.Serialize(monitor.Read(), Json));
                if (!args.Contains("--watch")) break;
                await Task.Delay(1000, stop.Token);
            } while (!stop.IsCancellationRequested);
            return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Keep stdout machine-readable and avoid dumping local paths or log contents.
            Console.WriteLine(JsonSerializer.Serialize(new { status = "unavailable", error = ex.GetType().Name }, Json));
            return 1;
        }
    }
}

internal sealed record SessionChoice(string Id, string Title, string Path, DateTime ModifiedAt);

internal sealed record MetricsSnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public string Status { get; init; } = "waiting";
    public string SelectionMode { get; init; } = "recentActivity";
    public string? SessionId { get; init; }
    public string Title { get; init; } = "";
    public DateTime? UpdatedAt { get; init; }
    public long? InputTokens { get; init; }
    public long? CachedInputTokens { get; init; }
    public long? UncachedInputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? ReasoningTokens { get; init; }
    public long? TotalTokens { get; init; }
    public double? CacheHitPercent { get; init; }
    public long? ContextUsedTokens { get; init; }
    public long? ContextWindowTokens { get; init; }
    public double? Speed { get; init; }
    public double? AverageSpeed { get; init; }
    public double? PeakSpeed { get; init; }
    public long? TaskMs { get; init; }
    public long? GenerationMs { get; init; }
    public bool Streaming { get; init; }
    public bool Held { get; init; }
    public string TimingSource { get; init; } = "logModelIntervals";
    public string Carrier { get; init; } = "none";
    public string ThemeKind { get; init; } = "unknown";
    public bool DesktopConnected { get; init; }
    public int DesktopLocalCount { get; init; }
    public int DesktopRemoteCount { get; init; }
    public string? RoutingError { get; init; }
    public string[] Warnings { get; init; } = [];
    // Paths never cross the UI bridge; they are only used by this process.
    public object[] Sessions { get; init; } = [];
}

internal sealed class MetricsMonitor(string home, string? selectedThread, Func<DesktopRoute>? desktopRoute = null)
{
    private readonly string _root = Path.Combine(home, "sessions");
    private readonly string _database = Path.Combine(home, "state_5.sqlite");
    private List<SessionChoice> _sessions = [];
    private DateTime _nextScan;
    private DateTime _nextMissingScan;
    private RolloutReader? _reader;
    private readonly CodexThemeSource _theme = new(Path.Combine(home, "config.toml"));

    internal static bool IsWithin(string root, string path)
    {
        // Real symlink resolution is also needed on macOS: do not follow a rollout
        // symlink out of the chosen sessions directory.
        var full = Path.GetFullPath(path);
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
        var item = new FileInfo(full);
        if (item.LinkTarget is not null) return false;
        for (var parent = item.Directory; parent is not null && parent.FullName.Length >= prefix.Length; parent = parent.Parent)
            if (parent.LinkTarget is not null) return false;
        return true;
    }

    public MetricsSnapshot Read()
    {
        var now = DateTime.UtcNow;
        if (now >= _nextScan)
        {
            _sessions = Scan();
            _nextScan = now.AddSeconds(15);
        }
        var desktop = selectedThread is null ? desktopRoute?.Invoke() : null;
        var mode = selectedThread is not null ? "pinned" : "recentActivity";
        var target = selectedThread;
        if (desktop is not null)
        {
            mode = !desktop.Connected ? "desktopDisconnected" : !desktop.Settled ? "desktopWaiting"
                : desktop.LocalCount + desktop.RemoteCount > 1 ? "desktopAmbiguous"
                : desktop.RemoteCount > 0 ? "desktopRemote" : desktop.LocalCount == 1 && desktop.ThreadId is not null ? "desktopObserved" : "desktopWaiting";
            target = mode == "desktopObserved" ? desktop.ThreadId : null;
        }
        var canSelect = desktop is null || target is not null;
        var recent = !canSelect ? null : target is null ? StateDatabase.TryReadMostRecentThread(_database)
            : StateDatabase.TryReadThread(_database, target);
        SessionChoice? choice = null;
        if (recent?.RolloutPath is string raw && Path.IsPathFullyQualified(raw) && IsWithin(_root, raw) && File.Exists(raw))
            choice = new(recent.Id, recent.Title ?? recent.Id, raw, File.GetLastWriteTimeUtc(raw));
        if (canSelect) choice ??= target is null ? _sessions.FirstOrDefault() : _sessions.FirstOrDefault(s => s.Id == target);
        if (canSelect && target is not null && choice is null && now >= _nextMissingScan)
        {
            _sessions = Scan(); _nextMissingScan = now.AddSeconds(2);
            choice = _sessions.FirstOrDefault(s => s.Id == target);
        }
        var choices = _sessions;
        if (choice is not null && !choices.Any(s => s.Id == choice.Id)) choices = [choice, .. choices];
        var basis = new MetricsSnapshot
        {
            ThemeKind = _theme.Current.ToString().ToLowerInvariant(),
            SelectionMode = mode,
            DesktopConnected = desktop?.Connected ?? false,
            DesktopLocalCount = desktop?.LocalCount ?? 0, DesktopRemoteCount = desktop?.RemoteCount ?? 0,
            RoutingError = desktop?.ErrorCode,
            SessionId = choice?.Id,
            Title = choice?.Title ?? "",
            Sessions = choices.Take(40).Select(s => (object)new { s.Id, s.Title }).ToArray()
        };
        if (choice is null) { _reader = null; return basis; }
        if (_reader?.Path != choice.Path) { _reader = new(choice.Path); _reader.Initialize(); }
        else if (_reader.TryUpdate() == RolloutUpdateResult.NeedsReinitialize) _reader.Initialize();
        var state = _reader.State;
        if (state.SessionMetaId is string actualId && actualId != choice.Id)
        {
            // A rotated path or stale database entry must not make a pinned
            // conversation display another thread's usage for even one frame.
            _reader = null; _nextScan = DateTime.MinValue;
            return basis with { Status = "sessionChanged", SessionId = null, Warnings = ["session-identity-changed"] };
        }
        if (!state.HasCumulativeUsage) return basis with { Status = "noUsage", Warnings = state.Warnings.ToArray() };
        return basis with
        {
            Status = "ready", UpdatedAt = File.GetLastWriteTimeUtc(choice.Path),
            InputTokens = state.InputTokens, CachedInputTokens = state.CachedInputTokens,
            UncachedInputTokens = state.UncachedInputTokens, OutputTokens = state.OutputTokens,
            ReasoningTokens = state.ReasoningOutputTokens, TotalTokens = state.TotalTokens,
            CacheHitPercent = MetricFormat.CacheHitPercent(state.InputTokens, state.CachedInputTokens),
            ContextUsedTokens = state.ContextUsedTokens > 0 ? state.ContextUsedTokens : null,
            ContextWindowTokens = state.ContextWindowTokens > 0 ? state.ContextWindowTokens : null,
            Speed = state.Tps.DisplayTps, AverageSpeed = state.Tps.AverageTps, PeakSpeed = state.Tps.PeakTps,
            TaskMs = state.TurnElapsedMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            GenerationMs = state.Tps.TurnItemsComplete && state.Tps.TurnGenerationMs > 0 ? state.Tps.TurnGenerationMs : null,
            Streaming = state.Streaming, Held = state.Tps.IsHeld, Carrier = state.Carrier,
            Warnings = state.Warnings.ToArray()
        };
    }

    private List<SessionChoice> Scan()
    {
        if (!Directory.Exists(_root)) return [];
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var files = new DirectoryInfo(_root).EnumerateFiles("*.jsonl", options).OrderByDescending(f => f.LastWriteTimeUtc).Take(40);
        var result = new List<SessionChoice>();
        foreach (var file in files)
        {
            try
            {
                if (!IsWithin(_root, file.FullName)) continue;
                // Bounded metadata read; never read session bodies to derive titles.
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bytes = new byte[64 * 1024]; var count = stream.Read(bytes); var end = Array.IndexOf(bytes, (byte)'\n', 0, count);
                using var doc = JsonDocument.Parse(bytes.AsMemory(0, end >= 0 ? end : count));
                if (!doc.RootElement.TryGetProperty("type", out var type) || type.GetString() != "session_meta") continue;
                var payload = doc.RootElement.GetProperty("payload");
                var id = payload.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                var row = StateDatabase.TryReadThread(_database, id);
                // Archived/CLI sessions may still appear in the chooser. The mode
                // never claims it identifies the foreground Desktop conversation.
                result.Add(new(id, row?.Title ?? id, file.FullName, file.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        return result;
    }
}
