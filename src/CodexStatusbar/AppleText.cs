using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace CodexStatusbar;
/// <summary>Render UI text with Skia. When a composite target bitmap is supplied, glyphs are
/// rendered with ClearType-style subpixel coverage and blended onto the real pixels underneath,
/// matching the crispness of native Windows and Chromium text. Inter supplies Latin and digits.
/// CJK uses the hinted Microsoft YaHei UI with whole-pixel glyph origins below 144 DPI, where the
/// unhinted CFF outlines of Noto Sans SC blur across pixel boundaries; at higher densities Noto
/// Sans SC (closest to PingFang) is preferred when installed.</summary>
internal static class AppleText
{
    internal const float HighDensityDpi = 144f;
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, SKTypeface?> FileFaces = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(string Family, int Weight), SKTypeface> SystemFaces = new();
    private static readonly Dictionary<(int Codepoint, int Weight), SKTypeface?> FallbackFaces = new();
    private static readonly Dictionary<(int Weight, bool Dense), SKTypeface?> CjkFaces = new();
    [ThreadStatic] private static Bitmap? _compositeTarget;
    [ThreadStatic] private static bool _highDensity;
    [ThreadStatic] private static bool _displaySize;
    private static readonly Lazy<SKPixelGeometry?> LcdGeometry = new(DetectLcdGeometry);
    internal static bool? ProbeHighDensity;

    /// <summary>Dark-on-light coverage lift. Apple platforms render text with heavier stems than a
    /// linear blend; unhinted CFF fonts such as SF Pro then look thin at 96 DPI. A mild gamma on the
    /// coverage (like DirectWrite's enhanced contrast) restores the intended weight.</summary>
    private static readonly byte[] DarkTextCoverage = Enumerable.Range(0, 256)
        .Select(value => (byte)Math.Round(255 * Math.Pow(value / 255d, 0.78))).ToArray();

    private static void UseDensity(float dpi) => _highDensity = ProbeHighDensity ?? dpi >= HighDensityDpi;
    private static void UseOpticalSize(Font font) => _displaySize = font.SizeInPoints * 96f / 72f >= 20f;

    internal static string CjkFamilyName(bool highDensity) => PreferredCjkFace((int)SKFontStyleWeight.Normal, highDensity)?.FamilyName ?? "";

    internal static IDisposable Composite(Bitmap target)
    {
        var previous = _compositeTarget;
        _compositeTarget = target;
        return new CompositeScope(previous);
    }

    private sealed class CompositeScope(Bitmap? previous) : IDisposable
    {
        public void Dispose() => _compositeTarget = previous;
    }

    private static SKPixelGeometry? DetectLcdGeometry()
    {
        try
        {
            if (!SystemInformation.IsFontSmoothingEnabled || SystemInformation.FontSmoothingType != 2) return null;
            var orientation = 1;
            SystemParametersInfo(0x2012, 0, ref orientation, 0);
            return orientation == 0 ? SKPixelGeometry.BgrHorizontal : SKPixelGeometry.RgbHorizontal;
        }
        catch { return null; }
    }

    private static int FontWeight(Font font)
    {
        if (font.Bold) return font.SizeInPoints >= 14f ? (int)SKFontStyleWeight.Bold : (int)SKFontStyleWeight.SemiBold;
        var family = font.FontFamily.Name;
        if (family.Contains("Semib", StringComparison.OrdinalIgnoreCase)) return (int)SKFontStyleWeight.SemiBold;
        if (family.Contains("Medium", StringComparison.OrdinalIgnoreCase)) return (int)SKFontStyleWeight.Medium;
        return (int)SKFontStyleWeight.Normal;
    }

    private static string NormalizeFamily(string family)
    {
        if (family.StartsWith("Noto Sans SC", StringComparison.OrdinalIgnoreCase)) return "Noto Sans SC";
        if (family.StartsWith("Segoe UI Variable", StringComparison.OrdinalIgnoreCase)) return "Segoe UI Variable";
        return family;
    }

