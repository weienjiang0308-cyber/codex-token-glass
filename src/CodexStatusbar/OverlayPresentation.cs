using System.Globalization;

namespace CodexStatusbar;

internal sealed record OverlayMetric(
    DisplayField Field,
    string CompactLabel,
    string ExpandedLabel,
    string Value,
    bool HasValue);

internal sealed record OverlayPresentation(
    OverlayMetric Primary,
    OverlayMetric Secondary,
    IReadOnlyList<OverlayMetric> ExpandedRows,
    double ContextPercent,
    bool ShowContextProgress,
    string? StatusText,
    string? CapsuleText = null,
    IReadOnlyList<string>? CapsuleVariants = null,
    string? SpeedExplanation = null,
    bool LiveReadingHeld = false,
    string? ConversationTitle = null,
    string? TimingCoverage = null)
{
    public IReadOnlyList<OverlayMetric> DetailRows
    {
        get
        {
            var rows = ExpandedRows.Where(r => r.Field is not DisplayField.PeakSpeed and not DisplayField.AverageSpeed and not DisplayField.Tps).ToList();
            if (Secondary.Field == DisplayField.Total && rows.All(r => r.Field != DisplayField.Total)) rows.Insert(0, Secondary);
            return rows;
        }
    }
    /// <summary>
    /// The collapsed strip's text at every supported verbosity, widest first. The last entry is the
    /// narrowest form that still carries all three metrics; callers pick the first one that fits and
    /// hide the strip when none does.
    /// </summary>
    public IReadOnlyList<string> Variants => CapsuleVariants ?? (CapsuleText is null ? [] : [CapsuleText]);

    /// <summary>The separator between the three metrics. A middle dot with single spaces: the double
    /// and triple spaces the strip used to carry read as a HUD, not as a native toolbar label.</summary>
    private const string Separator = " · ";

    /// <summary>The narrowest variant that is still worth showing: total and cache, no speed.</summary>
    public string NarrowestVariant => Variants.Count > 0 ? Variants[^1] : string.Empty;
}

internal static class OverlayPresentationBuilder
{
    private const string NoValue = "—";

    /// <summary>The separator between the three metrics. A middle dot with single spaces: the double
    /// and triple spaces the strip used to carry read as a HUD, not as a native toolbar label.</summary>
    private const string Separator = " · ";

    /// <summary>The collapsed strip before any conversation is known.</summary>
    public const string WaitingCapsuleText = "Connecting to chat";

    /// <summary>
    /// The strip's verbosity ladder, widest first. Level 0 is what the user sees at normal widths —
    /// the full wording is never abbreviated by default; the shorter forms exist only to keep the
    /// strip from colliding with the composer's own buttons when Codex is dragged very narrow.
    /// </summary>
    public static IReadOnlyList<string> BuildCapsuleVariants(
        string tps,
        string total,
        string cache,
        bool hasUsage)
    {
        var tokens = hasUsage ? total : "--";
        var cacheText = hasUsage ? cache : "--";
        return
        [
            // 0 — full
            $"⚡ {tps} tok/s{Separator}{tokens} tok{Separator}Cache {cacheText}",

            // 1 — drop the unit suffix from the token count and the "Cache" word
            $"⚡ {tps} t/s{Separator}{tokens}{Separator}{cacheText}",

            // 2 — drop the lightning glyph too
            $"{tps} t/s{Separator}{tokens}{Separator}{cacheText}",

            // 3 — the two quantities that need no unit: total tokens and cache ratio
            $"{tokens}{Separator}{cacheText}"
        ];
    }

    public static OverlayPresentation CreateWaiting(
        string statusText,
        DisplayField primaryField,
        DisplayField secondaryField,
        DisplayField visibleFields)
    {
        ValidateHighlightedFields(primaryField, secondaryField);
        // The strip is sized from its variants, so the status itself must be one of them: drawing
        // a longer sentence into a window measured for another string is what produced "…".
        // The sentence carries no trailing ellipsis either, and falls back to a short word.
        var status = SanitizeSingleLine(statusText).Trim().TrimEnd('…', '.', ' ');
        IReadOnlyList<string> variants = status.Length == 0
            ? [WaitingCapsuleText, "Connecting"]
            : [status, CodexUi.T("等待中", "Waiting")];
        return new OverlayPresentation(
            CreateWaitingMetric(primaryField),
            CreateWaitingMetric(secondaryField),
            CreateExpandedRows(visibleFields, primaryField, secondaryField, CreateWaitingMetric),
            0,
            false,
            status,
            variants[0],
            variants);
    }

