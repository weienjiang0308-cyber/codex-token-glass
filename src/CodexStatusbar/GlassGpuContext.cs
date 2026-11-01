using System.Runtime.InteropServices;
using SkiaSharp;

namespace CodexStatusbar;

/// <summary>Private, invisible OpenGL context for optical passes; never changes another app's renderer.</summary>
internal sealed class GlassGpuContext : IDisposable
{
    private readonly NativeWindow _window = new();
    private IntPtr _dc;
    private IntPtr _gl;
    public GRContext Context { get; private set; } = null!;
    public static GlassGpuContext? TryCreate()
    {
        var gpu = new GlassGpuContext();
        try
        {
            gpu._window.CreateHandle(new CreateParams { Caption="Codex glass renderer", X=-32000,Y=-32000,
                Width=1,Height=1, Style=unchecked((int)0x80000000), ClassStyle=0x20 });
            gpu._dc = GetDC(gpu._window.Handle);
            var descriptor = new PixelFormat { Size=(ushort)Marshal.SizeOf<PixelFormat>(),Version=1,
                Flags=0x4|0x20|0x1, ColorBits=32,AlphaBits=8,DepthBits=24,StencilBits=8 };
            var format = ChoosePixelFormat(gpu._dc, ref descriptor);
            if (format==0 || !SetPixelFormat(gpu._dc,format,ref descriptor)) throw new InvalidOperationException("No OpenGL pixel format");
            gpu._gl = wglCreateContext(gpu._dc);
            if (gpu._gl==IntPtr.Zero || !gpu.MakeCurrent()) throw new InvalidOperationException("No current OpenGL context");
            gpu.Context = GRContext.CreateGl() ?? throw new InvalidOperationException("No Skia GPU context");
            return gpu;
        }
        catch { gpu.Dispose(); return null; }
    }
    public bool MakeCurrent() => wglMakeCurrent(_dc,_gl);
    public void Dispose()
    {
        if (_gl!=IntPtr.Zero) { MakeCurrent(); Context?.Dispose(); wglMakeCurrent(IntPtr.Zero,IntPtr.Zero); wglDeleteContext(_gl);_gl=IntPtr.Zero; }
        if (_dc!=IntPtr.Zero) { ReleaseDC(_window.Handle,_dc);_dc=IntPtr.Zero; }
        if (_window.Handle!=IntPtr.Zero) _window.DestroyHandle();
    }
    [StructLayout(LayoutKind.Sequential)] private struct PixelFormat
    {
        public ushort Size,Version;public uint Flags;
        public byte PixelType,ColorBits,RedBits,RedShift,GreenBits,GreenShift,BlueBits,BlueShift,AlphaBits,AlphaShift,
            AccumBits,AccumRedBits,AccumGreenBits,AccumBlueBits,AccumAlphaBits,DepthBits,StencilBits,AuxBuffers,LayerType,Reserved;
        public uint LayerMask,VisibleMask,DamageMask;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h,IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int ChoosePixelFormat(IntPtr dc,ref PixelFormat descriptor);
    [DllImport("gdi32.dll")] private static extern bool SetPixelFormat(IntPtr dc,int format,ref PixelFormat descriptor);
    [DllImport("opengl32.dll")] private static extern IntPtr wglCreateContext(IntPtr dc);
    [DllImport("opengl32.dll")] private static extern bool wglMakeCurrent(IntPtr dc,IntPtr context);
    [DllImport("opengl32.dll")] private static extern bool wglDeleteContext(IntPtr context);
}
