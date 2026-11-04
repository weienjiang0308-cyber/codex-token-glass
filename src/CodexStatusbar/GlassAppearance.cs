namespace CodexStatusbar;
internal static class GlassAppearance
{
    public static float ControlTint(int clarity,bool hovered,bool prominent=false)=>prominent
        ?96-Math.Clamp(clarity,0,100)*.16f:Math.Max(0,CodexUi.GlassTint(clarity)-(hovered?12:0));
    // Clarity, not physical window opacity; foreground text remains opaque.
    public static int ReadingPlateAlpha(int clarity) => !DesktopMaterial.TransparencyEnabled
        || SystemInformation.HighContrast ? 255 : (int)Math.Round(220-1.05*Math.Clamp(clarity,0,100));
}
