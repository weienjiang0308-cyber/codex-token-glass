using System.Text.Json;

namespace Codex.SpeedMetrics;

/// <summary>DeepSeek Harness 0.2.1-rc.5 throughput semantics over normalized Harness
/// events. Input adaptation is separate: Codex log items are NOT first-token events.</summary>
public sealed class HarnessThroughputProjection
{
    private sealed record Step(string Turn, string Id, double Start, double? First);
    private Step? _step;
    public double DecodeTokens { get; private set; }
    public double DecodeMilliseconds { get; private set; }
    public double LlmMilliseconds { get; private set; }
    public double TtftMilliseconds { get; private set; }
    public int TtftSteps { get; private set; }
    public double? Tps => DecodeMilliseconds > 0 ? DecodeTokens * 1000 / DecodeMilliseconds : null;

    public void Observe(JsonElement e)
    {
        var type = Text(e, "type");
        if (!e.TryGetProperty("data", out var data)) return;
        var time = Number(e, "time");
        var turn = data.TryGetProperty("turn", out var t) ? t.GetRawText() : "";
        var id = data.TryGetProperty("step", out var s) ? s.GetRawText() : "";
        if (type == "step/start") { _step = new(turn, id, time ?? 0, null); return; }
        if (type == "step/end") { _step = null; return; }
        if (type is not ("assistant/attempt" or "assistant/message") || _step is not { } open
            || open.Turn != turn || open.Id != id) return;
        var first = open.First ?? (data.TryGetProperty("stream", out var stream) ? FirstTokenTime(stream) : null);
        if (type == "assistant/attempt") { _step = open with { First = first }; return; }
        _step = null; // A duplicate assembled message cannot accrue again.
        LlmMilliseconds += Math.Max(0, (time ?? open.Start) - open.Start);
        if (first is not double start) return;
        TtftMilliseconds += Math.Max(0, start - open.Start); TtftSteps++;
        if (data.TryGetProperty("usage", out var usage) && Number(usage, "outputTokens") is double output && output >= 0)
        {
            DecodeTokens += output;
            DecodeMilliseconds += Math.Max(0, (time ?? start) - start);
        }
    }

    public static double? FirstTokenTime(JsonElement stream)
    {
        if (stream.ValueKind != JsonValueKind.Array) return null;
        foreach (var record in stream.EnumerateArray())
        {
            var type = Text(record, "type");
            if (type == "chunk")
            {
                if (record.TryGetProperty("chunk", out var chunk) && IsTokenDelta(chunk)) return Number(record, "time");
                continue;
            }
            if (type is not ("text-chunks" or "reasoning-chunks" or "tool-call-chunks")) continue;
            var at = Number(record, "time0");
            if (type == "tool-call-chunks" && record.TryGetProperty("name", out _)) return at;
            if (!record.TryGetProperty(type == "tool-call-chunks" ? "args" : "texts", out var fragments)
                || fragments.ValueKind != JsonValueKind.Array) continue;
            record.TryGetProperty("dt", out var deltas);
            var index = 0;
            foreach (var fragment in fragments.EnumerateArray())
            {
                if (index > 0) at += deltas[index - 1].GetDouble();
                if (fragment.GetString() != "") return at;
                index++;
            }
        }
        return null;
    }
    private static bool IsTokenDelta(JsonElement c) => Text(c, "type") switch
    {
        "text-delta" or "reasoning-delta" => Text(c, "text") is { Length: > 0 },
        "tool-call-delta" => Text(c, "argumentsDelta") is { Length: > 0 } || c.TryGetProperty("name", out _),
        _ => false
    };
    private static string? Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double? Number(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
}
