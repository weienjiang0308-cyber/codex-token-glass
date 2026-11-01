using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CodexStatusbar;

/// <summary>High-density production-control previews. No desktop pixels or real state are read.</summary>
internal static class PublicGallery
{
    private const int Scale = 4;
    private const uint Dpi = 96 * Scale;
    private static readonly DisplayField Fields = DisplayField.Total | DisplayField.Input | DisplayField.Output |
        DisplayField.CacheHit | DisplayField.CacheMiss | DisplayField.CacheHitRate | DisplayField.Reasoning |
        DisplayField.Context | DisplayField.ContextPercent | DisplayField.PeakSpeed | DisplayField.AverageSpeed |
        DisplayField.TurnDuration | DisplayField.ModelTime | DisplayField.FirstTokenTime;

    internal static int Render(string output)
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (home is null || !File.Exists(Path.Combine(home, ".public-demo")))
            throw new InvalidOperationException("Use tools/render-public-gallery.ps1 to create an isolated synthetic home first.");
        Directory.CreateDirectory(output);
        foreach (var language in new[] { "zh", "en" })
        foreach (var theme in new[] { OverlayThemeKind.Light, OverlayThemeKind.Dark })
        {
            CodexUi.Language = language; CodexUi.DemoLanguage = language;
            CodexUi.GlassTransparency = 80;
            var key = $"{theme.ToString().ToLowerInvariant()}-{language}";
            var palette = OverlayThemePalette.For(theme);
            var snapshot = new TokenSnapshot("synthetic-session", "", 248000, 240000, 224000, 8000, 2000,
                62000, 475000, DateTime.UnixEpoch, Title: "Synthetic demo", Tps: 128, PeakTps: 164, AverageTps: 122,
                TurnElapsedMs: 95000, MergedModelElapsedMs: 65000, TtftMs: 1200);
            var presentation = OverlayPresentationBuilder.Create(snapshot, DisplayField.Tps, DisplayField.Total, Fields);
            RenderStrip(output, $"windows-strip-{key}", presentation, palette, expanded: false);
            RenderStrip(output, $"windows-details-{key}", presentation, palette, expanded: true);
            using (var form = new SettingsForm(() => palette, () => 475000))
            using (var scene = Scene(new Size(552 * Scale, 730 * Scale), theme))
            using (var bitmap = form.RenderPublicSnapshot(scene, Dpi))
                bitmap.Save(Path.Combine(output, $"windows-settings-{key}.png"), ImageFormat.Png);
            if (language == "zh")
            {
                var waiting = OverlayPresentationBuilder.CreateWaiting("等待演示会话数据", DisplayField.Tps, DisplayField.Total, Fields);
                RenderStrip(output, $"windows-waiting-{key}", waiting, palette, expanded: true);
                RenderStrip(output, $"windows-position-edit-{key}", presentation, palette, expanded: false, edit: true);
            }
        }
        CodexUi.DemoLanguage = "zh";
        RenderMenus(output);
        Console.WriteLine("Public gallery: 4x PNGs, synthetic records/config/backdrops only. No screen capture.");
        return 0;
    }

    private static void RenderStrip(string output, string name, OverlayPresentation presentation, OverlayThemePalette palette, bool expanded, bool edit = false)
    {
        var height = 212 + presentation.ExpandedRows.Count * 28;
        var size = expanded ? new Size(444 * Scale, (height + 64) * Scale) : new Size(460 * Scale, 84 * Scale);
        IntRect Rect(int x, int y, int w, int h) => new(x * Scale, y * Scale, w * Scale, h * Scale);
        var capsule = expanded ? Rect(196, height + 28, 232, 24) : Rect(24, 30, 412, 24);
        var panel = expanded ? Rect(12, 12, 420, height) : new IntRect(0, 0, 0, 0);
        using var scene = Scene(size, palette.Background.GetBrightness() > .5 ? OverlayThemeKind.Light : OverlayThemeKind.Dark);
        using var form = new TokenStripForm(clickToExpand: true);
        form.SetPresentation(presentation); form.ApplyTheme(palette);
        form.ApplyLayout(new OverlayLayoutResult(expanded ? OverlayVisualState.Expanded : OverlayVisualState.Collapsed,
            CollapsedDisplayMode.TwoFields, ExpansionDirection.Up, Dpi, new IntRect(0, 0, size.Width, size.Height), capsule, panel, 28 * Scale));
        if (edit) form.BeginEditMode(100);
        if (expanded) form.UseSyntheticBackdrop(scene, Dpi);
        using var surface = form.RenderSurfaceBitmap(size, Dpi);
        scene.SetResolution(Dpi, Dpi);
        using (var graphics = Graphics.FromImage(scene))
            graphics.DrawImage(surface, new Rectangle(Point.Empty, size), 0, 0, size.Width, size.Height, GraphicsUnit.Pixel);
        scene.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
    }

    private static Bitmap Scene(Size size, OverlayThemeKind theme)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var dark = theme == OverlayThemeKind.Dark;
        using var background = new LinearGradientBrush(new Rectangle(Point.Empty, size),
            dark ? Color.FromArgb(29, 31, 33) : Color.FromArgb(249, 249, 247),
            dark ? Color.FromArgb(55, 58, 61) : Color.FromArgb(218, 222, 225), 40);
        graphics.FillRectangle(background, new Rectangle(Point.Empty, size));
        using var glow = new SolidBrush(dark ? Color.FromArgb(19, 255, 255, 255) : Color.FromArgb(55, 255, 255, 255));
        graphics.FillEllipse(glow, -size.Width / 2, -size.Height / 5, size.Width * 2, size.Height / 2);
        using var stroke = new Pen(dark ? Color.FromArgb(14, 255, 255, 255) : Color.FromArgb(20, 55, 65, 75), Scale * 2);
        for (var i = 1; i <= 5; i++) graphics.DrawLine(stroke, -size.Width, size.Height * i / 5, size.Width * 2, size.Height * i / 5 - size.Width);
        return bitmap;
    }

    private static void RenderMenus(string output)
    {
        // Same WinForms menu controls and current item vocabulary, with static demo
        // state and no callbacks. Constructing OverlayContext would register startup,
        // watch processes and touch real IPC, so the documentation preview cannot do so.
        using var menu = new ContextMenuStrip { Font = CodexUi.FontForText("菜单", 9.5f * Scale), ImageScalingSize = new Size(16 * Scale, 16 * Scale) };
        ToolStripMenuItem Group(string title, params string[] items)
        {
            var group = new ToolStripMenuItem(title);
            foreach (var item in items) group.DropDownItems.Add(new ToolStripMenuItem(item));
            menu.Items.Add(group); return group;
        }
        var groups = new Dictionary<string, ToolStripMenuItem>();
        groups["status"] = Group("Status  ·  状态", "Codex：演示状态", "状态条：演示已附着");
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("会话：Synthetic demo") { Enabled = false });
        menu.Items.Add("锁定当前会话"); menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Adjust Position  ·  调整位置"); menu.Items.Add("Reset Position  ·  重置位置");
        menu.Items.Add(new ToolStripMenuItem("位置：演示位置") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem("Position diagnostics  ·  位置诊断（需要 --debug）") { Enabled = false });
        groups["anchor"] = Group("Anchor  ·  锚点", "左上", "上方居中", "右上", "左侧居中", "居中", "右侧居中", "左下", "下方居中", "右下");
        groups["position-mode"] = Group("Position  ·  定位", "Dock left of Context  ·  停靠到 Context 左侧", "Manual position  ·  手动位置", "Fixed on Screen  ·  固定屏幕位置");
        groups["theme"] = Group("主题", "Auto  ·  跟随系统", "Dark  ·  深色文字", "Light  ·  浅色文字");
        menu.Items.Add(new ToolStripMenuItem("透明背景") { Checked = true }); menu.Items.Add("文字阴影");
        menu.Items.Add(new ToolStripSeparator());
        groups["traditional-position"] = Group("传统定位", "标题栏右上", "自动吸附", "窗口内右上", "窗口内右下");
        menu.Items.Add(new ToolStripSeparator());
        var collapsed = Group("收起时显示");
        foreach (var (key, label) in new[] { ("primary-metric", "左侧指标"), ("secondary-metric", "右侧指标") })
        {
            var item = new ToolStripMenuItem(label);
            foreach (var field in DisplayFieldRules.Ordered) item.DropDownItems.Add(OverlayPresentationBuilder.GetFieldMenuText(field));
            collapsed.DropDownItems.Add(item); groups[key] = item;
        }
        groups["visible-fields"] = Group("显示字段", DisplayFieldRules.Ordered.Select(OverlayPresentationBuilder.GetFieldMenuText).ToArray());
        menu.Items.Add(new ToolStripSeparator());
        foreach (var item in new[] { "详情面板  ·  展开 / 收起", "设置…", "暂时隐藏", "Start with Windows  ·  开机自动启动", "Start / Attach now  ·  立即检测并附着", "Restart Statusbar  ·  重启状态条" }) menu.Items.Add(item);
        menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("退出");
        void Save(ToolStrip strip, string name)
        {
            // A live popup is clamped to the physical display height. Lay the
            // same native menu-item controls in a scroll-free documentation strip
            // so entries below the monitor edge are not silently omitted.
            var items = strip.Items.Cast<ToolStripItem>().ToArray();
            var width = items.Max(i => TextRenderer.MeasureText(i.Text, menu.Font).Width) + 44 * Scale;
            var height = items.Sum(i => i is ToolStripSeparator ? 8 * Scale : 28 * Scale) + 8;
            using var catalog = new ToolStrip { AutoSize = false, CanOverflow = false, GripStyle = ToolStripGripStyle.Hidden,
                LayoutStyle = ToolStripLayoutStyle.VerticalStackWithOverflow, Font = menu.Font,
                ImageScalingSize = menu.ImageScalingSize, Padding = new Padding(4), Size = new Size(width, height) };
            foreach (var item in items)
            {
                item.AutoSize = false; item.Margin = Padding.Empty; item.TextAlign = ContentAlignment.MiddleLeft;
                item.Size = new Size(width - 8, item is ToolStripSeparator ? 8 * Scale : 28 * Scale);
                catalog.Items.Add(item);
            }
            catalog.PerformLayout();
            using var bitmap = new Bitmap(width, height);
            catalog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.SetResolution(Dpi, Dpi);
            bitmap.Save(Path.Combine(output, "windows-menu-" + name + ".png"), ImageFormat.Png);
            foreach (var item in items) strip.Items.Add(item);
        }
        Save(menu, "root");
        foreach (var (name, group) in groups) Save(group.DropDown, name);
    }
}
