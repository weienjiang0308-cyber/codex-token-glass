using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.Json;
using Codex.SpeedMetrics;

namespace CodexStatusbar;

/// <summary>iOS-style second-level sheet for token statistics. It shares the 500 x 932 frame of the
/// first-level panel and the settings sheet: a fixed navigation bar and scope picker above a
/// scrolling inset-grouped list of token counts. No prices are shown.</summary>
internal sealed class StatisticsForm : Form
{
    private const int SheetWidth = OverlayLayoutCalculator.ExpandedWidthDip;
    private const int SheetHeight = OverlayLayoutCalculator.SheetHeightDip;
    private const float OuterRadius = 40f, CardRadius = 20f, RowHeight = 50f;
    private const float CardLeft = 20f, CardWidth = SheetWidth - 40f, ContentInset = 16f;
    // Navigation row and scope switch follow the top rhythm shared with the panel and settings sheets.
    private const float NavTop = OverlayLayoutCalculator.NavCenterYDip - 22f, ScopeTop = OverlayLayoutCalculator.FirstCardTopDip - 22f;
    private const float ViewportTop = ScopeTop + 46f, ViewportBottomInset = 20f, LayerInset = 6f;
    private const float BodyPx = 17f, FootnotePx = 13f, SegmentPx = 14f;

    private readonly UsageStatisticsService _service;
    private readonly Func<TokenSnapshot?> _selection;
    private readonly Func<OverlayThemePalette> _theme;
    private readonly Rectangle? _anchor;
    private readonly string? _backTitle;
    private readonly System.Windows.Forms.Timer _dataTimer = new() { Interval = 1000 };
    private readonly List<Hit> _hits = new();
    private OverlayThemePalette _palette;
    private StatisticsSnapshot? _state;
    private TokenSnapshot? _selected;
    private IReadOnlyList<DailyUsage> _daily = Array.Empty<DailyUsage>();
    private int _scope;
    private float _scroll, _contentHeight, _scale = 1f;
    private string? _hover, _pressed, _notice;
    private bool _painting;
    /// <summary>True when the page was left with Done, so the whole navigation stack is dismissed.</summary>
    internal bool DismissAll { get; private set; }

    internal void DismissStack() { DismissAll = true; Close(); }

    private sealed record Hit(string Key, RectangleF Bounds, bool Scrolls, Action Action);
    private sealed record Row(string Label, string Value);
    private sealed record Section(string Title, IReadOnlyList<Row> Rows, string? Footnote = null);

    public StatisticsForm(UsageStatisticsService service, Func<TokenSnapshot?> selection, Func<OverlayThemePalette> theme,
        Rectangle? anchor = null, string? backTitle = null)
    {
        _service = service; _selection = selection; _theme = theme; _palette = theme();
        _anchor = anchor; _backTitle = backTitle;
        Text = CodexUi.T("用量统计", "Usage statistics");
        FormBorderStyle = FormBorderStyle.None; AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = backTitle is null; TopMost = true; KeyPreview = true;
        StartPosition = FormStartPosition.Manual; BackColor = _palette.Background;
        _scale = DeviceDpi / 96f;
        if (backTitle is not null && anchor is Rectangle exact && exact.Height > 1)
        {
            ClientSize = exact.Size;
            Location = exact.Location;
        }
        else
        {
            var area = (anchor is Rectangle a ? Screen.FromRectangle(a) : Screen.FromPoint(Cursor.Position)).WorkingArea;
            ClientSize = new Size((int)Math.Round(SheetWidth * _scale), Math.Min((int)Math.Round(SheetHeight * _scale), area.Height));
            Location = ResolveLocation(area);
        }
        _dataTimer.Tick += (_, _) => RefreshData();
        RefreshData();
    }

