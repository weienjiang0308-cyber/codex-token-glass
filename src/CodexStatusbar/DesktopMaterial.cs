using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CodexStatusbar;

internal static class DesktopMaterial
{
    [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy
    { public int State; public int Flags; public uint Color; public int Animation; }
    [StructLayout(LayoutKind.Sequential)] private struct CompositionData
    { public int Attribute; public IntPtr Data; public int Size; }

    public static bool TransparencyEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("EnableTransparency") is not int enabled || enabled != 0;
            }
            catch { return false; }
        }
    }

    internal static uint Tint(Color color, byte alpha) =>
        ((uint)alpha << 24) | ((uint)color.B << 16) | ((uint)color.G << 8) | color.R;

    public static bool Apply(IntPtr window, Color tint, bool enabled)
    {
        if (window == IntPtr.Zero) return false;
        enabled &= TransparencyEnabled;
        var policy = new AccentPolicy { State = enabled ? 4 : 0, Flags = 2, Color = Tint(tint, 186) };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        try
        {
            Marshal.StructureToPtr(policy, memory, false);
            var data = new CompositionData { Attribute = 19, Data = memory, Size = Marshal.SizeOf<AccentPolicy>() };
            return SetWindowCompositionAttribute(window, ref data) != 0 && enabled;
        }
        catch (EntryPointNotFoundException) { return false; }
        finally { Marshal.FreeHGlobal(memory); }
    }
    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr window, ref CompositionData data);
}
