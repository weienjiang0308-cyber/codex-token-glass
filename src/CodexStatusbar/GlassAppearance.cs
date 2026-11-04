namespace CodexStatusbar;
internal static class GlassAppearance
{
    public static float ControlTint(int clarity,bool hovered,bool prominent=false)=>prominent
        ?96-Math.Clamp(clarity,0,100)*.16f:Math.Max(0,CodexUi.GlassTint(clarity)
            -(hovered ? 48*BackdropFraction(clarity)*(1-BackdropFraction(clarity)) : 0));
    private static float BackdropFraction(int clarity) => Math.Clamp(clarity,0,100)/100f;
    public static float BackdropVisibility(int clarity)
    { var value=BackdropFraction(clarity);return 1-(1-value)*(1-value); }
    // Clarity, not physical window opacity; foreground text remains opaque.
    public static int ReadingPlateAlpha(int clarity) => !DesktopMaterial.TransparencyEnabled
        || SystemInformation.HighContrast ? 255 : (int)Math.Round(220-1.05*Math.Clamp(clarity,0,100));
}
