using System.Drawing.Imaging;

namespace CodexStatusbar;

/// <summary>Text label painted onto its real opaque surface with subpixel (ClearType-style) glyph coverage.
/// The rendered image is cached until text, font, colour, size or the surface underneath changes.</summary>
internal sealed class GlassLabel : Label
{
    private Bitmap? _glyphs;
    private Color _glyphBackground;
    public GlassLabel() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
    private void ClearGlyphs() { _glyphs?.Dispose(); _glyphs = null; Invalidate(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); ClearGlyphs(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); ClearGlyphs(); }
    protected override void OnForeColorChanged(EventArgs e) { base.OnForeColorChanged(e); ClearGlyphs(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); ClearGlyphs(); }
    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 1 || Height < 1) return;
        var background = Parent is SettingsForm form ? form.ControlSurfaceColor(this) : Parent?.BackColor ?? SystemColors.Control;
        if (_glyphs is null || _glyphBackground != background)
        {
            _glyphs?.Dispose();
            _glyphBackground = background;
            _glyphs = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            _glyphs.SetResolution(DeviceDpi, DeviceDpi);
            using var graphics = Graphics.FromImage(_glyphs);
            graphics.Clear(background);
            var horizontal = TextAlign is ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight ? StringAlignment.Far
                : TextAlign is ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter ? StringAlignment.Center : StringAlignment.Near;
            var vertical = TextAlign is ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight ? StringAlignment.Center
                : TextAlign is ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight ? StringAlignment.Far : StringAlignment.Near;
            using (AppleText.Composite(_glyphs))
                AppleText.Draw(graphics, Text, Font, new RectangleF(0, 0, Width, Height), ForeColor, horizontal, vertical, true);
        }
        e.Graphics.DrawImageUnscaled(_glyphs, 0, 0);
    }

    protected override void Dispose(bool disposing) { if (disposing) _glyphs?.Dispose(); base.Dispose(disposing); }
}
