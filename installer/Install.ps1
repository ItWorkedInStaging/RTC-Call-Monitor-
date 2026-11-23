# Install.ps1
# Version: 1.1.0
# Changelog:
#   1.1.0 - Settings move out of Program Files to the config folder
#           (%ProgramData%\RtcCallMonitor\config), locked to SYSTEM and
#           Administrators: appsettings.Local.json, your own provider lists
#           (e.g. 3cx.txt) and the downloaded lists are moved there. The user
#           running the installer is allowed to change settings with the new
#           settings window (editors.json), which is installed in settings\.
#           A first install opens the settings window instead of Notepad.
#   1.0.3 - First install no longer fails when the release has no
#           appsettings.Local.example.json (it is looked for in the release too,
#           and a minimal file is written if it is nowhere).
#   1.0.2 - Started from the source folder instead of a release: points to the
#           built release's Install.cmd, or says to run build-release.ps1 first.
#   1.0.1 - .NET runtime check: dotnet writing to stderr no longer counts as
#           "runtime missing" in Windows PowerShell 5.1.
#   1.0.0 - Initial version. Installs or upgrades RTC Call Monitor from a release
#           folder: checks the .NET 10 runtimes, stops the running service and
#           tray, copies the files (keeping appsettings.Local.json, providers\auto
#           and logs), creates/updates the Windows service with crash recovery,
#           starts it, and sets the tray app to start at sign-in.
#
# Run it with Install.cmd (double-click). Works in Windows PowerShell 5.1 and PowerShell 7.
# The part that needs administrator rights runs in a second, elevated window;
# the tray auto-start and tray launch stay with the signed-in user.

[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'RtcCallMonitor'),
    [switch]$NoTray,
    [switch]$Elevated,        # internal: set for the elevated half
    [string]$ResultFile,      # internal: where the elevated half writes its outcome
    [string]$EditorSid,       # internal: the signed-in user who may change settings
    [string]$EditorName       # internal: their account name, for people reading editors.json
)

$ErrorActionPreference = 'Stop'
$ServiceName  = 'RTC Call Monitor'
$TrayProcess  = 'RtcCallMonitor.Tray'
$RunKey       = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunValue     = 'RtcCallMonitorTray'                       # same name the tray's "Start with Windows" uses
$BackupDir    = Join-Path $env:ProgramData 'RtcCallMonitor'
$ConfigDir    = Join-Path $BackupDir 'config'                 # settings, your provider lists, downloads
$SettingsProcess = 'RtcCallMonitor.Settings'
$PackageRoot  = Split-Path -Parent $PSScriptRoot           # folder with app\, tray\, Install.cmd

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok($text)   { Write-Host "    $text" -ForegroundColor Green }
function Write-Note($text) { Write-Host "    $text" -ForegroundColor Yellow }

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-NotAReleaseMessage($appSrc) {
    # started from the source folder (repository) rather than a built release?
    if (Test-Path (Join-Path $PackageRoot 'src\RtcCallMonitor.csproj')) {
        $latest = Get-ChildItem -Path (Join-Path $PackageRoot 'release') -Directory -Filter 'RtcCallMonitor-*' -ErrorAction SilentlyContinue |
                  Where-Object { Test-Path (Join-Path $_.FullName 'app\RtcCallMonitor.exe') } |
                  Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($latest) {
            return "This is the source folder, not a release. Run the built release instead: $(Join-Path $latest.FullName 'Install.cmd')"
        }
        return "This is the source folder, not a release. Build one first with .\build-release.ps1 (in $PackageRoot), then run release\RtcCallMonitor-<version>\Install.cmd."
    }
    return "RtcCallMonitor.exe not found in $appSrc. Run Install.cmd from the extracted release folder."
}

function Test-DotNetRuntimes {
    $ErrorActionPreference = 'Continue'   # see build-release.ps1 1.2.1
    $list = @()
    try { $list = & dotnet --list-runtimes 2>$null } catch { }
    $core    = $list | Where-Object { $_ -match '^Microsoft\.NETCore\.App 10\.' }
    $desktop = $list | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App 10\.' }
    return [pscustomobject]@{ Core = [bool]$core; Desktop = [bool]$desktop }
}

function Invoke-Robocopy($from, $to, [string[]]$extra) {
    # /E subfolders, /R:2 /W:2 retries, /NFL /NDL /NJH /NJS /NP quiet
    $rcArgs = @($from, $to, '/E', '/R:2', '/W:2', '/NFL', '/NDL', '/NJH', '/NJS', '/NP') + $extra
    & robocopy @rcArgs | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying $from to $to failed (robocopy exit code $LASTEXITCODE)." }
}

