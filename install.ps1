[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [string]$Repository='weienjiang0308-cyber/codex-token-glass',
    [ValidateSet('zh','en','auto')][string]$Language='zh',
    [ValidateRange(0,100)][int]$GlassTransparency=80,
    [string]$PackagePath,
    [switch]$FromSource,
    [string]$SourcePath=$PSScriptRoot,
    [string]$InstallDirectory=(Join-Path $env:LOCALAPPDATA 'Programs\CodexTokenGlass')
)
$ErrorActionPreference='Stop'
[Net.ServicePointManager]::SecurityProtocol=[Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$taskName='Codex Token Glass Watcher'
if($WhatIfPreference){
    [void]$PSCmdlet.ShouldProcess($InstallDirectory,'Download/build, verify, install and configure Codex Token Glass')
    Write-Output "Plan: $Repository; language=$Language; glass=$GlassTransparency; user startup task=$taskName"
    Write-Output 'Plan: preserve Codex model, context window, headroom, authentication and existing conversations.'
    return
}
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('CodexTokenGlass-install-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force|Out-Null
$headers=@{'User-Agent'='CodexTokenGlass-Installer';'Accept'='application/vnd.github+json'}
function Run-Hidden([string]$File,[string[]]$Arguments,[string]$Name){
    $stdout=Join-Path $scratch ($Name+'.out.txt');$stderr=Join-Path $scratch ($Name+'.err.txt')
    $p=Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if($p.ExitCode -ne 0){$message=Get-Content -LiteralPath $stderr -Raw -ErrorAction SilentlyContinue;throw "$Name failed with exit $($p.ExitCode): $message"}
}
if($FromSource){
    $sdk=Get-Command dotnet -ErrorAction SilentlyContinue
    $hasTen=$false
    if($sdk){$hasTen=((& $sdk.Source --list-sdks) -match '^10\.') -ne $null}
    if(-not $hasTen){
        Write-Output 'Downloading a verified private .NET 10 SDK for this build; system SDKs are unchanged.'
        $metadata=Invoke-RestMethod -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json' -Headers $headers
        $sdkFile=$metadata.releases[0].sdk.files|Where-Object {$_.rid -eq 'win-x64' -and $_.name -like '*.zip'}|Select-Object -First 1
        $archive=Join-Path $scratch 'dotnet-sdk.zip'
        Invoke-WebRequest -Uri $sdkFile.url -OutFile $archive -UseBasicParsing
        if((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $sdkFile.hash){throw 'Microsoft SDK checksum mismatch'}
        $sdkDir=Join-Path $scratch 'sdk';Expand-Archive -LiteralPath $archive -DestinationPath $sdkDir
        $dotnet=Join-Path $sdkDir 'dotnet.exe'
    }else{$dotnet=$sdk.Source}
    $stage=Join-Path $scratch 'package';New-Item -ItemType Directory -Path $stage -Force|Out-Null
    & $dotnet publish (Join-Path $SourcePath 'src\CodexStatusbar\CodexStatusbar.csproj') -c Release -r win-x64 --self-contained true -o $stage
    if($LASTEXITCODE -ne 0){throw 'Source build failed'}
    Copy-Item -LiteralPath (Join-Path $SourcePath 'fixtures') -Destination $stage -Recurse
    foreach($file in @('LICENSE','LICENSE-codex-token-overlay','THIRD-PARTY-NOTICES.md')){Copy-Item -LiteralPath (Join-Path $SourcePath $file) -Destination $stage}
}else{
    if(-not $PackagePath){
        $release=Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases/latest" -Headers $headers
        $asset=$release.assets|Where-Object {$_.name -eq 'CodexTokenGlass-win-x64.zip'}|Select-Object -First 1
        $sums=$release.assets|Where-Object {$_.name -eq 'SHA256SUMS.txt'}|Select-Object -First 1
        if(-not $asset -or -not $sums){throw 'Release assets are incomplete. Use -FromSource for a reproducible local build.'}
        $PackagePath=Join-Path $scratch $asset.name;$sumPath=Join-Path $scratch 'SHA256SUMS.txt'
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $PackagePath -UseBasicParsing
        Invoke-WebRequest -Uri $sums.browser_download_url -OutFile $sumPath -UseBasicParsing
        $match=Get-Content -LiteralPath $sumPath|Where-Object {$_ -match '\s+CodexTokenGlass-win-x64\.zip$'}|Select-Object -First 1
        if(-not $match){throw 'ZIP checksum is missing from the release manifest'}
        $expected=($match -split '\s+')[0]
        if((Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash -ne $expected){throw 'Release checksum mismatch'}
    }
    $stage=Join-Path $scratch 'package';Expand-Archive -LiteralPath $PackagePath -DestinationPath $stage
}
$candidate=Join-Path $stage 'CodexStatusbar.exe'
if(-not (Test-Path -LiteralPath $candidate)){throw 'Package does not contain CodexStatusbar.exe'}
Write-Output 'Running synthetic self-tests before installation.'
$testLog=Join-Path $scratch 'self-test.out.txt';$testErr=Join-Path $scratch 'self-test.err.txt'
$test=Start-Process -FilePath $candidate -ArgumentList @('--self-test',('"'+(Join-Path $stage 'fixtures')+'"')) -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $testLog -RedirectStandardError $testErr
if($test.ExitCode -ne 0){throw ('Self-test failed. Logs: '+$scratch)}
Get-Content -LiteralPath $testLog -Tail 3
if(-not $PSCmdlet.ShouldProcess($InstallDirectory,'Install Codex Token Glass and its per-user startup task')){return}
$target=[IO.Path]::GetFullPath($InstallDirectory)
$existing=Get-Process CodexStatusbar -ErrorAction SilentlyContinue
if($existing){
    $same=$existing|Where-Object {$_.Path -eq (Join-Path $target 'CodexStatusbar.exe')}
    if(@($same).Count -ne @($existing).Count){throw 'A different CodexStatusbar installation is running. Exit that version from its tray menu before switching.'}
    $oldExe=Join-Path $target 'CodexStatusbar.exe'
    Start-Process -FilePath $oldExe -ArgumentList '--control-exit' -WindowStyle Hidden -Wait|Out-Null
    foreach($process in @($existing)){if(-not $process.WaitForExit(8000)){throw 'Please use the existing tray Exit command, then rerun installation.'}}
}
New-Item -ItemType Directory -Path $target -Force|Out-Null
Copy-Item -Path (Join-Path $stage '*') -Destination $target -Recurse -Force
$homeDir=if($env:CODEX_HOME){$env:CODEX_HOME}else{Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex'}
$preferencesDir=Join-Path $homeDir 'codex-token-glass';New-Item -ItemType Directory -Path $preferencesDir -Force|Out-Null
$preferences=Join-Path $preferencesDir 'ui.json'
$values=@{}
if(Test-Path -LiteralPath $preferences){$prior=Get-Content -LiteralPath $preferences -Raw|ConvertFrom-Json;foreach($property in $prior.PSObject.Properties){$values[$property.Name]=$property.Value}}
if(-not (Test-Path -LiteralPath $preferences) -or $PSBoundParameters.ContainsKey('Language')){$values.language=$Language}
if(-not (Test-Path -LiteralPath $preferences) -or $PSBoundParameters.ContainsKey('GlassTransparency')){$values.glassTransparency=$GlassTransparency}
[IO.File]::WriteAllText($preferences,($values|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$scheduler=New-Object -ComObject 'Schedule.Service';$scheduler.Connect();$definition=$scheduler.NewTask(0)
$definition.RegistrationInfo.Description='Codex Token Glass: independent local token monitor.'
$definition.Principal.UserId=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$definition.Principal.LogonType=3;$definition.Principal.RunLevel=0
$definition.Settings.AllowDemandStart=$true;$definition.Settings.DisallowStartIfOnBatteries=$false
$definition.Settings.StopIfGoingOnBatteries=$false;$definition.Settings.ExecutionTimeLimit='PT0S'
$definition.Settings.MultipleInstances=2;$definition.Settings.RestartCount=3;$definition.Settings.RestartInterval='PT1M'
$trigger=$definition.Triggers.Create(9);$trigger.UserId=$definition.Principal.UserId
$action=$definition.Actions.Create(0);$action.Path=Join-Path $target 'CodexStatusbar.exe';$action.Arguments='--background';$action.WorkingDirectory=$target
$registered=$scheduler.GetFolder('\').RegisterTaskDefinition($taskName,$definition,6,$definition.Principal.UserId,$null,3)
$registered.Run($null)|Out-Null
Write-Output ('Installed: '+$target)
Write-Output ('Appearance: '+$preferences)
Write-Output ('Startup task: '+$taskName)
Write-Output 'Open Codex to see the status bar. Model/context settings and credentials were not modified.'
