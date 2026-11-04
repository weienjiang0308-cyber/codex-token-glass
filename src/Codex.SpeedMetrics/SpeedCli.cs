using System.Diagnostics;
using System.Text.Json;

namespace Codex.SpeedMetrics;

public static class SpeedCli
{
    public static int Run(string[] args)
    {
        string? Value(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var tid = Value("--thread");
        if (tid is null) { Console.WriteLine("{\"error\":\"--thread required\"}"); return 1; }
        var root = Value("--sessions") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
        var path = Value("--rollout") ?? Directory.EnumerateFiles(root, "*" + tid + "*.jsonl", SearchOption.AllDirectories).FirstOrDefault();
        if (path is null) { Console.WriteLine("{\"error\":\"rollout not found\"}"); return 1; }
        var duration = double.TryParse(Value("--duration"), out var seconds) ? seconds : 2;
        using var observer = new SpeedObserver(!args.Contains("--offline"));
        observer.Watch(new[] { tid });
        var clock = Stopwatch.StartNew();
        var display = new LiveSpeedDisplay();
        do
        {
            var reading = observer.Read(tid, path);
            if (args.Contains("--display-speed"))
            {
                var shown = display.Read(reading, Environment.TickCount64);
                Console.WriteLine(JsonSerializer.Serialize(new { Raw = reading, DisplayedTps = reading.Activity is null or "generating" ? shown.Rate : null, Activity = reading.Activity, ResponseAverageTps = reading.AverageTps,
                    LiveDetailTps = shown.Rate, LiveDetailHeld = shown.Held }));
            }
            else Console.WriteLine(JsonSerializer.Serialize(reading));
            Console.Out.Flush();
            Thread.Sleep(250);
        } while (clock.Elapsed.TotalSeconds < duration);
        return 0;
    }
}
