param([string]$Executable = (Join-Path $env:LOCALAPPDATA 'Programs\CodexStatusbar\CodexStatusbar.exe'))
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Executable)) { throw 'CodexStatusbar executable not found.' }
$scheduler = New-Object -ComObject 'Schedule.Service'
$scheduler.Connect()
$definition = $scheduler.NewTask(0)
$definition.RegistrationInfo.Description = 'Independent per-user Codex token status bar watcher.'
$definition.Principal.UserId = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$definition.Principal.LogonType = 3
$definition.Principal.RunLevel = 0
$definition.Settings.Enabled = $true
$definition.Settings.AllowDemandStart = $true
$definition.Settings.DisallowStartIfOnBatteries = $false
$definition.Settings.StopIfGoingOnBatteries = $false
$definition.Settings.ExecutionTimeLimit = 'PT0S'
$definition.Settings.MultipleInstances = 2
$definition.Settings.RestartCount = 3
$definition.Settings.RestartInterval = 'PT1M'
$trigger = $definition.Triggers.Create(9)
$trigger.UserId = $definition.Principal.UserId
$action = $definition.Actions.Create(0)
$action.Path = (Resolve-Path -LiteralPath $Executable).Path
$action.Arguments = '--background'
$action.WorkingDirectory = Split-Path $Executable
$task = $scheduler.GetFolder('\').RegisterTaskDefinition('CodexStatusbar Watcher', $definition, 6, $definition.Principal.UserId, $null, 3)
$task.Run($null) | Out-Null
