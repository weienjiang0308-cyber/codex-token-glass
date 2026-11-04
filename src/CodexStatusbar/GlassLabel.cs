using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace CodexStatusbar;
/// <summary>Cache text separately from its changing glass backdrop. Avoid replaying the parent's
/// entire paint path and reshaping unchanged text for every transparent WinForms label.</summary>
internal sealed class GlassLabel : Label
{
    private Bitmap? _glyphs;
    public GlassLabel(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);}
    private void ClearGlyphs(){_glyphs?.Dispose();_glyphs=null;Invalidate();}
    protected override void OnTextChanged(EventArgs e){base.OnTextChanged(e);ClearGlyphs();}
    protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);ClearGlyphs();}
    protected override void OnForeColorChanged(EventArgs e){base.OnForeColorChanged(e);ClearGlyphs();}
    protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);ClearGlyphs();}
    protected override void OnPaintBackground(PaintEventArgs e){}
    protected override void OnPaint(PaintEventArgs e)
    {
        if(Width<1||Height<1)return;
        if(Parent is not SettingsForm form || !form.DrawControlMaterial(this,e.Graphics))e.Graphics.Clear(Parent?.BackColor??SystemColors.Control);
        if(_glyphs is null)
        {
            _glyphs=new Bitmap(Width,Height,PixelFormat.Format32bppPArgb);_glyphs.SetResolution(DeviceDpi,DeviceDpi);
            using var g=Graphics.FromImage(_glyphs);g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
            using var brush=new SolidBrush(ForeColor);
            using var format=new StringFormat{
                Alignment=TextAlign is ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight?StringAlignment.Far
                    :TextAlign is ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter?StringAlignment.Center:StringAlignment.Near,
                LineAlignment=TextAlign is ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight?StringAlignment.Center
                    :TextAlign is ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight?StringAlignment.Far:StringAlignment.Near};
            if(!AppleText.Draw(g,Text,Font,new RectangleF(0,0,Width,Height),ForeColor,format.Alignment,format.LineAlignment,true))
                g.DrawString(Text,Font,brush,new RectangleF(0,0,Width,Height),format);
        }
        e.Graphics.DrawImageUnscaled(_glyphs,0,0);
    }
    protected override void Dispose(bool disposing){if(disposing)_glyphs?.Dispose();base.Dispose(disposing);}
}
