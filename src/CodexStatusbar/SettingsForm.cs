using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CodexStatusbar;

internal sealed class SettingsForm : Form
{
    private readonly OverlayThemePalette _palette;
    private readonly NumericUpDown _window;
    private readonly NumericUpDown _percent;
    private readonly ContextSlider _slider;
    private readonly ContextSlider _glassSlider;
    private readonly Label _glassValue;
    private readonly LiquidBackdropRenderer _optics=new();
    private readonly System.Windows.Forms.Timer _materialTimer=new(){Interval=16};
    private Bitmap? _materialFrame;
    private bool _materialDirty=true;
    private readonly Label _threshold;
    private readonly Label _status;
    private readonly Label _contextInfo;
    private readonly Func<long?> _getLiveContext;
    private readonly Button _restore;
    private readonly ContextValues _initial;
    private readonly System.Windows.Forms.Timer _themeTimer = new() { Interval = 1000 };
    private readonly Func<OverlayThemePalette> _currentPalette;
    private long CompactAt => (long)(_window.Value * _percent.Value / 100m);
    private bool _syncing;

    public SettingsForm(Func<OverlayThemePalette> palette, Func<long?> liveContext)
    {
        _currentPalette = palette;
        _palette = palette();
        _initial = ContextConfiguration.Read();
        _getLiveContext = liveContext;
        Text = CodexUi.T("用量设置", "Usage settings");
        Font = CodexUi.FontForText(CodexUi.T("中文", "English"), 10f);
        BackColor = _palette.Background;
        ForeColor = _palette.Value;
        ClientSize = new Size(552, 730);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;

        var heading=AddLabel(CodexUi.T("用量设置", "Usage settings"),28,14,430,36,18,true);
        heading.TextAlign=ContentAlignment.MiddleLeft;
        var close = AddButton("×",498,10,44,false);
        close.Height=44;
        close.AccessibleName = CodexUi.T("关闭设置", "Close settings");
        close.Click += (_, _) => Close();
        AddLabel(CodexUi.T("跟随 Codex 的外观，让指标和设置保持一致。", "A quieter view of your tokens, in Codex's own colors."), 29, 64, 494, 24, 9.5f, false, true);
        AddLabel(CodexUi.T("上下文窗口", "Context window"), 28, 115, 320, 25, 11, true);
        AddLabel(_initial.Model, 28, 146, 280, 22, 9.5f, false, true);
        _window = new NumericUpDown
        {
            Minimum = 32_000, Maximum = ContextConfiguration.SliderMaximum, Increment = 10_000,
            ThousandsSeparator = true, Value = Math.Clamp(_initial.Window, 32_000, ContextConfiguration.SliderMaximum),
            Bounds = new Rectangle(336, 141, 185, 32), Font = CodexUi.Font(11),
            BorderStyle = BorderStyle.None, BackColor = _palette.Background, ForeColor = _palette.Value,
            AccessibleName = CodexUi.T("上下文 token 数量", "Context tokens")
        };
        Controls.Add(_window);
        _slider = new ContextSlider(_palette) { Bounds = new Rectangle(28, 182, 494, 38), Minimum = 32_000,
            Maximum = (int)ContextConfiguration.SliderMaximum, Value = (int)_window.Value,
            AccessibleName = CodexUi.T("上下文容量滑块", "Context capacity slider") };
        Controls.Add(_slider);
        var minimum = AddLabel("32K", 28, 220, 44, 20, 9, false, true);
        var maximum = AddLabel("1.05M", 478, 220, 44, 20, 9, false, true);
        minimum.TextAlign = maximum.TextAlign = ContentAlignment.MiddleCenter;
        Button Preset(string text, long value, int x, int width)
        {
            var button = AddButton(text, x, 253, width, false);
            button.Click += (_, _) => _window.Value = value;
            return button;
        }
        Preset("500K", 500_000, 28, 110);
        Preset("1,000,000", 1_000_000, 151, 180);
        Preset("1,050,000", 1_050_000, 344, 178);
        _contextInfo = AddLabel("", 28, 302, 494, 69, 9.2f, false, true);
        AddLabel(CodexUi.T("自动压缩", "Auto-compaction"), 28, 389, 260, 24, 11, true);
        _percent = new NumericUpDown
        {
            Minimum = 50, Maximum = 90, DecimalPlaces = 1, Increment = 1,
            Value = Math.Clamp((decimal)_initial.CompactAt * 100 / _initial.Window, 50, 90),
            Bounds = new Rectangle(426, 385, 74, 30), BorderStyle = BorderStyle.None,
            BackColor = _palette.Background, ForeColor = _palette.Value,
            AccessibleName = CodexUi.T("自动压缩百分比", "Compaction percentage")
        };
        Controls.Add(_percent);
        AddLabel("%", 503, 390, 20, 24, 10);
        _threshold = AddLabel("", 28, 422, 494, 24, 9.5f, false, true);
        AddLabel(CodexUi.T("语言", "Language"), 28, 465, 240, 24, 11, true);
        var language = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList,
            Bounds = new Rectangle(298, 461, 224, 30), BackColor = _palette.Background, ForeColor = _palette.Value,
            FlatStyle = FlatStyle.Flat, AccessibleName = "Language" };
        language.Items.AddRange(new object[] { "Auto · Codex", "English", "简体中文" });
        language.SelectedIndex = CodexUi.Language switch { "en" => 1, "zh" => 2, _ => 0 };
        Controls.Add(language);
        language.SelectedIndexChanged += (_, _) =>
        {
            CodexUi.Language = language.SelectedIndex switch { 1 => "en", 2 => "zh", _ => "auto" };
            if (_status is not null) _status.Text = CodexUi.T("语言已更新，重新打开设置即可刷新。", "Language updated. Reopen Settings to refresh its labels.");
        };
        AddLabel(CodexUi.T("玻璃透明度","Glass transparency"),28,510,360,24,11,true);
        _glassValue=AddLabel($"{CodexUi.GlassTransparency}%",440,510,82,24,10.5f);
        _glassValue.TextAlign=ContentAlignment.MiddleRight;
        _glassSlider=new ContextSlider(_palette){Bounds=new Rectangle(28,542,494,38),Minimum=0,Maximum=100,Value=CodexUi.GlassTransparency,
            AccessibleName=CodexUi.T("玻璃透明度滑块","Glass transparency slider")};
        Controls.Add(_glassSlider);
        AddLabel(CodexUi.T("柔和","Frosted"),28,583,110,20,9,false,true);
        var clear=AddLabel(CodexUi.T("清透","Clear"),412,583,110,20,9,false,true);clear.TextAlign=ContentAlignment.MiddleRight;
        AddLabel(CodexUi.T("实时预览；松手自动保存，文字不随背景变淡。","Live preview. Saves on release; text stays solid."),28,610,494,22,9,false,true);
        _glassSlider.ValueChanged+=(_,_)=>{CodexUi.PreviewGlassTransparency(_glassSlider.Value);_glassValue.Text=$"{_glassSlider.Value}%";RefreshMaterial();};
        _glassSlider.MouseUp+=(_,_)=>CodexUi.CommitAppearance();
        _glassSlider.KeyUp+=(_,_)=>CodexUi.CommitAppearance();
        _status = AddLabel("", 28, 640, 494, 30, 9.2f, false, true);
        _restore = AddButton(CodexUi.T("恢复原值", "Restore original"), 14, 680, 166, false);
        _restore.Enabled = ContextConfiguration.CanRestore;
        _restore.Click += (_, _) => ApplyChanges(restore: true);
        var apply = AddButton(CodexUi.T("保存更改", "Save changes"), 372, 680, 166, true);
        apply.Click += (_, _) => ApplyChanges(restore: false);
        _window.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true; _slider.Value = (int)_window.Value; _syncing = false; UpdateThreshold(); UpdateContextInfo();
        };
        _slider.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _window.Value = _slider.Value;
        };
        _percent.ValueChanged += (_, _) => UpdateThreshold();
        UpdateThreshold();
        UpdateContextInfo();
        _themeTimer.Tick += (_, _) => { RefreshPalette(); UpdateContextInfo(); };
        _themeTimer.Start();
        _materialTimer.Tick+=(_,_)=>
        {
            if(!Visible || !_optics.Ready){_materialTimer.Stop();return;}
            _materialDirty=true;EnsureMaterial();Invalidate(true);
            if(!_optics.NeedsFrame)_materialTimer.Stop();
        };
    }

    private void UpdateThreshold() => _threshold.Text = CodexUi.T(
        $"达到 {CompactAt:N0} token 时压缩，保留响应空间。",
        $"Compact at {CompactAt:N0} tokens, leaving room for responses.");

    private void UpdateContextInfo()
    {
        var total = Math.Min((long)_window.Value, _initial.CatalogMaximum ?? (long)_window.Value);
        var usable = total * _initial.EffectivePercent / 100;
        var live = _getLiveContext();
        var status = live == usable ? CodexUi.T("已应用", "applied") : CodexUi.T("待应用", "pending");
        var display = live?.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) ?? CodexUi.T("等待连接", "waiting");
        _contextInfo.Text = CodexUi.T(
            $"总窗口 {_window.Value:N0}；Codex 保留 {100 - _initial.EffectivePercent}% 余量。\n按此设置预计可用 {usable:N0}。\n最近调用记录：{display}（{status}）。\n重载后发送一条消息，实际运行记录才会更新。",
            $"Total window: {_window.Value:N0}. Codex reserves {100 - _initial.EffectivePercent}%.\nExpected usable window: {usable:N0}.\nLast recorded usable window: {display} ({status}).\nAfter reloading, send a message to refresh the runtime record.");
    }

    private void ApplyChanges(bool restore)
    {
        try
        {
            if (restore) ContextConfiguration.Restore(); else ContextConfiguration.Save((long)_window.Value, CompactAt);
            var current = ContextConfiguration.Read();
            _window.Value = Math.Clamp(current.Window, (long)_window.Minimum, (long)_window.Maximum);
            _percent.Value = Math.Clamp((decimal)current.CompactAt * 100 / current.Window, 50, 90);
            _restore.Enabled = ContextConfiguration.CanRestore;
            _status.Text = CodexUi.T("已保存。新开对话后使用；原有配置可随时恢复。", "Saved. Start a new chat to use it. Your original values are backed up.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        { _status.Text = CodexUi.T("无法保存：", "Could not save: ") + e.Message; }
    }

    private Label AddLabel(string text, int x, int y, int width, int height, float size,
        bool bold = false, bool muted = false)
    {
        var label = new Label { Text = text, Bounds = new Rectangle(x, y, width, height),
            Font = CodexUi.FontForText(text, size, semibold: bold),
            ForeColor = muted ? _palette.Label : _palette.Value, BackColor = Color.Transparent,
            Tag = muted ? "muted" : "text" };
        Controls.Add(label); return label;
    }

    private Button AddButton(string text, int x, int y, int width, bool primary)
    {
        var button = new GlassButton { Text = text, Bounds = new Rectangle(x, y, width, 36),
            FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
            Font = CodexUi.FontForText(text, 9.5f, semibold: primary),
            ForeColor = primary ? _palette.Background : _palette.Value,
            BackColor = primary ? _palette.Value : _palette.Background,
            Tag = primary ? "primary" : "secondary" };
        button.FlatAppearance.BorderColor = _palette.Border;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        Controls.Add(button); return button;
    }

    private void RefreshPalette()
    {
        var palette = _currentPalette();
        BackColor = palette.Background;
        ForeColor = palette.Value;
        foreach (Control child in Controls)
        {
            if (child is ContextSlider slider) { slider.SetPalette(palette); continue; }
            child.ForeColor = Equals(child.Tag, "muted") ? palette.Label : Equals(child.Tag, "primary") ? palette.Background : palette.Value;
            if (child is not Label) child.BackColor = Equals(child.Tag, "primary") ? palette.Value : palette.Background;
            if (child is NumericUpDown) child.BackColor = CodexUi.Mix(palette.Background,palette.Value,.025);
            if (child is Button button) button.FlatAppearance.BorderColor = palette.Border;
        }
        var dark = palette.Background.GetBrightness() < .5 ? 1 : 0;
        if (IsHandleCreated) DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        RefreshMaterial();
    }

    internal void RefreshMaterial()
    {
        if(IsDisposed || Disposing)return;
        _materialDirty=true;Invalidate(true);
        if(_optics.Ready && Visible)_materialTimer.Start();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _optics.Capture(new Rectangle(Location,ClientSize),new Rectangle(Point.Empty,ClientSize),(uint)DeviceDpi);
        RefreshMaterial();
    }

    private void EnsureMaterial()
    {
        if(!_optics.Ready || !_materialDirty || ClientSize.Width<1 || ClientSize.Height<1)return;
        var palette=_currentPalette();var u=DeviceDpi/96f;
        var alpha=DesktopMaterial.TransparencyEnabled?Math.Clamp(238-CodexUi.GlassTransparency,138,238):255;
        _optics.ReadingSurfaces((new Rectangle((int)(16*u),(int)(104*u),ClientSize.Width-(int)(32*u),(int)(527*u)),16*u,Color.FromArgb(alpha,palette.Background)));
        foreach(Control control in Controls)
        {
            if(control is GlassButton button)
                _optics.Control(control.GetHashCode(),control.Bounds,control.Height/2f,button.MaterialHovered,button.MaterialPressed,Equals(control.Tag,"primary"));
            else if(control is ContextSlider slider)
            {
                _optics.Track(control.GetHashCode(),slider.Bounds,(slider.Value-slider.Minimum)/(float)Math.Max(1,slider.Maximum-slider.Minimum),palette.ProgressTrack,palette.Value);
                var thumb=slider.ThumbBounds;thumb.Offset(slider.Left,slider.Top);
                _optics.Control(control.GetHashCode(),thumb,thumb.Height/2f,slider.MaterialHovered,slider.Capture);
            }
        }
        if(_materialFrame is null || _materialFrame.Size!=ClientSize){_materialFrame?.Dispose();_materialFrame=new Bitmap(ClientSize.Width,ClientSize.Height,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);}
        _optics.RenderInto(_materialFrame,palette.Background.GetBrightness()<.5);
        _materialDirty=false;
    }

    internal bool DrawControlMaterial(Control control,Graphics graphics)
    {
        EnsureMaterial();if(!_optics.Ready || _materialFrame is null)return false;
        graphics.DrawImage(_materialFrame,control.ClientRectangle,control.Bounds,GraphicsUnit.Pixel);return true;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        EnsureMaterial();
        if(_optics.Ready && _materialFrame is not null)e.Graphics.DrawImageUnscaled(_materialFrame,0,0);
        else base.OnPaintBackground(e);
    }

    protected override void OnShown(EventArgs e) { base.OnShown(e); RefreshPalette(); }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        using var shape = FrameShape(new Rectangle(0, 0, Width, Height), 32 * DeviceDpi / 96);
        Region?.Dispose(); Region = new Region(shape);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var palette = _currentPalette();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var outline = FrameShape(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), 32 * DeviceDpi / 96);
        using var border = new Pen(palette.Border, 1);
        e.Graphics.DrawPath(border, outline);
        foreach (var number in new[] {_window,_percent})
        {
            if (number is null) continue;
            var area = Rectangle.Inflate(number.Bounds, 6, 5);
            using var field = FrameShape(area, 10 * DeviceDpi / 96);
            using var fill = new SolidBrush(CodexUi.Mix(palette.Background,palette.Value,.025));
            e.Graphics.FillPath(fill,field);
        }
    }
    private static GraphicsPath FrameShape(Rectangle r, int radius)
    {
        var p = new GraphicsPath(); var d = Math.Max(2, Math.Min(Math.Min(r.Width, r.Height), radius * 2));
        p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right-d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right-d, r.Bottom-d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom-d, d, d, 90, 90); p.CloseFigure();return p;
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && e.Y < 94) { ReleaseCapture(); SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); }
    }
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }
    protected override void OnFormClosing(FormClosingEventArgs e){CodexUi.CommitAppearance();base.OnFormClosing(e);}
    protected override void Dispose(bool disposing) { if (disposing){_themeTimer.Dispose();_materialTimer.Dispose();_materialFrame?.Dispose();_optics.Dispose();} base.Dispose(disposing); }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr w, IntPtr l);
}

