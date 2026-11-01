[CmdletBinding()]
param([string]$Configuration='Release',[string]$RuntimeIdentifier='win-x64',[switch]$SkipTest)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot;$project=Join-Path $root 'src\CodexStatusbar\CodexStatusbar.csproj';$dist=Join-Path $root 'dist'
dotnet publish $project -c $Configuration -r $RuntimeIdentifier --self-contained true -o $dist
if($LASTEXITCODE -ne 0){throw 'Build failed'}
if(-not $SkipTest){
    $log=Join-Path $dist 'self-test.txt';$err=Join-Path $dist 'self-test.err.txt'
    $test=Start-Process -FilePath (Join-Path $dist 'CodexStatusbar.exe') -ArgumentList @('--self-test',('"'+(Join-Path $root 'fixtures')+'"')) -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $log -RedirectStandardError $err
    Get-Content -LiteralPath $log -Tail 3
    if($test.ExitCode -ne 0){throw 'Self-tests failed'}
}
Write-Output ('Built: '+(Join-Path $dist 'CodexStatusbar.exe'))
