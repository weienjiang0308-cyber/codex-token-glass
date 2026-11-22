using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CodexStatusbar;

internal sealed record SettingsToggle(string Section, string Title, Func<bool> Get, Action<bool> Set);

/// <summary>iOS 18 inset-grouped settings page pushed from the first-level panel: a Back / Done
/// navigation bar, changes applied on leaving (as in iOS Settings). Opens at the same place and size
/// as the first-level panel so the levels read as one navigation stack.</summary>
internal sealed class SettingsForm : Form
{
    private const int DesignWidthDip = 500;
    private const int OuterRadiusDip = 40;
    private const int CardRadiusDip = 20;
    private const float BodyPoints = 12.75f;
    private const float FootnotePoints = 9.75f;
    private readonly float _unit;
    private readonly Func<OverlayThemePalette> _currentPalette;
    private readonly Func<long?> _getLiveContext;
    private readonly Rectangle? _anchor;
    private readonly Action<SettingsForm>? _openStatistics;
    private readonly bool _pushStatisticsOnShown;
    /// <summary>True when the page was left with Back, so the caller re-opens the first-level panel.</summary>
    internal bool ReturnToPanel { get; private set; }
    private readonly NumericUpDown _window;
    private readonly NumericUpDown _percent;
    private readonly ContextSlider _slider;
    private readonly IosStepper _windowStepper;
    private readonly IosStepper _percentStepper;
    private readonly IosValueField _windowField;
    private readonly IosValueField _percentField;
    private readonly Label _contextFooter;
    private readonly Label _compactionFooter;
    private readonly Label _status;
    private readonly GlassButton _restore;
    private readonly Font _segmentFont;
    private readonly Font _segmentSelectedFont;
    private readonly List<(GlassButton Button, long Value)> _presetButtons = new();
    private readonly List<(GlassButton Button, string Code)> _languageButtons = new();
    private readonly List<(IosSwitch Switch, SettingsToggle Toggle)> _switches = new();
    private readonly List<Rectangle> _cards = new();
    private readonly List<Rectangle> _segmentTracks = new();
    private readonly List<(int Y, int Left, int Right)> _separators = new();
    private readonly System.Windows.Forms.Timer _themeTimer = new() { Interval = 1000 };
    private readonly UiFrameClock _motionTimer = new();
    private SoftWindowEdge? _smoothEdge;
    private OverlayThemePalette? _appliedPalette;
    private int? _pendingContextValue;
    private bool _syncing;
    internal bool MotionProbeActive;
    internal readonly List<double> ProbeRenderMs = new();
    internal readonly List<double> ProbeFrameTimes = new();
    internal ContextSlider ProbeContext => _slider;
    internal Rectangle ReadingPlateBounds => _cards.Count == 0 ? Rectangle.Empty : _cards.Aggregate(Rectangle.Union);
    internal Ios18Palette ActiveIosPalette => Ios18Palette.FromOverlay(_currentPalette());
    internal int CardRadius => S(CardRadiusDip);
    private long CompactAt => (long)(_window.Value * _percent.Value / 100m);
    private int S(double dip) => (int)Math.Round(dip * _unit);

