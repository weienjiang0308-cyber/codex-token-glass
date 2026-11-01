using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using LiquidGlass.Skia;
using SkiaSharp;

namespace CodexStatusbar;

/// <summary>LiquidGlass 1.0 shader and spring adapter. Backdrop pixels stay in memory on this machine.</summary>
internal sealed class LiquidBackdropRenderer : IDisposable
{
    private GlassPainter? _painter;
    private SKImage? _source;
    private CalmInteraction? _interaction;
    private CalmInteraction? _buttonInteraction;
    private Rectangle _button;
    private bool _buttonHovered;
    private sealed class ControlSurface
    {
        public Rectangle Bounds;
        public float Radius;
        public bool Hovered,Pressed,Prominent;
        public CalmInteraction Motion = null!;
    }
    private readonly Dictionary<int,ControlSurface> _controls=new();
    private readonly Dictionary<int,(Rectangle Bounds,float Fraction,Color Track,Color Fill)> _tracks=new();
    private readonly List<(Rectangle Bounds,float Radius,Color Color)> _matte=new();
    private GlassGpuContext? _gpu;
    private SKSurface? _surface;
    private readonly Func<double> _clock;
    private double _opened;
    private double _animationUntil;
    private Rectangle _panel;
    private float _density = 1;
    private bool _motionEnabled;
    public bool Ready => _source is not null && _painter is not null;
    public bool NeedsFrame { get; private set; }
    public string? LastError { get; private set; }
    public double LastFrameMs { get; private set; }
    public bool GpuAccelerated => _gpu is not null;
    internal int? TransparencyOverride {get;set;}
    private int Transparency=>TransparencyOverride??CodexUi.GlassTransparency;
    private double Now => _clock();

