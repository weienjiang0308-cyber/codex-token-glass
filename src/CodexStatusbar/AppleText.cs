using System.Drawing.Imaging;
using SkiaSharp;

namespace CodexStatusbar;
/// <summary>Render the authorized original outlines directly with Skia. Avoid GDI+'s legacy CFF
/// rasterization for small SF/PingFang text, while retaining grayscale alpha-safe antialiasing.</summary>
internal static class AppleText
{
    private static readonly Dictionary<string,SKTypeface> Faces=new();
    private static SKTypeface? Face(Font font)
    {
        var family=font.FontFamily.Name;var bold=CodexUi.IsEmphasized(font)?"Semibold":"Regular";
        var name=family.StartsWith("PingFang",StringComparison.Ordinal)?$"PingFangSC-{bold}.ttf"
            :family.StartsWith("SF Pro Display",StringComparison.Ordinal)?$"SF-Pro-Display-{bold}.otf"
            :family.StartsWith("SF Pro",StringComparison.Ordinal)?$"SF-Pro-Text-{bold}.otf":null;
        if(name is null)return null;
        if(Faces.TryGetValue(name,out var face))return face;
        var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CodexStatusbar","fonts",name);
        if(!File.Exists(path))return null;
        face=SKTypeface.FromFile(path);if(face is not null)Faces[name]=face;return face;
    }
    private static SKFont MakeFont(SKTypeface face,float pixels)=>new(face,pixels)
        {Edging=SKFontEdging.Antialias,Hinting=SKFontHinting.Normal,Subpixel=true};
    public static float? Measure(Graphics g,string text,Font font)
    {
        var face=Face(font);if(face is null)return null;
        using var skFont=MakeFont(face,font.SizeInPoints*g.DpiY/72f);
        return skFont.MeasureText(text);
    }
    public static bool Draw(Graphics g,string text,Font font,RectangleF bounds,Color color,
        StringAlignment horizontal=StringAlignment.Near,StringAlignment vertical=StringAlignment.Center,bool multiline=false)
    {
        var face=Face(font);if(face is null||bounds.Width<=0||bounds.Height<=0)return false;
        using var transform=g.Transform;var scale=Math.Max(.1f,Math.Abs(transform.Elements[0]));
        var width=Math.Max(1,(int)Math.Ceiling(bounds.Width*scale));var height=Math.Max(1,(int)Math.Ceiling(bounds.Height*scale));
        using var raster=new Bitmap(width,height,PixelFormat.Format32bppPArgb);
        var bits=raster.LockBits(new Rectangle(0,0,width,height),ImageLockMode.WriteOnly,PixelFormat.Format32bppPArgb);
        try
        {
            using var surface=SKSurface.Create(new SKImageInfo(width,height,SKColorType.Bgra8888,SKAlphaType.Premul),bits.Scan0,bits.Stride);
            using var skFont=MakeFont(face,font.SizeInPoints*g.DpiY/72f*scale);
            using var paint=new SKPaint{Color=new SKColor(color.R,color.G,color.B,color.A),IsAntialias=true};
            surface.Canvas.Clear(SKColors.Transparent);
            var lines=multiline?text.Split('\n'):new[]{text.Replace('\n',' ')};
            var metrics=skFont.Metrics;var lineHeight=(float)Math.Ceiling(metrics.Descent-metrics.Ascent+metrics.Leading);
            var blockHeight=lineHeight*lines.Length;
            var top=vertical==StringAlignment.Center?(height-blockHeight)/2:vertical==StringAlignment.Far?height-blockHeight:0;
            for(var index=0;index<lines.Length;index++)
            {
                var line=lines[index].TrimEnd('\r');
                if(!multiline && skFont.MeasureText(line)>width)
                {
                    var low=0;var high=line.Length;
                    while(low<high){var mid=(low+high+1)/2;if(skFont.MeasureText(line[..mid]+"…")<=width)low=mid;else high=mid-1;}
                    line=line[..low]+"…";
                }
                var textWidth=skFont.MeasureText(line);
                var x=horizontal==StringAlignment.Center?(width-textWidth)/2:horizontal==StringAlignment.Far?width-textWidth:0;
                var y=MathF.Round(top+index*lineHeight-metrics.Ascent);
                surface.Canvas.DrawText(line,x,y,SKTextAlign.Left,skFont,paint);
            }
            surface.Canvas.Flush();
        }
        finally{raster.UnlockBits(bits);}
        g.DrawImage(raster,bounds,new RectangleF(0,0,width,height),GraphicsUnit.Pixel);return true;
    }
}