    public SettingsForm(Func<OverlayThemePalette> palette, Func<long?> liveContext, Rectangle? anchor = null,
        IReadOnlyList<SettingsToggle>? toggles = null, Action<SettingsForm>? openStatistics = null, bool pushStatisticsOnShown = false)
    {
        _openStatistics = openStatistics;
        _pushStatisticsOnShown = pushStatisticsOnShown && openStatistics is not null;
        _currentPalette = palette;
        _getLiveContext = liveContext;
        _anchor = anchor;
        var initial = ContextConfiguration.Read();
        _unit = DeviceDpi / 96f;
        var ios = Ios18Palette.FromOverlay(palette());
        Text = CodexUi.T("用量设置", "Usage settings");
        Font = CodexUi.UiFont(BodyPoints);
        BackColor = ios.WindowSurface;
        ForeColor = ios.TextPrimary;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        _segmentFont = CodexUi.UiFont(10.5f);
        _segmentSelectedFont = CodexUi.UiFont(10.5f, semibold: true);
        toggles ??= PreviewToggles();

        _window = new NumericUpDown
        {
            Minimum = 32_000, Maximum = ContextConfiguration.SliderMaximum, Increment = 10_000, ThousandsSeparator = true,
            Value = Math.Clamp(initial.Window, 32_000, ContextConfiguration.SliderMaximum),
            Visible = false, TabStop = false, Bounds = new Rectangle(0, 0, 1, 1),
            AccessibleName = CodexUi.T("上下文 token 数量", "Context tokens")
        };
        _percent = new NumericUpDown
        {
            Minimum = 50, Maximum = 90, DecimalPlaces = 2, Increment = 1,
            Value = Math.Clamp(Math.Round((decimal)initial.CompactAt * 100 / initial.Window, 2), 50, 90),
            Visible = false, TabStop = false, Bounds = new Rectangle(0, 0, 1, 1),
            AccessibleName = CodexUi.T("自动压缩百分比", "Compaction percentage")
        };
        Controls.Add(_window);
        Controls.Add(_percent);

        var width = anchor is { Width: var anchorWidth } && anchorWidth >= S(380) ? anchorWidth : S(DesignWidthDip);
        var cardLeft = S(20);
        var cardRight = width - S(20);
        var cardWidth = cardRight - cardLeft;
        var contentLeft = cardLeft + S(16);
        var contentRight = cardRight - S(16);
        var contentWidth = contentRight - contentLeft;
        var rowHeight = S(50);

        var navTop = S(OverlayLayoutCalculator.NavCenterYDip) - S(22);
        // The trailing control is centred on the centre of the sheet's top-right corner arc, exactly
        // where the panel's settings glyph sits, so nothing shifts between the pages; the back button
        // mirrors its distance from the edge.
        var doneText = CodexUi.T("完成", "Done");
        float doneWidthDip;
        using (var doneFont = CodexUi.UiFont(BodyPoints, semibold: true)) doneWidthDip = AppleText.MeasureAt96(doneText, doneFont);
        var back = AddButton(CodexUi.T("返回", "Back"), new Rectangle(S(Ios18Palette.NavEdgeInset(doneWidthDip)) - S(1), navTop, S(96), S(44)), "back", BodyPoints, semibold: false);
        back.AccessibleName = CodexUi.T("返回" + TokenStripForm.RootTitleChinese, "Back to " + TokenStripForm.RootTitleEnglish);
        back.Click += (_, _) => GoBack();
        var done = AddButton(doneText, new Rectangle(width - S(OverlayLayoutCalculator.NavCenterYDip) - S(44), navTop, S(88), S(44)), "text", BodyPoints, semibold: true);
        done.Click += (_, _) => CompleteAndClose();
        var title = AddLabel(Text, S(130), navTop, width - S(260), S(44), BodyPoints, bold: true);
        title.TextAlign = ContentAlignment.MiddleCenter;

        var y = SectionHeader(CodexUi.T("上下文窗口", "Context window"), contentLeft, contentWidth, S(OverlayLayoutCalculator.FirstCardTopDip - 22));
        var cardTop = y;
        AddLabel(CodexUi.T("窗口大小", "Window size"), contentLeft, y, S(120), rowHeight, BodyPoints);
        _windowStepper = AddStepper(new Rectangle(contentRight - S(94), y + (rowHeight - S(32)) / 2, S(94), S(32)),
            CodexUi.T("调整上下文窗口", "Adjust context window"));
        _windowField = AddValueField(new Rectangle(contentLeft + S(124), y, _windowStepper.Left - S(12) - (contentLeft + S(124)), rowHeight),
            CodexUi.T("上下文窗口数值", "Context window value"), ParseWindow,
            value => _window.Value = Math.Clamp(Math.Round(value), _window.Minimum, _window.Maximum));
        y += rowHeight;
        _separators.Add((y, contentLeft, cardRight));
        var minimumCaption = AddLabel("32K", contentLeft, y + 1, S(40), rowHeight - 2, FootnotePoints, muted: true);
        var maximumCaption = AddLabel("1.05M", contentRight - S(44), y + 1, S(44), rowHeight - 2, FootnotePoints, muted: true);
        maximumCaption.TextAlign = ContentAlignment.MiddleRight;
        _slider = new ContextSlider(palette())
        {
            Bounds = Rectangle.FromLTRB(minimumCaption.Right, y + (rowHeight - S(32)) / 2, maximumCaption.Left, y + (rowHeight + S(32)) / 2),
            Minimum = 32_000, Maximum = (int)ContextConfiguration.SliderMaximum, Value = (int)_window.Value,
            AccessibleName = CodexUi.T("上下文容量滑块", "Context capacity slider")
        };
        Controls.Add(_slider);
        y += rowHeight;
        _separators.Add((y, contentLeft, cardRight));
        // Last row of the card: the capsule keeps the same 16 DIP from the card's bottom edge as from
        // its sides, so it sits evenly inside the card's rounded corners.
        AddSegments(new Rectangle(contentLeft, y + S(9), contentWidth, S(32)),
            new[] { ("500K", 500_000L), ("1M", 1_000_000L), ("1.05M", 1_050_000L) },
            (button, value) =>
            {
                _presetButtons.Add((button, value));
                button.AccessibleName = value.ToString("N0", CultureInfo.InvariantCulture);
                button.Click += (_, _) => _window.Value = value;
            });
        y += S(57);
        _cards.Add(new Rectangle(cardLeft, cardTop, cardWidth, y - cardTop));
        _contextFooter = AddLabel("", contentLeft, y + S(7), contentWidth, S(36), FootnotePoints, muted: true);
        y += S(58);

        y = SectionHeader(CodexUi.T("自动压缩", "Auto-compaction"), contentLeft, contentWidth, y);
        cardTop = y;
        AddLabel(CodexUi.T("压缩阈值", "Compact at"), contentLeft, y, S(120), rowHeight, BodyPoints);
        _percentStepper = AddStepper(new Rectangle(contentRight - S(94), y + (rowHeight - S(32)) / 2, S(94), S(32)),
            CodexUi.T("调整压缩阈值", "Adjust compaction threshold"));
        _percentField = AddValueField(new Rectangle(contentLeft + S(124), y, _percentStepper.Left - S(12) - (contentLeft + S(124)), rowHeight),
            CodexUi.T("压缩阈值百分比", "Compaction percentage value"), ParsePercent,
            value => _percent.Value = Math.Clamp(Math.Round(value, 2), _percent.Minimum, _percent.Maximum));
        y += rowHeight;
        _cards.Add(new Rectangle(cardLeft, cardTop, cardWidth, y - cardTop));
        _compactionFooter = AddLabel("", contentLeft, y + S(7), contentWidth, S(18), FootnotePoints, muted: true);
        y += S(46);

        foreach (var section in toggles.GroupBy(toggle => toggle.Section))
        {
            y = SectionHeader(section.Key, contentLeft, contentWidth, y);
            cardTop = y;
            var first = true;
            foreach (var toggle in section)
            {
                if (!first) _separators.Add((y, contentLeft, cardRight));
                first = false;
                AddLabel(toggle.Title, contentLeft, y + 1, contentWidth - S(64), rowHeight - 1, BodyPoints);
                var control = new IosSwitch
                {
                    Bounds = new Rectangle(contentRight - S(51), y + (rowHeight - S(31)) / 2, S(51), S(31)),
                    AccessibleName = toggle.Title
                };
                control.SetCheckedSilently(toggle.Get());
                var captured = toggle;
                control.CheckedChanged += (_, _) =>
                {
                    captured.Set(control.Checked);
                    var actual = captured.Get();
                    if (actual != control.Checked) control.Checked = actual;
                };
                Controls.Add(control);
                _switches.Add((control, toggle));
                y += rowHeight;
            }
            _cards.Add(new Rectangle(cardLeft, cardTop, cardWidth, y - cardTop));
            y += S(24);
        }

        y = SectionHeader(CodexUi.T("语言", "Language"), contentLeft, contentWidth, y);
        // The control is the whole section, so it stands on the page at card width, as the scope
        // switch does on the statistics page. Inside a one-row card the capsule had 16 DIP of card at
        // its ends but 9 above and below, so it never sat evenly within the card's corners.
        AddSegments(new Rectangle(cardLeft, y, cardWidth, S(32)),
            new[] { (CodexUi.T("跟随 Codex", "Auto"), 0L), ("English", 1L), ("简体中文", 2L) },
            (button, value) =>
            {
                var code = value switch { 1 => "en", 2 => "zh", _ => "auto" };
                _languageButtons.Add((button, code));
                button.Click += (_, _) =>
                {
                    CodexUi.Language = code;
                    UpdateSegments();
                    _status!.Text = CodexUi.T("语言已更新，重新打开设置后生效。", "Language updated. Reopen Settings to apply.");
                };
            });
        y += S(32);
        _status = AddLabel("", contentLeft, y + S(7), contentWidth, S(18), FootnotePoints, muted: true);
        y += S(37);

        cardTop = y;
        var statistics = AddButton(CodexUi.T("用量统计", "Usage statistics"), new Rectangle(cardLeft, y, cardWidth, rowHeight), "list", BodyPoints, semibold: false);
        statistics.ListPosition = 1;
        statistics.ShowsChevron = true;
        statistics.Enabled = openStatistics is not null;
        statistics.Click += (_, _) => openStatistics?.Invoke(this);
        y += rowHeight;
        _restore = AddButton(CodexUi.T("恢复原始配置", "Restore original configuration"), new Rectangle(cardLeft, y, cardWidth, rowHeight), "list", BodyPoints, semibold: false);
        _restore.ListPosition = 2;
        _restore.Enabled = ContextConfiguration.CanRestore;
        _restore.Click += (_, _) => RestoreContext();
        y += rowHeight;
        _cards.Add(new Rectangle(cardLeft, cardTop, cardWidth, y - cardTop));
        y += S(20);
        ClientSize = new Size(width, Math.Max(y, S(OverlayLayoutCalculator.SheetHeightDip)));
        Location = ResolveLocation();

        _window.ValueChanged += (_, _) =>
        {
            if (!_syncing)
            {
                _syncing = true;
                _slider.Value = (int)_window.Value;
                _syncing = false;
            }
            UpdateValues();
        };
        _percent.ValueChanged += (_, _) => UpdateValues();
        _slider.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _pendingContextValue = _slider.Value;
            FlushPendingInput();
        };
        _windowStepper.Stepped += (_, delta) =>
            _window.Value = Math.Clamp(_window.Value + delta * _window.Increment, _window.Minimum, _window.Maximum);
        _percentStepper.Stepped += (_, delta) =>
            _percent.Value = Math.Clamp(_percent.Value + delta, _percent.Minimum, _percent.Maximum);
        UpdateValues();
        _themeTimer.Tick += (_, _) => { RefreshPalette(); UpdateFooters(); };
        _themeTimer.Start();
        _motionTimer.Tick += (_, _) =>
        {
            _slider.Invalidate();
            if (!_slider.PositionAnimating) _motionTimer.Stop();
        };
    }

    private static IReadOnlyList<SettingsToggle> PreviewToggles()
    {
        var state = new Dictionary<string, bool> { ["cache"] = true, ["context"] = true, ["timing"] = true, ["transparent"] = true, ["shadow"] = false };
        SettingsToggle Make(string section, string title, string key) => new(section, title, () => state[key], value => state[key] = value);
        var display = CodexUi.T("显示", "Display");
        return new[]
        {
            Make(display, CodexUi.T("缓存明细", "Cache details"), "cache"),
            Make(display, CodexUi.T("上下文用量", "Context usage"), "context"),
            Make(display, CodexUi.T("耗时统计", "Timing"), "timing"),
            Make(display, CodexUi.T("透明状态条", "Transparent strip"), "transparent"),
            Make(display, CodexUi.T("状态条文字阴影", "Strip text shadow"), "shadow")
        };
    }

    private static decimal? ParseWindow(string text)
    {
        var cleaned = text.Replace(",", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Trim().ToLowerInvariant();
        var multiplier = 1m;
        if (cleaned.EndsWith('k')) { multiplier = 1_000m; cleaned = cleaned[..^1]; }
        else if (cleaned.EndsWith('m')) { multiplier = 1_000_000m; cleaned = cleaned[..^1]; }
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value * multiplier : null;
    }

    private static decimal? ParsePercent(string text)
    {
        var cleaned = text.Replace("%", "", StringComparison.Ordinal).Replace(",", ".", StringComparison.Ordinal).Trim();
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private Point ResolveLocation()
    {
        if (_anchor is not Rectangle anchor)
        {
            var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
            return new Point(screen.Left + (screen.Width - Width) / 2, screen.Top + (screen.Height - Height) / 2);
        }
        var area = Screen.FromRectangle(anchor).WorkingArea;
        var anchorInLowerHalf = anchor.Top + anchor.Height / 2 > area.Top + area.Height / 2;
        var x = Math.Clamp(anchor.Left, area.Left, Math.Max(area.Left, area.Right - Width));
        var y = anchorInLowerHalf ? anchor.Bottom - Height : anchor.Top;
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - Height));
        return new Point(x, y);
    }

    internal void FlushPendingInput()
    {
        if (_pendingContextValue is not int context) return;
        _pendingContextValue = null;
        _syncing = true;
        try { _window.Value = Math.Clamp(context, _window.Minimum, _window.Maximum); }
        finally { _syncing = false; }
        UpdateValues();
    }

    private void UpdateValues()
    {
        _windowField.ShowValue(_window.Value.ToString("N0", CultureInfo.InvariantCulture));
        _percentField.ShowValue(_percent.Value.ToString("0.00", CultureInfo.InvariantCulture) + "%");
        UpdateFooters();
        _windowStepper.SetLimits(_window.Value > _window.Minimum, _window.Value < _window.Maximum);
        _percentStepper.SetLimits(_percent.Value > _percent.Minimum, _percent.Value < _percent.Maximum);
        UpdateSegments();
    }

    private void UpdateFooters()
    {
        var live = _getLiveContext();
        var display = live?.ToString("N0", CultureInfo.InvariantCulture) ?? CodexUi.T("暂无记录", "not recorded");
        _contextFooter.Text = CodexUi.T(
            $"写入 Codex 全局配置，可直接输入，如 800k。\n当前对话报告窗口 {display}（仅供参考）。",
            $"Global Codex setting. Type a value such as 800k.\nCurrent chat reports {display} (reference only).");
        _compactionFooter.Text = CodexUi.T(
            $"达到 {CompactAt.ToString("N0", CultureInfo.InvariantCulture)} token 时压缩 · 按钮每次 ±1%",
            $"Compacts at {CompactAt.ToString("N0", CultureInfo.InvariantCulture)} tokens · buttons step ±1%");
    }

    private void UpdateSegments()
    {
        var current = (long)_window.Value;
        foreach (var (button, value) in _presetButtons) SetSegment(button, current == value);
        foreach (var (button, code) in _languageButtons) SetSegment(button, CodexUi.Language == code);
    }

    private void SetSegment(GlassButton button, bool selected)
    {
        var font = selected ? _segmentSelectedFont : _segmentFont;
        if (button.SegmentSelected == selected && ReferenceEquals(button.Font, font)) return;
        button.SegmentSelected = selected;
        button.Font = font;
        button.Invalidate();
    }

    private bool SaveContext()
    {
        _windowField.Commit();
        _percentField.Commit();
        FlushPendingInput();
        try
        {
            var current = ContextConfiguration.Read();
            if (current.Window == (long)_window.Value && current.CompactAt == CompactAt) return true;
            ContextConfiguration.Save((long)_window.Value, CompactAt);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            _status.Text = CodexUi.T("无法保存：", "Could not save: ") + e.Message;
            return false;
        }
    }

    private void RestoreContext()
    {
        try
        {
            ContextConfiguration.Restore();
            var current = ContextConfiguration.Read();
            _window.Value = Math.Clamp(current.Window, (long)_window.Minimum, (long)_window.Maximum);
            _percent.Value = Math.Clamp(Math.Round((decimal)current.CompactAt * 100 / current.Window, 2), 50, 90);
            _restore.Enabled = ContextConfiguration.CanRestore;
            _status.Text = CodexUi.T("已恢复原始配置。", "Original configuration restored.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        { _status.Text = CodexUi.T("无法恢复：", "Could not restore: ") + e.Message; }
    }

    private int SectionHeader(string text, int left, int width, int y)
    {
        AddLabel(CodexUi.English ? text.ToUpperInvariant() : text, left, y, width, S(18), FootnotePoints, muted: true);
        return y + S(22);
    }

    private Label AddLabel(string text, int x, int y, int width, int height, float size, bool bold = false, bool muted = false)
    {
        var ios = ActiveIosPalette;
        var label = new GlassLabel
        {
            Text = text, Bounds = new Rectangle(x, y, width, height), UseCompatibleTextRendering = true,
            Font = CodexUi.UiFont(Math.Max(9f, size), semibold: bold),
            ForeColor = muted ? ios.TextSecondary : ios.TextPrimary, BackColor = Color.Transparent,
            TextAlign = height >= S(30) ? ContentAlignment.MiddleLeft : ContentAlignment.TopLeft,
            Tag = muted ? "muted" : "text"
        };
        Controls.Add(label);
        return label;
    }

    private GlassButton AddButton(string text, Rectangle bounds, string tag, float size, bool semibold = true)
    {
        var ios = ActiveIosPalette;
        var button = new GlassButton
        {
            Text = text, Bounds = bounds, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
            Font = tag == "segment" ? _segmentFont : CodexUi.UiFont(size, semibold),
            ForeColor = ios.TextPrimary, BackColor = ios.CardSurface, Tag = tag
        };
        button.FlatAppearance.BorderColor = ios.CardSurface;
        button.FlatAppearance.BorderSize = 0;
        Controls.Add(button);
        return button;
    }

    private IosStepper AddStepper(Rectangle bounds, string accessibleName)
    {
        var stepper = new IosStepper { Bounds = bounds, AccessibleName = accessibleName };
        Controls.Add(stepper);
        return stepper;
    }

    private IosValueField AddValueField(Rectangle row, string accessibleName, Func<string, decimal?> parse, Action<decimal> apply)
    {
        var ios = ActiveIosPalette;
        var field = new IosValueField(parse, apply)
        {
            Font = CodexUi.UiFont(BodyPoints), AccessibleName = accessibleName,
            BackColor = ios.CardSurface, ForeColor = ios.TextSecondary, Width = row.Width
        };
        field.Location = new Point(row.Left, row.Top + (row.Height - field.Height) / 2 + S(2));
        field.EditingColor = ios.TextPrimary;
        field.IdleColor = ios.TextSecondary;
        Controls.Add(field);
        return field;
    }

    private void AddSegments(Rectangle track, (string Text, long Value)[] items, Action<GlassButton, long> configure)
    {
        _segmentTracks.Add(track);
        var inset = S(2);
        var segmentWidth = (track.Width - inset * 2) / items.Length;
        for (var index = 0; index < items.Length; index++)
        {
            var left = track.Left + inset + index * segmentWidth;
            var right = index == items.Length - 1 ? track.Right - inset : left + segmentWidth;
            var button = AddButton(items[index].Text, new Rectangle(left, track.Top + inset, right - left, track.Height - inset * 2), "segment", 10.5f);
            configure(button, items[index].Value);
        }
    }

    internal bool IsInsideReadingPlate(Control control) =>
        _cards.Any(card => card.Contains(control.Left + control.Width / 2, control.Top + control.Height / 2));

    internal Color ControlSurfaceColor(Control control)
    {
        var ios = ActiveIosPalette;
        var center = new Point(control.Left + control.Width / 2, control.Top + control.Height / 2);
        return _cards.Any(card => card.Contains(center)) ? ios.CardSurface : ios.WindowSurface;
    }

    internal Rectangle? SegmentTrackFor(Control control)
    {
        var center = new Point(control.Left + control.Width / 2, control.Top + control.Height / 2);
        foreach (var track in _segmentTracks)
            if (track.Contains(center)) return track;
        return null;
    }

    /// <summary>iOS tertiary fill is a translucent grey, so a track is darker on the grey page than on
    /// a white card. Returns whether the track stands directly on the page.</summary>
    internal bool SegmentTrackOnPage(Rectangle track) =>
        !_cards.Any(card => card.Contains(track.Left + track.Width / 2, track.Top + track.Height / 2));

    internal Color SegmentTrackColor(Rectangle track) =>
        SegmentTrackOnPage(track) ? ActiveIosPalette.ControlFill : ActiveIosPalette.SegmentedTrack;

    internal bool DrawControlMaterial(Control control, Graphics graphics)
    {
        graphics.Clear(ControlSurfaceColor(control));
        return true;
    }

    private void RefreshPalette()
    {
        var palette = _currentPalette();
        if (_appliedPalette == palette) return;
        _appliedPalette = palette;
        var ios = Ios18Palette.FromOverlay(palette);
        BackColor = ios.WindowSurface;
        ForeColor = ios.TextPrimary;
        foreach (Control child in Controls)
        {
            if (child is ContextSlider slider) { slider.SetPalette(palette); continue; }
            if (child is Label) child.ForeColor = Equals(child.Tag, "muted") ? ios.TextSecondary : ios.TextPrimary;
            if (child is GlassButton button) button.FlatAppearance.BorderColor = ios.CardSurface;
            if (child is IosValueField field)
            {
                field.BackColor = ios.CardSurface;
                field.EditingColor = ios.TextPrimary;
                field.IdleColor = ios.TextSecondary;
                field.ForeColor = field.Focused ? ios.TextPrimary : ios.TextSecondary;
            }
        }
        var dark = ios.IsDark ? 1 : 0;
        if (IsHandleCreated) DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        Invalidate(true);
        UpdateSmoothEdge();
    }

    internal void RefreshMaterial()
    {
        if (IsDisposed || Disposing) return;
        if (Visible && _slider is not null && _slider.PositionAnimating) _motionTimer.Start();
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(ActiveIosPalette.WindowSurface);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var ios = ActiveIosPalette;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var card in _cards)
            Ios18Palette.FillRoundedCard(e.Graphics, card, CardRadius, ios.CardSurface, Color.Transparent, 0f);
        using (var separator = new Pen(ios.Separator, 1f))
            foreach (var (y, left, right) in _separators) e.Graphics.DrawLine(separator, left, y, right, y);
        foreach (var track in _segmentTracks)
            Ios18Palette.FillRoundedCard(e.Graphics, track, track.Height / 2f, SegmentTrackColor(track), Color.Transparent, 0f);
        if (_smoothEdge is null || !_smoothEdge.Visible)
        {
            using var outline = Ios18Palette.RoundedRect(new RectangleF(0.5f, 0.5f, ClientSize.Width - 1f, ClientSize.Height - 1f), S(OuterRadiusDip));
            using var border = new Pen(ios.WindowBorder, 1f);
            e.Graphics.DrawPath(border, outline);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _smoothEdge ??= new SoftWindowEdge(this);
        RefreshPalette();
        UpdateSmoothEdge();
        ActiveControl = null;
        if (_pushStatisticsOnShown) BeginInvoke(() => _openStatistics!(this));
    }

    protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); _smoothEdge?.Follow(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) _smoothEdge?.Hide(); else UpdateSmoothEdge(); }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        var inset = Math.Max(2, S(2));
        using var shape = Ios18Palette.RoundedRect(
            new RectangleF(inset, inset, Math.Max(1, Width - 2 * inset), Math.Max(1, Height - 2 * inset)),
            Math.Max(1f, S(OuterRadiusDip) - inset));
        Region?.Dispose();
        Region = new Region(shape);
        UpdateSmoothEdge();
    }

    private void UpdateSmoothEdge()
    {
        if (!Visible || IsDisposed || _smoothEdge is null || ClientSize.Width < 4 || ClientSize.Height < 4) return;
        var ios = ActiveIosPalette;
        using var frame = new Bitmap(ClientSize.Width, ClientSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(frame))
        {
            graphics.Clear(Color.Transparent);
            Ios18Palette.FillRoundedCard(graphics, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), S(OuterRadiusDip),
                ios.WindowSurface, ios.WindowBorder, Math.Max(1f, _unit));
        }
        _smoothEdge.Present(frame, S(OuterRadiusDip));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        ActiveControl = null;
        if (e.Button == MouseButtons.Left && e.Y < S(66)) { ReleaseCapture(); SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); }
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape && ActiveControl is not IosValueField) { GoBack(); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }

    /// <summary>Pops back to the first-level panel. Like iOS Settings, leaving the page applies it.</summary>
    internal void GoBack()
    {
        if (!SaveContext()) return;
        ReturnToPanel = true;
        Close();
    }

    /// <summary>Done: applies the page and dismisses the whole navigation stack.</summary>
    internal void CompleteAndClose()
    {
        if (!SaveContext()) return;
        ReturnToPanel = false;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e) { CodexUi.CommitAppearance(); base.OnFormClosing(e); }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _smoothEdge?.Dispose();
            _themeTimer.Dispose();
            _motionTimer.Dispose();
        }
        base.Dispose(disposing);
        if (disposing)
        {
            _segmentFont.Dispose();
            _segmentSelectedFont.Dispose();
        }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr w, IntPtr l);
}