    private Point ResolveLocation(Rectangle area)
    {
        if (_anchor is not Rectangle anchor)
            return new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        var lowerHalf = anchor.Top + anchor.Height / 2 > area.Top + area.Height / 2;
        var x = Math.Clamp(anchor.Left, area.Left, Math.Max(area.Left, area.Right - Width));
        var y = Math.Clamp(lowerHalf ? anchor.Bottom - Height : anchor.Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
        return new Point(x, y);
    }

    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x00080000; return p; } }
    protected override void OnLoad(EventArgs e) { base.OnLoad(e); _dataTimer.Start(); }
    protected override void OnShown(EventArgs e) { base.OnShown(e); PaintFrame(); }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) => PaintFrame();

    private float LogicalHeight => ClientSize.Height / _scale;
    private float ViewportHeight => Math.Max(0f, LogicalHeight - ViewportTop - ViewportBottomInset);
    private float MaxScroll => Math.Max(0f, _contentHeight - ViewportHeight);
    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
    private static DateOnly Day(UsageEntry entry) => DateOnly.FromDateTime(entry.At.UtcDateTime);
    private string ThreadName(string id) => _state?.ThreadNames?.GetValueOrDefault(id) ?? OverlayPresentationBuilder.ShortThreadId(id);
    private static string[] ScopeTitles => new[] { CodexUi.T("本对话", "Chat"), CodexUi.T("今天", "Today"), CodexUi.T("本周", "Week"), CodexUi.T("本月", "Month") };

    private static string DayLabel(DateOnly date) => CodexUi.English
        ? date.ToString("MMM d · ddd", CultureInfo.GetCultureInfo("en-US"))
        : $"{date:MM-dd} · 周{"日一二三四五六"[(int)date.DayOfWeek]}";

    private void RefreshData()
    {
        var state = _service.Current; var selected = _selection(); var theme = _theme();
        if (ReferenceEquals(state, _state) && selected?.ThreadId == _selected?.ThreadId && theme == _palette) return;
        _state = state; _selected = selected; _palette = theme;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _daily = state is null ? Array.Empty<DailyUsage>() : UsageMerge.Combine(state.OfficialDays, state.Entries, today);
        PaintFrame();
    }

    private void SelectScope(int scope)
    {
        if (scope == _scope) return;
        _scope = scope; _scroll = 0f; _notice = null;
        PaintFrame();
    }

    private (UsageTotals Totals, List<Section> Sections) BuildModel()
    {
        var entries = _state?.Entries ?? Array.Empty<UsageEntry>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monday = UsageMerge.Monday(today);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var scoped = (_scope switch
        {
            0 => entries.Where(e => e.Thread == _selected?.ThreadId),
            1 => entries.Where(e => Day(e) == today),
            2 => entries.Where(e => Day(e) >= monday),
            _ => entries.Where(e => Day(e) >= monthStart)
        }).ToArray();
        var totals = UsageTotals.Sum(scoped);
        var cacheAllUnknown = totals.Input > 0 && totals.EffectiveCacheInputDenominator == 0;
        var sections = new List<Section>
        {
            new(CodexUi.T("构成", "Breakdown"), new[]
            {
                new Row(CodexUi.T("输入", "Input"), N(totals.Input)),
                new Row(CodexUi.T("缓存输入", "Cached input"), cacheAllUnknown ? "—" : N(totals.Cached)),
                new Row(CodexUi.T("未缓存输入", "Uncached input"), cacheAllUnknown ? "—" : N(Math.Max(0, totals.EffectiveCacheInputDenominator - totals.Cached))),
                new Row(CodexUi.T("输出", "Output"), N(totals.Output)),
                new Row(CodexUi.T("其中推理", "Reasoning (in output)"), N(totals.Reasoning))
            }, CodexUi.T("总量 = 输入 + 输出；缓存是输入的一部分，推理是输出的一部分。", "Total = input + output. Cached is part of input; reasoning is part of output."))
        };
        IReadOnlyList<Row> Empty(IReadOnlyList<Row> rows) => rows.Count > 0 ? rows : new[] { new Row(CodexUi.T("暂无记录", "No records yet"), "") };
        Row[] Threads() => scoped.GroupBy(e => e.Thread)
            .Select(g => (Name: ThreadName(g.Key), Total: g.Sum(e => e.Input + e.Output)))
            .OrderByDescending(x => x.Total).Take(10).Select(x => new Row(x.Name, N(x.Total))).ToArray();
        if (_scope == 0)
        {
            sections.Add(new(CodexUi.T("按模型", "By model"), Empty(scoped.GroupBy(e => e.RouteAndModelKey)
                .Select(g => (Model: g.Key, Total: g.Sum(e => e.Input + e.Output)))
                .OrderByDescending(x => x.Total).Select(x => new Row(x.Model, N(x.Total))).ToArray())));
            sections.Add(new(CodexUi.T("按日期", "By day"), Empty(scoped.GroupBy(Day).OrderByDescending(g => g.Key).Take(14)
                .Select(g => new Row(DayLabel(g.Key), N(g.Sum(e => e.Input + e.Output)))).ToArray())));
        }
        else if (_scope == 1)
        {
            sections.Add(new(CodexUi.T("对话排行", "Top chats"), Empty(Threads())));
        }
        else
        {
            var from = _scope == 2 ? monday : monthStart;
            sections.Add(new(CodexUi.T("按日期", "By day"), Empty(_daily.Where(d => d.Date >= from).OrderByDescending(d => d.Date)
                .Select(d => new Row(DayLabel(d.Date), N(d.Tokens))).ToArray()),
                CodexUi.T("每日合计优先采用官方数据，可能包含其他设备。", "Daily totals prefer official data and may include other devices.")));
            sections.Add(new(CodexUi.T("对话排行", "Top chats"), Empty(Threads())));
        }
        return (totals, sections);
    }

    private void PaintFrame()
    {
        if (_painting || IsDisposed || !IsHandleCreated || !Visible || Width < 1 || Height < 1) return;
        _painting = true;
        try { using var bitmap = DrawFrame(); LayeredSurface.Present(this, bitmap); }
        finally { _painting = false; }
    }

    internal Bitmap DrawFrame()
    {
        var ios = Ios18Palette.FromOverlay(_palette);
        var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
        bitmap.SetResolution(96, 96);
        _hits.Clear();
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.ScaleTransform(_scale, _scale);
        Ios18Palette.FillRoundedCard(g, new Rectangle(0, 0, SheetWidth, (int)Math.Round(LogicalHeight)), OuterRadius, ios.WindowSurface, ios.WindowBorder, 1f);
        var (totals, sections) = BuildModel();
        _contentHeight = MeasureContent(sections);
        _scroll = Math.Clamp(_scroll, 0f, MaxScroll);
        DrawContentLayer(g, ios, totals, sections);
        using (AppleText.Composite(bitmap))
        {
            DrawNavigation(g, ios);
            DrawScopes(g, ios);
        }
        DrawScrollIndicator(g, ios);
        return bitmap;
    }

    private void DrawNavigation(Graphics g, Ios18Palette ios)
    {
        using var navFont = CodexUi.UiFont(BodyPx * .75f, semibold: true);
        TextAt(g, CodexUi.T("用量统计", "Usage statistics"), navFont, new RectangleF(84, NavTop, SheetWidth - 168, 44), ios.TextPrimary, StringAlignment.Center);
        // "Done" is centred on the centre of the top-right corner arc, where the panel's settings
        // glyph sits; the back button mirrors its distance from the edge. Same as the settings sheet.
        var doneText = CodexUi.T("完成", "Done");
        var edgeInset = Ios18Palette.NavEdgeInset(AppleText.Measure(g, doneText, navFont) ?? 34f);
        if (_backTitle is not null)
        {
            var bounds = new RectangleF((float)Math.Round(edgeInset) - 1f, NavTop, 128, 44);
            var color = NavColor(ios, "back");
            using var backFont = CodexUi.UiFont(BodyPx * .75f);
            var chevronLeft = bounds.Left + 2f;
            Ios18Palette.DrawBackChevron(g, chevronLeft, bounds.Top + bounds.Height / 2f, 1f, color);
            var textLeft = (float)Math.Round(chevronLeft + Ios18Palette.BackChevronWidth + 6f);
            TextAt(g, _backTitle, backFont, new RectangleF(textLeft, bounds.Top, bounds.Right - textLeft, bounds.Height), color, StringAlignment.Near);
            _hits.Add(new("back", bounds, false, Close));
        }
        var done = new RectangleF(SheetWidth - OverlayLayoutCalculator.NavCenterYDip - 44, NavTop, 88, 44);
        TextAt(g, doneText, navFont, done, NavColor(ios, "done"), StringAlignment.Center);
        _hits.Add(new("done", done, false, () => { DismissAll = true; Close(); }));
    }

    private Color NavColor(Ios18Palette ios, string key) =>
        _pressed == key ? Color.FromArgb(110, ios.Accent) : _hover == key ? Color.FromArgb(185, ios.Accent) : ios.Accent;

    private void DrawScopes(Graphics g, Ios18Palette ios)
    {
        var track = new Rectangle((int)CardLeft, (int)ScopeTop, (int)CardWidth, 32);
        // The track stands on the grey page, not on a white card, so it takes the darker control fill
        // (iOS tertiary fill over the grouped background); the settings page's language switch matches.
        Ios18Palette.FillRoundedCard(g, track, track.Height / 2f, ios.ControlFill, Color.Transparent, 0f);
        var titles = ScopeTitles;
        var width = (track.Width - 4f) / titles.Length;
        for (var index = 0; index < titles.Length; index++)
        {
            var key = "scope" + index;
            var segment = new RectangleF(track.Left + 2 + index * width, track.Top + 2, width, track.Height - 4);
            var pixel = Rectangle.Round(segment);
            if (index == _scope)
                Ios18Palette.FillRoundedCard(g, pixel, pixel.Height / 2f, ios.SegmentedSelected,
                    ios.IsDark ? Color.Transparent : Color.FromArgb(22, 0, 0, 0), ios.IsDark ? 0f : 0.75f);
            else if (_hover == key || _pressed == key)
                Ios18Palette.FillRoundedCard(g, pixel, pixel.Height / 2f, _pressed == key ? ios.ControlPressed : ios.ControlHover, Color.Transparent, 0f);
            using var font = CodexUi.UiFont(SegmentPx * .75f, semibold: index == _scope);
            TextAt(g, titles[index], font, segment, ios.TextPrimary, StringAlignment.Center);
            var captured = index;
            _hits.Add(new(key, segment, false, () => SelectScope(captured)));
        }
    }

    private static float MeasureContent(IReadOnlyList<Section> sections)
    {
        var y = 8f + 148f + 42f;
        foreach (var section in sections)
            y += 22f + section.Rows.Count * RowHeight + (section.Footnote is null ? 24f : 46f);
        return y + RowHeight + 7f + 18f + 12f;
    }

    private void DrawContentLayer(Graphics target, Ios18Palette ios, UsageTotals totals, IReadOnlyList<Section> sections)
    {
        var layerWidth = (int)Math.Round((SheetWidth - LayerInset * 2) * _scale);
        var layerHeight = (int)Math.Round(ViewportHeight * _scale);
        if (layerWidth < 1 || layerHeight < 1) return;
        using var layer = new Bitmap(layerWidth, layerHeight, PixelFormat.Format32bppPArgb);
        layer.SetResolution(96, 96);
        using (var g = Graphics.FromImage(layer))
        using (AppleText.Composite(layer))
        {
            g.Clear(ios.WindowSurface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.ScaleTransform(_scale, _scale);
            g.TranslateTransform(-LayerInset, -(ViewportTop + _scroll));
            DrawContent(g, ios, totals, sections);
        }
        Ios18Palette.DrawExact(target, layer, new RectangleF(LayerInset, ViewportTop, SheetWidth - LayerInset * 2, ViewportHeight));
        DrawEdgeFade(target, ios, ViewportTop, true);
        DrawEdgeFade(target, ios, ViewportTop + ViewportHeight, false);
    }

    private void DrawEdgeFade(Graphics g, Ios18Palette ios, float edge, bool top)
    {
        const float depth = 12f;
        if (top ? _scroll <= 0.5f : _scroll >= MaxScroll - 0.5f) return;
        var rect = new RectangleF(LayerInset, top ? edge : edge - depth, SheetWidth - LayerInset * 2, depth);
        using var brush = new LinearGradientBrush(new RectangleF(rect.X, rect.Y - 1, rect.Width, rect.Height + 2),
            top ? ios.WindowSurface : Color.FromArgb(0, ios.WindowSurface), top ? Color.FromArgb(0, ios.WindowSurface) : ios.WindowSurface, 90f);
        g.FillRectangle(brush, rect);
    }

    private void DrawContent(Graphics g, Ios18Palette ios, UsageTotals totals, IReadOnlyList<Section> sections)
    {
        using var body = CodexUi.UiFont(BodyPx * .75f);
        using var footnote = CodexUi.UiFont(FootnotePx * .75f);
        // Filled, not stroked: a 1 px pen straddles two pixel rows here and overshoots the card edge.
        using var separator = new SolidBrush(ios.Separator);
        var contentLeft = CardLeft + ContentInset;
        var contentWidth = CardWidth - ContentInset * 2;
        var y = ViewportTop + 8f;

        var hero = new Rectangle((int)CardLeft, (int)y, (int)CardWidth, 148);
        DrawHero(g, ios, hero, totals, footnote);
        var heroNote = _state is null ? CodexUi.T("正在后台读取用量…", "Reading usage in the background…")
            : _scope == 0 ? (_selected?.ThreadId is { } thread ? CodexUi.T("本对话：", "Chat: ") + ThreadName(thread) : CodexUi.T("尚未选择对话", "No chat selected"))
            : CodexUi.T($"共 {N(totals.Total)} token · 日期按 UTC", $"{N(totals.Total)} tokens · dates in UTC");
        TextAt(g, heroNote, footnote, new RectangleF(contentLeft, hero.Bottom + 7, contentWidth, 18), ios.TextSecondary, StringAlignment.Near);
        y = hero.Bottom + 42f;

        foreach (var section in sections)
        {
            TextAt(g, CodexUi.English ? section.Title.ToUpperInvariant() : section.Title, footnote,
                new RectangleF(contentLeft, y, contentWidth, 18), ios.TextSecondary, StringAlignment.Near);
            var card = new Rectangle((int)CardLeft, (int)(y + 22f), (int)CardWidth, (int)(section.Rows.Count * RowHeight));
            Ios18Palette.FillRoundedCard(g, card, CardRadius, ios.CardSurface, Color.Transparent, 0f);
            for (var index = 0; index < section.Rows.Count; index++)
            {
                var rowTop = card.Top + index * RowHeight;
                if (index > 0) g.FillRectangle(separator, contentLeft, rowTop, card.Right - contentLeft, 1f / _scale);
                var row = section.Rows[index];
                var valueWidth = row.Value.Length == 0 ? 0f : (AppleText.Measure(g, row.Value, body) ?? 0f) + 12f;
                TextAt(g, row.Label, body, new RectangleF(contentLeft, rowTop, contentWidth - valueWidth, RowHeight),
                    row.Value.Length == 0 ? ios.TextSecondary : ios.TextPrimary, StringAlignment.Near);
                if (row.Value.Length > 0)
                    TextAt(g, row.Value, body, new RectangleF(contentLeft, rowTop, contentWidth, RowHeight), ios.TextSecondary, StringAlignment.Far);
            }
            y = card.Bottom;
            if (section.Footnote is { } note)
            {
                TextAt(g, note, footnote, new RectangleF(contentLeft, y + 7, contentWidth, 18), ios.TextSecondary, StringAlignment.Near);
                y += 46f;
            }
            else y += 24f;
        }

        var export = new Rectangle((int)CardLeft, (int)y, (int)CardWidth, (int)RowHeight);
        var exportFill = _pressed == "export" ? ios.ControlFill : _hover == "export" ? ios.InputFill : ios.CardSurface;
        Ios18Palette.FillRoundedCard(g, export, CardRadius, exportFill, Color.Transparent, 0f);
        TextAt(g, CodexUi.T("导出 JSON", "Export JSON"), body, new RectangleF(contentLeft, export.Top, contentWidth, RowHeight),
            _state is null ? ios.TextSecondary : ios.Accent, StringAlignment.Near);
        _hits.Add(new("export", export, true, Export));
        y = export.Bottom + 7f;
        var last = _state?.OfficialDays.Count > 0 ? _state.OfficialDays.Keys.Max().ToString("MM-dd") : CodexUi.T("未连接", "offline");
        var local = _state?.LocalFetched.ToLocalTime().ToString("HH:mm:ss") ?? "—";
        TextAt(g, _notice ?? _state?.Error ?? CodexUi.T($"官方历史截至 {last} · 本机更新 {local}", $"Official history to {last} · local update {local}"),
            footnote, new RectangleF(contentLeft, y, contentWidth, 18), ios.TextSecondary, StringAlignment.Near);
    }

    /// <summary>Same iOS 18 summary tiles as the first-level panel: total, requests, cache hit rate and
    /// tokens per request as four equal cards in a two-column grid.</summary>
    private void DrawHero(Graphics g, Ios18Palette ios, Rectangle area, UsageTotals totals, Font caption)
    {
        using var valueFont = CodexUi.UiFont(18f, semibold: true);
        using var unitFont = CodexUi.UiFont(11.25f);
        void At(string text, Font font, float left, float width, float baseline, Color color)
        {
            var top = baseline - AppleText.BaselineOffset(g, font);
            AppleText.Draw(g, text, font, new RectangleF(left, top, width, 60f), color, StringAlignment.Near, StringAlignment.Near);
        }
        var empty = _state is null || (_scope == 0 && string.IsNullOrWhiteSpace(_selected?.ThreadId));
        var rate = totals.CachePercent is double percent ? percent.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—";
        var requestsText = !totals.RequestCountExact ? "—" : N(totals.Responses);
        var perRequest = totals.RequestCountExact && totals.Responses > 0 ? MetricFormat.CompactTokens(totals.Total / totals.Responses) : "—";
        var tiles = new[]
        {
            (CodexUi.T("总 Token", "Total tokens"), empty ? "—" : MetricFormat.CompactTokens(totals.Total), "token"),
            (CodexUi.T("请求", "Requests"), empty ? "—" : requestsText, CodexUi.T("次", "")),
            (CodexUi.T("缓存命中", "Cache hit"), empty ? "—" : rate, ""),
            (CodexUi.T("每次平均", "Per request"), empty ? "—" : perRequest, "token")
        };
        const float gap = 12f;
        var tileWidth = (area.Width - gap) / 2f;
        var tileHeight = (area.Height - gap) / 2f;
        for (var index = 0; index < tiles.Length; index++)
        {
            var tile = Rectangle.Round(new RectangleF(area.Left + (index % 2) * (tileWidth + gap), area.Top + (index / 2) * (tileHeight + gap), tileWidth, tileHeight));
            Ios18Palette.FillRoundedCard(g, tile, CardRadius, ios.CardSurface, Color.Transparent, 0f);
            var left = tile.Left + ContentInset;
            var width = tile.Width - 24f;
            At(tiles[index].Item1, caption, left, width, tile.Top + 25f, ios.TextSecondary);
            var value = tiles[index].Item2;
            At(value, valueFont, left, width, tile.Top + 53f, ios.TextPrimary);
            if (tiles[index].Item3.Length == 0 || value == "—") continue;
            var valueWidth = AppleText.Measure(g, value, valueFont) ?? 0f;
            At(tiles[index].Item3, unitFont, left + valueWidth + 4f, Math.Max(0f, width - valueWidth - 4f), tile.Top + 53f, ios.TextSecondary);
        }
    }

    private void DrawScrollIndicator(Graphics g, Ios18Palette ios)
    {
        if (MaxScroll <= 0f) return;
        var trackHeight = ViewportHeight - 16f;
        var thumbHeight = Math.Max(36f, trackHeight * ViewportHeight / _contentHeight);
        var thumbTop = ViewportTop + 8f + (trackHeight - thumbHeight) * (_scroll / MaxScroll);
        var color = ios.IsDark ? Color.FromArgb(110, 255, 255, 255) : Color.FromArgb(90, 0, 0, 0);
        Ios18Palette.FillRoundedCard(g, Rectangle.Round(new RectangleF(SheetWidth - 9f, thumbTop, 3f, thumbHeight)), 1.5f, color, Color.Transparent, 0f);
    }

    private static void TextAt(Graphics g, string text, Font font, RectangleF bounds, Color color, StringAlignment alignment)
    {
        if (AppleText.Draw(g, text, font, bounds, color, alignment, StringAlignment.Center)) return;
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = alignment, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(text, font, brush, bounds, format);
    }

    internal Bitmap Preview(float scale, int tab = 0)
    {
        _scale = scale;
        ClientSize = new Size((int)Math.Round(SheetWidth * scale), (int)Math.Round(SheetHeight * scale));
        _scope = Math.Clamp(tab, 0, ScopeTitles.Length - 1);
        _scroll = 0f;
        return DrawFrame();
    }

    private Hit? HitAt(Point location)
    {
        var point = new PointF(location.X / _scale, location.Y / _scale);
        for (var index = _hits.Count - 1; index >= 0; index--)
        {
            var hit = _hits[index];
            var bounds = hit.Scrolls ? new RectangleF(hit.Bounds.X, hit.Bounds.Y - _scroll, hit.Bounds.Width, hit.Bounds.Height) : hit.Bounds;
            if (hit.Scrolls && (point.Y < ViewportTop || point.Y > ViewportTop + ViewportHeight)) continue;
            if (bounds.Contains(point)) return hit;
        }
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var key = HitAt(e.Location)?.Key;
        if (key == _hover) return;
        _hover = key;
        Cursor = key is null ? Cursors.Default : Cursors.Hand;
        PaintFrame();
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover is null) return; _hover = null; PaintFrame(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || HitAt(e.Location) is not { } hit) return;
        _pressed = hit.Key; Capture = true; PaintFrame();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var hit = HitAt(e.Location);
        var pressed = _pressed;
        _pressed = null; Capture = false;
        if (e.Button == MouseButtons.Left && hit is not null && hit.Key == pressed) hit.Action();
        if (!IsDisposed) PaintFrame();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollBy(-e.Delta / 120f * 64f);
    }

    private void ScrollBy(float delta)
    {
        var next = Math.Clamp(_scroll + delta, 0f, MaxScroll);
        if (Math.Abs(next - _scroll) < 0.01f) return;
        _scroll = next;
        PaintFrame();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keys)
    {
        switch (keys)
        {
            case Keys.Escape: Close(); return true;
            case Keys.Down: ScrollBy(RowHeight); return true;
            case Keys.Up: ScrollBy(-RowHeight); return true;
            case Keys.PageDown or Keys.Space: ScrollBy(ViewportHeight - RowHeight); return true;
            case Keys.PageUp: ScrollBy(-(ViewportHeight - RowHeight)); return true;
            case Keys.Home: ScrollBy(-_scroll); return true;
            case Keys.End: ScrollBy(MaxScroll); return true;
            case Keys.Left: SelectScope((_scope + ScopeTitles.Length - 1) % ScopeTitles.Length); return true;
            case Keys.Right: SelectScope((_scope + 1) % ScopeTitles.Length); return true;
        }
        return base.ProcessCmdKey(ref msg, keys);
    }

    private void Export()
    {
        if (_state is null) return;
        using var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = "codex-token-usage-" + DateTime.Now.ToString("yyyyMMdd") + ".json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new
            {
                exportedAt = DateTimeOffset.Now,
                threadId = _selected?.ThreadId,
                officialDays = _state.OfficialDays.ToDictionary(pair => pair.Key.ToString("yyyy-MM-dd"), pair => pair.Value),
                entries = _state.Entries.Select(e => new { e.Id, e.Thread, e.Turn, e.At, e.Model, e.Input, e.Cached, e.Output, e.Reasoning })
            }, new JsonSerializerOptions { WriteIndented = true }));
            _notice = CodexUi.T("JSON 已导出", "JSON exported");
        }
        catch (IOException) { _notice = CodexUi.T("文件无法写入，请选择其他位置", "Could not write the file; choose another location"); }
        catch (UnauthorizedAccessException) { _notice = CodexUi.T("没有写入权限，请选择其他位置", "No write permission; choose another location"); }
        PaintFrame();
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new StatisticsAccessible(this);

    private sealed class StatisticsAccessible(StatisticsForm form) : ControlAccessibleObject(form)
    {
        public override string? Name { get => CodexUi.T("用量统计", "Usage statistics"); set { } }
        public override string? Value
        {
            get
            {
                var (totals, sections) = form.BuildModel();
                return $"{ScopeTitles[form._scope]}：{N(totals.Total)}；" + string.Join("；", sections.SelectMany(s => s.Rows).Select(r => $"{r.Label} {r.Value}"));
            }
            set { }
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) _dataTimer.Dispose(); base.Dispose(disposing); }
}
