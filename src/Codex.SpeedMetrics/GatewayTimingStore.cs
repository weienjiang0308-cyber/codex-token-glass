using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Codex.SpeedMetrics;

public readonly record struct GatewayResponseTiming(
    string ResponseId,
    long TotalMs,
    long? TtftMs,
    long? FirstReasoningMs,
    long? FirstTextMs,
    long? FlowMs,
    long? OutputTokens,
    long? ReasoningTokens,
    bool ReasoningHidden,
    int Status,
    string? Provider = null,
    string? RouteId = null,
    string? RequestedModel = null,
    string? ServedModel = null,
    string? Agent = null,
    string? Source = null,
    long? InputTokens = null,
    long? CacheReadTokens = null,
    long? CacheWriteTokens = null,
    bool CacheUnknown = false,
    bool IsAmbiguous = false)
{
    public long? FullInputTokens => InputTokens is long input
        ? input + (CacheReadTokens ?? 0) + (CacheWriteTokens ?? 0)
        : null;

    public string? EffectiveRoute => !string.IsNullOrWhiteSpace(Provider)
        ? Provider
        : !string.IsNullOrWhiteSpace(RouteId) ? RouteId : null;

    public string? EffectiveServedModel => !string.IsNullOrWhiteSpace(ServedModel)
        ? ServedModel
        : !string.IsNullOrWhiteSpace(RequestedModel) ? RequestedModel : null;
}

public interface IGatewayTimingLookup
{
    long Version { get; }
    bool TryGet(string responseId, out GatewayResponseTiming timing);
    bool TryGetInspect(string responseId, out GatewayResponseTiming timing) => TryGet(responseId, out timing);
    void Refresh();
}

public sealed class MagpieGatewayTimingStore : IGatewayTimingLookup
{
    private const int MaximumEntries = 32768;
    private const int MaximumLineLength = 256 * 1024;
    private const long RefreshIntervalMs = 250;

    private readonly object _gate = new();
    private readonly string? _explicitPath;
    private readonly Dictionary<string, GatewayResponseTiming> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly StringBuilder _pending = new();
    private Decoder _decoder = Encoding.UTF8.GetDecoder();

    private string? _resolvedPath;
    private long _offset;
    private bool _initialized;
    private DateTime _lastWriteUtc;
    private DateTime _createdUtc;
    private long _lastProbeAtMs;
    private long _lastRefreshAtMs;
    private long _version;

    public MagpieGatewayTimingStore(string? usagePath = null)
    {
        _explicitPath = string.IsNullOrWhiteSpace(usagePath) ? null : usagePath.Trim();
        _resolvedPath = _explicitPath;
    }

    public long Version => Volatile.Read(ref _version);

    public string? ActivePath
    {
        get
        {
            lock (_gate) return _resolvedPath;
        }
    }