/// <summary>Inline editable value, iOS detail-text style: grey and right-aligned until focused.</summary>
internal sealed class IosValueField : TextBox
{
    private readonly Func<string, decimal?> _parse;
    private readonly Action<decimal> _apply;
    private string _shown = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color EditingColor { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color IdleColor { get; set; }

    public IosValueField(Func<string, decimal?> parse, Action<decimal> apply)
    {
        _parse = parse;
        _apply = apply;
        BorderStyle = BorderStyle.None;
        TextAlign = HorizontalAlignment.Right;
        Cursor = Cursors.IBeam;
    }

    internal void ShowValue(string text)
    {
        _shown = text;
        if (!Focused) Text = text;
    }

    internal void Commit()
    {
        if (Text != _shown && _parse(Text) is decimal value) _apply(value);
        Text = _shown;
    }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        ForeColor = EditingColor;
        BeginInvoke(SelectAll);
    }

    protected override void OnLeave(EventArgs e)
    {
        Commit();
        ForeColor = IdleColor;
        base.OnLeave(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter) { Commit(); SelectAll(); e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Escape) { Text = _shown; SelectAll(); e.SuppressKeyPress = true; }
        base.OnKeyDown(e);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar) && e.KeyChar is not ('.' or ',' or '%' or 'k' or 'K' or 'm' or 'M'))
            e.Handled = true;
        base.OnKeyPress(e);
    }
}