# ---------------------------------------------------------------------------
# Config folder: %ProgramData%\RtcCallMonitor\config
# ---------------------------------------------------------------------------
function Move-IfMissing($from, $to) {
    if (-not (Test-Path $from)) { return $false }
    if (Test-Path $to) {
        Rename-Item -Path $from -NewName ((Split-Path -Leaf $from) + '.old') -Force -ErrorAction SilentlyContinue
        return $false
    }
    Move-Item -Path $from -Destination $to -Force
    return $true
}

function Move-ConfigOutOfProgramFiles($appSrc) {
    $customDir = Join-Path $ConfigDir 'providers'
    $autoDir   = Join-Path $customDir 'auto'
    New-Item -ItemType Directory -Force -Path $autoDir | Out-Null
    $settings = Join-Path $ConfigDir 'appsettings.Local.json'

    # settings: from the program folder (before 3.0), or the copy kept by an uninstall
    if (Move-IfMissing (Join-Path $InstallDir 'appsettings.Local.json') $settings) {
        Write-Ok "moved appsettings.Local.json to $ConfigDir"
    }
    $backup = Join-Path $BackupDir 'appsettings.Local.json.bak'
    if (-not (Test-Path $settings) -and (Test-Path $backup)) {
        Move-Item $backup $settings
        Write-Ok "restored your settings from the previous uninstall"
    }

    if (Test-Path (Join-Path $InstallDir 'providers')) {
        # downloaded lists
        $oldAuto = Join-Path $InstallDir 'providers\auto'
        if (Test-Path $oldAuto) {
            foreach ($f in Get-ChildItem -Path $oldAuto -File) { [void](Move-IfMissing $f.FullName (Join-Path $autoDir $f.Name)) }
            Remove-Item -Recurse -Force $oldAuto -ErrorAction SilentlyContinue
        }
        # your own lists: .txt files the release does not ship (e.g. 3cx.txt)
        $bundled = @(Get-ChildItem -Path (Join-Path $appSrc 'providers') -Filter '*.txt' -File -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
        foreach ($f in Get-ChildItem -Path (Join-Path $InstallDir 'providers') -Filter '*.txt' -File) {
            if ($bundled -notcontains $f.Name) {
                if (Move-IfMissing $f.FullName (Join-Path $customDir $f.Name)) { Write-Ok "moved your provider list $($f.Name) to $customDir" }
            }
        }
    }
    foreach ($old in @('update-state.json')) {
        [void](Move-IfMissing (Join-Path $InstallDir $old) (Join-Path $ConfigDir $old))
    }
}

function Set-ConfigFolderAccess {
    # SYSTEM and Administrators only: the folder holds the MQTT password
    $ErrorActionPreference = 'Continue'
    & icacls.exe "$ConfigDir" /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not set the permissions of $ConfigDir (icacls exit code $LASTEXITCODE)." }
    # moved files keep their old permissions: make everything inside inherit from the folder
    & icacls.exe "$ConfigDir\*" /reset /T /C /Q 2>$null | Out-Null
}

function Add-Editor {
    if (-not $EditorSid) { return }
    $file = Join-Path $ConfigDir 'editors.json'
    $editors = @()
    if (Test-Path $file) {
        try { $editors = @((Get-Content $file -Raw | ConvertFrom-Json).editors | Where-Object { $_ -and $_.sid }) } catch { $editors = @() }
    }
    if ($editors | Where-Object { $_.sid -eq $EditorSid }) {
        Write-Ok "$EditorName can change the settings"
        return
    }
    $editors += [pscustomobject]@{ sid = $EditorSid; name = $EditorName }
    $json = @{ editors = @($editors) } | ConvertTo-Json -Depth 3
    [IO.File]::WriteAllText($file, $json, (New-Object Text.UTF8Encoding($false)))
    Write-Ok "$EditorName can now change the settings (tray > Settings...)"
}

# ---------------------------------------------------------------------------
# Elevated half: service and program files
# ---------------------------------------------------------------------------
function Install-Elevated {
    $appSrc  = Join-Path $PackageRoot 'app'
    $traySrc = Join-Path $PackageRoot 'tray'
    $settingsSrc = Join-Path $PackageRoot 'settings'
    if (-not (Test-Path (Join-Path $appSrc 'RtcCallMonitor.exe'))) {
        throw (Get-NotAReleaseMessage $appSrc)
    }

    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    $upgrade = [bool]$service

    if ($service -and $service.Status -ne 'Stopped') {
        Write-Step "Stopping the monitor service"
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        Write-Ok "stopped"
    }

    $running = Get-Process -Name $TrayProcess, $SettingsProcess -ErrorAction SilentlyContinue
    if ($running) {
        Write-Step "Closing the tray app and the settings window"
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }

    Write-Step "Settings folder $ConfigDir"
    Move-ConfigOutOfProgramFiles $appSrc

    Write-Step ("{0} files in {1}" -f ($(if ($upgrade) { 'Updating' } else { 'Copying' }), $InstallDir))
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    # never overwrite settings left in the program folder (they were moved above; this is a safety net)
    Invoke-Robocopy $appSrc $InstallDir @('/XF', 'appsettings.Local.json', 'update-state.json', '/XD', (Join-Path $appSrc 'providers\auto'))
    if (-not $NoTray -and (Test-Path $traySrc)) {
        Invoke-Robocopy $traySrc (Join-Path $InstallDir 'tray') @()
    }
    if (-not $NoTray -and (Test-Path $settingsSrc)) {
        # /PURGE: the settings window carries its own Windows App SDK files, which change between versions
        Invoke-Robocopy $settingsSrc (Join-Path $InstallDir 'settings') @('/PURGE')
    }
    Write-Ok "done"

    $localConfig = Join-Path $ConfigDir 'appsettings.Local.json'
    $newConfig = $false
    if (-not (Test-Path $localConfig)) {
        $example = @((Join-Path $InstallDir 'appsettings.Local.example.json'),
                     (Join-Path $appSrc 'appsettings.Local.example.json'),
                     (Join-Path $PackageRoot 'appsettings.Local.example.json')) |
                   Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($example) {
            Copy-Item $example $localConfig
        } else {
            # no template in the release: start with the settings most people change
            @'
{
  "Mqtt": {
    "Enabled": false,
    "Host": "",
    "Username": "",
    "Password": ""
  },
  "Webhook": {
    "Enabled": false,
    "CallStartUrl": "",
    "CallEndUrl": ""
  }
}
'@ | Set-Content -Path $localConfig -Encoding UTF8
        }
        $newConfig = $true
        Write-Note "first install: fill in your MQTT or webhook details in the settings window (it opens at the end)"
    } else {
        Write-Ok "kept your settings"
    }
    Set-ConfigFolderAccess
    Add-Editor
    Write-Ok "only administrators can open the folder; you change settings with the settings window"

    $exe = Join-Path $InstallDir 'RtcCallMonitor.exe'
    $imagePath = '"' + $exe + '"'
    if (-not $service) {
        Write-Step "Creating the Windows service '$ServiceName'"
        New-Service -Name $ServiceName -BinaryPathName $imagePath -DisplayName $ServiceName -StartupType Automatic | Out-Null
    } else {
        Write-Step "Updating the Windows service '$ServiceName'"
        # set the path through the registry: sc.exe binPath= with quotes is unreliable in Windows PowerShell 5.1
        Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name ImagePath -Value $imagePath
    }
    & sc.exe config "$ServiceName" start= delayed-auto | Out-Null
    & sc.exe description "$ServiceName" "Detects voice/video calls and reports them over MQTT (Home Assistant) or webhooks." | Out-Null
    # restart after 1 minute if it crashes; reset the failure count after a day
    & sc.exe failure "$ServiceName" reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
    & sc.exe failureflag "$ServiceName" 1 | Out-Null
    Write-Ok "starts automatically (delayed start), restarts itself after a crash"

    Write-Step "Starting the monitor"
    Start-Service -Name $ServiceName
    $svc = Get-Service -Name $ServiceName
    try { $svc.WaitForStatus('Running', [TimeSpan]::FromSeconds(30)) } catch { }
    Start-Sleep -Seconds 3
    $svc.Refresh()
    if ($svc.Status -ne 'Running') {
        Write-Note "The service did not stay running. Recent log lines:"
        Show-LogTail
        throw "The service is $($svc.Status)."
    }
    Write-Ok "running"
    Show-LogTail -Lines 8

    return [pscustomobject]@{ Upgrade = $upgrade; InstallDir = $InstallDir; NewConfig = $newConfig }
}

function Show-LogTail([int]$Lines = 15) {
    $logDir = Join-Path $env:ProgramData 'RtcCallMonitor\logs'
    $latest = Get-ChildItem -Path $logDir -Filter 'rtc-call-monitor-*.log' -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latest) {
        Write-Host "    --- $($latest.FullName)" -ForegroundColor DarkGray
        Get-Content $latest.FullName -Tail $Lines | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    } else {
        Write-Note "No log file yet in $logDir; see Event Viewer > Windows Logs > Application."
    }
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
if ($Elevated) {
    try {
        Start-Transcript -Path (Join-Path $env:TEMP 'RtcCallMonitor-install.log') -Force | Out-Null
        $result = Install-Elevated
        $result | ConvertTo-Json | Set-Content -Path $ResultFile -Encoding UTF8
        Write-Host ""
        Write-Host "Done. This window closes in 10 seconds." -ForegroundColor Green
        Start-Sleep -Seconds 10
    }
    catch {
        Write-Host ""
        Write-Host "Installation failed: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "Details: $env:TEMP\RtcCallMonitor-install.log"
        @{ Error = $_.Exception.Message } | ConvertTo-Json | Set-Content -Path $ResultFile -Encoding UTF8
        Read-Host "Press Enter to close"
    }
    finally {
        try { Stop-Transcript | Out-Null } catch { }
    }
    exit
}

Write-Host ""
Write-Host "RTC Call Monitor - install / upgrade" -ForegroundColor White
Write-Host "Package: $PackageRoot"
Write-Host "Target:  $InstallDir"
Write-Host ""

if (-not (Test-Path (Join-Path $PackageRoot 'app\RtcCallMonitor.exe'))) {
    Write-Host (Get-NotAReleaseMessage (Join-Path $PackageRoot 'app')) -ForegroundColor Red
    exit 1
}

Write-Step "Checking the .NET 10 runtimes"
$rt = Test-DotNetRuntimes
if (-not $rt.Core -or (-not $NoTray -and -not $rt.Desktop)) {
    Write-Note "The .NET 10 Desktop Runtime is needed (it includes the .NET runtime)."
    $answer = Read-Host "    Install it now with winget? [Y/n]"
    if ($answer -eq '' -or $answer -match '^[Yy]') {
        & winget install --id Microsoft.DotNet.DesktopRuntime.10 --exact --accept-package-agreements --accept-source-agreements
        $rt = Test-DotNetRuntimes
    }
    if (-not $rt.Core) { throw "The .NET 10 runtime is still missing. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run Install.cmd again." }
}
Write-Ok "found"

# the service part needs administrator rights: run it in an elevated window and wait
$resultFile = Join-Path $env:TEMP ("RtcCallMonitor-install-{0}.json" -f [guid]::NewGuid().ToString('N'))
# the signed-in user (not the admin account UAC may switch to) gets to change the settings
$me = [Security.Principal.WindowsIdentity]::GetCurrent()
$editorSid  = $me.User.Value
$editorName = $me.Name
$argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'),
             '-Elevated', '-ResultFile', ('"' + $resultFile + '"'), '-InstallDir', ('"' + $InstallDir + '"'),
             '-EditorSid', $editorSid, '-EditorName', ('"' + $editorName + '"'))
