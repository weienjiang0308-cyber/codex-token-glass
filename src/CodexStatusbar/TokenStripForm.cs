using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Globalization;
using SkiaSharp;

namespace CodexStatusbar;

internal enum OverlayEditGestureKind
{
    Move,
    Resize
}

internal sealed record OverlayEditPreviewEventArgs(
    OverlayEditGestureKind Kind,
    Point CursorScreen,
    Point FixedTopLeft,
    int ScalePercent);

internal readonly record struct OverlayRenderMetrics(
    double LabelFontPoints,
    double CompactValueFontPoints,
    double PanelHeaderFontPoints,
    double HighlightedValueFontPoints,
    int CapsuleRadius,
    int PanelRadius,
    int HorizontalPadding,
    int MetricGap,
    int DividerHeight,
    int PanelPadding,
    int HeaderHeight,
    int HighlightTopGap,
    int HighlightHeight,
    int ProgressTrackHeight,
    int ProgressVerticalGap,
    int CompactMetricGap,
    int EditHandleSize,
    int StrokeWidth,
    int TextShadowOffset,
    int EditOutlineInset,
    double CapsuleTextFontPoints,
    double CapsuleIconFontPoints)
{
    public static OverlayRenderMetrics Create(uint dpi, int scalePercent)
    {
        var effectiveDpi = dpi == 0 ? 96u : dpi;
        var sanitizedScale = ManualAttachmentRules.SanitizeScale(scalePercent);
        var userFactor = sanitizedScale / 100d;
        var pixelFactor = effectiveDpi / 96d * userFactor;
        int Scale(int dip) => (int)Math.Round(
            dip * pixelFactor,
            MidpointRounding.AwayFromZero);
        double ScaleFont(double points) => Math.Round(
            points * userFactor,
            2,
            MidpointRounding.AwayFromZero);

        return new OverlayRenderMetrics(
            ScaleFont(10d),
            ScaleFont(10.5d),
            ScaleFont(16d),
            ScaleFont(18d),
            Scale(10),
            Scale(40),
            Scale(10),
            Scale(8),
            Scale(14),
            Scale(22),
            Scale(24),
            Scale(28),
            Scale(52),
            Math.Max(1, Scale(4)),
            Scale(10),
            Scale(4),
            Math.Max(1, Scale(12)),
            Math.Max(1, Scale(1)),
            // One DIP, never zero: at 96 DPI a 1 px shadow is already enough to lift the glyphs off
            // a busy background without turning into an outline.
            Math.Max(1, Scale(1)),
            Math.Max(1, Scale(1)),
            // The docked strip's own type size, in points. 9.75 pt == 13 DIP == exactly what Codex's
            // composer labels use: its `--text-sm` is overridden to 13px for
            // `[data-codex-window-type=electron]`, and the four full-width CJK glyphs of the
            // permission label measure 78 device px at 150% -- 19.5 px per glyph, i.e. a 13 DIP em.
            // The lightning glyph is drawn smaller (see CapsuleIconFontPoints) because U+26A1 comes
            // from a fallback family whose ideograph-like proportions run taller than the digits.
            ScaleFont(9.75),
            ScaleFont(8.5));
    }
}

internal readonly record struct OverlayRenderDecorationState(
    bool ShowBorder,
    bool ShowDragHint,
    bool ShowResizeHandle,
    string DragHintText,
    IntRect DragHintBounds,
    double DragHintFontPoints);

/// <summary>
/// The status strip window.
///
/// <para><b>Transparency.</b> The window is a layered window
/// (<c>WS_EX_LAYERED</c> + <c>UpdateLayeredWindow</c>) painted from a 32-bit premultiplied ARGB
/// surface, so "background" pixels are genuinely alpha 0 rather than painted a key colour. That matters
/// for text quality: <c>TransparencyKey</c> works by drawing the glyphs against a magic colour and then
/// punching it out, which leaves the anti-aliased edges tinted with that colour (the classic halo).
/// A real alpha surface composites the glyph coverage directly, so the edges stay clean. Text is
/// therefore rendered with GDI+ grayscale anti-aliasing — ClearType's sub-pixel filter assumes an
/// opaque backdrop and would fringe against transparency.</para>
/// </summary>
internal sealed class TokenStripForm : Form
{
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int WmNcHitTest = 0x0084;
    private const int MaNoActivate = 3;
    private const int HtTransparent = -1;
    private const int AcSrcOver = 0x00;
    private const int AcSrcAlpha = 0x01;
    private const int UlwAlpha = 0x02;