internal sealed class GlassButton : Button
{
    private bool _hovered;
    private bool _pressed;
    internal bool MaterialHovered=>_hovered;
    internal bool MaterialPressed=>_pressed;
    private void MaterialChanged(){Invalidate();if(Parent is SettingsForm form)form.RefreshMaterial();}
    public GlassButton() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    protected override void OnMouseEnter(EventArgs e) { _hovered = true; MaterialChanged(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; MaterialChanged(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e){if(e.Button==MouseButtons.Left){_pressed=true;MaterialChanged();}base.OnMouseDown(e);}
    protected override void OnMouseUp(MouseEventArgs e){_pressed=false;MaterialChanged();base.OnMouseUp(e);}
    protected override void OnMouseCaptureChanged(EventArgs e){if(!Capture){_pressed=false;MaterialChanged();}base.OnMouseCaptureChanged(e);}
    protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode is Keys.Space or Keys.Enter){_pressed=true;MaterialChanged();}base.OnKeyDown(e);}
    protected override void OnKeyUp(KeyEventArgs e){_pressed=false;MaterialChanged();base.OnKeyUp(e);}
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var optical=Parent is SettingsForm form && form.DrawControlMaterial(this,e.Graphics);
        if(!optical)e.Graphics.Clear(Parent?.BackColor ?? BackColor);
        var rect = new Rectangle(1, 1, Width - 3, Height - 3);
        var radius = rect.Height / 2;
        using var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, radius * 2, radius * 2, 90, 180);
        path.AddArc(rect.Right - radius * 2, rect.Top, radius * 2, radius * 2, 270, 180); path.CloseFigure();
        var surface = _hovered && Enabled ? CodexUi.Mix(BackColor, ForeColor, .07) : BackColor;
        using var fill = new LinearGradientBrush(rect, CodexUi.Mix(surface, Color.White, .035), surface, LinearGradientMode.Vertical);
        using var border = new Pen(FlatAppearance.BorderColor, 1);
        if(!optical){e.Graphics.FillPath(fill,path);e.Graphics.DrawPath(border,path);}
        if(Text=="×")
        {
            var x=ClientSize.Width/2f;var y=ClientSize.Height/2f;var u=DeviceDpi/96f;
            using var pen=new Pen(ForeColor,1.8f*u){StartCap=LineCap.Round,EndCap=LineCap.Round};
            e.Graphics.DrawLine(pen,x-5*u,y-5*u,x+5*u,y+5*u);e.Graphics.DrawLine(pen,x+5*u,y-5*u,x-5*u,y+5*u);
        }
        else TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle,
            Enabled ? ForeColor : CodexUi.Mix(BackColor, ForeColor, .40),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (Focused && ShowFocusCues) { using var focus = new Pen(ForeColor) { DashStyle = DashStyle.Dot }; e.Graphics.DrawPath(focus, path); }
    }
}