    public static OverlayPresentation Create(
        TokenSnapshot snapshot,
        DisplayField primaryField,
        DisplayField secondaryField,
        DisplayField visibleFields)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateHighlightedFields(primaryField, secondaryField);
        var contextPercent = Math.Clamp(snapshot.ContextPercent, 0, 100);
        return new OverlayPresentation(
            CreateMetric(snapshot, primaryField, contextPercent),
            CreateMetric(snapshot, secondaryField, contextPercent),
            CreateExpandedRows(
                visibleFields,
                primaryField,
                secondaryField,
                field => CreateMetric(snapshot, field, contextPercent)),
            contextPercent,
            (visibleFields & DisplayField.ContextPercent) != 0,
            null,
            BuildCapsuleVariants(snapshot)[0],
            BuildCapsuleVariants(snapshot),
            CodexUi.T($"输出速度 · 官方输出 / 生成时长 · 已计时 {snapshot.TimedResponseCount}/{snapshot.CompletedResponseCount} 响应",
                $"Throughput · output / generation time · {snapshot.TimedResponseCount}/{snapshot.CompletedResponseCount} timed responses"),
            snapshot.TpsHeld, snapshot.Title, $"{snapshot.TimedResponseCount}/{snapshot.CompletedResponseCount}");
    }

    public static string FormatTokenCount(long value) => value switch
    {
        >= 999_950 => (value / 1_000_000d).ToString("0.00", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (value / 1_000d).ToString("0.0", CultureInfo.InvariantCulture) + "k",
        _ => value.ToString("N0", CultureInfo.InvariantCulture)
    };

    public static string ShortThreadId(string threadId, int maximumLength = 12)
    {
        var singleLineThreadId = SanitizeSingleLine(threadId);
        if (singleLineThreadId.Length <= maximumLength)
        {
            return singleLineThreadId;
        }

        if (maximumLength <= 1)
        {
            return maximumLength == 1 ? "…" : string.Empty;
        }

        var prefixLength = Math.Min(4, maximumLength - 1);
        var suffixLength = Math.Min(6, maximumLength - prefixLength - 1);
        return string.Concat(
            singleLineThreadId.AsSpan(0, prefixLength),
            "…",
            singleLineThreadId.AsSpan(singleLineThreadId.Length - suffixLength, suffixLength));
    }

    public static string GetFieldMenuText(DisplayField field)
    {
        if (CodexUi.English) return GetLabels(field).Expanded;
        return field switch
        {
            DisplayField.Total => "总 token",
            DisplayField.Input => "输入 token",
            DisplayField.Output => "输出 token",
            DisplayField.CacheHit => "缓存命中",
            DisplayField.CacheHitRate => "缓存命中率",
            DisplayField.CacheMiss => "缓存未命中（推导）",
            DisplayField.Context => "上下文用量",
            DisplayField.ContextPercent => "上下文百分比",
            DisplayField.Reasoning => "推理输出",
            DisplayField.Thread => "会话 ID",
            DisplayField.Tps => "已完成响应输出速度",
            DisplayField.TurnDuration => "本轮任务用时",
            DisplayField.ModelTime => "本轮模型生成用时",
            DisplayField.FirstTokenTime => "首 token 等待时间",
            DisplayField.PeakSpeed => "本轮峰值响应速度",
            DisplayField.AverageSpeed => "已计时响应平均速度",
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "不支持的展示字段。")
        };
    }

    /// <summary>
    /// Compact visible-text speed, token total and known cache ratio. Working phases retain
    /// the last valid text reading beside the state. Hold semantics live in the details.
    /// Missing metrics are omitted. Response-average speed is never used as a fallback.
    /// </summary>
    public static IReadOnlyList<string> BuildCapsuleVariants(TokenSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var parts = new List<string>();
        if (snapshot.Tps is double rate && rate >= 0 && double.IsFinite(rate))
            parts.Add($"{rate.ToString("0.0", CultureInfo.InvariantCulture)} tok/s");
        if (snapshot.HasUsage) parts.Add("Tokens " + MetricFormat.CompactTokens(
            snapshot.InputTokens + snapshot.OutputTokens > 0 ? snapshot.InputTokens + snapshot.OutputTokens : snapshot.TotalTokens));
        if (snapshot.InputTokens > 0) parts.Add("Cache hit " + MetricFormat.FormatCachePercentCompact(snapshot.InputTokens, snapshot.CachedInputTokens));
        var status = snapshot.Activity switch
        {
            "command" => "Command", "searching" => "Searching", "tool" => "Using tools",
            "thinking" => "Thinking", "opening" => "Opening", "connecting" => "Connecting",
            "ready" => "Ready", "generating" => "Output", "working" => "Working",
            _ => snapshot.ToolRunning ? "Command" : snapshot.Streaming ? "Working" : "Ready"
        };
        parts.Insert(0, status);
        var full = string.Join(" · ", parts);
        var compact = full.Replace("Tokens ", "").Replace("Cache hit ", "Hit ");
        return [full, compact, compact.Replace(" tok/s", " t/s")];

    }

    /// <summary>The widest variant — what the strip shows whenever there is room for it.</summary>
    public static string BuildCapsuleText(TokenSnapshot snapshot) => BuildCapsuleVariants(snapshot)[0];

    private static void ValidateHighlightedFields(DisplayField primaryField, DisplayField secondaryField)
    {
        if (!DisplayFieldRules.IsSingleSupported(primaryField)
            || !DisplayFieldRules.IsSingleSupported(secondaryField))
        {
            throw new ArgumentException("收起指标必须是受支持的单个字段。");
        }
    }

    private static IReadOnlyList<OverlayMetric> CreateExpandedRows(
        DisplayField visibleFields,
        DisplayField primaryField,
        DisplayField secondaryField,
        Func<DisplayField, OverlayMetric> createMetric)
    {
        return DisplayFieldRules.Ordered
            .Where(field => (visibleFields & field) != 0)
            .Where(field => field != primaryField && field != secondaryField)
            .Select(createMetric)
            .ToArray();
    }

    private static OverlayMetric CreateWaitingMetric(DisplayField field)
    {
        var labels = GetLabels(field);
        return new OverlayMetric(field, labels.Compact, labels.Expanded, NoValue, false);
    }

    private static OverlayMetric CreateMetric(
        TokenSnapshot snapshot,
        DisplayField field,
        double contextPercent)
    {
        var labels = GetLabels(field);
        var value = field switch
        {
            DisplayField.Total => ExactTokens(snapshot.TotalTokens),
            DisplayField.Input => ExactTokens(snapshot.InputTokens),
            DisplayField.Output => ExactTokens(snapshot.OutputTokens),
            DisplayField.CacheHit => ExactTokens(snapshot.CachedInputTokens),
            DisplayField.CacheHitRate => snapshot.CacheHitPercent is double rate ? $"{rate:0.00}%" : NoValue,
            DisplayField.CacheMiss => ExactTokens(snapshot.UncachedInputTokens),
            DisplayField.Context => $"{FormatTokenCount(snapshot.ContextUsedTokens)} / {FormatTokenCount(snapshot.ContextWindowTokens)}",
            DisplayField.ContextPercent => $"{contextPercent:0}%",
            DisplayField.Reasoning => ExactTokens(snapshot.ReasoningOutputTokens),
            DisplayField.Thread => ShortThreadId(snapshot.ThreadId),
            DisplayField.Tps => FormatTpsValue(snapshot),
            DisplayField.TurnDuration => FormatDuration(snapshot.TurnElapsedMs),
            DisplayField.ModelTime => FormatDuration(snapshot.MergedModelElapsedMs > 0 ? snapshot.MergedModelElapsedMs : null),
            DisplayField.FirstTokenTime => FormatDuration(snapshot.TtftMs is double ttft ? (long)ttft : null),
            DisplayField.PeakSpeed => MetricFormat.FormatTps(snapshot.PeakTps, false) + " tok/s",
            DisplayField.AverageSpeed => MetricFormat.FormatTpsFloat(snapshot.AverageTps) + " tok/s",
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "不支持的展示字段。")
        };
        var hasValue = field != DisplayField.Thread || !string.IsNullOrWhiteSpace(snapshot.ThreadId);
        return new OverlayMetric(field, labels.Compact, labels.Expanded, value, hasValue);
    }

    private static (string Compact, string Expanded) GetLabels(DisplayField field)
    {
        if (CodexUi.English)
        {
            return field switch
            {
                DisplayField.Total => ("Total", "Total tokens"),
                DisplayField.Input => ("In", "Input tokens"),
                DisplayField.Output => ("Out", "Output tokens"),
                DisplayField.CacheHit => ("Cached", "Cached input"),
                DisplayField.CacheHitRate => ("Cache", "Cache hit rate"),
                DisplayField.CacheMiss => ("Uncached", "Uncached input"),
                DisplayField.Context => ("Context", "Latest call / window"),
                DisplayField.ContextPercent => ("Context", "Context used"),
                DisplayField.Reasoning => ("Reasoning", "Reasoning tokens"),
                DisplayField.Thread => ("Thread", "Conversation"),
                DisplayField.Tps => ("TPS", "Completed-response throughput"),
                DisplayField.TurnDuration => ("Elapsed", "Task elapsed"),
                DisplayField.ModelTime => ("Timed", "Timed generation"),
                DisplayField.FirstTokenTime => ("First token", "Time to first token"),
                DisplayField.PeakSpeed => ("Peak", "Peak response speed"),
                DisplayField.AverageSpeed => ("Average", "Completed-response average"),
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            };
        }
        return field switch
        {
            DisplayField.Total => ("总", "总 Token"),
            DisplayField.Input => ("入", "累计输入"),
            DisplayField.Output => ("出", "累计输出"),
            DisplayField.CacheHit => ("命中", "累计缓存输入"),
            DisplayField.CacheHitRate => ("命中率", "缓存命中率"),
            DisplayField.CacheMiss => ("未中", "累计未缓存输入"),
            DisplayField.Context => ("上下文", "最近调用 / 可用窗口"),
            DisplayField.ContextPercent => ("上下文", "上下文占用"),
            DisplayField.Reasoning => ("推理", "推理输出"),
            DisplayField.Thread => ("会话", "会话"),
            DisplayField.Tps => ("输出速度", "已完成响应输出速度"),
            DisplayField.TurnDuration => ("用时", "本轮任务用时"),
            DisplayField.ModelTime => ("计时", "已计时生成时长"),
            DisplayField.FirstTokenTime => ("首字", "首 token 等待"),
            DisplayField.PeakSpeed => ("峰值", "本轮峰值响应速度"),
            DisplayField.AverageSpeed => ("平均", "已计时响应平均速度"),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "不支持的展示字段。")
        };
    }

    /// <summary>TPS cell used by the expanded panel and the tray menu: e.g. "243 tok/s", "-- tok/s".</summary>
    private static string FormatTpsValue(TokenSnapshot snapshot) =>
        (snapshot.Tps is double speed ? speed.ToString("0.0", CultureInfo.InvariantCulture) : "—") + " tok/s";

    private static string ExactTokens(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    internal static string FormatDuration(long? milliseconds)
    {
        if (milliseconds is null || milliseconds < 0) return NoValue;
        var span = TimeSpan.FromMilliseconds(milliseconds.Value);
        if (CodexUi.English)
        {
            if (span.TotalHours >= 1) return $"{(long)span.TotalHours}h {span.Minutes}m {span.Seconds}s";
            if (span.TotalMinutes >= 1) return $"{(long)span.TotalMinutes}m {span.Seconds}s";
            return span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }
        if (span.TotalHours >= 1) return $"{(long)span.TotalHours} 小时 {span.Minutes} 分 {span.Seconds} 秒";
        if (span.TotalMinutes >= 1) return $"{(long)span.TotalMinutes} 分 {span.Seconds} 秒";
        return span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒";
    }

    private static string SanitizeSingleLine(string value)
    {
        return value.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
    }
}