    public static MagpieGatewayTimingStore? TryCreateDefault()
    {
        var overridePath = Environment.GetEnvironmentVariable("CODEX_GLASS_GATEWAY_USAGE");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var trimmed = overridePath.Trim();
            if (trimmed.Equals("off", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("0", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return new MagpieGatewayTimingStore(trimmed);
        }

        return new MagpieGatewayTimingStore();
    }

    public bool TryGet(string responseId, out GatewayResponseTiming timing)
    {
        if (string.IsNullOrWhiteSpace(responseId))
        {
            timing = default;
            return false;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(responseId, out timing) && !timing.IsAmbiguous)
            {
                return true;
            }

            timing = default;
            return false;
        }
    }

    public bool TryGetInspect(string responseId, out GatewayResponseTiming timing)
    {
        if (string.IsNullOrWhiteSpace(responseId))
        {
            timing = default;
            return false;
        }

        lock (_gate)
        {
            return _entries.TryGetValue(responseId, out timing);
        }
    }

    public void Refresh()
    {
        var nowMs = Environment.TickCount64;
        lock (_gate)
        {
            if (_initialized && nowMs - _lastRefreshAtMs < RefreshIntervalMs)
            {
                return;
            }
            _lastRefreshAtMs = nowMs;

            var path = ResolvePathLocked(nowMs);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var writtenUtc = File.GetLastWriteTimeUtc(path);
                var createdUtc = File.GetCreationTimeUtc(path);
                var sameLengthRewrite = _initialized && stream.Length == _offset && writtenUtc != _lastWriteUtc;
                if (_initialized && stream.Length == _offset && writtenUtc == _lastWriteUtc && createdUtc == _createdUtc)
                {
                    return;
                }

                if (!_initialized || stream.Length < _offset || sameLengthRewrite || createdUtc != _createdUtc)
                {
                    _initialized = true;
                    _entries.Clear();
                    _order.Clear();
                    _pending.Clear();
                    _decoder = Encoding.UTF8.GetDecoder();
                    _offset = 0;
                }

                _lastWriteUtc = writtenUtc;
                _createdUtc = createdUtc;
                stream.Position = _offset;

                var byteBuffer = new byte[64 * 1024];
                var charBuffer = new char[Encoding.UTF8.GetMaxCharCount(byteBuffer.Length)];
                var changed = false;
                int bytesRead;
                while ((bytesRead = stream.Read(byteBuffer, 0, byteBuffer.Length)) > 0)
                {
                    _offset += bytesRead;
                    var charCount = _decoder.GetChars(byteBuffer, 0, bytesRead, charBuffer, 0, false);
                    for (var index = 0; index < charCount; index++)
                    {
                        var current = charBuffer[index];
                        if (current == '\n')
                        {
                            if (_pending.Length > 0 && _pending.Length <= MaximumLineLength)
                            {
                                if (ObserveLineLocked(_pending.ToString()))
                                {
                                    changed = true;
                                }
                            }
                            _pending.Clear();
                            if (_pending.Capacity > 256 * 1024) _pending.Capacity = 32 * 1024;
                        }
                        else if (_pending.Length <= MaximumLineLength)
                        {
                            _pending.Append(current);
                        }
                    }
                }

                if (changed)
                {
                    Interlocked.Increment(ref _version);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private string? ResolvePathLocked(long nowMs)
    {
        if (_explicitPath is not null) return _explicitPath;
        if (_resolvedPath is not null && File.Exists(_resolvedPath)) return _resolvedPath;
        if (nowMs - _lastProbeAtMs < 5000 && _lastProbeAtMs != 0) return _resolvedPath;

        _lastProbeAtMs = nowMs;
        _resolvedPath = DiscoverDefaultUsagePath();
        return _resolvedPath;
    }

    private static string? DiscoverDefaultUsagePath()
    {
        var candidates = new List<string>();
        try
        {
            foreach (var process in Process.GetProcessesByName("magpie"))
            {
                try
                {
                    var exePath = process.MainModule?.FileName;
                    var dir = string.IsNullOrWhiteSpace(exePath) ? null : Path.GetDirectoryName(exePath);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        candidates.Add(Path.Combine(dir, "data", "usage.jsonl"));
                    }
                }
                catch { }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch { }

        if (HasLocalGatewayConfiguredInCodexConfig())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                candidates.Add(Path.Combine(localAppData, "Magpie", "data", "usage.jsonl"));
            }
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                candidates.Add(Path.Combine(userProfile, ".magpie", "data", "usage.jsonl"));
                candidates.Add(Path.Combine(userProfile, "Magpie", "data", "usage.jsonl"));
            }
            candidates.Add(@"D:\Magpie\data\usage.jsonl");
            candidates.Add(@"C:\Magpie\data\usage.jsonl");
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        return null;
    }

    private static bool HasLocalGatewayConfiguredInCodexConfig()
    {
        try
        {
            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(codexHome))
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(userProfile)) return false;
                codexHome = Path.Combine(userProfile, ".codex");
            }
            var configPath = Path.Combine(codexHome, "config.toml");
            if (!File.Exists(configPath)) return false;
            foreach (var rawLine in File.ReadLines(configPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                if ((line.StartsWith("openai_base_url", StringComparison.OrdinalIgnoreCase)
                     || line.StartsWith("base_url", StringComparison.OrdinalIgnoreCase))
                    && (line.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("localhost", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    private bool ObserveLineLocked(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var responseId = ReadString(root, "response_id");
            if (string.IsNullOrWhiteSpace(responseId)) return false;

            var source = ReadString(root, "source");
            if (IsIgnoredSource(source)) return false;

            var agent = ReadString(root, "agent");
            if (!string.IsNullOrWhiteSpace(agent)
                && !agent.Equals("codex", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var hasStatus = root.TryGetProperty("status", out var statusElement);
            var status = hasStatus
                ? (statusElement.ValueKind == JsonValueKind.Number && statusElement.TryGetInt64(out var parsedStatus) ? (int)parsedStatus : -1)
                : 200;
            if (status != 200) return false;

            var totalMs = ReadOptionalLong(root, "ms") ?? 0;
            if (totalMs <= 0) return false;

            var timing = new GatewayResponseTiming(
                responseId,
                totalMs,
                ReadOptionalLong(root, "ttft_ms"),
                ReadOptionalLong(root, "first_reasoning_ms"),
                ReadOptionalLong(root, "first_text_ms"),
                ReadOptionalLong(root, "flow_ms"),
                ReadOptionalLong(root, "out"),
                ReadOptionalLong(root, "reasoning"),
                ReadBool(root, "reasoning_hidden"),
                status,
                Provider: NormalizeOptional(ReadString(root, "provider")),
                RouteId: NormalizeOptional(ReadString(root, "route_id")),
                RequestedModel: NormalizeOptional(ReadString(root, "model")),
                ServedModel: NormalizeOptional(ReadString(root, "served")),
                Agent: NormalizeOptional(agent),
                Source: NormalizeOptional(source),
                InputTokens: ReadOptionalLong(root, "in"),
                CacheReadTokens: ReadOptionalLong(root, "cache_read"),
                CacheWriteTokens: ReadOptionalLong(root, "cache_write"),
                CacheUnknown: ReadBool(root, "cache_unknown"),
                IsAmbiguous: false);

            if (_entries.TryGetValue(responseId, out var existing))
            {
                if (!IsEquivalentRecord(existing, timing))
                {
                    _entries[responseId] = existing with { IsAmbiguous = true };
                    return true;
                }
                return false;
            }
            else
            {
                _order.Enqueue(responseId);
                _entries[responseId] = timing;
            }

            while (_order.Count > MaximumEntries)
            {
                var evicted = _order.Dequeue();
                _entries.Remove(evicted);
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsIgnoredSource(string? source) =>
        !string.IsNullOrWhiteSpace(source)
        && (source.Equals("probe", StringComparison.OrdinalIgnoreCase)
            || source.Equals("search", StringComparison.OrdinalIgnoreCase)
            || source.Equals("plugin", StringComparison.OrdinalIgnoreCase)
            || source.Equals("keepalive", StringComparison.OrdinalIgnoreCase));

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsEquivalentRecord(GatewayResponseTiming left, GatewayResponseTiming right) =>
        !left.IsAmbiguous
        && left.TotalMs == right.TotalMs
        && left.OutputTokens == right.OutputTokens
        && left.InputTokens == right.InputTokens
        && left.CacheReadTokens == right.CacheReadTokens
        && left.CacheWriteTokens == right.CacheWriteTokens
        && left.CacheUnknown == right.CacheUnknown
        && string.Equals(left.EffectiveRoute, right.EffectiveRoute, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.EffectiveServedModel, right.EffectiveServedModel, StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadOptionalLong(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var parsed)
        && parsed >= 0
            ? parsed
            : null;

    private static bool ReadBool(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;
}
