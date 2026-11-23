# Uninstall.ps1
# Version: 1.1.0
# Changelog:
#   1.1.0 - Closes the settings window too. Settings now live in
#           %ProgramData%\RtcCallMonitor\config and are simply left there
#           (unless you choose to delete them); the next install uses them.
#   1.0.0 - Initial version. Removes the RTC Call Monitor service, the tray app's
#           sign-in entry and the program files. Asks whether to keep the
#           settings and logs; kept settings are restored by the next install.
#
# Run it with Uninstall.cmd (double-click). Works in Windows PowerShell 5.1 and PowerShell 7.

[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'RtcCallMonitor'),
    [switch]$Elevated,
    [switch]$DeleteData
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'RTC Call Monitor'
$TrayProcess = 'RtcCallMonitor.Tray'
$SettingsProcess = 'RtcCallMonitor.Settings'
$RunKey      = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunValue    = 'RtcCallMonitorTray'
$DataDir     = Join-Path $env:ProgramData 'RtcCallMonitor'

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok($text)   { Write-Host "    $text" -ForegroundColor Green }

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Uninstall-Elevated {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service) {
        Write-Step "Removing the service '$ServiceName'"
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $ServiceName -Force
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
        & sc.exe delete "$ServiceName" | Out-Null
        Write-Ok "removed"
    }

    Get-Process -Name $TrayProcess, $SettingsProcess -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500

    if (Test-Path $InstallDir) {
        $local = Join-Path $InstallDir 'appsettings.Local.json'
        if ((Test-Path $local) -and -not $DeleteData) {
            New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
            Copy-Item $local (Join-Path $DataDir 'appsettings.Local.json.bak') -Force
            Write-Ok "settings left in the program folder are kept in $DataDir\appsettings.Local.json.bak (the next install restores them)"
        }
        Write-Step "Deleting $InstallDir"
        Remove-Item -Recurse -Force $InstallDir
        Write-Ok "deleted"
    }

    if ($DeleteData -and (Test-Path $DataDir)) {
        Write-Step "Deleting the settings, provider lists and logs in $DataDir"
        Remove-Item -Recurse -Force $DataDir
        Write-Ok "deleted"
    } elseif (Test-Path (Join-Path $DataDir 'config')) {
        Write-Ok "your settings are kept in $(Join-Path $DataDir 'config') (the next install uses them)"
    }
}

if ($Elevated) {
    try {
        Uninstall-Elevated
        Write-Host ""
        Write-Host "Done. This window closes in 5 seconds." -ForegroundColor Green
        Start-Sleep -Seconds 5
    } catch {
        Write-Host "Uninstall failed: $($_.Exception.Message)" -ForegroundColor Red
        Read-Host "Press Enter to close"
    }
    exit
}

Write-Host ""
Write-Host "RTC Call Monitor - uninstall" -ForegroundColor White
Write-Host ""
$answer = Read-Host "Also delete your settings, your provider lists and the logs in $DataDir? [y/N]"
$delete = $answer -match '^[Yy]'

Write-Step "Removing the tray app's sign-in entry for $env:USERNAME"
Remove-ItemProperty -Path $RunKey -Name $RunValue -ErrorAction SilentlyContinue
Get-Process -Name $TrayProcess, $SettingsProcess -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Write-Ok "done"

$argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'), '-Elevated', '-InstallDir', ('"' + $InstallDir + '"'))
if ($delete) { $argList += '-DeleteData' }

if (Test-Admin) {
    & $PSCommandPath -Elevated -InstallDir $InstallDir -DeleteData:$delete
} else {
    Write-Step "Asking for administrator rights (UAC) to remove the service"
    try {
        Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $argList -Verb RunAs -Wait | Out-Null
    } catch {
        throw "Administrator rights were not granted; the service was not removed."
    }
}

Write-Host ""
Write-Host "RTC Call Monitor uninstalled." -ForegroundColor Green
Write-Host ""