    public LiquidBackdropRenderer(Func<double>? clock = null) => _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);

    public bool Capture(Rectangle screen, Rectangle panel, uint dpi)
    {
        try
        {
            using var bitmap = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(screen.Location, Point.Empty, screen.Size, CopyPixelOperation.SourceCopy);
            SetBackdrop(bitmap, panel, dpi);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        { LastError = e.Message; Clear(); return false; }
    }

    internal void SetBackdrop(Bitmap bitmap, Rectangle panel, uint dpi, bool? motionOverride = null)
    {
        _painter ??= new GlassPainter();
        _gpu ??= GlassGpuContext.TryCreate();
        using var raster = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var bits = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var row = new byte[bitmap.Width * 4];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(bits.Scan0 + y * bits.Stride, row, 0, row.Length);
                for (var i = 3; i < row.Length; i += 4) row[i] = 255;
                Marshal.Copy(row, 0, raster.GetPixels() + y * raster.RowBytes, row.Length);
            }
        }
        finally { bitmap.UnlockBits(bits); }
        var image = SKImage.FromBitmap(raster);
        _painter.SetSource(image);
        _source?.Dispose(); _source = image;
        _panel = panel; _density = dpi / 96f;
        _motionEnabled = motionOverride ?? MotionEnabled;
        _interaction = new CalmInteraction(new CalmGeometry(panel.Width / _density, panel.Height / _density, 32,
            panel.X / _density, panel.Y / _density, bitmap.Width / _density, bitmap.Height / _density),
            new CalmOptions(ReducedMotion: !_motionEnabled));
        _opened = Now; _animationUntil = _opened + .35;
        NeedsFrame = _motionEnabled; LastError = null;
    }

    public void Press(float dx = 0, float dy = 0)
    {
        _interaction?.Press(Now, dx / _density, dy / _density);
        _animationUntil = Now + 1.2; NeedsFrame = Ready && _motionEnabled;
    }
    public void Release()
    {
        _interaction?.Release(Now);
        _animationUntil = Now + 1.2; NeedsFrame = Ready && _motionEnabled;
    }

    public void SetButtonBounds(Rectangle bounds, Size viewport)
    {
        _button = bounds;
        _buttonInteraction = bounds.IsEmpty ? null : new CalmInteraction(
            new CalmGeometry(bounds.Width/_density,bounds.Height/_density,bounds.Height/_density/2,
                bounds.X/_density,bounds.Y/_density,viewport.Width/_density,viewport.Height/_density),
            new CalmOptions(PressScale:1.09,PressGrowth:1,ReducedMotion:!_motionEnabled));
    }

    public void ButtonState(bool hovered, bool pressed)
    {
        _buttonHovered = hovered;
        if (pressed) _buttonInteraction?.Press(Now); else _buttonInteraction?.Release(Now);
        _animationUntil = Now + 1.2; NeedsFrame = Ready && _motionEnabled;
    }

    public void Control(int key,Rectangle bounds,float radius,bool hovered,bool pressed,bool prominent=false)
    {
        if(!Ready || bounds.Width<=0 || bounds.Height<=0)return;
        var create=!_controls.TryGetValue(key,out var state) || state.Bounds.Size!=bounds.Size;
        if(create)
        {
            state=new ControlSurface{Motion=new CalmInteraction(new CalmGeometry(bounds.Width/_density,bounds.Height/_density,
                Math.Min(radius,Math.Min(bounds.Width,bounds.Height)/2f)/_density,bounds.X/_density,bounds.Y/_density,
                _source!.Width/_density,_source.Height/_density),new CalmOptions(PressScale:1.07,PressGrowth:1,ReducedMotion:!_motionEnabled))};
            _controls[key]=state;
        }
        var current=state??throw new InvalidOperationException("Missing optical control state");
        if(create || current.Pressed!=pressed || current.Hovered!=hovered)
        {
            if(pressed)current.Motion.Press(Now);else current.Motion.Release(Now);
            _animationUntil=Now+1.2;NeedsFrame=_motionEnabled;
        }
        current.Bounds=bounds;current.Radius=radius;current.Hovered=hovered;current.Pressed=pressed;current.Prominent=prominent;
    }
    public void Track(int key,Rectangle bounds,float fraction,Color track,Color fill)=>
        _tracks[key]=(bounds,Math.Clamp(fraction,0,1),track,fill);
    public void ReadingSurfaces(params (Rectangle Bounds,float Radius,Color Color)[] surfaces)
    { _matte.Clear();_matte.AddRange(surfaces); }

    public bool RenderInto(Bitmap target, bool dark)
    {
        if (!Ready) return false;
        var clock = Stopwatch.StartNew();
        BitmapData? bits = null;
        try
        {
            bits = target.LockBits(new Rectangle(Point.Empty, target.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            var info = new SKImageInfo(target.Width,target.Height,SKColorType.Bgra8888,SKAlphaType.Premul);
            _gpu?.MakeCurrent();
            if (_gpu is not null && (_surface is null || _surface.Canvas.DeviceClipBounds.Width!=target.Width || _surface.Canvas.DeviceClipBounds.Height!=target.Height))
            { _surface?.Dispose(); _surface = SKSurface.Create(_gpu.Context,true,info); }
            using var cpu = _gpu is null ? SKSurface.Create(info,bits.Scan0,bits.Stride) : null;
            var surface = _surface ?? cpu;
            if (surface is null) return false;
            surface.Canvas.Clear(SKColors.Transparent);
            var now = Now;
            var progress = _motionEnabled ? Math.Clamp((now - _opened) / .24, 0, 1) : 1;
            var materialize = (float)(1 - Math.Pow(1 - progress, 3));
            var feedback = _interaction?.Sample(now) ?? GlassFeedback.Identity;
            _painter!.DrawSurface(surface.Canvas, SKRect.Create(_panel.X, _panel.Y, _panel.Width, _panel.Height),
                radius: 32 * _density,
                options: new GlassSurfaceOptions(Dark: dark, TintAmount: CodexUi.GlassTint(Transparency), Density: _density, Materialize: materialize,
                    ReducedTransparency: DesktopMaterial.TransparencyEnabled ? 0 : 1,
                    IncreasedContrast: SystemInformation.HighContrast ? 1 : 0), feedback: feedback);
            var buttonFeedback = _buttonInteraction?.Sample(now) ?? GlassFeedback.Identity;
            if (!_button.IsEmpty)
                _painter.DrawSurface(surface.Canvas,SKRect.Create(_button.X,_button.Y,_button.Width,_button.Height),
                    radius:_button.Height/2f,
                    options:new GlassSurfaceOptions(Dark:dark,TintAmount:_buttonHovered ? 6 : 18,Density:_density,
                        Materialize:materialize,ReducedTransparency:DesktopMaterial.TransparencyEnabled ? 0 : 1,
                        LightX:_buttonHovered ? .2f : -.35f),feedback:buttonFeedback);
            foreach(var region in _matte)
            {
                using var fill=new SKPaint{Color=new SKColor(region.Color.R,region.Color.G,region.Color.B,region.Color.A),IsAntialias=true};
                surface.Canvas.DrawRoundRect(SKRect.Create(region.Bounds.X,region.Bounds.Y,region.Bounds.Width,region.Bounds.Height),region.Radius,region.Radius,fill);
            }
            foreach(var track in _tracks.Values)
            {
                var inset=22*_density;var left=track.Bounds.Left+inset;var right=track.Bounds.Right-inset;var y=track.Bounds.Top+track.Bounds.Height/2f;
                using var pen=new SKPaint{IsAntialias=true,Style=SKPaintStyle.Stroke,StrokeWidth=5*_density,StrokeCap=SKStrokeCap.Round,
                    Color=new SKColor(track.Track.R,track.Track.G,track.Track.B)};
                surface.Canvas.DrawLine(left,y,right,y,pen);pen.Color=new SKColor(track.Fill.R,track.Fill.G,track.Fill.B);
                surface.Canvas.DrawLine(left,y,left+(right-left)*track.Fraction,y,pen);
            }
            var controlsActive=false;
            foreach(var control in _controls.Values)
            {
                var controlFeedback=control.Motion.Sample(now);controlsActive|=controlFeedback.Active;
                var tint=control.Prominent ? 96-Transparency*.16f : Math.Max(0,CodexUi.GlassTint(Transparency)-(control.Hovered?12:0));
                _painter.DrawSurface(surface.Canvas,SKRect.Create(control.Bounds.X,control.Bounds.Y,control.Bounds.Width,control.Bounds.Height),control.Radius,
                    new GlassSurfaceOptions(Dark:control.Prominent?!dark:dark,TintAmount:tint,Density:_density,Materialize:materialize,
                        ReducedTransparency:DesktopMaterial.TransparencyEnabled?0:1,IncreasedContrast:SystemInformation.HighContrast?1:0),controlFeedback);
            }
            surface.Canvas.Flush();
            if (_gpu is not null && !surface.ReadPixels(info,bits.Scan0,bits.Stride,0,0))
                throw new InvalidOperationException("Cannot read the optical GPU frame");
            NeedsFrame = _motionEnabled && (progress < 1 || (now < _animationUntil && (feedback.Active || buttonFeedback.Active || controlsActive)));
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        { LastError = e.Message; Clear(); return false; }
        finally
        {
            if (bits is not null) target.UnlockBits(bits);
            LastFrameMs = clock.Elapsed.TotalMilliseconds;
        }
    }

    public void Clear()
    { _source?.Dispose(); _source = null; _interaction = null; _buttonInteraction=null; _button=Rectangle.Empty; _buttonHovered=false; _controls.Clear();_tracks.Clear();_matte.Clear(); NeedsFrame = false; }
    public void Dispose() { Clear(); _gpu?.MakeCurrent(); _surface?.Dispose(); _painter?.Dispose(); _painter = null; _gpu?.Dispose(); _gpu=null; }
    public static bool MotionEnabled => SystemParametersInfo(0x1042, 0, out var enabled, 0) ? enabled : true;
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
}