internal sealed class PresentationProbeRequest
{
    public List<PresentationProbeCase> Cases { get; set; } = new();
}

internal sealed class PresentationProbeCase
{
    public string Name { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public TokenSnapshot? Snapshot { get; set; }
    public int? PrimaryField { get; set; }
    public int? SecondaryField { get; set; }
    public int? VisibleFields { get; set; }
    public string? StatusText { get; set; }
}

internal sealed record PresentationProbeCaseResult(string Name, OverlayPresentation Presentation);

internal sealed record PresentationProbeResult(IReadOnlyList<PresentationProbeCaseResult> Cases);

internal static class PresentationProbe
{
    public static PresentationProbeResult Execute(PresentationProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var results = new List<PresentationProbeCaseResult>();
        foreach (var probeCase in request.Cases)
        {
            var primaryField = RequireField(probeCase.PrimaryField, nameof(probeCase.PrimaryField));
            var secondaryField = RequireField(probeCase.SecondaryField, nameof(probeCase.SecondaryField));
            var visibleFields = (DisplayField)(probeCase.VisibleFields ?? 0);
            var presentation = probeCase.Operation switch
            {
                "Create" => OverlayPresentationBuilder.Create(
                    probeCase.Snapshot ?? throw new ArgumentException("Create 操作需要 Snapshot。", nameof(probeCase)),
                    primaryField,
                    secondaryField,
                    visibleFields),
                "Waiting" => OverlayPresentationBuilder.CreateWaiting(
                    probeCase.StatusText ?? string.Empty,
                    primaryField,
                    secondaryField,
                    visibleFields),
                _ => throw new ArgumentException($"不支持的展示探针操作：{probeCase.Operation}", nameof(probeCase))
            };
            results.Add(new PresentationProbeCaseResult(probeCase.Name, presentation));
        }

        return new PresentationProbeResult(results);
    }

    private static DisplayField RequireField(int? value, string parameterName)
    {
        if (!value.HasValue)
        {
            throw new ArgumentException("展示探针需要字段。", parameterName);
        }

        return (DisplayField)value.Value;
    }
}
