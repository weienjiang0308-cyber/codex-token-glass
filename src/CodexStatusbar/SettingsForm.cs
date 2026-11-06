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
    private readonly Label _clarityHint;
    internal Rectangle ReadingPlateBounds => Rectangle.FromLTRB(
        (int)(16 * DeviceDpi / 96f), (int)(104 * DeviceDpi / 96f),
        ClientSize.Width - (int)(16 * DeviceDpi / 96f), _clarityHint.Bottom + (int)(16 * DeviceDpi / 96f));
    private readonly LiquidBackdropRenderer _optics=new();
    private readonly UiFrameClock _materialTimer=new();
    private Bitmap? _materialFrame;
    private SoftWindowEdge? _smoothEdge;
    private bool _materialDirty=true;
    private bool _renderFromTimer;
    private bool _fullSurfaceDirty=true;
    private long _shownAt;
    private int? _pendingContextValue,_pendingClarityValue;
    private OverlayThemePalette? _appliedPalette;
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
    internal bool MotionProbeActive;
    internal readonly List<double> ProbeRenderMs = new();
    internal readonly List<double> ProbeFrameTimes = new();
    internal ContextSlider ProbeContext => _slider;
    internal ContextSlider ProbeClarity => _glassSlider;

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
        ClientSize = new Size(552, 770);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

        var heading=AddLabel(CodexUi.T("用量设置", "Usage settings"),28,14,430,36,18,true);
        heading.TextAlign=ContentAlignment.MiddleLeft;
        var close = AddButton("×",498,10,44,false);
        close.Height=44;
        close.AccessibleName = CodexUi.T("关闭设置", "Close settings");
        close.Click += (_, _) => Close();
        AddLabel(CodexUi.T("跟随 Codex 的外观，让指标和设置保持一致。", "A quieter view of your tokens, in Codex's own colors."), 29, 64, 494, 24, 9.5f, false, true);
        AddLabel(CodexUi.T("全局上下文窗口", "Global context window"), 28, 112, 284, 30, 11, true);
        AddLabel(CodexUi.T("修改 Codex 全局配置，不绑定单个模型。", "Global Codex configuration, independent of the selected model."), 28, 146, 494, 24, 10.5f, false, true);
        _window = new NumericUpDown
        {
            Minimum = 32_000, Maximum = ContextConfiguration.SliderMaximum, Increment = 10_000,
            ThousandsSeparator = true, Value = Math.Clamp(_initial.Window, 32_000, ContextConfiguration.SliderMaximum),
            Bounds = new Rectangle(336, 115, 185, 32), Font = CodexUi.Font(11),
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
        _contextInfo = AddLabel("", 28, 302, 494, 104, 9.2f, false, true);
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
        AddLabel(CodexUi.T("玻璃清透度","Glass clarity"),28,510,360,24,11,true);
        _glassValue=AddLabel($"{CodexUi.GlassTransparency}%",440,510,82,24,10.5f);
        _glassValue.TextAlign=ContentAlignment.MiddleRight;
        _glassSlider=new ContextSlider(_palette){Bounds=new Rectangle(28,542,494,38),Minimum=0,Maximum=100,Value=CodexUi.GlassTransparency,
            AccessibleName=CodexUi.T("玻璃清透度滑块","Glass clarity slider")};
        Controls.Add(_glassSlider);
        AddLabel(CodexUi.T("柔和","Frosted"),28,583,110,20,9,false,true);
        var clear=AddLabel(CodexUi.T("清透","Clear"),412,583,110,20,9,false,true);clear.TextAlign=ContentAlignment.MiddleRight;
        _clarityHint=AddLabel(CodexUi.T("实时预览；松手自动保存，文字不随背景变淡。","Live preview. Saves on release; text stays solid."),28,610,494,22,9,false,true);
        _glassSlider.ValueChanged+=(_,_)=>{_pendingClarityValue=_glassSlider.Value;RefreshMaterial();};
        _glassSlider.MouseUp+=(_,_)=>CodexUi.CommitAppearance();
        _glassSlider.KeyUp+=(_,_)=>CodexUi.CommitAppearance();
        _status = AddLabel("", 28, 640, 494, 30, 9.2f, false, true);
        _restore = AddButton(CodexUi.T("恢复原值", "Restore original"), 14, 680, 166, false);
        _restore.Enabled = ContextConfiguration.CanRestore;
        _restore.Click += (_, _) => ApplyChanges(restore: true);
        var apply = AddButton(CodexUi.T("保存更改", "Save changes"), 372, 680, 166, true);
        apply.Click += (_, _) => ApplyChanges(restore: false);
        // Four lines of context explanation need more height with Noto/PingFang. Keep everything
        // below it in the normal flow rather than painting compression controls over the last line.
        foreach (Control control in Controls) if (control.Top >= 385) control.Top += 40;
        // Keep the helper inside the reading plate with 16 DIP below its full label box.
        // Status and actions follow the card instead of sharing its bottom edge.
        _status.Top = _clarityHint.Bottom + 24;
        _restore.Top = apply.Top = _status.Bottom + 10;
        ClientSize = new Size(ClientSize.Width, apply.Bottom + 16);
        _window.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true; _slider.Value = (int)_window.Value; _syncing = false; UpdateThreshold(); UpdateContextInfo();
        };
        _slider.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            _pendingContextValue = _slider.Value;RefreshMaterial();
        };
        _percent.ValueChanged += (_, _) => UpdateThreshold();
        UpdateThreshold();
        UpdateContextInfo();
        _themeTimer.Tick += (_, _) => { RefreshPalette(); UpdateContextInfo(); };
        _themeTimer.Start();
        _materialTimer.Tick+=(_,_)=>
        {
            if(!Visible || !_optics.Ready){_materialTimer.Stop();return;}
            FlushPendingInput();
            _renderFromTimer=true;
            try{if(_optics.NeedsFrame||_slider.PositionAnimating||_glassSlider.PositionAnimating)_materialDirty=true;EnsureMaterial();}
            finally{_renderFromTimer=false;}
            if(_fullSurfaceDirty || Environment.TickCount64-_shownAt<350)
            {_fullSurfaceDirty=false;Invalidate(true);}
            else
            {
                foreach(Control c in Controls)if(c is GlassButton or ContextSlider)
                {Invalidate(Rectangle.Inflate(c.Bounds,6,6));c.Invalidate();}
            }
            if(!_materialDirty && !_optics.NeedsFrame && !_slider.PositionAnimating && !_glassSlider.PositionAnimating)_materialTimer.Stop();
        };
    }

    internal void FlushPendingInput()
    {
        if(_pendingContextValue is int context)
        {
            _pendingContextValue=null;_syncing=true;
            try{_window.Value=context;}finally{_syncing=false;}
            UpdateThreshold();UpdateContextInfo();
        }
        if(_pendingClarityValue is int clarity)
        {
            _pendingClarityValue=null;CodexUi.PreviewGlassTransparency(clarity);
            _glassValue.Text=$"{clarity}%";_fullSurfaceDirty=true;
        }
    }

    private void UpdateThreshold() => _threshold.Text = CodexUi.T(
        $"达到 {CompactAt:N0} token 时压缩，保留响应空间。",
        $"Compact at {CompactAt:N0} tokens, leaving room for responses.");

    private void UpdateContextInfo()
    {
        var live = _getLiveContext();
        var display = live?.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) ?? CodexUi.T("暂无记录", "not recorded");
        _contextInfo.Text = CodexUi.T(
            $"全局配置：{_window.Value:N0} token。\n自动压缩阈值：{CompactAt:N0} token。\n当前对话报告窗口：{display}（仅供参考）。\n模型上限及预留空间另行生效；重载后新请求采用配置。",
            $"Global setting: {_window.Value:N0} tokens.\nCompaction threshold: {CompactAt:N0} tokens.\nCurrent chat reports: {display} (reference only).\nModel limits and reserves still apply. Reload for new requests.");

    }

    private void ApplyChanges(bool restore)
    {
        FlushPendingInput();
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
        var label = new GlassLabel { Text = text, Bounds = new Rectangle(x, y, width, height),
            UseCompatibleTextRendering = true,
            Font = CodexUi.FontForText(text, Math.Max(10.5f,size), semibold: bold),
            ForeColor = muted ? CodexUi.Mix(_palette.Background,_palette.Value,.82) : _palette.Value, BackColor = Color.Transparent,
            Tag = muted ? "muted" : "text" };
        Controls.Add(label); return label;
    }

    private Button AddButton(string text, int x, int y, int width, bool primary)
    {
        var button = new GlassButton { Text = text, Bounds = new Rectangle(x, y, width, 36),
            FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
            Font = CodexUi.FontForText(text, 11.5f, semibold: true),
            ForeColor = primary ? _palette.Background : _palette.Value,
            BackColor = primary ? _palette.Value : _palette.Background,
            Tag = primary ? "primary" : "secondary" };
        button.FlatAppearance.BorderColor = _palette.Border;
        // The optical frame already contains the complete button surface. A native
        // flat-button border is rectangular and becomes visible around the round lens.
        button.FlatAppearance.BorderSize = 0;
        Controls.Add(button); return button;
    }

    private void RefreshPalette()
    {
        var palette = _currentPalette();
        if(_appliedPalette==palette)return;
        _appliedPalette=palette;_fullSurfaceDirty=true;
        BackColor = palette.Background;
        ForeColor = palette.Value;
        foreach (Control child in Controls)
        {
            if (child is ContextSlider slider) { slider.SetPalette(palette); continue; }
            child.ForeColor = Equals(child.Tag, "muted") ? CodexUi.Mix(palette.Background,palette.Value,.82) : Equals(child.Tag, "primary") ? palette.Background : palette.Value;
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
        _materialDirty=true;
        if(_optics.Ready && Visible)_materialTimer.Start();
        else Invalidate(true);
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
        if(_materialFrame is not null && !_renderFromTimer)return;
        var started=System.Diagnostics.Stopwatch.GetTimestamp();
        _optics.CacheBackgroundEnabled=!_glassSlider.Capture;
        _optics.UseClarityPreview=_glassSlider.Capture;
        // Geometry retains native 4x MSAA while dragging. Restore full supersampling on release;
        // this avoids a whole-window 2x readback on every pointer update. Text stays native/sharp.
        _optics.RenderScaleOverride=(_slider.Capture||_glassSlider.Capture)?1:null;
        var palette=_currentPalette();var u=DeviceDpi/96f;
        var alpha=GlassAppearance.ReadingPlateAlpha(CodexUi.GlassTransparency);
        _optics.ReadingSurfaces((ReadingPlateBounds,16*u,Color.FromArgb(alpha,palette.Background)));
        foreach(Control control in Controls)
        {
            if(control is GlassButton button)
                _optics.Control(control.GetHashCode(),control.Bounds,control.Height/2f,button.MaterialHovered,button.MaterialPressed,Equals(control.Tag,"primary"));
            else if(control is ContextSlider slider)
            {
                _optics.Track(control.GetHashCode(),slider.Bounds,(float)slider.VisualFraction,palette.ProgressTrack,palette.Value);
                var thumb=slider.ThumbBounds;thumb.Offset(slider.Left,slider.Top);
                _optics.Control(control.GetHashCode(),thumb,thumb.Height/2f,slider.MaterialHovered,slider.Capture);
            }
        }
        if(_materialFrame is null || _materialFrame.Size!=ClientSize){_materialFrame?.Dispose();_materialFrame=new Bitmap(ClientSize.Width,ClientSize.Height,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);}
        _optics.PrepareClarityPreview(_materialFrame,palette.Background.GetBrightness()<.5);
        _optics.RenderInto(_materialFrame,palette.Background.GetBrightness()<.5);
        _materialDirty=false;
        // The fringe and native host must show the same frame, including the final
        // release frame. Keeping an older alpha fringe leaves optical trails.
        _smoothEdge?.Present(_materialFrame,32*u);
        if(MotionProbeActive)
        {
            ProbeRenderMs.Add((System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000d/System.Diagnostics.Stopwatch.Frequency);
            ProbeFrameTimes.Add(started*1000d/System.Diagnostics.Stopwatch.Frequency);
        }
    }

    internal bool DrawControlMaterial(Control control,Graphics graphics)
    {
        EnsureMaterial();if(!_optics.Ready || _materialFrame is null)return false;
        // Copy the same pixel grid as the parent. DrawImage(sourceRect,destRect)
        // resamples the crop boundary and leaves a rectangular seam around round controls.
        var composition=graphics.CompositingMode;
        try{graphics.CompositingMode=CompositingMode.SourceCopy;graphics.DrawImageUnscaled(_materialFrame,-control.Left,-control.Top);}
        finally{graphics.CompositingMode=composition;}
        return true;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        EnsureMaterial();
        if(_optics.Ready && _materialFrame is not null)
        {
            var composition=e.Graphics.CompositingMode;
            try{e.Graphics.CompositingMode=CompositingMode.SourceCopy;e.Graphics.DrawImageUnscaled(_materialFrame,0,0);}
            finally{e.Graphics.CompositingMode=composition;}
        }
        else base.OnPaintBackground(e);
    }

    protected override void OnShown(EventArgs e)
    { base.OnShown(e);_shownAt=Environment.TickCount64;_smoothEdge??=new SoftWindowEdge(this);RefreshPalette();RefreshMaterial(); }
    protected override void OnLocationChanged(EventArgs e){base.OnLocationChanged(e);_smoothEdge?.Follow();}
    protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);if(!Visible)_smoothEdge?.Hide();}
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        var inset=Math.Max(2,(int)Math.Round(2*DeviceDpi/96d));
        using var shape = FrameShape(new Rectangle(inset,inset,Math.Max(1,Width-2*inset),Math.Max(1,Height-2*inset)),Math.Max(1,32*DeviceDpi/96-inset));
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
    protected override void OnFormClosing(FormClosingEventArgs e){FlushPendingInput();CodexUi.CommitAppearance();base.OnFormClosing(e);}
    protected override void Dispose(bool disposing) { if (disposing){_smoothEdge?.Dispose();_themeTimer.Dispose();_materialTimer.Dispose();_materialFrame?.Dispose();_optics.Dispose();} base.Dispose(disposing); }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr w, IntPtr l);
}

