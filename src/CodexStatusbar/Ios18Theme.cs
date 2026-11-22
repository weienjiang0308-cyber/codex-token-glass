using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SkiaSharp;

namespace CodexStatusbar;

internal sealed record Ios18Palette(
    bool IsDark,
    Color WindowSurface,
    Color CardSurface,
    Color ControlFill,
    Color ControlHover,
    Color ControlPressed,
    Color TintedFill,
    Color TintedHover,
    Color TintedPressed,
    Color SegmentedTrack,
    Color SegmentedSelected,
    Color InputFill,
    Color Accent,
    Color AccentHover,
    Color AccentPressed,
    Color AccentText,
    Color TextPrimary,
    Color TextSecondary,
    Color Separator,
    Color CardBorder,
    Color WindowBorder,
    Color RowHighlight,
    Color CardShadow,
    Color SwitchOn = default,
    Color SwitchOff = default)
{
    public static readonly Ios18Palette Light = new(
        IsDark: false,
        WindowSurface: Color.FromArgb(242, 242, 247),
        CardSurface: Color.FromArgb(255, 255, 255),
        ControlFill: Color.FromArgb(227, 227, 233),
        ControlHover: Color.FromArgb(216, 216, 223),
        ControlPressed: Color.FromArgb(203, 203, 211),
        TintedFill: Color.FromArgb(227, 227, 233),
        TintedHover: Color.FromArgb(216, 216, 223),
        TintedPressed: Color.FromArgb(203, 203, 211),
        SegmentedTrack: Color.FromArgb(238, 238, 240),
        SegmentedSelected: Color.FromArgb(255, 255, 255),
        InputFill: Color.FromArgb(238, 238, 240),
        Accent: Color.FromArgb(0, 122, 255),
        AccentHover: Color.FromArgb(0, 108, 232),
        AccentPressed: Color.FromArgb(0, 94, 210),
        AccentText: Color.FromArgb(255, 255, 255),
        TextPrimary: Color.FromArgb(0, 0, 0),
        TextSecondary: Color.FromArgb(108, 108, 112),
        Separator: Color.FromArgb(222, 222, 227),
        CardBorder: Color.FromArgb(0, 0, 0, 0),
        WindowBorder: Color.FromArgb(36, 0, 0, 0),
        RowHighlight: Color.FromArgb(236, 244, 255),
        CardShadow: Color.FromArgb(34, 0, 0, 0),
        SwitchOn: Color.FromArgb(52, 199, 89),
        SwitchOff: Color.FromArgb(233, 233, 234));

    public static readonly Ios18Palette Dark = new(
        IsDark: true,
        WindowSurface: Color.FromArgb(28, 28, 30),
        CardSurface: Color.FromArgb(44, 44, 46),
        ControlFill: Color.FromArgb(58, 58, 61),
        ControlHover: Color.FromArgb(70, 70, 74),
        ControlPressed: Color.FromArgb(84, 84, 88),
        TintedFill: Color.FromArgb(58, 58, 61),
        TintedHover: Color.FromArgb(70, 70, 74),
        TintedPressed: Color.FromArgb(84, 84, 88),
        SegmentedTrack: Color.FromArgb(56, 56, 59),
        SegmentedSelected: Color.FromArgb(99, 99, 102),
        InputFill: Color.FromArgb(56, 56, 59),
        Accent: Color.FromArgb(10, 132, 255),
        AccentHover: Color.FromArgb(40, 148, 255),
        AccentPressed: Color.FromArgb(0, 112, 232),
        AccentText: Color.FromArgb(255, 255, 255),
        TextPrimary: Color.FromArgb(245, 245, 247),
        TextSecondary: Color.FromArgb(160, 160, 166),
        Separator: Color.FromArgb(62, 62, 66),
        CardBorder: Color.FromArgb(0, 0, 0, 0),
        WindowBorder: Color.FromArgb(44, 255, 255, 255),
        RowHighlight: Color.FromArgb(34, 54, 79),
        CardShadow: Color.FromArgb(70, 0, 0, 0),
        SwitchOn: Color.FromArgb(48, 209, 88),
        SwitchOff: Color.FromArgb(57, 57, 61));

    public static Ios18Palette FromOverlay(OverlayThemePalette palette) =>
        palette.Background.GetBrightness() < 0.5f ? Dark : Light;

    // Mathematically verified G2-continuous Apple/Figma squircle corner geometry:
    // Straight edge -> strictly convex cubic Bezier (zero curvature at start, 1/r curvature at tangent)
    // -> circular arc of radius r -> symmetric cubic Bezier -> perpendicular edge.
    private static (float R, float P, float BetaRad, float XArc, float YArc, float XInt, float XC1) CornerGeometry(
        float radius, float halfWidth, float halfHeight, float smoothing = 0f)
    {
        var r = Math.Max(0f, Math.Min(radius, Math.Min(halfWidth, halfHeight)));
        if (r <= 0.5f) return (0f, 0f, 0f, 0f, 0f, 0f, 0f);
        var maxP = Math.Min(halfWidth, halfHeight);
        var p = Math.Min((1f + smoothing) * r, maxP);
        var effS = Math.Max(0f, p / r - 1f);
        var beta = (float)(Math.PI / 4.0 * effS);
        if (beta < 1e-4f)
        {
            return (r, r, 0f, r, 0f, r, r);
        }
        var sinB = MathF.Sin(beta);
        var cosB = MathF.Cos(beta);
        var xArc = r * (1f - sinB);
        var yArc = r * (1f - cosB);
        var xInt = xArc + yArc / MathF.Tan(beta);
        var tangentLen = yArc / sinB;
        var d = Math.Min(1.5f * tangentLen * tangentLen / (r * sinB), (p - xInt) * 0.85f);
        var xC1 = xInt + d;
        return (r, p, beta, xArc, yArc, xInt, xC1);
    }

    public static SKPath CreateContinuousSkPath(SKRect bounds, float radius)
    {
        using var builder = new SKPathBuilder();
        if (bounds.Width <= 0 || bounds.Height <= 0) return builder.Detach();
        var l = bounds.Left;
        var t = bounds.Top;
        var r = bounds.Right;
        var b = bounds.Bottom;
        var minHalf = Math.Min(bounds.Width, bounds.Height) / 2f;
        if (radius >= minHalf - 0.5f)
        {
            builder.AddRoundRect(bounds, minHalf, minHalf);
            return builder.Detach();
        }
        var (rad, p, beta, xArc, yArc, xInt, xC1) = CornerGeometry(radius, bounds.Width / 2f, bounds.Height / 2f);
        if (rad <= 0.5f)
        {
            builder.AddRect(bounds);
            return builder.Detach();
        }
        if (beta < 1e-4f)
        {
            builder.AddRoundRect(bounds, rad, rad);
            return builder.Detach();
        }
        var betaDeg = beta * 180f / MathF.PI;
        var sweepDeg = 90f - 2f * betaDeg;

        builder.MoveTo(l + p, t);
        builder.LineTo(r - p, t);
        // Top-right corner
        builder.CubicTo(r - xC1, t, r - xInt, t, r - xArc, t + yArc);
        builder.ArcTo(new SKRect(r - 2f * rad, t, r, t + 2f * rad), -90f + betaDeg, sweepDeg, false);
        builder.CubicTo(r, t + xInt, r, t + xC1, r, t + p);

        builder.LineTo(r, b - p);
        // Bottom-right corner
        builder.CubicTo(r, b - xC1, r, b - xInt, r - yArc, b - xArc);
        builder.ArcTo(new SKRect(r - 2f * rad, b - 2f * rad, r, b), betaDeg, sweepDeg, false);
        builder.CubicTo(r - xInt, b, r - xC1, b, r - p, b);

        builder.LineTo(l + p, b);
        // Bottom-left corner
        builder.CubicTo(l + xC1, b, l + xInt, b, l + xArc, b - yArc);
        builder.ArcTo(new SKRect(l, b - 2f * rad, l + 2f * rad, b), 90f + betaDeg, sweepDeg, false);
        builder.CubicTo(l, b - xInt, l, b - xC1, l, b - p);

        builder.LineTo(l, t + p);
        // Top-left corner
        builder.CubicTo(l, t + xC1, l, t + xInt, l + yArc, t + xArc);
        builder.ArcTo(new SKRect(l, t, l + 2f * rad, t + 2f * rad), 180f + betaDeg, sweepDeg, false);
        builder.CubicTo(l + xInt, t, l + xC1, t, l + p, t);
        builder.Close();
        return builder.Detach();
    }

    public static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0) return path;
        var l = bounds.Left;
        var t = bounds.Top;
        var r = bounds.Right;
        var b = bounds.Bottom;
        var (rad, p, beta, xArc, yArc, xInt, xC1) = CornerGeometry(radius, bounds.Width / 2f, bounds.Height / 2f);
        if (rad <= 0.5f)
        {
            path.AddRectangle(bounds);
            return path;
        }
        var diam = rad * 2f;
        if (beta < 1e-4f)
        {
            path.AddArc(l, t, diam, diam, 180, 90);
            path.AddArc(r - diam, t, diam, diam, 270, 90);
            path.AddArc(r - diam, b - diam, diam, diam, 0, 90);
            path.AddArc(l, b - diam, diam, diam, 90, 90);
            path.CloseFigure();
            return path;
        }
        var betaDeg = beta * 180f / MathF.PI;
        var sweepDeg = 90f - 2f * betaDeg;

        path.StartFigure();
        path.AddLine(l + p, t, r - p, t);
        path.AddBezier(r - p, t, r - xC1, t, r - xInt, t, r - xArc, t + yArc);
        path.AddArc(r - diam, t, diam, diam, -90f + betaDeg, sweepDeg);
        path.AddBezier(r - yArc, t + xArc, r, t + xInt, r, t + xC1, r, t + p);

        path.AddLine(r, t + p, r, b - p);
        path.AddBezier(r, b - p, r, b - xC1, r, b - xInt, r - yArc, b - xArc);
        path.AddArc(r - diam, b - diam, diam, diam, betaDeg, sweepDeg);
        path.AddBezier(r - xArc, b - yArc, r - xInt, b, r - xC1, b, r - p, b);

        path.AddLine(r - p, b, l + p, b);
        path.AddBezier(l + p, b, l + xC1, b, l + xInt, b, l + xArc, b - yArc);
        path.AddArc(l, b - diam, diam, diam, 90f + betaDeg, sweepDeg);
        path.AddBezier(l + yArc, b - xArc, l, b - xInt, l, b - xC1, l, b - p);

        path.AddLine(l, b - p, l, t + p);
        path.AddBezier(l, t + p, l, t + xC1, l, t + xInt, l + yArc, t + xArc);
        path.AddArc(l, t, diam, diam, 180f + betaDeg, sweepDeg);
        path.AddBezier(l + xArc, t + yArc, l + xInt, t, l + xC1, t, l + p, t);
        path.CloseFigure();
        return path;
    }

    public static void FillRoundedCard(Graphics graphics, Rectangle bounds, float radius, Color fillColor, Color borderColor, float borderWidth = 1f, Color? shadowColor = null, float shadowBlur = 0f, float shadowOffsetY = 0f)
    {
        if (bounds.Width <= 1 || bounds.Height <= 1) return;
        using var transform = graphics.Transform;
        var scale = Math.Max(0.1f, Math.Abs(transform.Elements[0]));
        var padDip = shadowColor.HasValue && shadowBlur > 0f ? (int)Math.Ceiling(shadowBlur * 2f + Math.Abs(shadowOffsetY)) : 0;
        var padPx = (int)Math.Ceiling(padDip * scale);
        var width = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
        var canvasWidth = width + padPx * 2;
        var canvasHeight = height + padPx * 2;
        using var raster = new Bitmap(canvasWidth, canvasHeight, PixelFormat.Format32bppPArgb);
        var bits = raster.LockBits(new Rectangle(0, 0, canvasWidth, canvasHeight), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(canvasWidth, canvasHeight, SKColorType.Bgra8888, SKAlphaType.Premul), bits.Scan0, bits.Stride);
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            var skRadius = radius * scale;
            var rect = new SKRect(padPx, padPx, padPx + width, padPx + height);
            using var fillPath = CreateContinuousSkPath(rect, skRadius);
            if (shadowColor is Color sc && sc.A > 0 && shadowBlur > 0f)
            {
                using var shadowPaint = new SKPaint
                {
                    Style = SKPaintStyle.Fill,
                    IsAntialias = true,
                    Color = new SKColor(sc.R, sc.G, sc.B, sc.A),
                    ImageFilter = SKImageFilter.CreateBlur(shadowBlur * scale * 0.5f, shadowBlur * scale * 0.5f)
                };
                canvas.Save();
                canvas.Translate(0f, shadowOffsetY * scale);
                canvas.DrawPath(fillPath, shadowPaint);
                canvas.Restore();
            }
            using var fillPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
                Color = new SKColor(fillColor.R, fillColor.G, fillColor.B, fillColor.A)
            };
            canvas.DrawPath(fillPath, fillPaint);
            if (borderWidth > 0f && borderColor.A > 0)
            {
                var stroke = Math.Max(1f, borderWidth * scale);
                var half = stroke / 2f;
                using var borderPath = CreateContinuousSkPath(new SKRect(padPx + half, padPx + half, padPx + width - half, padPx + height - half), Math.Max(0f, skRadius - half));
                using var borderPaint = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = stroke,
                    IsAntialias = true,
                    Color = new SKColor(borderColor.R, borderColor.G, borderColor.B, borderColor.A)
                };
                canvas.DrawPath(borderPath, borderPaint);
            }
            canvas.Flush();
        }
        finally
        {
            raster.UnlockBits(bits);
        }
        var dest = new Rectangle(bounds.X - padDip, bounds.Y - padDip, bounds.Width + padDip * 2, bounds.Height + padDip * 2);
        DrawExact(graphics, raster, dest);
    }

    /// <summary>Copies a raster rendered at device resolution without resampling. GDI+'s default
    /// pixel offset shifts a same-size DrawImage by half a pixel and blurs every edge.</summary>
    internal static void DrawExact(Graphics graphics, Image raster, RectangleF dest)
    {
        var state = graphics.Save();
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        graphics.DrawImage(raster, dest, new RectangleF(0, 0, raster.Width, raster.Height), GraphicsUnit.Pixel);
        graphics.Restore(state);
    }

    internal const float BackChevronWidth = 9.5f;

    /// <summary>Distance, in DIP, from a sheet's side edge to a navigation control of the given width
    /// when that control is centred on the centre of the top corner arc (the corner radius equals the
    /// navigation row's centre line). The leading control uses the same distance on its side.</summary>
    internal static float NavEdgeInset(float controlWidthDip) =>
        Math.Max(8f, OverlayLayoutCalculator.NavCenterYDip - controlWidthDip / 2f);

    /// <summary>iOS navigation-bar back chevron (SF Symbols chevron.backward, semibold, 17 pt bar
    /// size), drawn in the bar tint. Shared by every pushed sheet so all back buttons match.</summary>
    internal static void DrawBackChevron(Graphics graphics, float left, float centerY, float unit, Color color)
    {
        var state = graphics.Save();
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        using var pen = new Pen(color, 2.6f * unit)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round
        };
        var halfHeight = 8.5f * unit;
        var width = BackChevronWidth * unit;
        graphics.DrawLines(pen, new[] { new PointF(left + width, centerY - halfHeight), new PointF(left, centerY), new PointF(left + width, centerY + halfHeight) });
        graphics.Restore(state);
    }

    public static Bitmap CreateGearshapeIcon(int pixelSize, Color color)
    {
        var size = Math.Max(16, pixelSize);
        using var raster = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(raster))
        {
            canvas.Clear(SKColors.Transparent);
            var cx = size / 2f;
            var cy = size / 2f;
            // The ink (tooth radius plus half the stroke) fills the bitmap edge to edge, so callers can
            // align the bitmap's bounds directly with neighbouring text.
            const float toothRadius = 10.0f, rootRadius = 7.4f, toothHalfAngle = 9.5f, rootHalfAngle = 15f, strokeWidth = 2.1f;
            var unit = size / (2f * toothRadius + strokeWidth);
            // SF Symbols "gearshape" (outline) as used for iOS 18 bar buttons: eight trapezoidal teeth
            // with open notches between them and a round bore, stroked at the weight of semibold 17 pt
            // text so it matches the back chevron and "Done". Meant to be rasterised at device size.
            using var builder = new SKPathBuilder();
            var started = false;
            for (var index = 0; index < 8; index++)
            {
                var center = index * 45f - 90f;
                foreach (var (angle, radius) in new[]
                {
                    (center - rootHalfAngle, rootRadius), (center - toothHalfAngle, toothRadius),
                    (center + toothHalfAngle, toothRadius), (center + rootHalfAngle, rootRadius)
                })
                {
                    var radians = angle * MathF.PI / 180f;
                    var x = cx + radius * unit * MathF.Cos(radians);
                    var y = cy + radius * unit * MathF.Sin(radians);
                    if (started) builder.LineTo(x, y);
                    else builder.MoveTo(x, y);
                    started = true;
                }
            }
            builder.Close();
            using var silhouette = builder.Detach();
            using var stroke = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeWidth * unit,
                StrokeJoin = SKStrokeJoin.Round,
                IsAntialias = true,
                Color = new SKColor(color.R, color.G, color.B, color.A)
            };
            canvas.DrawPath(silhouette, stroke);
            canvas.DrawCircle(cx, cy, 3.3f * unit, stroke);
            canvas.Flush();
        }
        using var bridge = new Bitmap(size, size, raster.RowBytes, PixelFormat.Format32bppPArgb, raster.GetPixels());
        return new Bitmap(bridge);
    }
}
