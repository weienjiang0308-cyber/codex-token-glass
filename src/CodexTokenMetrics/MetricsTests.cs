using System.Text.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CodexStatusbar;

namespace CodexTokenMetrics;

internal static class MetricsTests
{
    internal static int Run(string fixtures)
    {
        var checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new InvalidOperationException("FAIL: " + name); }
        var scratch = Path.Combine(Path.GetTempPath(), "CodexTokenGlass-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(scratch, "sessions"));
        try
        {
            var modern = new RolloutReader(Path.Combine(fixtures, "fixture-modern.jsonl")); modern.Initialize();
            var s = modern.State;
            Check(s.InputTokens == 220000 && s.OutputTokens == 1700 && s.TotalTokens == 221700, "modern provider counts");
            Check(s.CachedInputTokens == 204000 && s.UncachedInputTokens == 16000, "cache subset accounting");
            Check(s.Tps.LastTurnTps == 100 && s.Tps.TurnGenerationMs == 3000, "original TPS excludes tool windows");
            var legacy = new RolloutReader(Path.Combine(fixtures, "fixture-legacy.jsonl")); legacy.Initialize();
            Check(legacy.State.TotalTokens == 5699084 && legacy.State.CachedInputTokens == 5526656, "legacy provider carrier");
            Check(legacy.State.Tps.DisplayTps is null, "no fabricated legacy speed");
            var target = Path.Combine(scratch, "sessions", "synthetic.jsonl");
            File.Copy(modern.Path, target);
            var monitor = new MetricsMonitor(scratch, null);
            var snapshot = monitor.Read();
            Check(snapshot.Status == "ready" && snapshot.SessionId == s.SessionMetaId, "discovery uses session metadata");
            Check(snapshot.Speed == s.Tps.DisplayTps && snapshot.AverageSpeed == s.Tps.AverageTps && snapshot.PeakSpeed == s.Tps.PeakTps, "portable UI receives identical metrics");
            Check(snapshot.SelectionMode == "recentActivity", "fallback is explicitly labeled");
            Check(new MetricsMonitor(scratch, "nonexistent").Read().SessionId is null, "pinned session never falls back to another thread");
            // Repeat the terminal carrier rather than rewinding cumulative usage.
            var lastUsage = File.ReadLines(modern.Path).Last(l => l.Contains("thread_token_usage"));
            File.AppendAllText(target, lastUsage + "\n");
            Check(monitor.Read().TotalTokens == snapshot.TotalTokens, "incremental duplicate is not summed");
            File.WriteAllText(target, File.ReadAllText(legacy.Path));
            Check(monitor.Read().Status == "sessionChanged", "rotation cannot leak another session's usage under the previous identity");
            Check(monitor.Read().TotalTokens == legacy.State.TotalTokens, "rotation reinitializes parser");
            Check(!MetricsMonitor.IsWithin(Path.Combine(scratch, "sessions"), Path.Combine(scratch, "outside.jsonl")), "rollout path containment");
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, Program.Json));
            Check(json.RootElement.GetProperty("inputTokens").GetInt64() == 220000, "camelCase wire schema");
            Check(!json.RootElement.TryGetProperty("path", out _) && !json.RootElement.GetProperty("sessions")[0].TryGetProperty("path", out _), "no local file paths in UI bridge");
            var empty = new MetricsMonitor(Path.Combine(scratch, "missing"), null).Read();
            Check(empty.InputTokens is null && empty.Speed is null, "missing data remains unknown");
            var dbLog = Path.Combine(scratch, "sessions", "database-selected.jsonl");
            File.Copy(modern.Path, dbLog);
            var dbPath = Path.Combine(scratch, "state_5.sqlite");
            var sql = "CREATE TABLE threads(id TEXT, title TEXT, rollout_path TEXT, tokens_used INTEGER, recency_at_ms INTEGER, archived INTEGER);" +
                $"INSERT INTO threads VALUES ('{s.SessionMetaId}', 'Synthetic database title', '{dbLog.Replace("'", "''")}', 221700, 1234, 0);";
            CreateDatabase(dbPath, sql);
            var before = SHA256.HashData(File.ReadAllBytes(dbPath));
            var dbSnapshot = new MetricsMonitor(scratch, s.SessionMetaId).Read();
            Check(dbSnapshot.Title == "Synthetic database title" && dbSnapshot.TotalTokens == 221700, "platform SQLite library and explicit home selection");
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(dbPath))), "Codex database remains unchanged after reading");
            Check(!File.Exists(Path.Combine(scratch, "config.toml")) && !File.Exists(Path.Combine(scratch, "auth.json")), "metrics do not create configuration or credentials");
            DesktopIpcTests.Run(Check, scratch);
            Console.WriteLine($"PASS: {checks} portable checks; shared Windows parser unchanged.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        finally
        {
            var resolved = Path.GetFullPath(scratch);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(tempRoot, StringComparison.Ordinal) || !Path.GetFileName(resolved).StartsWith("CodexTokenGlass-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing test cleanup outside the generated temp directory");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static void CreateDatabase(string path, string sql)
    {
        if (sqlite3_open_v2(path, out var db, 6, IntPtr.Zero) != 0) throw new InvalidOperationException("Could not create isolated SQLite fixture");
        try
        {
            var status = sqlite3_exec(db, sql, IntPtr.Zero, IntPtr.Zero, out var message);
            if (message != IntPtr.Zero) sqlite3_free(message);
            if (status != 0) throw new InvalidOperationException("Could not populate isolated SQLite fixture");
        }
        finally { _ = sqlite3_close_v2(db); }
    }
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr arg, out IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void sqlite3_free(IntPtr message);
}