/// <summary>iOS switch: 51×31 capsule track, green when on, white knob with soft shadow.</summary>
internal sealed class IosSwitch : Control
{
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 15 };
    private bool _checked;
    private double _position;
    private double _from;
    private long _animationStart;
    private bool _pressed;
    public event EventHandler? CheckedChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            _from = _position;
            _animationStart = Environment.TickCount64;
            _animation.Start();
            AccessibleDescription = value ? CodexUi.T("开", "On") : CodexUi.T("关", "Off");
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IosSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.CheckButton;
        _animation.Tick += (_, _) =>
        {
            var t = Math.Clamp((Environment.TickCount64 - _animationStart) / 220d, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3);
            _position = _from + ((_checked ? 1 : 0) - _from) * eased;
            if (t >= 1) _animation.Stop();
            Invalidate();
        };
    }

    internal void SetCheckedSilently(bool value)
    {
        _checked = value;
        _position = value ? 1 : 0;
        AccessibleDescription = value ? CodexUi.T("开", "On") : CodexUi.T("关", "Off");
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _pressed = true; Focus(); Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_pressed) return;
        _pressed = false;
        if (ClientRectangle.Contains(e.Location)) Checked = !Checked; else Invalidate();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) Checked = !Checked;
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var form = Parent as SettingsForm;
        var ios = form?.ActiveIosPalette ?? Ios18Palette.Light;
        var graphics = e.Graphics;
        var unit = DeviceDpi / 96f;
        if (Width < 2 || Height < 2) return;
        var surface = form?.ControlSurfaceColor(this) ?? ios.CardSurface;
        var on = ios.SwitchOn;
        var off = ios.SwitchOff;
        var p = _position;
        var track = Color.FromArgb(
            (int)Math.Round(off.R + (on.R - off.R) * p),
            (int)Math.Round(off.G + (on.G - off.G) * p),
            (int)Math.Round(off.B + (on.B - off.B) * p));
        using (var bitmap = new SkiaSharp.SKBitmap(Width, Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul))
        {
            using (var canvas = new SkiaSharp.SKCanvas(bitmap))
            {
                canvas.Clear(new SkiaSharp.SKColor(surface.R, surface.G, surface.B));
                var trackRect = new SkiaSharp.SKRect(0, 0, Width, Height);
                var trackRadius = Height / 2f;
                using (var trackPaint = new SkiaSharp.SKPaint { IsAntialias = true, Color = new SkiaSharp.SKColor(track.R, track.G, track.B) })
                    canvas.DrawRoundRect(trackRect, trackRadius, trackRadius, trackPaint);
                var inset = 2f * unit;
                var knobHeight = Height - inset * 2;
                var knobWidth = _pressed ? knobHeight + 6f * unit : knobHeight;
                var travel = Width - inset * 2 - knobWidth;
                var left = inset + (float)(travel * p);
                var knob = new SkiaSharp.SKRect(left, inset, left + knobWidth, inset + knobHeight);
                var knobRadius = knobHeight / 2f;
                canvas.Save();
                using (var clip = new SkiaSharp.SKRoundRect(trackRect, trackRadius, trackRadius))
                    canvas.ClipRoundRect(clip, SkiaSharp.SKClipOperation.Intersect, true);
                using (var shadowPaint = new SkiaSharp.SKPaint
                {
                    IsAntialias = true,
                    Color = new SkiaSharp.SKColor(0, 0, 0, (byte)(ios.IsDark ? 90 : 46)),
                    MaskFilter = SkiaSharp.SKMaskFilter.CreateBlur(SkiaSharp.SKBlurStyle.Normal, 1.6f * unit)
                })
                {
                    var shadowRect = knob;
                    shadowRect.Offset(0, 1.2f * unit);
                    canvas.DrawRoundRect(shadowRect, knobRadius, knobRadius, shadowPaint);
                }
                canvas.Restore();
                using (var knobPaint = new SkiaSharp.SKPaint { IsAntialias = true, Color = SkiaSharp.SKColors.White })
                    canvas.DrawRoundRect(knob, knobRadius, knobRadius, knobPaint);
                using (var rimPaint = new SkiaSharp.SKPaint { IsAntialias = true, Style = SkiaSharp.SKPaintStyle.Stroke, StrokeWidth = 0.75f * unit, Color = new SkiaSharp.SKColor(0, 0, 0, 14) })
                {
                    var rim = knob;
                    rim.Inflate(-0.375f * unit, -0.375f * unit);
                    canvas.DrawRoundRect(rim, rim.Height / 2f, rim.Height / 2f, rimPaint);
                }
            }
            using var frame = new Bitmap(Width, Height, bitmap.RowBytes, System.Drawing.Imaging.PixelFormat.Format32bppPArgb, bitmap.GetPixels());
            graphics.DrawImageUnscaled(frame, 0, 0);
        }
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (Focused && ShowFocusCues)
        {
            using var focusPath = Ios18Palette.RoundedRect(new RectangleF(1, 1, Width - 2, Height - 2), (Height - 2) / 2f);
            using var focus = new Pen(ios.Accent, 1.5f) { DashStyle = DashStyle.Dot };
            graphics.DrawPath(focus, focusPath);
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) _animation.Dispose(); base.Dispose(disposing); }
}