    private static SKTypeface? TryLoadLocalAppleFont(string family, int weight)
    {
        if (family.StartsWith("Inter", StringComparison.OrdinalIgnoreCase))
            return LoadLocalSfPro(weight) ?? TryLoadEmbeddedFont(InterFile(weight));
        var suffix = SfSuffix(weight);
        var fileName = family.StartsWith("PingFang", StringComparison.OrdinalIgnoreCase) ? $"PingFangSC-{suffix}.ttf"
            : family.StartsWith("SF Pro Display", StringComparison.OrdinalIgnoreCase) ? $"SF-Pro-Display-{suffix}.otf"
            : family.StartsWith("SF Pro", StringComparison.OrdinalIgnoreCase) ? $"SF-Pro-Text-{suffix}.otf"
            : null;
        return fileName is null ? null : LoadLocalFile(fileName);
    }

    private static string SfSuffix(int weight) =>
        weight >= (int)SKFontStyleWeight.Bold ? "Bold"
        : weight >= (int)SKFontStyleWeight.SemiBold ? "Semibold"
        : weight >= (int)SKFontStyleWeight.Medium ? "Medium"
        : "Regular";

    /// <summary>A locally installed SF Pro (never bundled; Apple's licence forbids redistribution)
    /// replaces Inter. Like iOS, the Text optical size is used below 20 pt and Display from 20 pt.</summary>
    private static SKTypeface? LoadLocalSfPro(int weight) =>
        LoadLocalFile($"SF-Pro-{(_displaySize ? "Display" : "Text")}-{SfSuffix(weight)}.otf");

    private static SKTypeface? LoadLocalFile(string fileName)
    {
        lock (CacheGate)
        {
            if (FileFaces.TryGetValue(fileName, out var cached)) return cached;
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexStatusbar", "fonts", fileName);
            var loaded = File.Exists(path) ? SKTypeface.FromFile(path) : null;
            FileFaces[fileName] = loaded;
            return loaded;
        }
    }

    private static SKTypeface? TryLoadEmbeddedFont(string fileName)
    {
        lock (CacheGate)
        {
            if (FileFaces.TryGetValue("embedded:" + fileName, out var cached)) return cached;
            SKTypeface? loaded = null;
            var assembly = Assembly.GetExecutingAssembly();
            var resource = assembly.GetManifestResourceNames().FirstOrDefault(name => name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
            if (resource is not null)
            {
                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream is not null)
                {
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    loaded = SKTypeface.FromData(SKData.CreateCopy(buffer.ToArray()));
                }
            }
            FileFaces["embedded:" + fileName] = loaded;
            return loaded;
        }
    }

