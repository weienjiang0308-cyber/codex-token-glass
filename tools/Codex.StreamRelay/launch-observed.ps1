param(
    [Parameter(Mandatory)][string]$DesktopExe,
    [Parameter(Mandatory)][string]$OriginalCodexExe,
    [Parameter(Mandatory)][string]$RelayExe
)
$ErrorActionPreference = 'Stop'
foreach ($file in @($DesktopExe,$OriginalCodexExe,$RelayExe)) {
    if (-not [IO.Path]::IsPathFullyQualified($file) -or -not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Executable does not exist: $file" }
}
if (Get-Process ChatGPT -ErrorAction SilentlyContinue) {
    throw 'Close Codex normally first. This launcher does not terminate active work.'
}
$launch = [Diagnostics.ProcessStartInfo]::new($DesktopExe)
$launch.UseShellExecute = $false
$launch.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$launch.Environment['CODEX_CLI_PATH'] = $RelayExe
$launch.Environment['CODEX_RAW_REAL_EXE'] = $OriginalCodexExe
[Diagnostics.Process]::Start($launch) | Out-Null
# Variables apply only to this launched process; normal app launch is the rollback.
