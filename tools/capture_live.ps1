param(
    [ValidateSet("panel", "settings", "stats", "none")]
    [string]$Open = "panel",
    [Parameter(Mandatory = $true)][string]$Output,
    [string]$Exe = (Join-Path $env:LOCALAPPDATA "Programs\CodexStatusbar\CodexStatusbar.exe"),
    [int]$DelayMs = 900,
    [int]$Margin = 24,
    [switch]$CloseAfter
)

# Opens a surface in the running Codex Token Glass instance through its local control pipe and
# captures the real on-screen pixels (including layered windows) of its largest visible window.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class LiveWindows {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr lparam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    public static List<RECT> For(uint pid) {
        var result = new List<RECT>();
        EnumWindows((hwnd, l) => {
            uint owner; GetWindowThreadProcessId(hwnd, out owner);
            RECT r;
            if (owner == pid && IsWindowVisible(hwnd) && GetWindowRect(hwnd, out r) && r.Right - r.Left > 40 && r.Bottom - r.Top > 40) result.Add(r);
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
"@

$commands = @{ panel = "--control-panel"; settings = "--control-settings"; stats = "--control-stats" }
if ($Open -ne "none") {
    $process = Start-Process -FilePath $Exe -ArgumentList $commands[$Open] -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw "control command failed: $($process.ExitCode)" }
    Start-Sleep -Milliseconds $DelayMs
}

$running = Get-Process -Name "CodexStatusbar" -ErrorAction Stop | Where-Object { $_.Path -eq $Exe } | Select-Object -First 1
if (-not $running) { $running = Get-Process -Name "CodexStatusbar" | Select-Object -First 1 }
$rects = [LiveWindows]::For([uint32]$running.Id)
if ($rects.Count -eq 0) { throw "no visible CodexStatusbar window" }
$target = $rects | Sort-Object { ($_.Right - $_.Left) * ($_.Bottom - $_.Top) } -Descending | Select-Object -First 1
$screen = [System.Windows.Forms.Screen]::FromPoint([System.Drawing.Point]::new($target.Left, $target.Top)).Bounds
$left = [Math]::Max($screen.Left, $target.Left - $Margin)
$top = [Math]::Max($screen.Top, $target.Top - $Margin)
$right = [Math]::Min($screen.Right, $target.Right + $Margin)
$bottom = [Math]::Min($screen.Bottom, $target.Bottom + $Margin)
$bitmap = New-Object System.Drawing.Bitmap ($right - $left), ($bottom - $top)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($left, $top, 0, 0, $bitmap.Size, [System.Drawing.CopyPixelOperation]::SourceCopy)
$graphics.Dispose()
$bitmap.Save($Output, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
Write-Output "captured $Open $($right - $left)x$($bottom - $top) at $left,$top -> $Output"

if ($CloseAfter) {
    $close = @{ panel = "--control-panel"; settings = "--control-close-settings"; stats = "--control-close-settings" }
    if ($Open -ne "none") { Start-Process -FilePath $Exe -ArgumentList $close[$Open] -Wait -WindowStyle Hidden | Out-Null }
}