internal class ContextSlider : Control
{
    private OverlayThemePalette _palette;
    private int _value;
    private bool _hovered;
    internal bool MaterialHovered=>_hovered;
    internal Rectangle ThumbBounds
    {
        get
        {
            var u=DeviceDpi/96f;var x=22*u+(Width-44*u)*(_value-Minimum)/Math.Max(1f,Maximum-Minimum);
            var width=(Capture?40:32)*u;var height=(Capture?28:24)*u;
            return Rectangle.Round(new RectangleF(x-width/2,Height/2f-height/2,width,height));
        }
    }
    private void MaterialChanged(){Invalidate();if(Parent is SettingsForm form)form.RefreshMaterial();}
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
    protected override void OnMouseEnter(EventArgs e){_hovered=true;MaterialChanged();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e){_hovered=false;MaterialChanged();base.OnMouseLeave(e);}
    protected override void OnPaint(PaintEventArgs e)
    {
        if(Parent is SettingsForm form && form.DrawControlMaterial(this,e.Graphics))
        {
            if(Focused && ShowFocusCues){using var focus=new Pen(_palette.Value,1);var r=ThumbBounds;e.Graphics.DrawEllipse(focus,r);}
            return;
        }
        var g = e.Graphics; g.Clear(_palette.Background); g.SmoothingMode = SmoothingMode.AntiAlias;
        var start = 22f; var end = Width - 22f; var y = Height / 2f;
        var x = start + (end - start) * (_value - Minimum) / Math.Max(1f, Maximum - Minimum);
        using var track = new Pen(_palette.ProgressTrack, 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var fill = new Pen(_palette.Value, 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(track, start, y, end, y); g.DrawLine(fill, start, y, x, y);
        var width = Capture ? 40f : 32f; var height = Capture ? 28f : 24f;
        using var shape = new GraphicsPath();
        shape.AddArc(x-width/2,y-height/2,height,height,90,180);
        shape.AddArc(x+width/2-height,y-height/2,height,height,270,180);shape.CloseFigure();
        using var shadow = new SolidBrush(Color.FromArgb(20,Color.Black));
        var saved = g.Save();g.TranslateTransform(0,2);g.FillPath(shadow,shape);g.Restore(saved);
        var bright = _palette.Background.GetBrightness() > .5;
        using var glass = new LinearGradientBrush(new RectangleF(x-width/2,y-height/2,width,height),
            bright ? Color.White : Color.FromArgb(112,112,112), bright ? Color.FromArgb(242,242,242) : Color.FromArgb(76,76,76),LinearGradientMode.Vertical);
        using var rim = new Pen(CodexUi.Mix(_palette.Background,_palette.Value,.12),1);
        g.FillPath(glass,shape);g.DrawPath(rim,shape);
        if (Focused && ShowFocusCues) { using var focus = new Pen(_palette.Label);g.DrawPath(focus,shape); }
    }
    private void SetFromX(int x)
    {
        var u=DeviceDpi/96d;var step=Maximum-Minimum<=100?1:1000;
        Value=Math.Clamp((int)Math.Round((Minimum+Math.Clamp((x-22*u)/Math.Max(1,Width-44*u),0,1)*(Maximum-Minimum))/step)*step,Minimum,Maximum);
    }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Focus(); Capture = true; SetFromX(e.X); MaterialChanged(); } base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (Capture && e.Button == MouseButtons.Left) SetFromX(e.X); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { Capture = false; MaterialChanged(); base.OnMouseUp(e); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var step=Maximum-Minimum<=100?1:10_000;
        if (e.KeyCode == Keys.Left) Value = Math.Max(Minimum, Value - step);
        if (e.KeyCode == Keys.Right) Value = Math.Min(Maximum, Value + step);
        if (e.KeyCode == Keys.Home) Value = Minimum;
        if (e.KeyCode == Keys.End) Value = Maximum;
        base.OnKeyDown(e);
    }
}