    private OverlayPresentation _presentation;
    private OverlayThemePalette _palette = OverlayThemePalette.For(OverlayThemeKind.Dark);
    private OverlayEditGestureKind? _editGesture;
    private Point _gestureStartCursorScreen;
    private Rectangle _gestureStartBounds;
    private Point _fixedTopLeft;
    private int _gestureStartScalePercent = ManualAttachmentRules.DefaultScalePercent;
    private OverlayEditPreviewEventArgs? _lastEditPreview;
    private bool _transparentBackground = true;
    private bool _textShadow;
    private bool _settingsHovered;
    private bool _settingsPressed;
    private Bitmap? _settingsIcon;
    private Color _settingsIconColor;
    private bool _backdropEnabled;
    private (Color Tint, bool Enabled)? _backdropRequest;
    private readonly LiquidBackdropRenderer _liquid = new();
    private readonly UiFrameClock _liquidTimer = new();
    private bool _glassPressed;
    private Point _glassGrasp;
    private int _capsuleVariantIndex;
    private long _lastSurfaceAudit;
    internal int LastPresentError { get; private set; }
    private long _lastPresentedAt;
    private long _lastHealthPresentation;
    internal void EnsureSurfacePresented()
    {
        if (!Visible || !IsHandleCreated || IsDisposed) return;
        var now=Environment.TickCount64;
        if (LastPresentError != 0 || now - _lastHealthPresentation > 2000)
        {
            _lastHealthPresentation=now;
            RestorePopupLayer(force:true);UpdateLayeredSurface();DwmFlush();
        }
    }
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) { _blittedPresentationVersion = -1; UpdateLayeredSurface(); }
    }
    private Region? _inputRegion;

    /// <summary>
    /// Bumped whenever something that affects the drawn pixels changes. It is the cache key for the
    /// variant measurement and the gate for <see cref="OnPaint"/>: a repaint that is not the result of
    /// a content change has nothing to do, which is what makes a 60 Hz position update free.
    /// </summary>
    private int _presentationVersion;

    private int _blittedPresentationVersion = -1;
    private double[]? _measuredWidths;
    private uint _measuredWidthsDpi;
    private int _measuredWidthsVersion = -1;

    /// <summary>
    /// The UI font family. Not a guess: Codex's own CSS declares
    /// <c>--font-sans-default: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif</c>, and of
    /// those only "Segoe UI" resolves on Windows. Rendering the model selector's label in Segoe UI
    /// Regular at 13 DIP gives 170 px of ink against the 172 px measured on screen; Segoe UI Variable
    /// gives 167 and Segoe UI Semibold 182, so Regular is the match.
    /// </summary>
    private ComposerTypeface _typeface = ComposerTypeface.Resolve(null, null);
    private string StripFontFamily => _typeface.Family;
    internal void SetComposerTypeface(ComposerTypeface? typeface)
    {
        var next = typeface ?? ComposerTypeface.Resolve(null, null);
        if (next == _typeface) return;
        _typeface = next; _measuredWidths = null; _presentationVersion++;
        InvalidateSurface();
    }

    public bool ClickToExpand { get; }

    public TokenStripForm(bool clickToExpand = false)
    {
        ClickToExpand = clickToExpand;
        _liquidTimer.Tick += (_, _) =>
        {
            if (!Visible || !_liquid.Ready) { _liquidTimer.Stop(); return; }
            InvalidateSurface();
            if (!_liquid.NeedsFrame) _liquidTimer.Stop();
        };
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ForeColor = _palette.Value;
        DoubleBuffered = true;

        _presentation = OverlayPresentationBuilder.CreateWaiting(
            string.Empty,
            DisplayField.Total,
            DisplayField.ContextPercent,
            DisplayField.Total | DisplayField.ContextPercent);
        ApplyLayout(new OverlayLayoutResult(
            OverlayVisualState.Collapsed,
            CollapsedDisplayMode.TwoFields,
            ExpansionDirection.Down,
            96,
            new IntRect(0, 0, 196, 34),
            new IntRect(0, 0, 196, 34),
            default,
            0));
    }

    public event EventHandler? CapsuleClicked;
    public event EventHandler? SettingsRequested;
    public event EventHandler<OverlayEditPreviewEventArgs>? EditPreviewChanged;
    public event EventHandler<OverlayEditPreviewEventArgs>? EditGestureCompleted;
    public event EventHandler? EditSaveRequested;
    public event EventHandler? EditCancelRequested;

    public OverlayLayoutResult? CurrentLayout { get; private set; }

    internal Rectangle? PanelScreenBounds => CurrentLayout is { PanelBounds.IsEmpty: false } layout
        ? new Rectangle(layout.WindowBounds.X + layout.PanelBounds.X, layout.WindowBounds.Y + layout.PanelBounds.Y,
            layout.PanelBounds.Width, layout.PanelBounds.Height)
        : null;

    internal Rectangle? CapsuleScreenBounds => CurrentLayout is { CapsuleBounds.IsEmpty: false } layout
        ? new Rectangle(layout.WindowBounds.X + layout.CapsuleBounds.X, layout.WindowBounds.Y + layout.CapsuleBounds.Y,
            layout.CapsuleBounds.Width, layout.CapsuleBounds.Height)
        : null;
    public bool IsEditMode { get; private set; }
    internal OverlayPresentation CurrentPresentation => _presentation;
    internal OverlayThemePalette CurrentThemePalette => _palette;
    internal Color DataLabelColor => CodexUi.Mix(_palette.Background,_palette.Value,.82);

    /// <summary>When <paramref name="value"/> is true the strip draws text only — no fill, no border.</summary>
    internal void SetTransparentBackground(bool value)
    {
        if (_transparentBackground == value)
        {
            return;
        }

        _transparentBackground = value;
        InvalidateSurface();
    }

    /// <summary>
    /// A faint drop shadow behind the glyphs. Off by default: docked inside the composer the strip
    /// sits on Codex's own background with Codex's own text colour, so the shadow is not what is
    /// carrying readability — the theme match is. Turn it on only if the strip has to survive a
    /// backdrop it disagrees with.
    /// </summary>
    internal void SetTextShadow(bool value)
    {
        if (_textShadow == value)
        {
            return;
        }

        _textShadow = value;
        InvalidateSurface();
    }

    internal bool TextShadow => _textShadow;

    /// <summary>
    /// Which entry of <see cref="OverlayPresentation.Variants"/> to draw. The responsive policy picks
    /// it, so the text that was measured for the layout is exactly the text that gets rendered.
    /// </summary>
    internal void SetCapsuleVariant(int index)
    {
        var clamped = Math.Clamp(index, 0, Math.Max(0, _presentation.Variants.Count - 1));
        if (_capsuleVariantIndex == clamped)
        {
            return;
        }

        _capsuleVariantIndex = clamped;
        InvalidateSurface();
    }

    internal int CapsuleVariantIndex => _capsuleVariantIndex;

    internal string CurrentCapsuleVariantText =>
        _presentation.Variants.Count == 0
            ? string.Empty
            : _presentation.Variants[Math.Clamp(_capsuleVariantIndex, 0, _presentation.Variants.Count - 1)];

    /// <summary>
    /// The width of every strip variant, in DIP <b>at 100% overlay scale</b>, measured with the same
    /// font, DPI and string format the renderer will use. The responsive policy compares these against
    /// the room the composer actually leaves, so a variant is never chosen on an estimate.
    ///
    /// <para>The measurement is deliberately unscaled: <see cref="OverlayLayoutCalculator.GetCapsuleSizeForText"/>
    /// applies the user's scale itself, and a width that already carried it would be scaled twice.</para>
    ///
    /// <paramref name="dpiOverride"/> exists so a probe can measure before any layout has been applied;
    /// the running application leaves it null and the current layout's DPI is used.
    /// </summary>
    internal double[] MeasureCapsuleWidthsDip(uint? dpiOverride = null)
    {
        var variants = _presentation.Variants;
        if (variants.Count == 0)
        {
            return Array.Empty<double>();
        }

        var dpi = dpiOverride is { } explicitDpi && explicitDpi != 0 ? explicitDpi : EffectiveDpi;

        // The measurement is a bitmap, a Graphics, two fonts and one DrawString per variant — far too
        // much to repeat on every frame of a drag, and the answer cannot change unless the text or the
        // DPI does. A probe passing an explicit DPI deliberately bypasses the cache: it is measuring a
        // DPI this window is not currently laid out at.
        if (dpiOverride is null
            && _measuredWidths is { } cached
            && _measuredWidthsDpi == dpi
            && _measuredWidthsVersion == _presentationVersion)
        {
            return cached;
        }

        var widths = new double[variants.Count];
        var metrics = OverlayRenderMetrics.Create(dpi, ManualAttachmentRules.DefaultScalePercent);
        using var bitmap = new Bitmap(1, 1);
        // The string is measured against the surface DPI so the result can be converted back to DIP
        // exactly; measuring at 96 DPI would quantise 150% text to 100% metrics.
        bitmap.SetResolution(dpi, dpi);
        using var graphics = Graphics.FromImage(bitmap);
        ConfigureSurfaceGraphics(graphics);
        using var font = CreateStripFont((float)metrics.CapsuleTextFontPoints);
        using var iconFont = CreateStripFont((float)metrics.CapsuleIconFontPoints);
        for (var index = 0; index < variants.Count; index++)
        {
            var deviceWidth = MeasureStripLine(graphics, variants[index], font, iconFont);
            widths[index] = deviceWidth * 96d / dpi;
        }

        if (dpiOverride is null)
        {
            _measuredWidths = widths;
            _measuredWidthsDpi = dpi;
            _measuredWidthsVersion = _presentationVersion;
        }

        return widths;
    }

    /// <summary>The strip's type size in points for the current DPI, exposed for probes and debug output.</summary>
    internal double StripFontPoints => OverlayRenderMetrics
        .Create(EffectiveDpi, EffectiveScalePercent)
        .CapsuleTextFontPoints;

    private uint EffectiveDpi =>
        CurrentLayout?.Dpi is { } dpi && dpi != 0 ? dpi : (uint)Math.Max(96, DeviceDpi);

    private int EffectiveScalePercent =>
        CurrentLayout?.ScalePercent ?? ManualAttachmentRules.DefaultScalePercent;

    private Font CreateStripFont(float points) => new(_typeface.Family,
        (float)(points * _typeface.Points / 9.75), FontStyle.Regular, GraphicsUnit.Point);

    internal int SetBoundsCoreCallCount { get; private set; }
    internal bool IsEditGestureActive => _editGesture is not null;
    internal bool IsLayered =>
        IsHandleCreated && (GetWindowLongPtr(Handle, GwlExStyle).ToInt64() & WsExLayered) != 0;

    /// <summary>True while locked: <c>WS_EX_TRANSPARENT</c> plus <c>WS_EX_NOACTIVATE</c>, i.e. the whole
    /// window — glyph pixels included — is transparent to the mouse and never takes focus.</summary>
    internal bool IsClickThrough
    {
        get
        {
            if (!IsHandleCreated)
            {
                return false;
            }

            var exStyle = GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
            return (exStyle & WsExTransparent) != 0 && (exStyle & WsExNoActivate) != 0;
        }
    }

    internal OverlayRenderDecorationState RenderDecorations
    {
        get
        {
            if (!IsEditMode
                || CurrentLayout is null
                || CurrentLayout.CapsuleBounds.IsEmpty)
            {
                return new OverlayRenderDecorationState(
                    false,
                    false,
                    false,
                    string.Empty,
                    default,
                    0d);
            }

            var metrics = OverlayRenderMetrics.Create(
                CurrentLayout.Dpi,
                CurrentLayout.ScalePercent);
            var capsule = CurrentLayout.CapsuleBounds.ToRectangle();
            var content = Rectangle.Inflate(
                capsule,
                -metrics.HorizontalPadding,
                0);
            var hintRight = Math.Max(
                content.Left,
                content.Right - metrics.EditHandleSize - metrics.MetricGap);
            return new OverlayRenderDecorationState(
                true,
                true,
                true,
                "拖动调整位置",
                new IntRect(
                    content.Left,
                    content.Top,
                    hintRight - content.Left,
                    content.Height),
                metrics.LabelFontPoints);
        }
    }

    internal Rectangle EditResizeHandleBounds
    {
        get
        {
            if (CurrentLayout is null || CurrentLayout.CapsuleBounds.IsEmpty)
            {
                return Rectangle.Empty;
            }

            var metrics = OverlayRenderMetrics.Create(
                CurrentLayout.Dpi,
                CurrentLayout.ScalePercent);
            var capsule = CurrentLayout.CapsuleBounds.ToRectangle();
            var size = Math.Min(
                metrics.EditHandleSize,
                Math.Min(capsule.Width, capsule.Height));
            return new Rectangle(
                capsule.Right - size,
                capsule.Bottom - size,
                size,
                size);
        }
    }

    protected override bool ShowWithoutActivation => !IsEditMode;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _backdropRequest = null;
        UpdateBackdrop();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow;

            // Always layered: the surface is per-pixel alpha whether or not the capsule fill is drawn.
            parameters.ExStyle |= WsExLayered;
            if (!IsEditMode)
            {
                parameters.ExStyle |= WsExNoActivate;

                // Locked means *completely* click-through: every pixel, including the ones the glyphs
                // occupy, must let Codex have the mouse. Alpha-based hit testing alone would still
                // swallow clicks that land on a glyph, so the whole window is marked transparent here
                // and its WM_NCHITTEST answers HTTRANSPARENT unconditionally while locked.
                //
                // Both are dropped while adjusting the position — CreateParams is re-evaluated by the
                // RecreateHandle() in BeginEditMode/EndEditMode, so the switch is atomic with the
                // handle that carries it.
                if (!ClickToExpand)
                {
                    parameters.ExStyle |= WsExTransparent;
                }
            }

            return parameters;
        }
    }

    public void SetPresentation(OverlayPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        _presentation = presentation;
        InvalidateSurface();
    }

    public void ApplyTheme(OverlayThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (_palette == palette)
        {
            return;
        }

        _palette = palette;
        ForeColor = palette.Value;
        UpdateBackdrop();
        InvalidateSurface();
    }

    public void ApplyLayout(OverlayLayoutResult layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.State == OverlayVisualState.Expanded && CurrentLayout?.State != OverlayVisualState.Expanded && ClickToExpand && IsHandleCreated && Visible)
        {
            _liquid.Capture(layout.WindowBounds.ToRectangle(), layout.PanelBounds.ToRectangle(), layout.Dpi);
            if (_liquid.Ready) _liquid.SetButtonBounds(GetSettingsButtonBounds(layout),layout.WindowBounds.ToRectangle().Size);
            if (_liquid.Ready) _liquidTimer.Start();
        }
        else if (layout.State != OverlayVisualState.Expanded)
        {
            _liquidTimer.Stop(); _liquid.Clear(); _glassPressed = false;
            _settingsPressed=false;_settingsHovered=false;
        }
        CurrentLayout = layout;
        SetBounds(
            layout.WindowBounds.X,
            layout.WindowBounds.Y,
            layout.WindowBounds.Width,
            layout.WindowBounds.Height,
            BoundsSpecified.All);
        ApplyInputRegion(layout);
        UpdateBackdrop();
        InvalidateSurface();
    }

    private void UpdateBackdrop()
    {
        if (!IsHandleCreated) return;
        var request = (_palette.Background, false);
        if (_backdropRequest == request) return;
        _backdropRequest = request;
        _backdropEnabled = DesktopMaterial.Apply(Handle, _palette.Background, false);
    }

    /// <summary>
    /// The fast path's write: move the window to <paramref name="layout"/>'s rectangle and nothing else.
    ///
    /// <para>This is the whole point of separating position from render. A host window that only moved
    /// changes no pixel of the strip, so the expensive half of <see cref="ApplyLayout"/> — measuring the
    /// variants, re-rendering the 32-bit surface and blitting it through <c>UpdateLayeredWindow</c> —
    /// would produce a byte-identical bitmap. <c>SetWindowPos</c> with <c>SWP_NOREDRAW</c> moves the
    /// existing surface instead, at a cost measured in microseconds.</para>
    ///
    /// </summary>
    /// <returns>
    /// False when the fast path cannot answer and the caller must fall back to
    /// <see cref="ApplyLayout"/>: the window's shape changed (so the surface really does have to be
    /// rebuilt), or the strip's hit region/state changed with it.
    /// </returns>
    internal bool ApplyLayoutPositionOnly(OverlayLayoutResult layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!IsHandleCreated || IsDisposed)
        {
            return false;
        }

        // A different window size means a different surface, and a different capsule or panel box means
        // a different hit region. Neither is a move; both need the full path.
        if (layout.WindowBounds.Width != Width
            || layout.WindowBounds.Height != Height
            || CurrentLayout is not { } previous
            || previous.State != layout.State
            || previous.CapsuleBounds != layout.CapsuleBounds
            || previous.PanelBounds != layout.PanelBounds)
        {
            return false;
        }

        CurrentLayout = layout;
        return MoveWindowTo(layout.WindowBounds);
    }

    internal void UseSyntheticBackdrop(Bitmap scene,uint dpi)
    {
        if(CurrentLayout is not {} layout)throw new InvalidOperationException("Apply the demo layout first");
        _liquid.SetBackdrop(scene,layout.PanelBounds.ToRectangle(),dpi,motionOverride:false);
        _liquid.SetButtonBounds(SettingsButtonBounds,scene.Size);
    }

    internal void RestorePopupLayer(bool force=false)
    {
        if(!IsHandleCreated || !Visible || IsDisposed)return;
        // WinForms modal dialogs may restore this no-activate window below its owner.
        // Reassert the native flag instead of the cached managed TopMost property.
        if(force || (GetWindowLongPtr(Handle,GwlExStyle).ToInt64() & 0x8)==0)
            SetWindowPos(Handle,new IntPtr(-1),0,0,0,0,SwpNoSize|0x2|SwpNoActivate);
    }

    /// <summary>
    /// Moves the window without touching its content, its Z-order, its activation state or its input
    /// region. Returns false when no move was needed or the move could not be made.
    /// </summary>
    internal bool MoveWindowTo(IntRect bounds)
    {
        if (!IsHandleCreated || IsDisposed)
        {
            return false;
        }

        // §14: a move to where the window already is must not reach DWM at all.
        if (bounds.X == Left && bounds.Y == Top && bounds.Width == Width && bounds.Height == Height)
        {
            return true;
        }

        SetWindowPosCallCount++;
        return SetWindowPos(
            Handle,
            IntPtr.Zero,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            // SWP_NOSIZE: a position-only write can never resize the window away from the surface it
            // already has. SWP_NOZORDER: with a null insert-after, omitting this would drop the strip
            // to the bottom of the Z-order — the one thing that must not change. NOACTIVATE and
            // NOOWNERZORDER keep focus and ownership exactly as they were, NOSENDCHANGING skips the
            // WM_WINDOWPOSCHANGING round trip, and NOREDRAW means no WM_PAINT is generated for a move
            // that changed no pixel.
            SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging | SwpNoRedraw);
    }

    /// <summary>How many real <c>SetWindowPos</c> calls the process has made for the strip.</summary>
    internal int SetWindowPosCallCount { get; private set; }

    /// <summary>
    /// Repaints the layered content directly instead of waiting for <c>WM_PAINT</c>.
    ///
    /// <para>This matters: a layered window's content only exists once <c>UpdateLayeredWindow</c> has
    /// been called, and relying on the paint cycle to do it left the strip invisible in the real
    /// application while every offscreen render looked perfect. Driving the blit from the state changes
    /// themselves removes that dependency — the surface is ready the moment the layout is applied.</para>
    /// </summary>
    private void InvalidateSurface()
    {
        // Every content-changing setter (presentation, theme, transparency, shadow, variant, layout,
        // edit mode) funnels through here, so this is the exact set of events that make the drawn
        // pixels differ from the ones already in the surface.
        _presentationVersion++;
        Invalidate();
        UpdateLayeredSurface();
    }

    /// <summary>
    /// Hit-testing is independent of the rendered alpha. Assigning a rounded Form.Region cuts off
    /// partial-alpha edge pixels in the fallback renderer and turns smooth curves into stair steps.
    /// </summary>
    private void ApplyInputRegion(OverlayLayoutResult layout)
    {
        using var combined = new GraphicsPath();
        if (!layout.CapsuleBounds.IsEmpty)
        {
            combined.AddRectangle(layout.CapsuleBounds.ToRectangle());
        }

        if (!layout.PanelBounds.IsEmpty)
        {
            var metrics = OverlayRenderMetrics.Create(layout.Dpi, layout.ScalePercent);
            var rect = layout.PanelBounds.ToRectangle();
            var margin = _liquid.Ready ? (int)Math.Round(12d * layout.Dpi / 96d * layout.ScalePercent / 100d) : 0;
            rect.Inflate(margin, margin);
            if (_liquid.Ready) combined.AddRectangle(rect); // Animated shader alpha owns the optical outline.
            else
            {
                using var panel = CreateRoundedRectanglePath(rect, metrics.PanelRadius);
                combined.AddPath(panel, false);
            }
        }

        _inputRegion?.Dispose();
        _inputRegion = combined.PointCount == 0 ? null : new Region(combined);
        // Keep an explicit rectangular native region so alpha-light whitespace remains clickable.
        // It never intersects a curved visual edge; the separate hit region rejects empty corners.
        var previous = Region;
        Region = new Region(new Rectangle(0,0,layout.WindowBounds.Width,layout.WindowBounds.Height));
        previous?.Dispose();
    }

    public void BeginEditMode(int scalePercent)
    {
        if (IsEditMode)
        {
            return;
        }
        if (CurrentLayout?.State != OverlayVisualState.Collapsed
            || !CurrentLayout.PanelBounds.IsEmpty)
        {
            throw new InvalidOperationException("编辑模式只能从收起布局开始。");
        }

        _gestureStartScalePercent = ManualAttachmentRules.SanitizeScale(scalePercent);
        IsEditMode = true;
        if (IsHandleCreated)
        {
            RecreateHandle();
        }
        Activate();
        Focus();
        InvalidateSurface();
    }

    public void EndEditMode()
    {
        if (!IsEditMode)
        {
            return;
        }

        CancelEditGesture();
        IsEditMode = false;
        if (IsHandleCreated)
        {
            RecreateHandle();
        }
        InvalidateSurface();
    }

    internal void SimulateCapsuleClick(Point screenPoint) =>
        HandleMouseUp(MouseButtons.Left, PointToClient(screenPoint), screenPoint);

    internal void SimulateEditDrag(Point startScreen, Point currentScreen)
    {
        HandleMouseDown(MouseButtons.Left, PointToClient(startScreen), startScreen);
        HandleMouseMove(PointToClient(currentScreen), currentScreen);
    }

    internal void SimulateEditResize(Point startScreen, Point currentScreen)
    {
        HandleMouseDown(MouseButtons.Left, PointToClient(startScreen), startScreen);
        HandleMouseMove(PointToClient(currentScreen), currentScreen);
    }

    internal void SimulateEditGestureCompleted(Point currentScreen) =>
        HandleMouseUp(MouseButtons.Left, PointToClient(currentScreen), currentScreen);

    internal void SimulateEditCaptureLost()
    {
        Capture = false;
        if (_editGesture is not null)
        {
            OnMouseCaptureChanged(EventArgs.Empty);
        }
    }

    internal bool SimulateEditCommand(Keys keyData) => HandleEditCommand(keyData);

    internal void SimulatePaint(Graphics graphics, Size size) => RenderSurface(graphics, size);

    /// <summary>
    /// Forces the layered surface to exist now instead of on the next paint message. Until the first
    /// <c>UpdateLayeredWindow</c> the window has no surface at all, which makes it invisible to
    /// hit-testing — so this is what lets a probe measure real click-through, and it also removes the
    /// brief window-exists-but-is-empty moment right after <c>Show()</c>.
    /// </summary>
    internal void RefreshSurface() => UpdateLayeredSurface();

    /// <summary>
    /// Renders the strip into a fresh 32-bit premultiplied ARGB surface. This is the one and only
    /// rendering path: <see cref="UpdateLayeredSurface"/> blits exactly what this returns, and the
    /// render probe inspects exactly what this returns, so a probe result is evidence about the real
    /// window rather than about a reimplementation of it.
    /// </summary>
    internal Bitmap RenderSurfaceBitmap(Size size, uint dpi)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        // Fonts are specified in points, and GDI+ resolves those against the Graphics DPI. The bitmap
        // must therefore carry the host DPI, or 150% scaling would render 100%-sized text.
        bitmap.SetResolution(dpi, dpi);
        if (CurrentLayout?.State == OverlayVisualState.Expanded) UpdateBackdrop();
        using var graphics = Graphics.FromImage(bitmap);
        ConfigureSurfaceGraphics(graphics);
        // Per-pixel layered hit testing needs nonzero alpha across the clickable strip, including
        // whitespace between digits. 1/255 alpha is visually transparent but keeps the full target.
        if (ClickToExpand && CurrentLayout is { } hitLayout)
        {
            using var hitFill = new SolidBrush(Color.FromArgb(1,_palette.Background));
            graphics.FillRectangle(hitFill,hitLayout.CapsuleBounds.ToRectangle());
        }
        using (AppleText.Composite(bitmap))
        {
            RenderSurface(graphics, size);
        }
        return bitmap;
    }

    private static void ConfigureSurfaceGraphics(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        // Grayscale, not ClearType. See the class remarks: sub-pixel filtering assumes an opaque
        // backdrop and would fringe against a per-pixel-alpha surface.
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    internal bool InputContainsClientPoint(Point point) => _inputRegion?.IsVisible(point) == true;
    public bool ContainsScreenPoint(Point screenPoint) => InputContainsClientPoint(PointToClient(screenPoint));

    protected override void SetBoundsCore(
        int x,
        int y,
        int width,
        int height,
        BoundsSpecified specified)
    {
        SetBoundsCoreCallCount++;
        base.SetBoundsCore(x, y, width, height, specified);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmMouseActivate && !IsEditMode)
        {
            message.Result = (IntPtr)MaNoActivate;
            return;
        }

        if (message.Msg == WmNcHitTest)
        {
            // Locked: transparent everywhere, glyph pixels included, so Codex keeps every click.
            if (!IsEditMode && !ClickToExpand)
            {
                message.Result = (IntPtr)HtTransparent;
                return;
            }

            if (CurrentLayout is not null)
            {
                var packed = message.LParam.ToInt64();
                var screenPoint = new Point(
                    unchecked((short)(packed & 0xffff)),
                    unchecked((short)((packed >> 16) & 0xffff)));
                var clientPoint = PointToClient(screenPoint);
                if (!InputContainsClientPoint(clientPoint))
                {
                    message.Result = (IntPtr)HtTransparent;
                    return;
                }
            }
        }

        base.WndProc(ref message);
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
        if (!IsEditMode && eventArgs.Button==MouseButtons.Left && SettingsButtonBounds.Contains(eventArgs.Location))
        {
            _settingsPressed=true;_liquid.ButtonState(true,true);_liquidTimer.Start();Capture=true;
        }
        if (!IsEditMode && _liquid.Ready && eventArgs.Button == MouseButtons.Left &&
            CurrentLayout?.PanelBounds.Contains(eventArgs.X, eventArgs.Y) == true && !SettingsButtonBounds.Contains(eventArgs.Location))
        {
            _glassPressed = true; _glassGrasp = eventArgs.Location; Capture = true;
            _liquid.Press(); _liquidTimer.Start();
        }
        HandleMouseDown(
            eventArgs.Button,
            eventArgs.Location,
            PointToScreen(eventArgs.Location));
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        if (_glassPressed)
        {
            _liquid.Press(eventArgs.X - _glassGrasp.X, eventArgs.Y - _glassGrasp.Y);
            _liquidTimer.Start();
        }
        var hovering = SettingsButtonBounds.Contains(eventArgs.Location);
        if (hovering != _settingsHovered)
        {
            _settingsHovered = hovering;_liquid.ButtonState(hovering,_settingsPressed);_liquidTimer.Start();InvalidateSurface();
        }
        if (!IsEditMode) Cursor = hovering || CurrentLayout?.CapsuleBounds.Contains(eventArgs.X, eventArgs.Y) == true
            ? Cursors.Hand : Cursors.Default;
        HandleMouseMove(eventArgs.Location, PointToScreen(eventArgs.Location));
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        if (_settingsPressed) {_settingsPressed=false;Capture=false;_liquid.ButtonState(_settingsHovered,false);_liquidTimer.Start();}
        if (_glassPressed)
        {
            _glassPressed = false; Capture = false; _liquid.Release(); _liquidTimer.Start();
        }
        HandleMouseUp(
            eventArgs.Button,
            eventArgs.Location,
            PointToScreen(eventArgs.Location));
    }

    protected override void OnMouseCaptureChanged(EventArgs eventArgs)
    {
        base.OnMouseCaptureChanged(eventArgs);
        if(!Capture && _settingsPressed)
        {_settingsPressed=false;_liquid.ButtonState(_settingsHovered,false);_liquidTimer.Start();}
        if (!Capture && _glassPressed)
        { _glassPressed = false; _liquid.Release(); _liquidTimer.Start(); }
        if (Capture || _editGesture is null)
        {
            return;
        }

        if (_lastEditPreview is not null)
        {
            CompleteEditGesture(_lastEditPreview, releaseCapture: false);
        }
        else
        {
            CancelEditGesture(releaseCapture: false);
        }
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (IsEditMode && HandleEditCommand(keyData))
        {
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    private void HandleMouseDown(MouseButtons button, Point clientPoint, Point cursorScreen)
    {
        if (!IsEditMode
            || button != MouseButtons.Left
            || CurrentLayout is null
            || !CurrentLayout.CapsuleBounds.Contains(clientPoint.X, clientPoint.Y))
        {
            return;
        }

        _editGesture = EditResizeHandleBounds.Contains(clientPoint)
            ? OverlayEditGestureKind.Resize
            : OverlayEditGestureKind.Move;
        _gestureStartCursorScreen = cursorScreen;
        _gestureStartBounds = Bounds;
        _fixedTopLeft = Location;
        _gestureStartScalePercent = ManualAttachmentRules.SanitizeScale(
            CurrentLayout.ScalePercent);
        _lastEditPreview = null;
        Capture = true;
    }

    private void HandleMouseMove(Point clientPoint, Point cursorScreen)
    {
        _ = clientPoint;
        if (!IsEditMode || _editGesture is null)
        {
            return;
        }

        var deltaX = cursorScreen.X - _gestureStartCursorScreen.X;
        var deltaY = cursorScreen.Y - _gestureStartCursorScreen.Y;
        var scalePercent = _gestureStartScalePercent;
        if (_editGesture == OverlayEditGestureKind.Move)
        {
            Location = new Point(
                _gestureStartBounds.X + deltaX,
                _gestureStartBounds.Y + deltaY);
        }
        else
        {
            scalePercent = ManualAttachmentCalculator.CalculateScale(
                _gestureStartBounds.Size,
                _gestureStartScalePercent,
                deltaX,
                deltaY);
        }

        _lastEditPreview = new OverlayEditPreviewEventArgs(
            _editGesture.Value,
            cursorScreen,
            _fixedTopLeft,
            scalePercent);
        EditPreviewChanged?.Invoke(this, _lastEditPreview);
    }

    private void HandleMouseUp(MouseButtons button, Point clientPoint, Point cursorScreen)
    {
        if (button != MouseButtons.Left)
        {
            return;
        }

        if (!IsEditMode && SettingsButtonBounds.Contains(clientPoint))
        {
            SettingsRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (IsEditMode)
        {
            if (_editGesture is null)
            {
                return;
            }

            HandleMouseMove(clientPoint, cursorScreen);
            var completed = _lastEditPreview ?? new OverlayEditPreviewEventArgs(
                _editGesture.Value,
                cursorScreen,
                _fixedTopLeft,
                _gestureStartScalePercent);
            CompleteEditGesture(completed, releaseCapture: true);
            return;
        }

        if (CurrentLayout is not null
            && CurrentLayout.CapsuleBounds.Contains(clientPoint.X, clientPoint.Y))
        {
            CapsuleClicked?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CompleteEditGesture(
        OverlayEditPreviewEventArgs completed,
        bool releaseCapture)
    {
        _editGesture = null;
        _lastEditPreview = null;
        if (releaseCapture && Capture)
        {
            Capture = false;
        }
        EditGestureCompleted?.Invoke(this, completed);
    }

    private void CancelEditGesture(bool releaseCapture = true)
    {
        _editGesture = null;
        _lastEditPreview = null;
        if (releaseCapture && Capture)
        {
            Capture = false;
        }
    }

    private bool HandleEditCommand(Keys keyData)
    {
        if (!IsEditMode)
        {
            return false;
        }

        switch (keyData & Keys.KeyCode)
        {
            case Keys.Enter:
                EditSaveRequested?.Invoke(this, EventArgs.Empty);
                return true;
            case Keys.Escape:
                EditCancelRequested?.Invoke(this, EventArgs.Empty);
                return true;
            default:
                return false;
        }
    }

    // ------------------------------------------------------------------ rendering

    /// <summary>The layered surface is the only paint path, so the default erase must not run.</summary>
    protected override void OnPaintBackground(PaintEventArgs eventArgs)
    {
        _ = eventArgs;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        // A repaint request that is not the result of a content change has nothing to do. This matters
        // for the position fast path: a window move can generate WM_PAINT, and the naive handler would
        // re-render the whole surface and blit it for every frame of a drag — exactly the cost the fast
        // path exists to avoid.
        if (_blittedPresentationVersion != _presentationVersion)
        {
            UpdateLayeredSurface();
        }
    }

    /// <summary>
    /// Renders the strip onto a fresh 32-bit premultiplied ARGB bitmap and hands it to
    /// <c>UpdateLayeredWindow</c>. Anything not drawn stays fully transparent.
    /// </summary>
    private void UpdateLayeredSurface()
    {
        if (!IsHandleCreated || IsDisposed)
        {
            return;
        }

        var size = Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        var dpi = CurrentLayout?.Dpi is { } layoutDpi && layoutDpi != 0
            ? layoutDpi
            : (uint)Math.Max(96, DeviceDpi);

        using var bitmap = RenderSurfaceBitmap(size, dpi);
        if (BlitLayeredSurface(bitmap, size))
        { _blittedPresentationVersion = _presentationVersion; _lastPresentedAt = Environment.TickCount64; }
        var audit = Environment.GetEnvironmentVariable("CODEX_STATUSBAR_RENDER_AUDIT");
        if (!string.IsNullOrEmpty(audit) && Environment.TickCount64 - _lastSurfaceAudit > 1000)
        {
            _lastSurfaceAudit = Environment.TickCount64;
            Directory.CreateDirectory(audit);
            bitmap.Save(Path.Combine(audit,"live-surface.png"),ImageFormat.Png);
            File.WriteAllText(Path.Combine(audit,"live-surface.json"),System.Text.Json.JsonSerializer.Serialize(new {
                error=LastPresentError,text=CurrentCapsuleVariantText,window=Bounds,capsule=CurrentLayout?.CapsuleBounds,
                font=StripFontFamily,points=StripFontPoints,dpi,backdrop=_backdropEnabled,glass=_liquid.Ready,
                gpu=_liquid.GpuAccelerated,glassError=_liquid.LastError
            }));
        }
    }

    private bool BlitLayeredSurface(Bitmap bitmap, Size size)
    {
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            LastPresentError = Marshal.GetLastWin32Error();
            return false;
        }

        var memoryDc = CreateCompatibleDC(screenDc);
        var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var previous = SelectObject(memoryDc, hBitmap);
        try
        {
            var destination = new NativePoint { X = Left, Y = Top };
            var source = new NativePoint { X = 0, Y = 0 };
            var nativeSize = new NativeSize { CX = size.Width, CY = size.Height };
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha
            };
            var success = UpdateLayeredWindow(
                Handle,
                screenDc,
                ref destination,
                ref nativeSize,
                memoryDc,
                ref source,
                0,
                ref blend,
                UlwAlpha);
            LastPresentError = success ? 0 : Marshal.GetLastWin32Error();
            return success;
        }
        finally
        {
            _ = SelectObject(memoryDc, previous);
            _ = DeleteObject(hBitmap);
            _ = DeleteDC(memoryDc);
            _ = ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void RenderSurface(Graphics graphics, Size size)
    {
        if (CurrentLayout is null)
        {
            return;
        }

        var metrics = OverlayRenderMetrics.Create(
            CurrentLayout.Dpi,
            CurrentLayout.ScalePercent);
        var decorations = RenderDecorations;
        using var labelFont = CodexUi.Font((float)(decorations.ShowDragHint
            ? decorations.DragHintFontPoints : metrics.LabelFontPoints));
        using var compactValueFont = CodexUi.Font((float)metrics.CompactValueFontPoints);
        using var panelHeaderFont = CodexUi.Font((float)metrics.PanelHeaderFontPoints, semibold: true);
        using var highlightedValueFont = CodexUi.Font((float)metrics.HighlightedValueFontPoints, semibold: true);
        using var stripFont = CreateStripFont((float)metrics.CapsuleTextFontPoints);
        using var backgroundBrush = new SolidBrush(_palette.Background);
        using var borderPen = new Pen(_palette.Border, metrics.StrokeWidth);
        using var dividerPen = new Pen(_palette.Divider, metrics.StrokeWidth);
        using var progressTrackBrush = new SolidBrush(_palette.ProgressTrack);

        DrawCapsule(
            graphics,
            labelFont,
            stripFont,
            compactValueFont,
            backgroundBrush,
            dividerPen,
            metrics,
            decorations);
        if (!CurrentLayout.PanelBounds.IsEmpty)
        {
            DrawPanel(
                graphics,
                labelFont,
                compactValueFont,
                panelHeaderFont,
                highlightedValueFont,
                backgroundBrush,
                borderPen,
                dividerPen,
                progressTrackBrush,
                metrics,
                decorations);
        }

        _ = size;
    }

    private void DrawCapsule(
        Graphics graphics,
        Font labelFont,
        Font stripFont,
        Font valueFont,
        Brush backgroundBrush,
        Pen dividerPen,
        OverlayRenderMetrics metrics,
        OverlayRenderDecorationState decorations)
    {
        var layout = CurrentLayout!;
        var bounds = layout.CapsuleBounds.ToRectangle();
        var editing = decorations.ShowBorder;

        // Locked + transparent: nothing but glyphs. Editing: a barely-there wash and outline so the
        // drag target is obvious — still not the capsule. Opaque fallback: the original look.
        if (editing)
        {
            using var path = CreateRoundedRectanglePath(bounds, metrics.CapsuleRadius);
            using var editFill = new SolidBrush(_palette.EditFill);
            using var editOutline = new Pen(_palette.EditOutline, metrics.StrokeWidth);
            graphics.FillPath(editFill, path);
            graphics.DrawPath(editOutline, path);
        }
        else if (!_transparentBackground)
        {
            using var path = CreateRoundedRectanglePath(bounds, metrics.CapsuleRadius);
            graphics.FillPath(backgroundBrush, path);
        }
        else if (ClickToExpand)
        {
            // Layered windows pass alpha-zero pixels through. Keep the whole text strip clickable,
            // including spaces, without introducing a visible capsule or taking keyboard focus.
            // Very low alpha may be quantized away by the Windows hit-test surface. A theme-matched
            // 12.5% wash is visually neutral on the composer and keeps inter-word gaps clickable.
            using var hitSurface = new SolidBrush(Color.FromArgb(32, _palette.Background));
            graphics.FillRectangle(hitSurface, bounds);
        }

        // The shadow exists to lift glyphs off an unknown backdrop, so it follows transparency — and the
        // user's switch, because docked in the composer the strip is meant to read as part of Codex.
        var shadow = _transparentBackground && _textShadow;
        using var editPen = new Pen(_palette.EditOutline, metrics.StrokeWidth);

        if (decorations.ShowDragHint)
        {
            DrawAlignedText(
                graphics,
                decorations.DragHintText,
                labelFont,
                decorations.DragHintBounds.ToRectangle(),
                _palette.Hint,
                TextAlignment.Center,
                shadow);
            DrawEditHandle(graphics, editPen, metrics, decorations);
            return;
        }

        var padding = metrics.HorizontalPadding;
        var content = Rectangle.Inflate(bounds, -padding, 0);
        // The collapsed strip is one pre-composed line:
        //     ⚡ 243 tok/s · 5.7M tok · Cache 98%
        // All three primary metrics are visible without any click. It is drawn as a single string at
        // the composer's own type size (Segoe UI Regular, 13 DIP) so it reads as a native toolbar
        // label rather than as a HUD.
        //
        // It is drawn into the *whole* window, not into the padding-inset content rectangle the metric
        // layouts use: the window was sized from this exact string's measured advance plus
        // OverlayLayoutCalculator.CapsulePaddingXDip on each side, so the full rectangle is by
        // construction wider than the text. Insetting it further clipped the last characters — the
        // strip rendered "Cache 9…" on screen while the surface itself was correct.
        var capsuleText = CurrentCapsuleVariantText;
        if (!string.IsNullOrWhiteSpace(capsuleText))
        {
            var iconFontPoints = metrics.CapsuleIconFontPoints;
            using var iconFont = CreateStripFont((float)iconFontPoints);
            DrawStripLine(
                graphics,
                capsuleText,
                stripFont,
                iconFont,
                bounds,
                _palette.Value,
                shadow,
                metrics);
            DrawEditHandle(graphics, editPen, metrics, decorations);
            return;
        }

        if (layout.CollapsedDisplay == CollapsedDisplayMode.PrimaryOnly)
        {
            DrawCompactMetric(
                graphics,
                _presentation.Primary,
                content,
                labelFont,
                valueFont,
                metrics,
                shadow);
            DrawEditHandle(graphics, editPen, metrics, decorations);
            return;
        }

        var gap = metrics.MetricGap;
        var dividerX = content.Left + (content.Width / 2);
        var dividerHeight = Math.Min(metrics.DividerHeight, content.Height);
        var dividerTop = content.Top + ((content.Height - dividerHeight) / 2);
        DrawDivider(graphics, dividerPen, dividerX, dividerTop, dividerHeight, shadow);

        var primaryBounds = Rectangle.FromLTRB(content.Left, content.Top, dividerX - gap, content.Bottom);
        var secondaryBounds = Rectangle.FromLTRB(dividerX + gap, content.Top, content.Right, content.Bottom);
        DrawCompactMetric(
            graphics, _presentation.Primary, primaryBounds, labelFont, valueFont, metrics, shadow);
        DrawCompactMetric(
            graphics, _presentation.Secondary, secondaryBounds, labelFont, valueFont, metrics, shadow);
        DrawEditHandle(graphics, editPen, metrics, decorations);
    }

    private void DrawDivider(
        Graphics graphics,
        Pen pen,
        int x,
        int top,
        int height,
        bool shadow)
    {
        if (shadow)
        {
            using var shadowPen = new Pen(_palette.TextShadow, pen.Width);
            graphics.DrawLine(shadowPen, x, top + 1, x, top + height + 1);
        }

        graphics.DrawLine(pen, x, top, x, top + height);
    }

    private void DrawEditHandle(
        Graphics graphics,
        Pen pen,
        OverlayRenderMetrics metrics,
        OverlayRenderDecorationState decorations)
    {
        if (!decorations.ShowResizeHandle || EditResizeHandleBounds.IsEmpty)
        {
            return;
        }

        var handle = EditResizeHandleBounds;
        var inset = metrics.StrokeWidth;
        var middle = Math.Max(inset, handle.Width / 2);
        graphics.DrawLine(
            pen,
            handle.Right - middle,
            handle.Bottom - inset,
            handle.Right - inset,
            handle.Bottom - middle);
        graphics.DrawLine(
            pen,
            handle.Right - Math.Max(inset, handle.Width / 3),
            handle.Bottom - inset,
            handle.Right - inset,
            handle.Bottom - Math.Max(inset, handle.Height / 3));
    }

    private void DrawPanel(
        Graphics graphics, Font labelFont, Font valueFont, Font headerFont, Font highlightedValueFont,
        Brush backgroundBrush, Pen borderPen, Pen dividerPen, Brush progressTrackBrush,
        OverlayRenderMetrics metrics, OverlayRenderDecorationState decorations)
    {
        var layout=CurrentLayout!;
        using var panelLabelFont=CodexUi.UiFont(12.75f*layout.ScalePercent/100f);
        using var panelValueFont=CodexUi.UiFont(12.75f*layout.ScalePercent/100f);
        using var panelHeaderFont=CodexUi.UiFont(12.75f*layout.ScalePercent/100f,semibold:true);
        headerFont=panelHeaderFont;
        labelFont=panelLabelFont;valueFont=panelValueFont;
        var bounds=layout.PanelBounds.ToRectangle();
        var unit=layout.Dpi/96d*layout.ScalePercent/100d;
        int D(int n)=>(int)Math.Round(n*unit);
        var ios = Ios18Palette.FromOverlay(_palette);
        var panelRadius = metrics.PanelRadius;
        var inset = D(20);
        var cardRadius = Math.Max(D(12), panelRadius - inset);
        Ios18Palette.FillRoundedCard(graphics, bounds, panelRadius, ios.WindowSurface, ios.WindowBorder, Math.Max(1f, (float)unit));

        var left=bounds.Left+inset;var width=bounds.Width-inset*2;
        var contentLeft=left+D(16);var contentWidth=width-D(32);
        using var sectionFont=CodexUi.UiFont(9.75f*layout.ScalePercent/100f);
        // The settings glyph is centred on the centre of the panel's top-right corner arc, so it is
        // concentric with the corner: the same distance from the top edge, the right edge and the arc.
        // "Done" on the pushed pages is centred on the same point, so nothing shifts between pages.
        // The tiles start as far below the glyph as the glyph is from the top. The pushed pages centre
        // their title between a button on each side; the root has one button, so its title balances it
        // from the left on the 36 DIP text margin: a wordmark in a comic display face, as tall as the
        // glyph, on its centre line.
        var navCenter=bounds.Top+D(OverlayLayoutCalculator.NavCenterYDip);
        var rootTitle=CodexUi.T(RootTitleChinese,RootTitleEnglish);
        var titleBounds=new Rectangle(contentLeft,navCenter-D(22),contentWidth-D(SettingsGlyphDip+12),D(44));
        if(!AppleText.DrawDisplay(graphics,rootTitle,RootTitleFontFile,(float)(RootTitlePixels*unit),titleBounds,ios.TextPrimary))
        {
            using var navTitle=CodexUi.UiBoldFont(12.75f*layout.ScalePercent/100f);
            DrawAlignedText(graphics,rootTitle,navTitle,titleBounds,ios.TextPrimary,TextAlignment.Near,false);
        }
        DrawSettingsGlyph(graphics,SettingsButtonBounds,unit);

        void Section(string text,int y,bool caps=true)=>DrawAlignedText(graphics,CodexUi.English&&caps?text.ToUpperInvariant():text,sectionFont,
            new Rectangle(contentLeft,y,contentWidth,D(18)),ios.TextSecondary,TextAlignment.Near,false);

        var tilesTop=bounds.Top+D(TilesTopDip);
        var speedCard=new Rectangle(left,tilesTop,width,D(TilesHeightDip));
        DrawSpeedTiles(graphics,speedCard,unit,ios,sectionFont,cardRadius);
        // The conversation the figures belong to has a cell of its own: a full-width row closing the
        // tile grid, one 12 DIP gutter below it, laid out like every other row (label left, value right).
        var conversationCell=new Rectangle(left,speedCard.Bottom+D(12),width,D(ConversationCellDip));
        Ios18Palette.FillRoundedCard(graphics,conversationCell,cardRadius,ios.CardSurface,Color.Transparent,0f);
        var conversationLabel=CodexUi.T("本对话","This chat");
        var conversationLabelWidth=MeasureWidth(graphics,conversationLabel,labelFont)+D(16);
        DrawAlignedText(graphics,conversationLabel,labelFont,new Rectangle(contentLeft,conversationCell.Top,conversationLabelWidth,conversationCell.Height),
            ios.TextPrimary,TextAlignment.Near,false);
        DrawAlignedText(graphics,string.IsNullOrWhiteSpace(_presentation.ConversationTitle) ? "—" : _presentation.ConversationTitle,valueFont,
            new Rectangle(contentLeft+conversationLabelWidth,conversationCell.Top,contentWidth-conversationLabelWidth,conversationCell.Height),
            ios.TextSecondary,TextAlignment.Far,false);

        int Group(OverlayMetric r)=>r.Field switch
        {
            DisplayField.Context or DisplayField.ContextPercent=>1,
            DisplayField.TurnDuration or DisplayField.ModelTime or DisplayField.FirstTokenTime=>2,
            _=>0
        };
        // Input's two subsets (cached, uncached) follow input directly; output comes after them.
        static int Rank(DisplayField field)=>field switch
        {
            DisplayField.Total=>0, DisplayField.Input=>1, DisplayField.CacheHit=>2, DisplayField.CacheMiss=>3,
            DisplayField.Output=>4, DisplayField.Reasoning=>5, DisplayField.CacheHitRate=>6, _=>7
        };
        var rows=_presentation.PanelRows.OrderBy(Group).ThenBy(r=>Rank(r.Field)).ToList();
        string GroupTitle(int group)=>group switch
        {
            1=>CodexUi.T("上下文","Context"),
            2=>CodexUi.T("耗时","Timing"),
            _=>CodexUi.T("Token 用量","Tokens")
        };
        var rowHeight=layout.ExpandedRowHeight;
        var top=conversationCell.Bottom+D(24);
        // Hairlines are filled, not stroked: a 1 px pen on this surface straddles two pixel rows and
        // renders as a faint 2 px smear, and a line to card.Right overshoots the card by one pixel.
        using var separator=new SolidBrush(ios.Separator);
        const int hairline=1;
        var start=0;
        while(start<rows.Count)
        {
            var end=start;
            while(end+1<rows.Count && Group(rows[end+1])==Group(rows[start]))end++;
            var count=end-start+1;
            // Header ink ends 9 DIP above its card, as on the settings page.
            Section(GroupTitle(Group(rows[start])),top-D(2));
            var card=new Rectangle(left,top+D(22),width,count*rowHeight);
            Ios18Palette.FillRoundedCard(graphics, card, cardRadius, ios.CardSurface, Color.Transparent, 0f);
            for(var i=start;i<=end;i++)
            {
                var rowTop=card.Top+(i-start)*rowHeight;
                if(i>start)graphics.FillRectangle(separator,card.Left+D(16),rowTop,card.Width-D(16),hairline);
                DrawExpandedRow(graphics,rows[i],new Rectangle(card.Left+D(16),rowTop,card.Width-D(32),rowHeight),labelFont,valueFont);
            }
            top=card.Bottom+D(24);
            start=end+1;
        }
    }

    /// <summary>iOS 18 summary tiles (as in Weather and Fitness): four equal cards in a two-column
    /// grid, each a caption above one figure, so no metric dominates the layout. Captions share one
    /// baseline per row and figures another; speeds use grouping and one decimal. As the front page's
    /// headline these are 60 DIP tall with 28 pt figures; the statistics page's are 68 with 24.</summary>
    private void DrawSpeedTiles(Graphics g,Rectangle area,double unit,Ios18Palette ios,Font captionFont,float radius)
    {
        float P(float n)=>(float)(n*unit);
        var scale=CurrentLayout!.ScalePercent;
        static string Speed(string? text)
        {
            if(string.IsNullOrWhiteSpace(text))return "—";
            var raw=text.EndsWith(" tok/s",StringComparison.Ordinal)?text[..^6]:text;
            return double.TryParse(raw,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)
                ? value.ToString("N1",CultureInfo.InvariantCulture) : raw;
        }
        using var valueFont=CodexUi.UiFont(21f*scale/100f,semibold:true);
        using var unitFont=CodexUi.UiFont(11.25f*scale/100f);
        void At(string text,Font font,float left,float width,float baseline,Color color)
        {
            var top=baseline-AppleText.BaselineOffset(g,font);
            AppleText.Draw(g,text,font,new RectangleF(left,top,width,P(60)),color,StringAlignment.Near,StringAlignment.Near);
        }
        var primary=_presentation.Primary;
        var isSpeed=primary.Field is DisplayField.AverageSpeed or DisplayField.Tps;
        var peak=_presentation.ExpandedRows.FirstOrDefault(r=>r.Field==DisplayField.PeakSpeed)?.Value;
        var coverage=_presentation.TimingCoverage?.Replace("/"," / ",StringComparison.Ordinal);
        var source=_presentation.TimingSourceText is { Length: > 0 } text ? " · "+text : "";
        var tiles=new[]
        {
            (isSpeed?CodexUi.T("平均速度","Average speed"):primary.ExpandedLabel,isSpeed?Speed(primary.Value):primary.Value,isSpeed?"tok/s":""),
            (CodexUi.T("最近一次","Last response"),Speed(_presentation.LastResponseText),"tok/s"),
            (CodexUi.T("峰值","Peak"),Speed(peak),"tok/s"),
            (CodexUi.T("已计时","Timed")+source,coverage??"—",CodexUi.T("次","")),
        };
        var gap=P(12);
        var tileWidth=(area.Width-gap)/2f;
        var tileHeight=(area.Height-gap)/2f;
        for(var index=0;index<tiles.Length;index++)
        {
            var tile=Rectangle.Round(new RectangleF(area.Left+(index%2)*(tileWidth+gap),area.Top+(index/2)*(tileHeight+gap),tileWidth,tileHeight));
            Ios18Palette.FillRoundedCard(g,tile,radius,ios.CardSurface,Color.Transparent,0f);
            var left=tile.Left+P(16);
            var width=tile.Width-P(24);
            At(tiles[index].Item1,captionFont,left,width,tile.Top+P(21),ios.TextSecondary);
            var value=tiles[index].Item2;
            At(value,valueFont,left,width,tile.Top+P(48),ios.TextPrimary);
            if(tiles[index].Item3.Length==0||value=="—")continue;
            var valueWidth=MeasureWidth(g,value,valueFont);
            At(tiles[index].Item3,unitFont,left+valueWidth+P(4),Math.Max(0,width-valueWidth-P(4)),tile.Top+P(48),ios.TextSecondary);
        }
    }

    /// <summary>Margin between the panel edge and every white shape (cards, tiles).</summary>
    internal const int PanelMarginDip = 20;
    /// <summary>Corner radius of the panel; <see cref="OverlayRenderMetrics.PanelRadius"/> scales it.</summary>
    internal const int PanelCornerRadiusDip = 40;
    /// <summary>Settings symbol: 26 DIP of ink and no backing shape (the user rejected a circle behind
    /// it, and a caption under it; 22 and 24 were too small, 28 too large), centred on the top-right
    /// corner's centre.</summary>
    internal const int SettingsGlyphDip = 26;
    /// <summary>The tiles start as far below the settings glyph as the glyph is from the top and right
    /// edges, which puts them one corner diameter from the top whatever the glyph's size.</summary>
    internal const int TilesTopDip = PanelCornerRadiusDip * 2;
    internal const int TilesHeightDip = 132;
    /// <summary>Height of the conversation's own cell, one standard row.</summary>
    internal const int ConversationCellDip = 50;
    /// <summary>Text margin shared by every label on the left and by "Done" and row values on the right.</summary>
    internal const int TextMarginDip = 36;
    /// <summary>Front-page title, named by the user, set in the comic-style ZCOOL KuaiLe (SIL OFL 1.1)
    /// at a size whose ink is as tall as the settings glyph.</summary>
    internal const string RootTitleChinese = "控制面板";
    internal const string RootTitleEnglish = "Control Panel";
    internal const string RootTitleFontFile = "ZCOOLKuaiLe-Regular.ttf";
    private const float RootTitlePixels = 31f;
    internal Rectangle SettingsButtonBounds=>CurrentLayout is {} layout?GetSettingsButtonBounds(layout):Rectangle.Empty;
    private static Rectangle GetSettingsButtonBounds(OverlayLayoutResult layout)
    {
        if(layout.PanelBounds.IsEmpty)return Rectangle.Empty;
        var unit=layout.Dpi/96d*layout.ScalePercent/100d;
        // 44 DIP touch target centred on the centre of the panel's top-right corner arc.
        var side=(int)Math.Round(44*unit);
        var radius=OverlayRenderMetrics.Create(layout.Dpi,layout.ScalePercent).PanelRadius;
        var panel=layout.PanelBounds;
        return new Rectangle(panel.Right-radius-side/2,panel.Top+radius-side/2,side,side);
    }

    private void DrawSettingsGlyph(Graphics graphics,Rectangle bounds,double unit)
    {
        if(bounds.IsEmpty)return;
        var ios = Ios18Palette.FromOverlay(_palette);
        // Same pointer feedback as the navigation text buttons on the pushed pages.
        var iconColor = _settingsPressed ? Color.FromArgb(110, ios.Accent) : _settingsHovered ? Color.FromArgb(185, ios.Accent) : ios.Accent;
        // Rasterised at device size and copied without resampling, so the stroke stays crisp.
        var size=(int)Math.Round(SettingsGlyphDip*unit);
        if((bounds.Width-size)%2!=0)size++;
        if(_settingsIcon is null || _settingsIcon.Width!=size || _settingsIconColor!=iconColor)
        {
            _settingsIcon?.Dispose();
            _settingsIconColor = iconColor;
            _settingsIcon = Ios18Palette.CreateGearshapeIcon(size, iconColor);
        }
        Ios18Palette.DrawExact(graphics,_settingsIcon,new RectangleF(bounds.Left+(bounds.Width-size)/2,bounds.Top+(bounds.Height-size)/2,size,size));
    }

    private void DrawCompactMetric(
        Graphics graphics,
        OverlayMetric metric,
        Rectangle bounds,
        Font labelFont,
        Font valueFont,
        OverlayRenderMetrics metrics,
        bool shadow)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var labelWidth = MeasureWidth(graphics, metric.CompactLabel, labelFont);
        var gap = metrics.CompactMetricGap;
        labelWidth = Math.Min(labelWidth, bounds.Width);
        var labelBounds = new Rectangle(bounds.Left, bounds.Top, labelWidth, bounds.Height);
        var valueBounds = Rectangle.FromLTRB(
            Math.Min(bounds.Right, labelBounds.Right + gap),
            bounds.Top,
            bounds.Right,
            bounds.Bottom);
        DrawAlignedText(
            graphics, metric.CompactLabel, labelFont, labelBounds, _palette.Label,
            TextAlignment.Near, shadow, metrics);
        DrawAlignedText(
            graphics, metric.Value, valueFont, valueBounds, ValueColorFor(metric),
            TextAlignment.Far, shadow, metrics);
    }

    private void DrawHighlightedMetric(
        Graphics graphics,
        OverlayMetric metric,
        Rectangle bounds,
        Font labelFont,
        Font valueFont)
    {
        var labelHeight = Math.Max(1, bounds.Height / 2);
        DrawAlignedText(
            graphics,
            metric.ExpandedLabel,
            labelFont,
            new Rectangle(bounds.Left, bounds.Top, bounds.Width, labelHeight),
            _palette.Label,
            TextAlignment.Near,
            shadow: false);
        DrawAlignedText(
            graphics,
            metric.Value,
            valueFont,
            new Rectangle(bounds.Left, bounds.Top + labelHeight, bounds.Width, bounds.Height - labelHeight),
            ValueColorFor(metric),
            TextAlignment.Near,
            shadow: false);
    }

    private void DrawExpandedRow(
        Graphics graphics,
        OverlayMetric metric,
        Rectangle bounds,
        Font labelFont,
        Font valueFont)
    {
        // Reserve the actual value width, not an arbitrary half-column. Long Chinese context labels
        // then receive the remaining room and can wrap instead of disappearing behind the value.
        var gap = (int)Math.Ceiling(12 * graphics.DpiX / 96d);
        var valueWidth = MeasureWidth(graphics,metric.Value,valueFont)+gap;
        var labelWidth = Math.Max(0,bounds.Width-valueWidth-gap);
        var localized = labelFont;
        var ios = Ios18Palette.FromOverlay(_palette);
        if (MeasureWidth(graphics,metric.ExpandedLabel,localized) <= labelWidth)
            DrawAlignedText(graphics,metric.ExpandedLabel,localized,new Rectangle(bounds.Left,bounds.Top,labelWidth,bounds.Height),
                ios.TextPrimary,TextAlignment.Near,false);
        else
        {
            using var smaller=CodexUi.FontForText(metric.ExpandedLabel,Math.Max(9.5f,labelFont.Size-.5f));
            using var brush=new SolidBrush(ios.TextPrimary);
            using var wrap=new StringFormat{LineAlignment=StringAlignment.Center,Trimming=StringTrimming.None};
            graphics.DrawString(metric.ExpandedLabel,smaller,brush,new Rectangle(bounds.Left,bounds.Top,labelWidth,bounds.Height),wrap);
        }
        DrawAlignedText(
            graphics,
            metric.Value,
            valueFont,
            new Rectangle(bounds.Right-valueWidth,bounds.Top,valueWidth,bounds.Height),
            ios.TextSecondary,
            TextAlignment.Far,
            shadow: false);
    }

    private Color ValueColorFor(OverlayMetric metric) =>
        metric.Field is DisplayField.Context or DisplayField.ContextPercent
            ? _palette.Accent
            : _palette.Value;

    private enum TextAlignment
    {
        Near,
        Center,
        Far
    }

    /// <summary>
    /// GDI+ text, not <c>TextRenderer</c>. GDI's ClearType filter blends each sub-pixel channel
    /// independently against whatever is behind the glyph, and on a transparent surface "behind" is
    /// nothing — the result is a coloured fringe. Grayscale anti-aliasing through GDI+ carries a single
    /// coverage value per pixel, which is exactly what an alpha surface can represent.
    /// </summary>
    private static void DrawAlignedText(
        Graphics graphics,
        string text,
        Font font,
        Rectangle bounds,
        Color color,
        TextAlignment alignment,
        bool shadow,
        OverlayRenderMetrics? metrics = null)
    {
        using var format = CreateFormat(alignment);
        using var localizedFont = !IsInterFont(font) && text.Any(c => c is >= '\u2e80' and <= '\u9fff')
            ? CodexUi.FontForText(text, font.Size, CodexUi.IsEmphasized(font)) : null;
        font = localizedFont ?? font;

        if (shadow)
        {
            var offset = metrics?.TextShadowOffset ?? 1;
            // Two passes: one at 1 DIP, one fainter at 2 DIP. Still a drop shadow, not an outline —
            // but a soft edge holds up on a busy backdrop where a single hard pixel does not.
            using var shadowBrush = new SolidBrush(OverlayThemePalette.ShadowFor(color));
            var shadowBounds = new Rectangle(bounds.X, bounds.Y + offset, bounds.Width, bounds.Height);
            if (!AppleText.Draw(graphics, text, font, shadowBounds, shadowBrush.Color, format.Alignment, format.LineAlignment))
                graphics.DrawString(text, font, shadowBrush, shadowBounds, format);
            using var softBrush = new SolidBrush(OverlayThemePalette.ShadowFor(color, 0.45d));
            var softBounds = new Rectangle(bounds.X, bounds.Y + (offset * 2), bounds.Width, bounds.Height);
            if (!AppleText.Draw(graphics, text, font, softBounds, softBrush.Color, format.Alignment, format.LineAlignment))
                graphics.DrawString(text, font, softBrush, softBounds, format);
        }

        using var brush = new SolidBrush(color);
        if(AppleText.Draw(graphics,text,font,bounds,color,format.Alignment,format.LineAlignment))return;
        graphics.DrawString(text, font, brush, bounds, format);
    }

    /// <summary>
    /// Draws the strip line, giving the leading lightning glyph its own smaller font.
    ///
    /// <para>U+26A1 is not in Segoe UI, so GDI+ falls back to a symbol family whose glyph is drawn
    /// near the full em box — at the text size it stood noticeably taller than the digits beside it.
    /// Drawing it at a smaller size is the whole fix; the two runs share one baseline so the digits
    /// do not move. The baseline correction is the arithmetic difference between the two fonts'
    /// ascent/descent midpoints, because <c>LineAlignment.Center</c> centres whichever line box it
    /// is given and a smaller box therefore lands its baseline slightly higher.</para>
    /// </summary>
    private void DrawStripLine(
        Graphics graphics,
        string text,
        Font textFont,
        Font iconFont,
        Rectangle bounds,
        Color color,
        bool shadow,
        OverlayRenderMetrics metrics)
    {
        var (icon, body) = SplitLeadingIcon(text);
        if (icon.Length == 0)
        {
            // Match the Harness composer: quiet labels, 14 DIP outline icons, 6 DIP
            // gaps and stable numeric slots. Activity is a peer, never a speed prefix.
            var scale = (float)(EmPixels(textFont, graphics) / 12d);
            var gap = 7f * scale;
            var pieces = text.Split(" · ", StringSplitOptions.None);
            var widths = pieces.Select(p => StripPartWidth(graphics, p, textFont)).ToArray();
            float x = bounds.Left + (bounds.Width - (float)widths.Sum() - gap * (pieces.Length - 1)) / 2f;
            for (var i = 0; i < pieces.Length; i++)
            {
                var part = pieces[i];
                var hasIcon = part.EndsWith("tok/s", StringComparison.Ordinal) || part.EndsWith("t/s", StringComparison.Ordinal)
                    || part.StartsWith("Tokens ", StringComparison.Ordinal);
                var inset = hasIcon ? 20f * scale : 0;
                if (hasIcon) DrawMetricIcon(graphics, part.StartsWith("Tokens ", StringComparison.Ordinal),
                    x, bounds.Top + (bounds.Height - 14f * scale) / 2, scale, _palette.Label);
                DrawAlignedText(graphics, part, textFont,
                    new Rectangle((int)Math.Round(x + inset), bounds.Top, (int)Math.Ceiling(widths[i] - inset), bounds.Height),
                    _palette.Label, TextAlignment.Near, shadow, metrics);
                x += (float)widths[i] + gap;
            }
            return;
        }

        using var format = CreateFormat(TextAlignment.Near);
        var iconAdvance = graphics.MeasureString(icon, iconFont, new SizeF(512f, 512f), format).Width;
        var spaceAdvance = MeasureSpaceAdvance(graphics, textFont);
        var bodyAdvance = graphics.MeasureString(body, textFont, new SizeF(8192f, 512f), format).Width;
        var total = iconAdvance + spaceAdvance + bodyAdvance;
        var left = bounds.Left + (int)Math.Round((bounds.Width - total) / 2d, MidpointRounding.AwayFromZero);

        var baselineShift = (
            AscentPixels(textFont, graphics) - AscentPixels(iconFont, graphics)
            - DescentPixels(textFont, graphics) + DescentPixels(iconFont, graphics)) / 2d;

        var iconWidth = (int)Math.Ceiling(iconAdvance);
        DrawAlignedText(
            graphics,
            icon,
            iconFont,
            new Rectangle(
                left,
                bounds.Top + (int)Math.Round(baselineShift, MidpointRounding.AwayFromZero),
                iconWidth,
                bounds.Height),
            color,
            TextAlignment.Near,
            shadow,
            metrics);

        var bodyLeft = left + iconWidth + (int)Math.Round(spaceAdvance, MidpointRounding.AwayFromZero);
        DrawAlignedText(
            graphics,
            body,
            textFont,
            new Rectangle(bodyLeft, bounds.Top, Math.Max(0, bounds.Right - bodyLeft), bounds.Height),
            color,
            TextAlignment.Near,
            shadow,
            metrics);
    }

    /// <summary>The strip's leading glyph, split from the numbers so it can be drawn at its own size.</summary>
    internal static (string Icon, string Body) SplitLeadingIcon(string text)
    {
        if (text.Length > 1 && text[0] == '\u26A1')
        {
            var body = text[1] == ' ' ? text[2..] : text[1..];
            return ("\u26A1", body);
        }

        return (string.Empty, text);
    }

    /// <summary>The width the strip line will occupy, in device pixels, using the same split layout.</summary>
    private static double MeasureStripLine(Graphics graphics, string text, Font textFont, Font iconFont)
    {
        var (icon, body) = SplitLeadingIcon(text);
        using var localized = body.Any(c => c is >= '\u2e80' and <= '\u9fff') ? CodexUi.FontForText(body,textFont.Size,CodexUi.IsEmphasized(textFont)) : null;
        textFont = localized ?? textFont;
        using var format = CreateFormat(TextAlignment.Near);
        if (icon.Length == 0)
        {
            var parts = text.Split(" · ", StringSplitOptions.None);
            return parts.Sum(p => StripPartWidth(graphics, p, textFont))
                + Math.Max(0, parts.Length - 1) * EmPixels(textFont, graphics) * 7d / 12d;
        }

        return graphics.MeasureString(icon, iconFont, new SizeF(512f, 512f), format).Width
            + MeasureSpaceAdvance(graphics, textFont)
            + graphics.MeasureString(body, textFont, new SizeF(8192f, 512f), format).Width;
    }

    private static double StripPartWidth(Graphics graphics, string part, Font font)
    {
        using var format = CreateFormat(TextAlignment.Near);
        // DrawAlignedText swaps in the CJK face for CJK text; measure with that same face or the
        // slot comes out narrower than the glyphs and the text is trimmed.
        using var localized = part.Any(c => c is >= '⺀' and <= '鿿')
            ? CodexUi.FontForText(part, font.Size, CodexUi.IsEmphasized(font)) : null;
        var measured = localized ?? font;
        double Width(string value) => Math.Ceiling(graphics.MeasureString(value, measured, new SizeF(8192, 512), format).Width) + (localized is null ? 0 : 1);
        var speed = part.EndsWith("tok/s", StringComparison.Ordinal) || part.EndsWith("t/s", StringComparison.Ordinal);
        var tokens = part.StartsWith("Tokens ", StringComparison.Ordinal);
        // Use actual content widths so each visible group has the same gap.
        // Reserving different hidden padding per field made short states look uneven.
        return Width(part) + (speed || tokens ? 20d * EmPixels(font, graphics) / 12d : 0);
    }

    private static void DrawMetricIcon(Graphics graphics, bool database, float x, float y, float scale, Color color)
    {
        using var pen = new Pen(color, Math.Max(1f, 1.2f * scale));
        if (database)
        {
            graphics.DrawEllipse(pen, x + 2 * scale, y + 2 * scale, 10 * scale, 3 * scale);
            graphics.DrawLine(pen, x + 2 * scale, y + 3.5f * scale, x + 2 * scale, y + 11 * scale);
            graphics.DrawLine(pen, x + 12 * scale, y + 3.5f * scale, x + 12 * scale, y + 11 * scale);
            graphics.DrawArc(pen, x + 2 * scale, y + 6 * scale, 10 * scale, 3 * scale, 0, 180);
            graphics.DrawArc(pen, x + 2 * scale, y + 9 * scale, 10 * scale, 3 * scale, 0, 180);
        }
        else
        {
            graphics.DrawArc(pen, x + scale, y + 2 * scale, 12 * scale, 12 * scale, 150, 240);
            graphics.DrawLine(pen, x + 7 * scale, y + 8 * scale, x + 10 * scale, y + 4 * scale);
        }
    }

    /// <summary>
    /// The advance of one space, in device pixels.
    ///
    /// <para><c>StringFormat.GenericTypographic</c> — which the strip uses everywhere else so that no
    /// extra padding is added around glyphs — deliberately reports a whitespace-only string as zero
    /// wide. Measuring the gap between the lightning glyph and the numbers with it therefore produced
    /// no gap at all and rendered "⚡113". <c>MeasureTrailingSpaces</c> is the flag that turns the
    /// whitespace back on, and it only affects this one measurement.</para>
    /// </summary>
    private static double MeasureSpaceAdvance(Graphics graphics, Font font)
    {
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces
        };
        return graphics.MeasureString(" ", font, new SizeF(512f, 512f), format).Width;
    }

    private static double EmPixels(Font font, Graphics graphics) =>
        font.SizeInPoints * graphics.DpiY / 72d;

    private static double AscentPixels(Font font, Graphics graphics) =>
        font.FontFamily.GetCellAscent(font.Style) / (double)font.FontFamily.GetEmHeight(font.Style)
        * EmPixels(font, graphics);

    private static double DescentPixels(Font font, Graphics graphics) =>
        font.FontFamily.GetCellDescent(font.Style) / (double)font.FontFamily.GetEmHeight(font.Style)
        * EmPixels(font, graphics);

    private static StringFormat CreateFormat(TextAlignment alignment)
    {
        var format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
            HotkeyPrefix = HotkeyPrefix.None,
            LineAlignment = StringAlignment.Center
        };
        format.Alignment = alignment switch
        {
            TextAlignment.Center => StringAlignment.Center,
            TextAlignment.Far => StringAlignment.Far,
            _ => StringAlignment.Near
        };
        return format;
    }

    private static int MeasureWidth(Graphics graphics, string text, Font font)
    {
        using var localized = !IsInterFont(font) && text.Any(c => c is >= '\u2e80' and <= '\u9fff') ? CodexUi.FontForText(text,font.Size,CodexUi.IsEmphasized(font)) : null;
        font = localized ?? font;
        var nativeWidth=AppleText.Measure(graphics,text,font);if(nativeWidth.HasValue)return (int)Math.Ceiling(nativeWidth.Value);
        using var format = CreateFormat(TextAlignment.Near);
        return (int)Math.Ceiling(
            graphics.MeasureString(text, font, new SizeF(4096f, 4096f), format).Width);
    }

    private static bool IsInterFont(Font font) => font.FontFamily.Name.StartsWith("Inter", StringComparison.OrdinalIgnoreCase);

    private static GraphicsPath CreateRoundedRectanglePath(Rectangle rectangle, int radius)
    {
        var path = new GraphicsPath();
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            return path;
        }

        var clampedRadius = Math.Clamp(radius, 0, Math.Min(rectangle.Width, rectangle.Height) / 2);
        if (clampedRadius == 0)
        {
            path.AddRectangle(rectangle);
            return path;
        }

        var diameter = clampedRadius * 2;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _liquidTimer.Stop(); _liquidTimer.Dispose(); _liquid.Dispose();
            _settingsIcon?.Dispose();
            CancelEditGesture();
            Region?.Dispose();
            Region = null;
            _inputRegion?.Dispose(); _inputRegion = null;
        }
        base.Dispose(disposing);
    }

    private const int GwlExStyle = -20;
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();

    // SetWindowPos flags used by the position fast path. The combination is deliberate: see
    // MoveWindowTo. Not one of them changes Z-order, activation, focus or ownership.
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpNoSendChanging = 0x0400;
    private const uint SwpNoRedraw = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int CX;
        public int CY;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(
        IntPtr windowHandle,
        IntPtr destinationDc,
        ref NativePoint destination,
        ref NativeSize size,
        IntPtr sourceDc,
        ref NativePoint source,
        int colorKey,
        ref BlendFunction blend,
        int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);
}