internal sealed class GlassButton : Button
{
    private Bitmap? _textImage;
    private (string Text,Size Size,int Dpi,Font Font,int Color)? _textKey;
    private bool _hovered;
    private bool _pressed;
    internal bool MaterialHovered=>_hovered;
    internal bool MaterialPressed=>_pressed;
    private void MaterialChanged(){Invalidate();if(Parent is SettingsForm form)form.RefreshMaterial();}
    public GlassButton() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    protected override void OnPaintBackground(PaintEventArgs e) { }
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
        else
        {
            var color=Enabled?ForeColor:CodexUi.Mix(BackColor,ForeColor,.40);
            var key=(Text,ClientSize,DeviceDpi,Font,color.ToArgb());
            if(_textImage is null||_textKey!=key)
            {
                _textImage?.Dispose();_textImage=new Bitmap(Width,Height,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);_textImage.SetResolution(DeviceDpi,DeviceDpi);
                using var textGraphics=Graphics.FromImage(_textImage);textGraphics.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using var brush=new SolidBrush(color);using var format=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center,FormatFlags=StringFormatFlags.NoWrap};
                if(!AppleText.Draw(textGraphics,Text,Font,ClientRectangle,color,format.Alignment,format.LineAlignment))textGraphics.DrawString(Text,Font,brush,ClientRectangle,format);
                _textKey=key;
            }
            e.Graphics.DrawImageUnscaled(_textImage,0,0);
        }
        if (Focused && ShowFocusCues) { using var focus = new Pen(ForeColor) { DashStyle = DashStyle.Dot }; e.Graphics.DrawPath(focus, path); }
    }
    protected override void Dispose(bool disposing){if(disposing)_textImage?.Dispose();base.Dispose(disposing);}
}

