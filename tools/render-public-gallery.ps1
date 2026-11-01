[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Executable, [Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$demoHome=Join-Path ([IO.Path]::GetTempPath()) ('CodexTokenGlass-public-demo-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $demoHome 'codex-token-glass') -Force|Out-Null
[IO.File]::WriteAllText((Join-Path $demoHome '.public-demo'),'synthetic only')
[IO.File]::WriteAllText((Join-Path $demoHome 'config.toml'),"model = 'demo-model'`nmodel_context_window = 500000`nmodel_auto_compact_token_limit = 450000`n[desktop]`nappearanceTheme = 'light'`n")
[IO.File]::WriteAllText((Join-Path $demoHome 'codex-token-glass\ui.json'),'{"language":"zh","glassTransparency":80}')
$previousHome=$env:CODEX_HOME
try {
    $env:CODEX_HOME=$demoHome
    $log=Join-Path $demoHome 'render.log';$err=Join-Path $demoHome 'render.err'
    $arguments=@('--public-gallery',('"'+[IO.Path]::GetFullPath($OutputDirectory)+'"'))
    $process=Start-Process -FilePath ([IO.Path]::GetFullPath($Executable)) -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $log -RedirectStandardError $err
    if($process.ExitCode -ne 0){Get-Content -LiteralPath $err;throw "Gallery renderer failed with exit $($process.ExitCode)"}
    Get-Content -LiteralPath $log
} finally {
    $env:CODEX_HOME=$previousHome
    $resolved=[IO.Path]::GetFullPath($demoHome)
    $tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    if(-not $resolved.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -or -not ([IO.Path]::GetFileName($resolved)).StartsWith('CodexTokenGlass-public-demo-')){throw 'Refusing cleanup outside synthetic temp home'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
