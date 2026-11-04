using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CodexStatusbar;

/// <summary>Premultiplied-alpha window surface: smooth edges without a binary window Region.</summary>
internal static class LayeredSurface
{
    public static void Present(Form form, Bitmap bitmap)
    {
        var dc = GetDC(IntPtr.Zero); if (dc == IntPtr.Zero) return;
        var memory = CreateCompatibleDC(dc); var handle = bitmap.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memory, handle);
        try
        {
            var target = new NativePoint(form.Left, form.Top); var source = new NativePoint(0, 0);
            var size = new NativeSize(bitmap.Width, bitmap.Height); var blend = new Blend { Alpha = 255, Format = 1 };
            UpdateLayeredWindow(form.Handle, dc, ref target, ref size, memory, ref source, 0, ref blend, 2);
        }
        finally { SelectObject(memory, old); DeleteObject(handle); DeleteDC(memory); ReleaseDC(IntPtr.Zero, dc); }
    }
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var d = Math.Max(1, Math.Min(Math.Min(r.Width, r.Height), radius * 2));
        var path = new GraphicsPath(); path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right-d, r.Top, d, d, 270, 90); path.AddArc(r.Right-d, r.Bottom-d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom-d, d, d, 90, 90); path.CloseFigure(); return path;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X=x,Y=y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize(int x, int y) { public int X=x,Y=y; }
    [StructLayout(LayoutKind.Sequential, Pack=1)] private struct Blend { public byte Op, Flags, Alpha, Format; }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h,IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr h,IntPtr screen,ref NativePoint pos,
        ref NativeSize size,IntPtr dc,ref NativePoint source,uint key,ref Blend blend,uint flags);
}
