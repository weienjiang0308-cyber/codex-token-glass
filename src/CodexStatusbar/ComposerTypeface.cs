using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace CodexStatusbar;

internal sealed record ComposerTypeface(string? ReportedFamily, string Family, double? ReportedPoints, double Points, string Source)
{
    private static readonly HashSet<string> Installed = FontFamily.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public static ComposerTypeface Resolve(string? family, double? points)
    {
        var available = !string.IsNullOrWhiteSpace(family) && Installed.Contains(family);
        var fallback = SystemFonts.MessageBoxFont?.FontFamily.Name ?? FontFamily.GenericSansSerif.Name;
        // User requested a legible toolbar: follow larger native text, with a 13 DIP floor.
        var size = points is > 0 and <= 72 && double.IsFinite(points.Value) ? Math.Clamp(points.Value, 9.75, 14) : 9.75;
        return new(family, available ? family! : fallback, points, size,
            available ? "uia-toolbar" : family is null ? "system-fallback" : "uia-font-unavailable-fallback");
    }

    public static ComposerTypeface Read(AutomationElement? model)
    {
        if (model is null) return Resolve(null, null);
        try
        {
            var clock = Stopwatch.StartNew();
            var samples = new List<AutomationElement> { model };
            var child = TreeWalker.ControlViewWalker.GetFirstChild(model);
            for (var i = 0; child is not null && i < 4 && clock.ElapsedMilliseconds < 200; i++)
            { samples.Add(child); child = TreeWalker.ControlViewWalker.GetNextSibling(child); }
            // Read only the model selector's range, never the entire chat's mixed typography.
            for (AutomationElement? owner = model; owner is not null && clock.ElapsedMilliseconds < 250;
                owner = TreeWalker.ControlViewWalker.GetParent(owner))
            {
                if (!owner.TryGetCurrentPattern(TextPattern.Pattern, out var raw)) continue;
                var pattern = (TextPattern)raw;
                foreach (var sample in samples)
                {
                    try
                    {
                        var range = owner.Equals(sample) ? pattern.DocumentRange : pattern.RangeFromChild(sample);
                        if (range.GetAttributeValue(TextPattern.FontNameAttribute) is not string name || string.IsNullOrWhiteSpace(name)) continue;
                        var points = range.GetAttributeValue(TextPattern.FontSizeAttribute) is double size ? (double?)size : null;
                        return Resolve(name, points);
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                }
            }
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or ArgumentException) { }
        return Resolve(null, null);
    }
}