/// <summary>iOS stepper: one capsule track split into − and + halves.</summary>
internal sealed class IosStepper : Control
{
    private readonly System.Windows.Forms.Timer _repeat = new() { Interval = 420 };
    private int _hover = -1;
    private int _pressed = -1;
    private bool _canDecrement = true;
    private bool _canIncrement = true;
    public event EventHandler<int>? Stepped;

    public IosStepper()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.SpinButton;
        _repeat.Tick += (_, _) => { _repeat.Interval = 70; Fire(_pressed); };
    }

    internal void SetLimits(bool canDecrement, bool canIncrement)
    {
        if (_canDecrement == canDecrement && _canIncrement == canIncrement) return;
        _canDecrement = canDecrement;
        _canIncrement = canIncrement;
        Invalidate();
    }

    internal void Press(int direction) => Fire(direction < 0 ? 0 : 1);

    private void Fire(int half)
    {
        if (half == 0 && _canDecrement) Stepped?.Invoke(this, -1);
        else if (half == 1 && _canIncrement) Stepped?.Invoke(this, 1);
        else _repeat.Stop();
    }

    private int HalfAt(int x) => x < Width / 2 ? 0 : 1;
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); var half = HalfAt(e.X); if (half != _hover) { _hover = half; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        _pressed = HalfAt(e.X);
        Invalidate();
        Fire(_pressed);
        _repeat.Interval = 420;
        _repeat.Start();
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = -1; _repeat.Stop(); Invalidate(); }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) { _pressed = -1; _repeat.Stop(); Invalidate(); } }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Left or Keys.Down) Fire(0);
        if (e.KeyCode is Keys.Right or Keys.Up) Fire(1);
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var form = Parent as SettingsForm;
        var ios = form?.ActiveIosPalette ?? Ios18Palette.Light;
        var graphics = e.Graphics;
        var unit = DeviceDpi / 96f;
        graphics.Clear(form?.ControlSurfaceColor(this) ?? ios.CardSurface);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new Rectangle(0, 0, Width, Height);
        var radius = Height / 2f;
        Ios18Palette.FillRoundedCard(graphics, track, radius, ios.SegmentedTrack, Color.Transparent, 0f);
        for (var half = 0; half < 2; half++)
        {
            var enabled = half == 0 ? _canDecrement : _canIncrement;
            if (!enabled || (_pressed != half && _hover != half)) continue;
            graphics.SetClip(new Rectangle(half == 0 ? 0 : Width / 2, 0, Width / 2 + 1, Height));
            Ios18Palette.FillRoundedCard(graphics, track, radius, _pressed == half ? ios.ControlPressed : ios.ControlFill, Color.Transparent, 0f);
            graphics.ResetClip();
        }
        using (var divider = new Pen(ios.IsDark ? Color.FromArgb(88, 88, 94) : Color.FromArgb(204, 204, 210), 1f))
            graphics.DrawLine(divider, Width / 2f, Height * .28f, Width / 2f, Height * .72f);
        var arm = 5.5f * unit;
        var centerY = Height / 2f;
        var minusX = Width / 4f;
        var plusX = Width * 3f / 4f;
        using (var minus = new Pen(_canDecrement ? ios.TextPrimary : Color.FromArgb(80, ios.TextPrimary), 1.8f * unit) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            graphics.DrawLine(minus, minusX - arm, centerY, minusX + arm, centerY);
        using (var plus = new Pen(_canIncrement ? ios.TextPrimary : Color.FromArgb(80, ios.TextPrimary), 1.8f * unit) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            graphics.DrawLine(plus, plusX - arm, centerY, plusX + arm, centerY);
            graphics.DrawLine(plus, plusX, centerY - arm, plusX, centerY + arm);
        }
        if (Focused && ShowFocusCues)
        {
            using var focusPath = Ios18Palette.RoundedRect(new RectangleF(1, 1, Width - 2, Height - 2), radius);
            using var focus = new Pen(ios.Accent, 1.5f) { DashStyle = DashStyle.Dot };
            graphics.DrawPath(focus, focusPath);
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) _repeat.Dispose(); base.Dispose(disposing); }
}

