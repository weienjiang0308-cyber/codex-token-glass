using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Drawing.Text;
using System.Reflection;

namespace CodexStatusbar;

internal static class CodexUi
{
    private static DateTime _readAt;
    private static Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private static Preferences? _preferences;
    private sealed class Preferences
    {
        public Preferences(){}
        public string Language { get; set; } = "auto";
        public int GlassTransparency { get; set; } = 70;
    }
    private static Preferences Settings
    {
        get
        {
            if(_preferences is not null)return _preferences;
            var source=File.Exists(PreferencesPath)?PreferencesPath:LegacyPreferencesPath;
            try{_preferences=JsonSerializer.Deserialize<Preferences>(File.ReadAllText(source),new JsonSerializerOptions{PropertyNameCaseInsensitive=true});}
            catch(Exception e)when(e is IOException or JsonException or UnauthorizedAccessException){}
            return _preferences??=new();
        }
    }
    private static void SavePreferences()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
        var temp=PreferencesPath+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(Settings,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
        File.Move(temp,PreferencesPath,true);
    }
    public static int GlassTransparency
    {
        get=>Math.Clamp(Settings.GlassTransparency,0,100);
        set{Settings.GlassTransparency=Math.Clamp(value,0,100);SavePreferences();}
    }
    internal static void PreviewGlassTransparency(int value)=>Settings.GlassTransparency=Math.Clamp(value,0,100);
    internal static void CommitAppearance()=>SavePreferences();
    internal static float GlassTint(int transparency)=>Math.Clamp(100-transparency,0,100);
    internal static (string Language,int Transparency) ParseAppearance(string json)
    {
        var p=JsonSerializer.Deserialize<Preferences>(json,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??new();
        return(p.Language,Math.Clamp(p.GlassTransparency,0,100));
    }
    private static readonly string PreferencesPath = Environment.GetEnvironmentVariable("CODEX_STATUSBAR_UI_SETTINGS") ?? Path.Combine(
        Path.GetDirectoryName(CodexThemeSource.DefaultConfigPath)!,"codex-token-glass","ui.json");
    private static readonly string LegacyPreferencesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexStatusbar", "ui.json");

    private static readonly PrivateFontCollection ModernFonts = LoadFonts();
    private static readonly HashSet<string> InstalledFonts = System.Drawing.FontFamily.Families
        .Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public static string FontName => PreferredFont("Segoe UI Variable Text", "Segoe UI");
    public static string DisplayFontName => PreferredFont("Segoe UI Variable Display", "Segoe UI");
    public static string ChineseFontName => PreferredFont("Noto Sans SC", "Microsoft YaHei UI");
    private static string PreferredFont(params string[] names) => names.FirstOrDefault(InstalledFonts.Contains) ?? "Segoe UI";

    private static PrivateFontCollection LoadFonts()
    {
        var fonts = new PrivateFontCollection();
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames().Where(x => x.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var buffer = new MemoryStream(); stream.CopyTo(buffer);
            var bytes = buffer.ToArray(); var pointer = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            // Font data must remain alive for the process's private GDI/GDI+ font collections.
            fonts.AddMemoryFont(pointer, bytes.Length);
            uint count = 0; AddFontMemResourceEx(pointer, (uint)bytes.Length, IntPtr.Zero, ref count);
        }
        return fonts;
    }

    public static Font Font(float size, bool semibold = false)
    {
        var name = size >= 18 ? DisplayFontName : FontName;
        if (semibold && name.StartsWith("Segoe UI Variable", StringComparison.Ordinal))
        {
            name = size >= 18 ? PreferredFont("Segoe UI Variable Display Semib", "Segoe UI Variable Display", "Segoe UI")
                : PreferredFont("Segoe UI Variable Text Semibold", "Segoe UI Variable Text", "Segoe UI");
            return new Font(name,size,FontStyle.Regular,GraphicsUnit.Point);
        }
        return new Font(name,size,semibold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Point);
    }

    /// <summary>iOS-style UI font: Inter for Latin and digits (closest open substitute for SF Pro);
    /// AppleText falls back to Noto Sans SC / Microsoft YaHei UI for CJK glyphs.</summary>
    public static Font UiFont(float size, bool semibold = false)
    {
        var families = ModernFonts.Families;
        var family = semibold
            ? families.FirstOrDefault(f => f.Name.Contains("Inter", StringComparison.OrdinalIgnoreCase) && f.Name.Contains("Semi", StringComparison.OrdinalIgnoreCase))
            : families.FirstOrDefault(f => f.Name.Equals("Inter", StringComparison.OrdinalIgnoreCase));
        if (family is not null)
        {
            var style = family.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold;
            return new Font(family, size, style, GraphicsUnit.Point);
        }
        return Font(size, semibold);
    }

    /// <summary>Bold UI font for iOS large titles (34 pt Bold).</summary>
    public static Font UiBoldFont(float size)
    {
        var family = ModernFonts.Families.FirstOrDefault(f => f.Name.Equals("Inter", StringComparison.OrdinalIgnoreCase));
        return family is not null && family.IsStyleAvailable(FontStyle.Bold)
            ? new Font(family, size, FontStyle.Bold, GraphicsUnit.Point)
            : UiFont(size, semibold: true);
    }

    public static Font FontForText(string text, float size, bool semibold = false)
    {
        // Restore the original Windows UI and bundled CJK font selection.
        // Noto Sans SC is the explicit fallback; never let CJK silently fall back to SimSun.
        if (text.Any(c => c is >= '\u2e80' and <= '\u9fff') || (text.Length == 0 && !English))
        {
            var name = ChineseFontName;
            if (semibold && name == "Noto Sans SC" && InstalledFonts.Contains("Noto Sans SC Medium"))
                return new Font("Noto Sans SC Medium",size,FontStyle.Regular,GraphicsUnit.Point);
            return new Font(name,size,semibold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Point);
        }
        return Font(size, semibold);
    }

    internal static bool IsEmphasized(Font font) => font.Bold || font.FontFamily.Name.Contains("Semi", StringComparison.OrdinalIgnoreCase);

    private static string PickFont(params string[] candidates)
    {
        var names = System.Drawing.FontFamily.Families.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates.FirstOrDefault(names.Contains) ?? "Segoe UI";
    }

    public static string Language
    {
        get
        {
            return Settings.Language;
        }
        set
        {
            Settings.Language=value;SavePreferences();
        }
    }

    internal static string? DemoLanguage {get;set;}
    public static bool English => IsEnglish(DemoLanguage??Language, Get("desktop.localeOverride"));
    internal static bool IsEnglish(string language, string? codexLocale) =>
        !(language == "zh" || (language == "auto" &&
            (string.IsNullOrEmpty(codexLocale) || codexLocale == "system" ? CultureInfo.CurrentUICulture.Name : codexLocale)
            .StartsWith("zh", StringComparison.OrdinalIgnoreCase)));
    public static string T(string chinese, string english) => English ? english : chinese;

    private static string? Get(string key)
    {
        Refresh();
        return _values.GetValueOrDefault(key);
    }

    private static void Refresh()
    {
        if (DateTime.UtcNow - _readAt < TimeSpan.FromSeconds(2)) return;
        _readAt = DateTime.UtcNow;
        try { _values = Parse(File.ReadAllLines(CodexThemeSource.DefaultConfigPath)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    internal static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        foreach (var line in lines)
        {
            var text = line.Trim();
            if (text.StartsWith('[')) { section = text.Trim('[', ']', ' ', '\t'); continue; }
            if (section != "desktop" && !section.StartsWith("desktop.appearance", StringComparison.OrdinalIgnoreCase)) continue;
            var match = Regex.Match(text, "^(?<key>[A-Za-z]+)\\s*=\\s*[\"'](?<value>[^\"']*)[\"']");
            if (match.Success) result[section + "." + match.Groups["key"].Value] = match.Groups["value"].Value;
        }
        return result;
    }

    public static OverlayThemePalette Palette(OverlayThemeKind kind)
    {
        Refresh();
        return PaletteFrom(_values, kind);
    }

    internal static OverlayThemePalette PaletteFrom(IReadOnlyDictionary<string, string> values, OverlayThemeKind kind)
    {
        var prefix = kind == OverlayThemeKind.Light ? "desktop.appearanceLightChromeTheme." : "desktop.appearanceDarkChromeTheme.";
        var fallback = OverlayThemePalette.For(kind);
        Color ColorValue(string key, Color defaultValue)
        {
            if (!values.TryGetValue(prefix + key, out var text) || !Regex.IsMatch(text, "^#[0-9a-fA-F]{6}$")) return defaultValue;
            return ColorTranslator.FromHtml(text);
        }
        var surface = ColorValue("surface", fallback.Background);
        var ink = ColorValue("ink", fallback.Value);
        var accent = ColorValue("accent", ink);
        return fallback with
        {
            Background = surface, Value = ink, Accent = ink,
            Label = Mix(surface, ink, .72), Border = Mix(surface, ink, .13),
            Divider = Mix(surface, ink, .10), ProgressTrack = Mix(surface, ink, .08),
            ProgressStart = Mix(surface, ink, .55), ProgressEnd = Mix(surface, ink, .55),
            TargetHighlight = accent
        };
    }

    internal static Color Mix(Color background, Color foreground, double ratio) => Color.FromArgb(
        (int)Math.Round(background.R + (foreground.R - background.R) * ratio),
        (int)Math.Round(background.G + (foreground.G - background.G) * ratio),
        (int)Math.Round(background.B + (foreground.B - background.B) * ratio));

    public static CodexThemeKind DetectVisibleTheme(ComposerDockSnapshot dock)
    {
        if (dock.ComposerRect.IsEmpty) return CodexThemeKind.Unknown;
        // The input card's left inset is outside the overlay and avoids prompt text. Three points down
        // that inset are read, and the middle brightness decides, so one odd pixel cannot.
        var x = dock.ComposerRect.Left + 6;
        var rows = new[] { 8, 12, 16 }.Select(offset => dock.ComposerRect.Top + offset)
            .Where(y => y < dock.ComposerRect.Bottom).ToArray();
        if (rows.Length == 0) return CodexThemeKind.Unknown;
        // Screen pixels only describe Codex where Codex is what is showing. If another window is on
        // top at a sample point (a screenshot tool's dim layer, a dialog, this program's own panel),
        // the colour there is not Codex's and must not be read as its theme.
        if (dock.WindowHandle != 0)
        {
            var host = GetAncestor(new IntPtr(dock.WindowHandle), GaRoot);
            foreach (var y in rows)
            {
                var top = WindowFromPoint(new NativePoint { X = x, Y = y });
                if (top == IntPtr.Zero || GetAncestor(top, GaRoot) != host) return CodexThemeKind.Unknown;
            }
        }
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return CodexThemeKind.Unknown;
        try
        {
            var levels = new List<double>(rows.Length);
            foreach (var y in rows)
            {
                var pixel = GetPixel(dc, x, y);
                if (pixel == uint.MaxValue) return CodexThemeKind.Unknown;
                levels.Add(.2126 * (pixel & 255) + .7152 * ((pixel >> 8) & 255) + .0722 * ((pixel >> 16) & 255));
            }
            levels.Sort();
            return levels[levels.Count / 2] >= 128 ? CodexThemeKind.Light : CodexThemeKind.Dark;
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    private const uint GaRoot = 2;
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("gdi32.dll")] private static extern IntPtr AddFontMemResourceEx(IntPtr data, uint length, IntPtr reserved, ref uint count);
}