internal class ContextSlider : Control
{
    protected override void OnPaintBackground(PaintEventArgs e) { }
    internal void ProbeDown(int x)=>OnMouseDown(new MouseEventArgs(MouseButtons.Left,1,x,Height/2,0));
    internal void ProbeMove(int x)=>OnMouseMove(new MouseEventArgs(MouseButtons.Left,0,x,Height/2,0));
    internal void ProbeUp(int x)=>OnMouseUp(new MouseEventArgs(MouseButtons.Left,1,x,Height/2,0));
    private OverlayThemePalette _palette;
    private int _value;
    private bool _hovered;
    internal bool MaterialHovered=>_hovered;
    private double? _dragFraction;
    private double _releaseFrom;
    private long _releasedAt;
    private double NumericFraction=>(_value-Minimum)/(double)Math.Max(1,Maximum-Minimum);
    internal bool PositionAnimating=>!Capture&&_releasedAt>0&&Environment.TickCount64-_releasedAt<GlassMotion.PositionSettleMilliseconds&&Math.Abs(_releaseFrom-NumericFraction)>.00001;
    internal double VisualFraction
    {
        get
        {
            if(Capture&&_dragFraction is double value)return value;
            if(!PositionAnimating)return NumericFraction;
            var t=Math.Clamp((Environment.TickCount64-_releasedAt)/(double)GlassMotion.PositionSettleMilliseconds,0,1);
            return _releaseFrom+(NumericFraction-_releaseFrom)*(1-Math.Pow(1-t,3));
        }
    }
    internal Rectangle ThumbBounds
    {
        get
        {
            var u=DeviceDpi/96f;var x=(float)(22*u+(Width-44*u)*VisualFraction);
            var width=32*u;var height=24*u;
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
        var x = (float)(start + (end - start) * VisualFraction);
        using var track = new Pen(_palette.ProgressTrack, 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var fill = new Pen(_palette.Value, 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(track, start, y, end, y); g.DrawLine(fill, start, y, x, y);
        var width = 32f; var height = 24f;
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
        _dragFraction=Math.Clamp((x-22*u)/Math.Max(1,Width-44*u),0,1);
        Value=Math.Clamp((int)Math.Round((Minimum+_dragFraction.Value*(Maximum-Minimum))/step)*step,Minimum,Maximum);
        MaterialChanged();
    }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Focus(); Capture = true; SetFromX(e.X); MaterialChanged(); } base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (Capture && e.Button == MouseButtons.Left) SetFromX(e.X); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { if(Capture)SetFromX(e.X);Capture = false; if(Parent is SettingsForm form)form.FlushPendingInput();MaterialChanged(); base.OnMouseUp(e); }
    protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture){if(_dragFraction is double from){_releaseFrom=from;_releasedAt=Environment.TickCount64;_dragFraction=null;}if(Parent is SettingsForm form)form.FlushPendingInput();MaterialChanged();}}
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
