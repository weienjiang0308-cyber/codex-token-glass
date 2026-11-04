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
    private SKBitmap? _highResolution;
    private SKImage? _baseLayer;
    private (Size Size,int Sample,bool Dark,int Tint,bool Transparent,bool Contrast,float Radius,int Matte)? _baseKey;
    private int _matteRevision;
    internal bool CacheBackgroundEnabled { get; set; } = true;
    internal bool UseClarityPreview { get; set; }
    internal int? RenderScaleOverride { get; set; }
    private readonly Dictionary<int,SKImage> _clarityLayers=new();
    private Size _claritySize;
    private bool _clarityDark,_warmingClarity;
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
    internal float PanelRadiusDip { get; set; } = 32;
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
        _gpu?.MakeCurrent();_baseLayer?.Dispose();_baseLayer=null;_baseKey=null;ClearClarityLayers();
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
        // Regular glass must scatter background text, not leave legible ghost copies behind metrics.
        // Alpha/transparency stays independently controlled by the user's slider.
        using var original = SKImage.FromBitmap(raster);
        using var blurred = SKSurface.Create(raster.Info);
        // The library already supplies a wide blur; a second 10-DIP prefilter destroys fine detail.
        using var blurFilter = SKImageFilter.CreateBlur(3*dpi/96f,3*dpi/96f,SKShaderTileMode.Clamp);
        using var blurPaint = new SKPaint { ImageFilter = blurFilter };
        blurred.Canvas.DrawImage(original,0,0,new SKSamplingOptions(SKFilterMode.Linear,SKMipmapMode.None),blurPaint);
        var image = blurred.Snapshot();
        _painter.SetSource(image);
        _source?.Dispose(); _source = image;
        _panel = panel; _density = dpi / 96f;
        _motionEnabled = motionOverride ?? MotionEnabled;
        _interaction = new CalmInteraction(new CalmGeometry(panel.Width / _density, panel.Height / _density, PanelRadiusDip,
            panel.X / _density, panel.Y / _density, bitmap.Width / _density, bitmap.Height / _density),
            new CalmOptions(ReducedMotion: !_motionEnabled));
        _opened = Now; _animationUntil = _opened + .35;
        NeedsFrame = _motionEnabled; LastError = null;
    }

    public void Press(float dx = 0, float dy = 0)
    {
        _interaction?.Press(Now, dx / _density, dy / _density);
        _animationUntil = Now + GlassMotion.FeedbackSeconds; NeedsFrame = Ready && _motionEnabled;
    }
    public void Release()
    {
        _interaction?.Release(Now);
        _animationUntil = Now + GlassMotion.FeedbackSeconds; NeedsFrame = Ready && _motionEnabled;
    }

    public void SetButtonBounds(Rectangle bounds, Size viewport)
    {
        _button = bounds;
        _buttonInteraction = bounds.IsEmpty ? null : new CalmInteraction(
            new CalmGeometry(bounds.Width/_density,bounds.Height/_density,bounds.Height/_density/2,
                bounds.X/_density,bounds.Y/_density,viewport.Width/_density,viewport.Height/_density),
            new CalmOptions(PressScale:GlassMotion.PressScale,PressGrowth:1,ReducedMotion:!_motionEnabled));
    }

    public void ButtonState(bool hovered, bool pressed)
    {
        _buttonHovered = hovered;
        if (pressed) _buttonInteraction?.Press(Now); else _buttonInteraction?.Release(Now);
        _animationUntil = Now + GlassMotion.FeedbackSeconds; NeedsFrame = Ready && _motionEnabled;
    }

    public void Control(int key,Rectangle bounds,float radius,bool hovered,bool pressed,bool prominent=false)
    {
        if(!Ready || bounds.Width<=0 || bounds.Height<=0)return;
        var create=!_controls.TryGetValue(key,out var state) || state.Bounds.Size!=bounds.Size;
        if(create)
        {
            state=new ControlSurface{Motion=new CalmInteraction(new CalmGeometry(bounds.Width/_density,bounds.Height/_density,
                Math.Min(radius,Math.Min(bounds.Width,bounds.Height)/2f)/_density,bounds.X/_density,bounds.Y/_density,
                _source!.Width/_density,_source.Height/_density),new CalmOptions(PressScale:GlassMotion.PressScale,PressGrowth:1,ReducedMotion:!_motionEnabled))};
            _controls[key]=state;
        }
        var current=state??throw new InvalidOperationException("Missing optical control state");
        if(create || current.Pressed!=pressed || current.Hovered!=hovered)
        {
            if(pressed)current.Motion.Press(Now);else current.Motion.Release(Now);
            _animationUntil=Now+GlassMotion.FeedbackSeconds;NeedsFrame=_motionEnabled;
        }
        current.Bounds=bounds;current.Radius=radius;current.Hovered=hovered;current.Pressed=pressed;current.Prominent=prominent;
    }
    public void Track(int key,Rectangle bounds,float fraction,Color track,Color fill)=>
        _tracks[key]=(bounds,Math.Clamp(fraction,0,1),track,fill);
    public void ReadingSurfaces(params (Rectangle Bounds,float Radius,Color Color)[] surfaces)
    {
        if(_matte.SequenceEqual(surfaces))return;
        if(_matte.Count!=surfaces.Length || _matte.Where((r,i)=>r.Bounds!=surfaces[i].Bounds || r.Radius!=surfaces[i].Radius ||
            r.Color.R!=surfaces[i].Color.R || r.Color.G!=surfaces[i].Color.G || r.Color.B!=surfaces[i].Color.B).Any())ClearClarityLayers();
        _matte.Clear();_matte.AddRange(surfaces);_matteRevision++;
    }
    private void ClearClarityLayers(){foreach(var image in _clarityLayers.Values)image.Dispose();_clarityLayers.Clear();}
    internal void PrepareClarityPreview(Bitmap target,bool dark)
    {
        if(!Ready || (_clarityLayers.Count==11 && _claritySize==target.Size && _clarityDark==dark))return;
        ClearClarityLayers();var original=_matte.ToArray();var oldOverride=TransparencyOverride;
        var oldMotion=_motionEnabled;var oldCache=CacheBackgroundEnabled;var oldPreview=UseClarityPreview;
        _warmingClarity=true;_motionEnabled=false;CacheBackgroundEnabled=true;UseClarityPreview=false;
        try
        {
            for(int value=0;value<=100;value+=10)
            {
                TransparencyOverride=value;
                ReadingSurfaces(original.Select(r=>(r.Bounds,r.Radius,Color.FromArgb(GlassAppearance.ReadingPlateAlpha(value),r.Color.R,r.Color.G,r.Color.B))).ToArray());
                if(!RenderInto(target,dark)||_baseLayer is null)break;
                _clarityLayers[value]=_baseLayer;_baseLayer=null;_baseKey=null;
            }
            _claritySize=target.Size;_clarityDark=dark;
        }
        finally
        {
            ReadingSurfaces(original);TransparencyOverride=oldOverride;_motionEnabled=oldMotion;
            CacheBackgroundEnabled=oldCache;UseClarityPreview=oldPreview;_warmingClarity=false;_opened=Now;
        }
    }

    public bool RenderInto(Bitmap target, bool dark)
    {
        if (!Ready) return false;
        var clock = Stopwatch.StartNew();
        BitmapData? bits = null;
        try
        {
            bits = target.LockBits(new Rectangle(Point.Empty, target.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            // Supersample small popovers at low display density; text is drawn later at native DPI.
            var sample = RenderScaleOverride ?? (target.Width < 900 && _density < 1.75f ? 2 : 1);
            var outputInfo = new SKImageInfo(target.Width,target.Height,SKColorType.Bgra8888,SKAlphaType.Premul);
            var info = new SKImageInfo(target.Width*sample,target.Height*sample,SKColorType.Bgra8888,SKAlphaType.Premul);
            if(sample>1 && (_highResolution is null || _highResolution.Width!=info.Width || _highResolution.Height!=info.Height))
            {_highResolution?.Dispose();_highResolution=new SKBitmap(info);}
            var highResolution=sample>1?_highResolution:null;
            var destination = highResolution?.GetPixels() ?? bits.Scan0;
            var stride = highResolution?.RowBytes ?? bits.Stride;
            _gpu?.MakeCurrent();
            if (_gpu is not null && (_surface is null || _surface.Canvas.DeviceClipBounds.Width!=info.Width || _surface.Canvas.DeviceClipBounds.Height!=info.Height))
            { _surface?.Dispose(); _surface = SKSurface.Create(_gpu.Context,true,info,4) ?? SKSurface.Create(_gpu.Context,true,info); }
            using var cpu = _gpu is null ? SKSurface.Create(info,destination,stride) : null;
            var surface = _surface ?? cpu;
            if (surface is null) return false;
            surface.Canvas.ResetMatrix();surface.Canvas.Clear(SKColors.Transparent);surface.Canvas.Scale(sample);
            var now = Now;
            var progress = _motionEnabled ? Math.Clamp((now - _opened) / GlassMotion.EntrySeconds, 0, 1) : 1;
            var materialize = (float)(1 - Math.Pow(1 - progress, 3));
            var feedback = _interaction?.Sample(now) ?? GlassFeedback.Identity;
            var key=(target.Size,sample,dark,Transparency,DesktopMaterial.TransparencyEnabled,SystemInformation.HighContrast,PanelRadiusDip,_matteRevision);
            var cacheable=CacheBackgroundEnabled && progress>=1 && !feedback.Active;
            if(UseClarityPreview && progress>=1 && _clarityLayers.Count==11 && _claritySize==target.Size && _clarityDark==dark)
            {
                var low=Transparency/10*10;var high=Math.Min(100,low+10);var weight=(Transparency-low)/10f;
                using var first=new SKPaint{Color=SKColors.White.WithAlpha((byte)Math.Round(255*(1-weight))),BlendMode=SKBlendMode.Src};
                using var second=new SKPaint{Color=SKColors.White.WithAlpha((byte)(255-first.Color.Alpha)),BlendMode=SKBlendMode.Plus};
                var destinationRect=SKRect.Create(target.Width,target.Height);var sampling=new SKSamplingOptions(SKFilterMode.Nearest,SKMipmapMode.None);
                surface.Canvas.DrawImage(_clarityLayers[low],destinationRect,sampling,first);
                if(weight>0)surface.Canvas.DrawImage(_clarityLayers[high],destinationRect,sampling,second);
            }
            else if(cacheable && _baseLayer is not null && _baseKey==key)
                surface.Canvas.DrawImage(_baseLayer,SKRect.Create(target.Width,target.Height),new SKSamplingOptions(SKFilterMode.Nearest,SKMipmapMode.None));
            else
            {
            _painter!.DrawSurface(surface.Canvas, SKRect.Create(_panel.X, _panel.Y, _panel.Width, _panel.Height),
                radius: PanelRadiusDip * _density,
                options: new GlassSurfaceOptions(Dark: dark, TintAmount: CodexUi.GlassTint(Transparency), Density: _density, Materialize: materialize,
                    ReducedTransparency: DesktopMaterial.TransparencyEnabled ? 0 : 1,
                    IncreasedContrast: SystemInformation.HighContrast ? 1 : 0), feedback: feedback);
            foreach(var region in _matte)
            {
                using var fill=new SKPaint{Color=new SKColor(region.Color.R,region.Color.G,region.Color.B,region.Color.A),IsAntialias=true};
                surface.Canvas.DrawRoundRect(SKRect.Create(region.Bounds.X,region.Bounds.Y,region.Bounds.Width,region.Bounds.Height),region.Radius,region.Radius,fill);
            }
            if(cacheable){_baseLayer?.Dispose();_baseLayer=surface.Snapshot();_baseKey=key;}
            }
            if(_warmingClarity)return true;
            var buttonFeedback = _buttonInteraction?.Sample(now) ?? GlassFeedback.Identity;
            if (!_button.IsEmpty)
                _painter!.DrawSurface(surface.Canvas,SKRect.Create(_button.X,_button.Y,_button.Width,_button.Height),
                    radius:_button.Height/2f,
                    options:new GlassSurfaceOptions(Dark:dark,TintAmount:GlassAppearance.ControlTint(Transparency,_buttonHovered),Density:_density,
                        Materialize:materialize,ReducedTransparency:DesktopMaterial.TransparencyEnabled ? 0 : 1,
                        LightX:_buttonHovered ? .2f : -.35f),feedback:buttonFeedback);
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
                var tint=GlassAppearance.ControlTint(Transparency,control.Hovered,control.Prominent);
                _painter!.DrawSurface(surface.Canvas,SKRect.Create(control.Bounds.X,control.Bounds.Y,control.Bounds.Width,control.Bounds.Height),control.Radius,
                    new GlassSurfaceOptions(Dark:control.Prominent?!dark:dark,TintAmount:tint,Density:_density,Materialize:materialize,
                        ReducedTransparency:DesktopMaterial.TransparencyEnabled?0:1,IncreasedContrast:SystemInformation.HighContrast?1:0),controlFeedback);
            }
            surface.Canvas.Flush();
            if (_gpu is not null && !surface.ReadPixels(info,destination,stride,0,0))
                throw new InvalidOperationException("Cannot read the optical GPU frame");
            if (highResolution is not null)
            {
                using var output = SKSurface.Create(outputInfo,bits.Scan0,bits.Stride);
                using var highImage = SKImage.FromBitmap(highResolution);
                output.Canvas.Clear(SKColors.Transparent);
                output.Canvas.DrawImage(highImage,SKRect.Create(target.Width,target.Height),
                    new SKSamplingOptions(SKFilterMode.Linear,SKMipmapMode.None));
                output.Canvas.Flush();
            }
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
    { ClearClarityLayers();_baseLayer?.Dispose();_baseLayer=null;_baseKey=null;_source?.Dispose(); _source = null; _interaction = null; _buttonInteraction=null; _button=Rectangle.Empty; _buttonHovered=false; _controls.Clear();_tracks.Clear();_matte.Clear(); NeedsFrame = false; }
    public void Dispose() { Clear(); _gpu?.MakeCurrent(); _surface?.Dispose();_highResolution?.Dispose(); _painter?.Dispose(); _painter = null; _gpu?.Dispose(); _gpu=null; }
    public static bool MotionEnabled => SystemParametersInfo(0x1042, 0, out var enabled, 0) ? enabled : true;
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
}
