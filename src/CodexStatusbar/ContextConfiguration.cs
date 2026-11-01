using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexStatusbar;

internal sealed record ContextValues(string Model, long Window, long CompactAt, long CatalogDefault, long? CatalogMaximum, int EffectivePercent = 95);

internal static class ContextConfiguration
{
    public const long SliderMaximum = 1_050_000;
    public static ContextValues Read(string? configPath = null)
    {
        var path = configPath ?? CodexThemeSource.DefaultConfigPath;
        var text = File.ReadAllText(path);
        var table = Regex.Match(text, @"(?m)^\s*\[");
        var root = table.Success ? text[..table.Index] : text;
        var model = Regex.Match(root, "(?m)^model\\s*=\\s*[\"']([^\"']+)").Groups[1].Value;
        long? Value(string key) => ReadRootValue(text, key);
        long modelDefault = 500_000;
        long? modelMaximum = null;
        var effectivePercent = 95;
        if (configPath is null)
        {
            try
            {
                var source = Regex.Match(root, "(?m)^model_catalog_json\\s*=\\s*(['\"])(.+?)\\1");
                var catalogPath = source.Success ? source.Groups[2].Value : Path.Combine(Path.GetDirectoryName(path)!, "models_cache.json");
                if (!Path.IsPathFullyQualified(catalogPath)) catalogPath = Path.Combine(Path.GetDirectoryName(path)!, catalogPath);
                using var catalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
                foreach (var item in catalog.RootElement.GetProperty("models").EnumerateArray())
                {
                    if (item.GetProperty("slug").GetString() != model) continue;
                    if (item.TryGetProperty("context_window", out var normal)) modelDefault = normal.GetInt64();
                    if (item.TryGetProperty("max_context_window", out var max) && max.ValueKind == JsonValueKind.Number) modelMaximum = max.GetInt64();
                    if (item.TryGetProperty("effective_context_window_percent", out var percent)) effectivePercent = percent.GetInt32();
                    break;
                }
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException) { }
        }
        var window = Value("model_context_window") ?? modelDefault;
        var compact = Math.Min(Value("model_auto_compact_token_limit") ?? window * 90 / 100, window * 90 / 100);
        return new(model, window, compact, modelDefault, modelMaximum, effectivePercent);
    }

    internal static long? ReadRootValue(string text, string key)
    {
        var table = Regex.Match(text, @"(?m)^\s*\[");
        var root = table.Success ? text[..table.Index] : text;
        var match = Regex.Match(root, "(?m)^" + Regex.Escape(key) + @"\s*=\s*(\d[\d_]*)");
        return match.Success && long.TryParse(match.Groups[1].Value.Replace("_", ""), out var number) ? number : null;
    }

    internal static string SetRootValue(string text, string key, long? value)
    {
        var firstTable = Regex.Match(text, @"(?m)^\s*\[");
        var split = firstTable.Success ? firstTable.Index : text.Length;
        var root = text[..split];
        var rest = text[split..];
        var pattern = "(?m)^" + Regex.Escape(key) + @"\s*=[^\r\n]*(?:\r?\n|$)";
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var replacement = value is null ? "" : key + " = " + value.Value.ToString(CultureInfo.InvariantCulture) + newline;
        root = Regex.IsMatch(root, pattern) ? Regex.Replace(root, pattern, replacement) : replacement + root;
        return root + rest;
    }

    public static void Save(long window, long compactAt, string? configPath = null)
    {
        if (window < 32_000 || window > SliderMaximum || compactAt < 1 || compactAt > window * 90 / 100)
            throw new ArgumentOutOfRangeException(nameof(window), "Invalid context or compaction threshold.");
        var path = configPath ?? CodexThemeSource.DefaultConfigPath;
        var original = File.ReadAllText(path);
        var backup = path + ".statusbar-context-original.json";
        if (!File.Exists(backup))
            File.WriteAllText(backup, JsonSerializer.Serialize(new Dictionary<string, long?>
            {
                ["model_context_window"] = ReadRootValue(original, "model_context_window"),
                ["model_auto_compact_token_limit"] = ReadRootValue(original, "model_auto_compact_token_limit")
            }));
        var updated = SetRootValue(SetRootValue(original, "model_context_window", window), "model_auto_compact_token_limit", compactAt);
        Commit(path, original, updated);
    }

    public static bool CanRestore => File.Exists(CodexThemeSource.DefaultConfigPath + ".statusbar-context-original.json");

    public static void Restore(string? configPath = null)
    {
        var path = configPath ?? CodexThemeSource.DefaultConfigPath;
        var saved = JsonSerializer.Deserialize<Dictionary<string, long?>>(File.ReadAllText(path + ".statusbar-context-original.json"))!;
        var original = File.ReadAllText(path);
        var updated = original;
        foreach (var key in new[] { "model_context_window", "model_auto_compact_token_limit" })
            updated = SetRootValue(updated, key, saved[key]);
        Commit(path, original, updated);
    }

    private static void Commit(string path, string expected, string updated)
    {
        // Edit only these two root keys. Preserve unrelated settings and reject concurrent writes.
        var temp = path + ".statusbar-" + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, updated, new UTF8Encoding(false));
        try
        {
            if (File.ReadAllText(path) != expected) throw new IOException("Codex settings changed. Reopen Settings and try again.");
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