internal sealed class GlassButton : Button
{
    private bool _hovered;
    private bool _pressed;
    internal bool MaterialHovered => _hovered;
    internal bool MaterialPressed => _pressed;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool SegmentSelected { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int ListPosition { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool ShowsChevron { get; set; }
    private void MaterialChanged() { Invalidate(); if (Parent is SettingsForm form) form.RefreshMaterial(); }
    public GlassButton() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnMouseEnter(EventArgs e) { _hovered = true; MaterialChanged(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; MaterialChanged(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; MaterialChanged(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; MaterialChanged(); base.OnMouseUp(e); }
    protected override void OnMouseCaptureChanged(EventArgs e) { if (!Capture) { _pressed = false; MaterialChanged(); } base.OnMouseCaptureChanged(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode is Keys.Space or Keys.Enter) { _pressed = true; MaterialChanged(); } base.OnKeyDown(e); }
    protected override void OnKeyUp(KeyEventArgs e) { _pressed = false; MaterialChanged(); base.OnKeyUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    internal static Color ProminentFill(Ios18Palette ios, bool hovered, bool pressed) =>
        pressed ? ios.AccentPressed : hovered ? ios.AccentHover : ios.Accent;

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 1 || Height < 1) return;
        using var buffer = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        buffer.SetResolution(DeviceDpi, DeviceDpi);
        using (var graphics = Graphics.FromImage(buffer))
        using (AppleText.Composite(buffer))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            PaintButton(graphics);
        }
        e.Graphics.DrawImageUnscaled(buffer, 0, 0);
    }

    private void PaintButton(Graphics graphics)
    {
        var form = Parent as SettingsForm;
        var ios = form?.ActiveIosPalette ?? Ios18Palette.Light;
        var unit = DeviceDpi / 96f;
        var tag = Tag as string;
        var isNavText = tag is "text" or "text-leading" or "text-trailing" or "back";
        var isClose = Text == "×";
        var bounds = new Rectangle(0, 0, Width, Height);
        var capsule = Height / 2f;
        graphics.Clear(tag is "row" or "list" ? ios.WindowSurface : form?.ControlSurfaceColor(this) ?? ios.WindowSurface);
        Color textColor;
        if (tag == "segment")
        {
            var onPage = false;
            if (form?.SegmentTrackFor(this) is Rectangle track)
            {
                var local = new Rectangle(track.Left - Left, track.Top - Top, track.Width, track.Height);
                onPage = form.SegmentTrackOnPage(track);
                Ios18Palette.FillRoundedCard(graphics, local, track.Height / 2f, form.SegmentTrackColor(track), Color.Transparent, 0f);
            }
            if (SegmentSelected)
                Ios18Palette.FillRoundedCard(graphics, bounds, capsule, ios.SegmentedSelected,
                    ios.IsDark ? Color.Transparent : Color.FromArgb(22, 0, 0, 0), ios.IsDark ? 0f : 0.75f);
            else if (_hovered || _pressed)
                Ios18Palette.FillRoundedCard(graphics, bounds, capsule, _pressed ? ios.ControlPressed : onPage ? ios.ControlHover : ios.ControlFill, Color.Transparent, 0f);
            textColor = ios.TextPrimary;
        }
        else if (isNavText)
        {
            textColor = !Enabled ? ios.TextSecondary
                : _pressed ? Color.FromArgb(110, ios.Accent)
                : _hovered ? Color.FromArgb(185, ios.Accent)
                : ios.Accent;
        }
        else if (tag == "row")
        {
            var radius = form?.CardRadius ?? 12f * unit;
            Ios18Palette.FillRoundedCard(graphics, bounds, radius, Enabled && _pressed ? ios.ControlFill : Enabled && _hovered ? ios.InputFill : ios.CardSurface, Color.Transparent, 0f);
            textColor = Enabled ? ios.Accent : ios.TextSecondary;
        }
        else if (tag == "list")
        {
            var radius = form?.CardRadius ?? 20f * unit;
            var reach = (int)Math.Ceiling(radius * 2);
            var shape = ListPosition switch
            {
                1 => new Rectangle(0, 0, Width, Height + reach),
                2 => new Rectangle(0, -reach, Width, Height + reach),
                _ => bounds
            };
            var fill = Enabled && _pressed ? ios.ControlFill : Enabled && _hovered ? ios.InputFill : ios.CardSurface;
            Ios18Palette.FillRoundedCard(graphics, shape, radius, fill, Color.Transparent, 0f);
            if (ListPosition == 2)
            {
                using var separator = new Pen(ios.Separator, 1f);
                graphics.DrawLine(separator, 16f * unit, 0f, Width, 0f);
            }
            textColor = !Enabled ? ios.TextSecondary : ShowsChevron ? ios.TextPrimary : ios.Accent;
        }
        else if (isClose)
        {
            var diameter = 30f * unit;
            using var circle = new SolidBrush(_pressed ? ios.ControlPressed : _hovered ? ios.ControlHover : ios.ControlFill);
            graphics.FillEllipse(circle, new RectangleF((Width - diameter) / 2f, (Height - diameter) / 2f, diameter, diameter));
            textColor = ios.TextSecondary;
        }
        else if (tag == "primary")
        {
            var surface = Enabled ? ProminentFill(ios, _hovered, _pressed) : ios.ControlFill;
            Ios18Palette.FillRoundedCard(graphics, bounds, capsule, surface, Color.Transparent, 0f);
            textColor = Enabled ? ios.AccentText : ios.TextSecondary;
        }
        else
        {
            var surface = Enabled && _pressed ? ios.ControlPressed : Enabled && _hovered ? ios.ControlHover : ios.ControlFill;
            Ios18Palette.FillRoundedCard(graphics, bounds, capsule, surface, Color.Transparent, 0f);
            textColor = Enabled ? ios.Accent : ios.TextSecondary;
        }

        if (isClose)
        {
            var x = ClientSize.Width / 2f;
            var y = ClientSize.Height / 2f;
            var arm = 4.5f * unit;
            using var pen = new Pen(textColor, 2f * unit) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLine(pen, x - arm, y - arm, x + arm, y + arm);
            graphics.DrawLine(pen, x + arm, y - arm, x - arm, y + arm);
        }
        else if (tag == "list")
        {
            var inset = (int)Math.Round(16 * unit);
            var chevronSpace = ShowsChevron ? (int)Math.Round(24 * unit) : 0;
            AppleText.Draw(graphics, Text, Font, new Rectangle(inset, 0, Width - inset * 2 - chevronSpace, Height), textColor,
                StringAlignment.Near, StringAlignment.Center);
            if (ShowsChevron) DrawChevron(graphics, new PointF(Width - inset - 4f * unit, Height / 2f), unit, ios);
        }
        else if (tag == "back")
        {
            var chevronLeft = 2f * unit;
            Ios18Palette.DrawBackChevron(graphics, chevronLeft, Height / 2f, unit, textColor);
            var textLeft = (int)Math.Round(chevronLeft + (Ios18Palette.BackChevronWidth + 6f) * unit);
            AppleText.Draw(graphics, Text, Font, new Rectangle(textLeft, 0, Width - textLeft, Height), textColor,
                StringAlignment.Near, StringAlignment.Center);
        }
        else
        {
            var alignment = tag == "text-leading" ? StringAlignment.Near : tag == "text-trailing" ? StringAlignment.Far : StringAlignment.Center;
            var textBounds = isNavText ? Rectangle.Inflate(bounds, -(int)Math.Round(8 * unit), 0) : bounds;
            AppleText.Draw(graphics, Text, Font, textBounds, textColor, alignment, StringAlignment.Center);
        }
        if (Focused && ShowFocusCues && !isNavText)
        {
            using var focusPath = Ios18Palette.RoundedRect(new RectangleF(1, 1, Width - 2, Height - 2), tag is "row" or "list" ? Math.Min(Height / 2f - 1, form?.CardRadius ?? 12f * unit) : capsule);
            using var focus = new Pen(ios.Accent, 1.5f) { DashStyle = DashStyle.Dot };
            graphics.DrawPath(focus, focusPath);
        }
    }

    /// <summary>SF Symbols chevron.forward as used by iOS disclosure rows: tertiary label colour,
    /// semibold stroke, about 7 x 12 pt.</summary>
    internal static void DrawChevron(Graphics graphics, PointF tip, float unit, Ios18Palette ios)
    {
        var color = ios.IsDark ? Color.FromArgb(92, 235, 235, 245) : Color.FromArgb(77, 60, 60, 67);
        using var pen = new Pen(color, 2f * unit) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        var halfHeight = 6f * unit;
        var depth = 5.5f * unit;
        graphics.DrawLines(pen, new[] { new PointF(tip.X - depth, tip.Y - halfHeight), tip, new PointF(tip.X - depth, tip.Y + halfHeight) });
    }
}

internal class ContextSlider : Control
{
    private const float TrackInsetDip = 14f;
    protected override void OnPaintBackground(PaintEventArgs e) { }
    internal void ProbeDown(int x) => OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, x, Height / 2, 0));
    internal void ProbeMove(int x) => OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, x, Height / 2, 0));
    internal void ProbeUp(int x) => OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, x, Height / 2, 0));
    private OverlayThemePalette _palette;
    private int _value;
    private bool _hovered;
    internal bool MaterialHovered => _hovered;
    private double? _dragFraction;
    private double _releaseFrom;
    private long _releasedAt;
    private float Inset => TrackInsetDip * DeviceDpi / 96f;
    private double NumericFraction => (_value - Minimum) / (double)Math.Max(1, Maximum - Minimum);
    internal bool PositionAnimating => !Capture && _releasedAt > 0 && Environment.TickCount64 - _releasedAt < GlassMotion.PositionSettleMilliseconds && Math.Abs(_releaseFrom - NumericFraction) > .00001;
    internal double VisualFraction
    {
        get
        {
            if (Capture && _dragFraction is double value) return value;
            if (!PositionAnimating) return NumericFraction;
            var t = Math.Clamp((Environment.TickCount64 - _releasedAt) / (double)GlassMotion.PositionSettleMilliseconds, 0, 1);
            return _releaseFrom + (NumericFraction - _releaseFrom) * (1 - Math.Pow(1 - t, 3));
        }
    }
    internal Rectangle ThumbBounds
    {
        get
        {
            var unit = DeviceDpi / 96f;
            var x = (float)(Inset + (Width - 2 * Inset) * VisualFraction);
            var diameter = 26 * unit;
            return Rectangle.Round(new RectangleF(x - diameter / 2, Height / 2f - diameter / 2, diameter, diameter));
        }
    }
    private void MaterialChanged() { Invalidate(); if (Parent is SettingsForm form) form.RefreshMaterial(); }
    [DefaultValue(0)] public int Minimum { get; set; }
    [DefaultValue(1_050_000)] public int Maximum { get; set; } = 1_050_000;
    [DefaultValue(0)] public int Value { get => _value; set { var next = Math.Clamp(value, Minimum, Maximum); if (next == _value) return; _value = next; MaterialChanged(); ValueChanged?.Invoke(this, EventArgs.Empty); } }
    public event EventHandler? ValueChanged;
    public ContextSlider(OverlayThemePalette palette)
    {
        _palette = palette; Cursor = Cursors.Hand; TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        AccessibleRole = AccessibleRole.Slider;
    }
    public void SetPalette(OverlayThemePalette palette) { _palette = palette; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { _hovered = true; MaterialChanged(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; MaterialChanged(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var form = Parent as SettingsForm;
        var ios = form?.ActiveIosPalette ?? Ios18Palette.FromOverlay(_palette);
        var unit = DeviceDpi / 96f;
        var graphics = e.Graphics;
        graphics.Clear(form?.ControlSurfaceColor(this) ?? ios.CardSurface);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var start = Inset;
        var end = Width - Inset;
        var y = Height / 2f;
        var x = (float)(start + (end - start) * VisualFraction);
        var thickness = Math.Max(4f, 4f * unit);
        using var track = new Pen(ios.ControlFill, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var fill = new Pen(ios.Accent, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawLine(track, start, y, end, y);
        graphics.DrawLine(fill, start, y, x, y);
        var diameter = 26f * unit;
        var knob = new RectangleF(x - diameter / 2f, y - diameter / 2f, diameter, diameter);
        using var shadow = new SolidBrush(Color.FromArgb(ios.IsDark ? 80 : 30, Color.Black));
        graphics.FillEllipse(shadow, new RectangleF(knob.X, knob.Y + 1.5f * unit, knob.Width, knob.Height));
        using var knobFill = new SolidBrush(Color.White);
        using var knobBorder = new Pen(Color.FromArgb(ios.IsDark ? 40 : 22, Color.Black), 1f);
        graphics.FillEllipse(knobFill, knob);
        graphics.DrawEllipse(knobBorder, knob);
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(ios.Accent, 1.5f);
            graphics.DrawEllipse(focus, RectangleF.Inflate(knob, 2f * unit, 2f * unit));
        }
    }
    private void SetFromX(int x)
    {
        var step = Maximum - Minimum <= 100 ? 1 : 1000;
        _dragFraction = Math.Clamp((x - Inset) / Math.Max(1, Width - 2 * Inset), 0, 1);
        Value = Math.Clamp((int)Math.Round((Minimum + _dragFraction.Value * (Maximum - Minimum)) / step) * step, Minimum, Maximum);
        MaterialChanged();
    }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Focus(); Capture = true; SetFromX(e.X); MaterialChanged(); } base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (Capture && e.Button == MouseButtons.Left) SetFromX(e.X); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { if (Capture) SetFromX(e.X); Capture = false; if (Parent is SettingsForm form) form.FlushPendingInput(); MaterialChanged(); base.OnMouseUp(e); }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) { if (_dragFraction is double from) { _releaseFrom = from; _releasedAt = Environment.TickCount64; _dragFraction = null; } if (Parent is SettingsForm form) form.FlushPendingInput(); MaterialChanged(); } }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var step = Maximum - Minimum <= 100 ? 1 : 10_000;
        if (e.KeyCode == Keys.Left) Value = Math.Max(Minimum, Value - step);
        if (e.KeyCode == Keys.Right) Value = Math.Min(Maximum, Value + step);
        if (e.KeyCode == Keys.Home) Value = Minimum;
        if (e.KeyCode == Keys.End) Value = Maximum;
        base.OnKeyDown(e);
    }
}