if ($NoTray) { $argList += '-NoTray' }

if (Test-Admin) {
    # already elevated (e.g. run from an admin prompt): do it in this window
    & $PSCommandPath -Elevated -ResultFile $resultFile -InstallDir $InstallDir -NoTray:$NoTray -EditorSid $editorSid -EditorName $editorName
} else {
    Write-Step "Asking for administrator rights (UAC) to install the service"
    try {
        $p = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $argList -Verb RunAs -Wait -PassThru
    } catch {
        throw "Administrator rights were not granted; nothing was installed."
    }
}

if (-not (Test-Path $resultFile)) { throw "The elevated installer did not finish; see $env:TEMP\RtcCallMonitor-install.log" }
$result = Get-Content $resultFile -Raw | ConvertFrom-Json
Remove-Item $resultFile -ErrorAction SilentlyContinue
if ($result.Error) { throw $result.Error }

if (-not $NoTray) {
    $trayExe = Join-Path $InstallDir 'tray\RtcCallMonitor.Tray.exe'
    if (Test-Path $trayExe) {
        Write-Step "Setting up the tray app for $env:USERNAME"
        New-Item -Path $RunKey -Force | Out-Null
        Set-ItemProperty -Path $RunKey -Name $RunValue -Value ('"' + $trayExe + '"')
        Start-Process -FilePath $trayExe
        Write-Ok "started, and starts when you sign in (untick 'Start with Windows' in its menu to stop that)"
    }
    $settingsExe = Join-Path $InstallDir 'settings\RtcCallMonitor.Settings.exe'
    if ($result.NewConfig -and (Test-Path $settingsExe)) {
        Write-Step "Opening the settings window"
        Start-Process -FilePath $settingsExe
    }
}

Write-Host ""
if ($result.Upgrade) { Write-Host "RTC Call Monitor upgraded." -ForegroundColor Green }
else { Write-Host "RTC Call Monitor installed." -ForegroundColor Green }
Write-Host "  Program:  $InstallDir"
Write-Host "  Settings: tray icon > Settings...  (stored in $ConfigDir)"
Write-Host "  Logs:     $(Join-Path $env:ProgramData 'RtcCallMonitor\logs')"
Write-Host ""
