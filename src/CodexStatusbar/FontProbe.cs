using System.Drawing.Imaging;
using System.Text.Json;

namespace CodexStatusbar;

/// <summary>Renders the production text pipeline at low and high pixel density next to native GDI
/// ClearType, so CJK crispness and font selection can be inspected without a live session.</summary>
internal static class FontProbe
{
    private static readonly string[] Samples = { "窗口大小 缓存明细 上下文用量 1,050,000", "写入 Codex 全局配置，可直接输入，如 800k。 85.00%" };

    internal static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        const int rowHeight = 30;
        const int width = 620;
        using var bitmap = new Bitmap(width, 3 * (rowHeight * 2 + 18) + 8, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        using var labelFont = new Font("Segoe UI", 8f);
        using var body = CodexUi.UiFont(17);
        using var foot = CodexUi.UiFont(13);
        var y = 4;
        foreach (var (name, dense) in new[] { ("production low density (<144 DPI)", false), ("production high density (>=144 DPI)", true) })
        {
            AppleText.ProbeHighDensity = dense;
            TextRenderer.DrawText(g, $"{name}: {AppleText.CjkFamilyName(dense)}", labelFont, new Point(6, y), Color.Gray);
            y += 16;
            using (AppleText.Composite(bitmap))
            {
                AppleText.Draw(g, Samples[0], body, new RectangleF(10, y, width - 20, rowHeight), Color.Black, StringAlignment.Near);
                AppleText.Draw(g, Samples[1], foot, new RectangleF(10, y + rowHeight, width - 20, rowHeight), Color.FromArgb(0x6D, 0x6D, 0x72), StringAlignment.Near);
            }
            y += rowHeight * 2 + 2;
        }
        AppleText.ProbeHighDensity = null;
        TextRenderer.DrawText(g, "native GDI ClearType Microsoft YaHei UI", labelFont, new Point(6, y), Color.Gray);
        y += 16;
        using (var native = new Font("Microsoft YaHei UI", 17f, GraphicsUnit.Pixel))
        using (var nativeFoot = new Font("Microsoft YaHei UI", 13f, GraphicsUnit.Pixel))
        {
            TextRenderer.DrawText(g, Samples[0], native, new Point(10, y + 4), Color.Black);
            TextRenderer.DrawText(g, Samples[1], nativeFoot, new Point(10, y + rowHeight + 6), Color.FromArgb(0x6D, 0x6D, 0x72));
        }
        bitmap.Save(Path.Combine(directory, "font-probe.png"), ImageFormat.Png);
        File.WriteAllText(Path.Combine(directory, "font-probe.json"), JsonSerializer.Serialize(new
        {
            lowDensityCjk = AppleText.CjkFamilyName(false),
            highDensityCjk = AppleText.CjkFamilyName(true),
            thresholdDpi = AppleText.HighDensityDpi,
            registeredCjk = SkiaSharp.SKFontManager.Default.FontFamilies.Where(f => f.Contains("YaHei", StringComparison.OrdinalIgnoreCase) || f.Contains("Noto Sans SC", StringComparison.OrdinalIgnoreCase)).ToArray(),
            systemFallbackForHan = SkiaSharp.SKFontManager.Default.MatchCharacter(null, SkiaSharp.SKFontStyle.Normal, new[] { "zh-CN" }, '窗')?.FamilyName
        }));
        return AppleText.CjkFamilyName(false).Contains("YaHei", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }
}