    private static SKTypeface? PreferredCjkFace(int weight, bool dense)
    {
        lock (CacheGate)
        {
            if (CjkFaces.TryGetValue((weight, dense), out var cached)) return cached;
            var style = new SKFontStyle(weight, (int)SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            var face = dense
                ? TryLoadEmbeddedFont(NotoFile(weight)) ?? SKFontManager.Default.MatchFamily("Microsoft YaHei UI", style) ?? LoadUnregisteredYaHei(weight)
                : SKFontManager.Default.MatchFamily("Microsoft YaHei UI", style) ?? LoadUnregisteredYaHei(weight) ?? TryLoadEmbeddedFont(NotoFile(weight));
            CjkFaces[(weight, dense)] = face;
            return face;
        }
    }

    private static string InterFile(int weight) =>
        weight >= (int)SKFontStyleWeight.Bold ? "Inter-Bold.ttf"
        : weight >= (int)SKFontStyleWeight.Medium ? "Inter-SemiBold.ttf"
        : "Inter-Regular.ttf";

    /// <summary>The bundled Noto Sans SC (SIL OFL) ships Regular, Medium and Bold; semibold UI text
    /// uses Bold so it matches the weight YaHei offers at low density.</summary>
    private static string NotoFile(int weight) =>
        weight >= (int)SKFontStyleWeight.SemiBold ? "NotoSansSC-Bold.otf"
        : weight >= (int)SKFontStyleWeight.Medium ? "NotoSansSC-Medium.otf"
        : "NotoSansSC-Regular.otf";

    /// <summary>English Windows editions can ship msyh.ttc without registering it, so the system
    /// font manager never offers YaHei and CJK silently falls back to Japanese Yu Gothic.</summary>
    private static SKTypeface? LoadUnregisteredYaHei(int weight)
    {
        var fileName = weight >= (int)SKFontStyleWeight.SemiBold ? "msyhbd.ttc" : "msyh.ttc";
        if (FileFaces.TryGetValue("system:" + fileName, out var cached)) return cached;
        SKTypeface? loaded = null;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), fileName);
            if (File.Exists(path)) loaded = SKTypeface.FromFile(path, 1) ?? SKTypeface.FromFile(path, 0);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        FileFaces["system:" + fileName] = loaded;
        return loaded;
    }

    private static SKTypeface ResolveTypeface(string family, int weight)
    {
        if (TryLoadLocalAppleFont(family, weight) is { } appleFace) return appleFace;
        var normalized = NormalizeFamily(family);
        if (normalized == "Noto Sans SC" && PreferredCjkFace(weight, _highDensity) is { } chinese) return chinese;
        var key = (normalized, weight);
        lock (CacheGate)
        {
            if (SystemFaces.TryGetValue(key, out var cached)) return cached;
            var style = new SKFontStyle(weight, (int)SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            var matched = SKFontManager.Default.MatchFamily(normalized, style)
                ?? SKFontManager.Default.MatchFamily("Segoe UI", style)
                ?? SKTypeface.Default;
            SystemFaces[key] = matched;
            return matched;
        }
    }

    private static SKTypeface ResolveForRune(Rune rune, SKTypeface primary, int weight)
    {
        var codepoint = rune.Value;
        if (codepoint <= 0x20) return primary;
        using (var probe = new SKFont(primary))
        {
            if (probe.ContainsGlyph(codepoint)) return primary;
        }
        if (PreferredCjkFace(weight, _highDensity) is { } cjk)
        {
            using var cjkProbe = new SKFont(cjk);
            if (cjkProbe.ContainsGlyph(codepoint)) return cjk;
        }
        lock (CacheGate)
        {
            var key = (codepoint, weight);
            if (FallbackFaces.TryGetValue(key, out var cached)) return cached ?? primary;
            var style = new SKFontStyle(weight, (int)SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            var fallback = SKFontManager.Default.MatchCharacter(null, style, new[] { "zh-CN", "en-US" }, codepoint);
            if (FallbackFaces.Count > 2048) FallbackFaces.Clear();
            FallbackFaces[key] = fallback;
            return fallback ?? primary;
        }
    }

    private static SKFont MakeFont(SKTypeface face, float pixels, bool lcd = false) => new(face, pixels)
    {
        Edging = lcd ? SKFontEdging.SubpixelAntialias : SKFontEdging.Antialias,
        Hinting = lcd ? SKFontHinting.Normal : SKFontHinting.Slight,
        Subpixel = _highDensity,
        LinearMetrics = _highDensity
    };

    private static List<(string Segment, SKTypeface Face)> SegmentRuns(string line, SKTypeface primary, int weight)
    {
        var runs = new List<(string, SKTypeface)>(4);
        if (string.IsNullOrEmpty(line)) return runs;
        SKTypeface? activeFace = null;
        var builder = new StringBuilder(line.Length);
        foreach (var rune in line.EnumerateRunes())
        {
            var face = ResolveForRune(rune, primary, weight);
            if (activeFace is null)
            {
                activeFace = face;
            }
            else if (!ReferenceEquals(face, activeFace))
            {
                runs.Add((builder.ToString(), activeFace));
                builder.Clear();
                activeFace = face;
            }
            builder.Append(rune.ToString());
        }
        if (builder.Length > 0 && activeFace is not null)
        {
            runs.Add((builder.ToString(), activeFace));
        }
        return HarmonizeWeights(runs, primary);
    }

    /// <summary>Chinese fonts offer Regular and Bold but no Semibold. When a semibold line mixes
    /// Latin with Bold CJK glyphs, Latin is lifted to Inter Bold so the line has one visual weight.</summary>
    private static List<(string Segment, SKTypeface Face)> HarmonizeWeights(List<(string Segment, SKTypeface Face)> runs, SKTypeface primary)
    {
        if (primary.FontWeight is < (int)SKFontStyleWeight.Medium or >= (int)SKFontStyleWeight.Bold
            || !runs.Any(run => !ReferenceEquals(run.Face, primary) && run.Face.FontWeight >= (int)SKFontStyleWeight.Bold)
            || BoldSibling(primary) is not { } bold)
            return runs;
        for (var index = 0; index < runs.Count; index++)
            if (ReferenceEquals(runs[index].Face, primary)) runs[index] = (runs[index].Segment, bold);
        return runs;
    }

    private static SKTypeface? BoldSibling(SKTypeface primary) =>
        primary.FamilyName.StartsWith("SF Pro", StringComparison.OrdinalIgnoreCase) ? LoadLocalSfPro((int)SKFontStyleWeight.Bold)
        : primary.FamilyName.StartsWith("Inter", StringComparison.OrdinalIgnoreCase) ? TryLoadEmbeddedFont("Inter-Bold.ttf")
        : null;

    /// <summary>Distance from the top of a single-line, top-aligned text box to its baseline, in the
    /// caller's logical units. Lets callers put different sizes on one shared baseline.</summary>
    internal static float BaselineOffset(Graphics g, Font font)
    {
        using var transform = g.Transform;
        var scale = Math.Max(0.1f, Math.Abs(transform.Elements[0]));
        UseDensity(g.DpiY * scale);
        UseOpticalSize(font);
        var primary = ResolveTypeface(font.FontFamily.Name, FontWeight(font));
        using var skFont = MakeFont(primary, font.SizeInPoints * g.DpiY / 72f * scale, true);
        return -skFont.Metrics.Ascent / scale;
    }

    private static float MeasureLine(string line, SKTypeface primary, int weight, float pixelSize)
    {
        if (string.IsNullOrEmpty(line)) return 0f;
        var total = 0f;
        foreach (var (segment, face) in SegmentRuns(line, primary, weight))
        {
            using var skFont = MakeFont(face, pixelSize);
            total += skFont.MeasureText(segment);
        }
        return total;
    }

    private static string FitLine(string line, float maxWidth, SKTypeface primary, int weight, float pixelSize)
    {
        if (MeasureLine(line, primary, weight, pixelSize) <= maxWidth) return line;
        var low = 0;
        var high = line.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (MeasureLine(line[..mid] + "…", primary, weight, pixelSize) <= maxWidth) low = mid;
            else high = mid - 1;
        }
        return low > 0 ? line[..low] + "…" : "…";
    }

    public static float? Measure(Graphics g, string text, Font font)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        UseDensity(g.DpiY);
        UseOpticalSize(font);
        var weight = FontWeight(font);
        var primary = ResolveTypeface(font.FontFamily.Name, weight);
        var pixelSize = font.SizeInPoints * g.DpiY / 72f;
        return MeasureLine(text, primary, weight, pixelSize);
    }

    /// <summary>Width of a line at 96 DPI, for layout code that has no surface to measure on yet.</summary>
    internal static float MeasureAt96(string text, Font font)
    {
        using var bitmap = new Bitmap(1, 1);
        bitmap.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(bitmap);
        return Measure(graphics, text, font) ?? 0f;
    }

    /// <summary>Draws one line in a bundled display typeface with its ink starting at the left edge of
    /// <paramref name="bounds"/> and centred vertically in it. Used for the front page's wordmark-style
    /// title, the one place that does not follow the system text faces. Returns false, drawing
    /// nothing, when the typeface is missing or lacks a glyph, so the caller can fall back.</summary>
    internal static bool DrawDisplay(Graphics g, string text, string embeddedFile, float pixelSize, RectangleF bounds, Color color)
    {
        if (string.IsNullOrEmpty(text) || bounds.Width <= 0 || bounds.Height <= 0) return true;
        if (TryLoadEmbeddedFont(embeddedFile) is not { } face) return false;
        using var font = new SKFont(face, pixelSize) { Edging = SKFontEdging.Antialias, Hinting = SKFontHinting.None, Subpixel = true };
        if (!font.ContainsGlyphs(text)) return false;
        var width = Math.Max(1, (int)Math.Ceiling(bounds.Width));
        var height = Math.Max(1, (int)Math.Ceiling(bounds.Height));
        font.MeasureText(text, out var ink);
        using var raster = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        var bits = raster.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), bits.Scan0, bits.Stride);
            using var paint = new SKPaint { Color = new SKColor(color.R, color.G, color.B, color.A), IsAntialias = true };
            surface.Canvas.Clear(SKColors.Transparent);
            var baseline = MathF.Floor((height - ink.Height) / 2f - ink.Top);
            surface.Canvas.DrawText(text, MathF.Round(-ink.Left), baseline, SKTextAlign.Left, font, paint);
            surface.Canvas.Flush();
        }
        finally
        {
            raster.UnlockBits(bits);
        }
        Ios18Palette.DrawExact(g, raster, new RectangleF(MathF.Round(bounds.X), MathF.Round(bounds.Y), width, height));
        return true;
    }

    public static bool Draw(Graphics g, string text, Font font, RectangleF bounds, Color color,
        StringAlignment horizontal = StringAlignment.Near, StringAlignment vertical = StringAlignment.Center, bool multiline = false)
    {
        if (string.IsNullOrEmpty(text) || bounds.Width <= 0 || bounds.Height <= 0) return true;
        using var transform = g.Transform;
        var elements = transform.Elements;
        var scale = Math.Max(0.1f, Math.Abs(elements[0]));
        UseDensity(g.DpiY * scale);
        UseOpticalSize(font);
        var weight = FontWeight(font);
        var primary = ResolveTypeface(font.FontFamily.Name, weight);
        var width = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
        var pixelSize = font.SizeInPoints * g.DpiY / 72f * scale;

        if (_compositeTarget is { } target && LcdGeometry.Value is { } geometry && Math.Abs(elements[1]) < 1e-4 && Math.Abs(elements[2]) < 1e-4)
        {
            var originX = (int)Math.Round(bounds.X * elements[0] + elements[4]);
            var originY = (int)Math.Round(bounds.Y * elements[3] + elements[5]);
            DrawLcd(g, target, text, primary, weight, pixelSize, width, height, originX, originY, color, horizontal, vertical, multiline, geometry);
            return true;
        }

        using var raster = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        var bits = raster.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), bits.Scan0, bits.Stride);
            using var paint = new SKPaint { Color = new SKColor(color.R, color.G, color.B, color.A), IsAntialias = true };
            surface.Canvas.Clear(SKColors.Transparent);
            RenderLines(surface.Canvas, paint, text, primary, weight, pixelSize, width, height, horizontal, vertical, multiline, false);
            surface.Canvas.Flush();
        }
        finally
        {
            raster.UnlockBits(bits);
        }
        Ios18Palette.DrawExact(g, raster, bounds);
        return true;
    }

    private static void RenderLines(SKCanvas canvas, SKPaint paint, string text, SKTypeface primary, int weight, float pixelSize,
        int width, int height, StringAlignment horizontal, StringAlignment vertical, bool multiline, bool lcd)
    {
        using var primaryFont = MakeFont(primary, pixelSize, lcd);
        var lines = multiline ? text.Split('\n') : new[] { text.Replace('\n', ' ') };
        var metrics = primaryFont.Metrics;
        var glyphHeight = metrics.Descent - metrics.Ascent;
        var rawLineHeight = lines.Length > 1 ? glyphHeight + Math.Max(metrics.Leading, pixelSize * 0.14f) : glyphHeight;
        var lineHeight = (float)Math.Ceiling(rawLineHeight);
        var blockHeight = lineHeight * lines.Length;
        var top = vertical == StringAlignment.Center ? (height - blockHeight) / 2f
            : vertical == StringAlignment.Far ? height - blockHeight
            : 0f;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (!multiline) line = FitLine(line, width, primary, weight, pixelSize);
            var textWidth = MeasureLine(line, primary, weight, pixelSize);
            var cursorX = horizontal == StringAlignment.Center ? MathF.Round((width - textWidth) / 2f)
                : horizontal == StringAlignment.Far ? MathF.Round(width - textWidth)
                : 0f;
            var baselineY = MathF.Round(top + index * lineHeight - metrics.Ascent);
            if (lines.Length == 1 && vertical == StringAlignment.Center && OpticalInk(line, primary, weight, pixelSize, lcd) is (float inkTop, float inkBottom))
                baselineY = MathF.Round(height / 2f - (inkTop + inkBottom) / 2f);
            foreach (var (segment, face) in SegmentRuns(line, primary, weight))
            {
                using var segFont = MakeFont(face, pixelSize, lcd);
                canvas.DrawText(segment, cursorX, baselineY, SKTextAlign.Left, segFont, paint);
                cursorX += segFont.MeasureText(segment);
            }
        }
    }

    /// <summary>Optical vertical extent relative to the baseline: the tallest glyph top, and the
    /// baseline itself for Latin/digits (so commas and descenders do not pull text upward) or the
    /// real ideograph bottom for CJK, whose ink sits higher than Latin font metrics imply.</summary>
    private static (float Top, float Bottom)? OpticalInk(string line, SKTypeface primary, int weight, float pixelSize, bool lcd)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        float top = 0f, cjkBottom = 0f;
        var hasCjk = false;
        foreach (var (segment, face) in SegmentRuns(line, primary, weight))
        {
            using var font = MakeFont(face, pixelSize, lcd);
            font.MeasureText(segment, out var bounds);
            if (bounds.Height <= 0) continue;
            top = Math.Min(top, bounds.Top);
            if (segment.Any(c => c is >= '\u2e80' and <= '\u9fff' or >= '\uff00' and <= '\uffef'))
            {
                hasCjk = true;
                cjkBottom = Math.Max(cjkBottom, bounds.Bottom);
            }
        }
        if (top >= 0f) return null;
        return (top, hasCjk ? cjkBottom : 0f);
    }

    private static void DrawLcd(Graphics g, Bitmap target, string text, SKTypeface primary, int weight, float pixelSize,
        int width, int height, int originX, int originY, Color color, StringAlignment horizontal, StringAlignment vertical,
        bool multiline, SKPixelGeometry geometry)
    {
        var region = Rectangle.Intersect(new Rectangle(originX, originY, width, height), new Rectangle(Point.Empty, target.Size));
        if (region.Width <= 0 || region.Height <= 0) return;
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var coverage = new SKBitmap(info);
        using (var surface = SKSurface.Create(info, coverage.GetPixels(), coverage.RowBytes, new SKSurfaceProperties(geometry)))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        {
            surface.Canvas.Clear(SKColors.White);
            RenderLines(surface.Canvas, paint, text, primary, weight, pixelSize, width, height, horizontal, vertical, multiline, true);
            surface.Canvas.Flush();
        }
        g.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
        var coverageBytes = coverage.Bytes;
        var coverageStride = coverage.RowBytes;
        var data = target.LockBits(region, ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            var row = new byte[region.Width * 4];
            int sourceAlpha = color.A, sourceR = color.R, sourceG = color.G, sourceB = color.B;
            var boost = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) < 128 ? DarkTextCoverage : null;
            for (var y = 0; y < region.Height; y++)
            {
                var rowPointer = data.Scan0 + y * data.Stride;
                Marshal.Copy(rowPointer, row, 0, row.Length);
                var coverageRow = (region.Y - originY + y) * coverageStride + (region.X - originX) * 4;
                for (var x = 0; x < region.Width; x++)
                {
                    var c = coverageRow + x * 4;
                    int rawB = 255 - coverageBytes[c], rawG = 255 - coverageBytes[c + 1], rawR = 255 - coverageBytes[c + 2];
                    if (boost is not null) { rawB = boost[rawB]; rawG = boost[rawG]; rawR = boost[rawR]; }
                    var coverB = rawB * sourceAlpha / 255;
                    var coverG = rawG * sourceAlpha / 255;
                    var coverR = rawR * sourceAlpha / 255;
                    if ((coverB | coverG | coverR) == 0) continue;
                    var p = x * 4;
                    var destinationAlpha = row[p + 3];
                    if (destinationAlpha == 255)
                    {
                        row[p] = (byte)(row[p] + (sourceB - row[p]) * coverB / 255);
                        row[p + 1] = (byte)(row[p + 1] + (sourceG - row[p + 1]) * coverG / 255);
                        row[p + 2] = (byte)(row[p + 2] + (sourceR - row[p + 2]) * coverR / 255);
                    }
                    else
                    {
                        var cover = (coverB + coverG + coverR) / 3;
                        var keep = 255 - cover;
                        row[p] = (byte)((sourceB * cover + row[p] * keep) / 255);
                        row[p + 1] = (byte)((sourceG * cover + row[p + 1] * keep) / 255);
                        row[p + 2] = (byte)((sourceR * cover + row[p + 2] * keep) / 255);
                        row[p + 3] = (byte)(cover + destinationAlpha * keep / 255);
                    }
                }
                Marshal.Copy(row, 0, rowPointer, row.Length);
            }
        }
        finally
        {
            target.UnlockBits(data);
        }
    }

    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(int action, int parameter, ref int value, int flags);
}
