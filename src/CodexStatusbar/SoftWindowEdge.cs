using System.Drawing.Imaging;
using SkiaSharp;

namespace CodexStatusbar;

/// <summary>Per-pixel alpha fringe for a native-control dialog. Native controls keep their normal
/// hit targets; the binary host region is inset beneath this smooth, mouse-transparent edge.</summary>
internal sealed class SoftWindowEdge : Form
{
    private readonly Form _host;
    public SoftWindowEdge(Form host)
    {
        _host=host;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;
        StartPosition=FormStartPosition.Manual;AutoScaleMode=AutoScaleMode.None;
        TopMost=true;
    }
    protected override bool ShowWithoutActivation=>true;
    protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x80000|0x20|0x80|0x08000000;return p;}}
    public void Follow(){if(!IsDisposed)Bounds=new Rectangle(_host.Location,_host.ClientSize);}
    public void Present(Bitmap frame,float radius)
    {
        if(!_host.Visible||_host.IsDisposed)return;
        Follow();if(!Visible)Show(_host);
        using var ring=BuildRing(frame,radius,3f*_host.DeviceDpi/96f);
        // The native host already paints its region. Erase that exact binary region
        // from the alpha fringe instead of blending a second copy over the same pixels.
        if(_host.Region is { } hostRegion)
        {
            using var g=Graphics.FromImage(ring);
            g.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            using var clear=new SolidBrush(Color.Transparent);
            g.FillRegion(clear,hostRegion);
        }
        LayeredSurface.Present(this,ring);
    }
    internal static Bitmap BuildRing(Bitmap frame,float radius,float thickness)
    {
        var result=frame.Clone(new Rectangle(Point.Empty,frame.Size),PixelFormat.Format32bppPArgb);
        var bits=result.LockBits(new Rectangle(Point.Empty,result.Size),ImageLockMode.ReadWrite,PixelFormat.Format32bppPArgb);
        try
        {
            using var surface=SKSurface.Create(new SKImageInfo(result.Width,result.Height,SKColorType.Bgra8888,SKAlphaType.Premul),bits.Scan0,bits.Stride);
            using var erase=new SKPaint{BlendMode=SKBlendMode.DstOut,Color=SKColors.White,IsAntialias=true};
            surface.Canvas.DrawRoundRect(new SKRect(thickness,thickness,result.Width-thickness,result.Height-thickness),
                Math.Max(0,radius-thickness),Math.Max(0,radius-thickness),erase);
            surface.Canvas.Flush();
        }
        finally{result.UnlockBits(bits);}
        return result;
    }
    protected override void OnPaintBackground(PaintEventArgs e){}
}
