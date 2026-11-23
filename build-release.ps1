# build-release.ps1
# Version: 1.2.0
# Changelog:
#   1.2.0 - Installs what is missing instead of only reporting it: the .NET 10
#           SDK and Desktop Runtime (winget) and the nuget.org source. Checks
#           that the source folder has every part of the release (an older
#           clone without the tray/installer is reported). -NoInstall turns the
#           installing off.
#   1.1.0 - Pre-flight checks before building: Windows, .NET 10 SDK and the
#           .NET 10 runtimes the tests need, a usable NuGet source, package
#           restore, all Microsoft.Extensions.* packages on 10.x (no .NET 11
#           mix), and known-vulnerable packages. -CheckOnly runs just the checks,
#           -SkipChecks skips them.
#   1.0.0 - Initial version. Runs the tests, publishes the monitor and the tray
#           app, and builds release\RtcCallMonitor-<version>\ plus a .zip with
#           Install.cmd / Uninstall.cmd. Never packages appsettings.Local.json.
#
# Usage (from the repository root):
#   .\build-release.ps1               checks, tests, build
#   .\build-release.ps1 -CheckOnly    only the pre-flight checks
#   .\build-release.ps1 -SkipTests    checks and build, no tests
#   .\build-release.ps1 -SkipChecks   tests and build, no pre-flight checks
#   .\build-release.ps1 -NoInstall    report missing .NET / NuGet setup instead of installing it

[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipChecks,
    [switch]$CheckOnly,
    [switch]$NoInstall
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

[xml]$proj = Get-Content (Join-Path $root 'src\RtcCallMonitor.csproj')
$version = ($proj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { throw "No <Version> in src\RtcCallMonitor.csproj" }

$name    = "RtcCallMonitor-$version"
$outRoot = Join-Path $root 'release'
$out     = Join-Path $outRoot $name
$zip     = Join-Path $outRoot "$name.zip"

Write-Host "Building $name" -ForegroundColor White

# ---------------------------------------------------------------------------
# Pre-flight checks
# ---------------------------------------------------------------------------
$script:Failures = New-Object System.Collections.Generic.List[string]
$script:Warnings = New-Object System.Collections.Generic.List[string]

function Pass($text) { Write-Host "    [ OK ] $text" -ForegroundColor Green }
function Warn($text) { Write-Host "    [WARN] $text" -ForegroundColor Yellow; $script:Warnings.Add($text) }
function Fail($text) { Write-Host "    [FAIL] $text" -ForegroundColor Red; $script:Failures.Add($text) }

function Update-SessionPath {
    # pick up PATH changes made by an installer that just ran
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user    = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = (@($machine, $user) | Where-Object { $_ }) -join ';'
}

function Install-WithWinget($id, $what) {
    if ($NoInstall) { return $false }
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $winget) {
        Write-Host "    winget is not available, so $what can't be installed automatically." -ForegroundColor Yellow
        return $false
    }
    Write-Host "    Installing $what with winget (Windows may ask for administrator rights)..." -ForegroundColor Yellow
    & winget install --id $id --exact --silent --accept-package-agreements --accept-source-agreements | Out-Host
    $ok = ($LASTEXITCODE -eq 0)
    Update-SessionPath
    return $ok
}

function Get-DotNetList($what) {
    try { return @(& dotnet $what 2>$null) } catch { return @() }
}

function Invoke-PreflightChecks {
    Write-Host "==> Pre-flight checks" -ForegroundColor Cyan

    # Windows: the release is a Windows service plus a WinForms tray app
    if ([Environment]::OSVersion.Platform -eq 'Win32NT') { Pass "Windows $([Environment]::OSVersion.Version)" }
    else { Fail "Not Windows: the release (service + tray app) can only be built and used on Windows" }

    # dotnet itself; installed with winget if missing
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Host "    dotnet not found" -ForegroundColor Yellow
        Install-WithWinget 'Microsoft.DotNet.SDK.10' 'the .NET 10 SDK' | Out-Null
    }
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Fail "dotnet not found. Install the SDK (winget install --id Microsoft.DotNet.SDK.10 --exact), open a new PowerShell window and run this again."
        return
    }
    Pass "dotnet found ($($dotnet.Source))"

    # the source folder has every part of the release
    $required = @(
        'src\RtcCallMonitor.csproj',
        'tray\RtcCallMonitor.Tray\RtcCallMonitor.Tray.csproj',
        'tests\RtcCallMonitor.Tests\RtcCallMonitor.Tests.csproj',
        'installer\Install.ps1', 'installer\Uninstall.ps1', 'installer\Install.cmd', 'installer\Uninstall.cmd',
        'RtcCallMonitor.sln', 'README.md', 'CHANGELOG.md')
    $missing = $required | Where-Object { -not (Test-Path (Join-Path $root $_)) }
    if ($missing) {
        Fail ("this source folder is missing parts of the release (an older clone?): " + ($missing -join ', ') +
              ". Pull the latest code, or extract the full source zip and run build-release.ps1 from there.")
        return
    }
    Pass "source folder complete (version $version)"

    # .NET 10 SDK (to build); installed with winget if missing
    $sdks = Get-DotNetList '--list-sdks'
    $sdk10 = $sdks | Where-Object { $_ -match '^10\.' } | Select-Object -Last 1
    if (-not $sdk10) {
        Write-Host "    .NET 10 SDK not found (found: $(if ($sdks) { ($sdks | ForEach-Object { ($_ -split ' ')[0] }) -join ', ' } else { 'none' }))" -ForegroundColor Yellow
        if (Install-WithWinget 'Microsoft.DotNet.SDK.10' 'the .NET 10 SDK') {
            $sdks = Get-DotNetList '--list-sdks'
            $sdk10 = $sdks | Where-Object { $_ -match '^10\.' } | Select-Object -Last 1
        }
    }
    if ($sdk10) { Pass ".NET 10 SDK $(($sdk10 -split ' ')[0])" }
    else {
        Fail ".NET 10 SDK not installed. Install it (winget install --id Microsoft.DotNet.SDK.10 --exact), open a new PowerShell window and run this again."
        return
    }

    # global.json could pin another SDK
    $globalJson = Join-Path $root 'global.json'
    if (Test-Path $globalJson) {
        $used = (& dotnet --version 2>$null)
        if ($used -notmatch '^10\.') { Fail "global.json selects SDK $used; it must be a 10.x SDK" } else { Pass "global.json selects SDK $used" }
    }

    # .NET 10 runtimes (to run the tests; the tray needs the desktop one)
    $runtimes = Get-DotNetList '--list-runtimes'
    $desktopPattern = '^Microsoft\.WindowsDesktop\.App 10\.'
    if (-not ($runtimes | Where-Object { $_ -match $desktopPattern })) {
        Write-Host "    .NET 10 Desktop Runtime not found" -ForegroundColor Yellow
        if (Install-WithWinget 'Microsoft.DotNet.DesktopRuntime.10' 'the .NET 10 Desktop Runtime') { $runtimes = Get-DotNetList '--list-runtimes' }
    }
    foreach ($rt in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        $hit = $runtimes | Where-Object { $_ -match ('^' + [regex]::Escape($rt) + ' 10\.') } | Select-Object -Last 1
        if ($hit) { Pass "$rt $(($hit -split ' ')[1])" }
        elseif ($rt -eq 'Microsoft.NETCore.App') { Fail "$rt 10.x runtime missing (it comes with the .NET 10 SDK; try repairing the SDK install)" }
        else { Warn "$rt 10.x runtime missing: the tray app builds, but can't run on this PC (winget install --id Microsoft.DotNet.DesktopRuntime.10 --exact)" }
    }

    # NuGet source (an empty list gives NU1100 on restore); nuget.org is added if there is none
    $sources = @(& dotnet nuget list source 2>$null)
    $enabled = $sources | Where-Object { $_ -match '\[Enabled\]' }
    if (-not $enabled) {
        $hasNugetOrg = $sources | Where-Object { $_ -match 'nuget\.org' }
        if (-not $NoInstall) {
            if ($hasNugetOrg) {
                Write-Host "    nuget.org is disabled; enabling it" -ForegroundColor Yellow
                & dotnet nuget enable source nuget.org | Out-Null
            } else {
                Write-Host "    no NuGet source; adding nuget.org" -ForegroundColor Yellow
                & dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org | Out-Null
            }
            $sources = @(& dotnet nuget list source 2>$null)
            $enabled = $sources | Where-Object { $_ -match '\[Enabled\]' }
        }
    }
    if ($enabled) { Pass "NuGet sources: $((@($enabled) | ForEach-Object { ($_ -replace '^\s*\d+\.\s*', '' -replace '\s*\[Enabled\]', '').Trim() }) -join ', ')" }
    else { Fail "No enabled NuGet source. Run: dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org" }

    if ($script:Failures.Count -gt 0) { return } # restore would only fail the same way

    # restore: every package can be found
    $restore = & dotnet restore (Join-Path $root 'RtcCallMonitor.sln') --nologo 2>&1
    if ($LASTEXITCODE -eq 0) { Pass "all packages restore" }
    else {
        $errs = $restore | Where-Object { $_ -match 'error' } | Select-Object -First 5
        Fail ("package restore failed:`n        " + (($errs | ForEach-Object { "$_".Trim() }) -join "`n        "))
        return
    }

    # package versions: Microsoft.Extensions.* must stay on 10.x (an 11.x package pulls .NET 11 libraries in)
    $listJson = & dotnet list (Join-Path $root 'RtcCallMonitor.sln') package --include-transitive --format json 2>$null
    if ($LASTEXITCODE -eq 0 -and $listJson) {
        $list = ($listJson -join "`n") | ConvertFrom-Json
        $mismatch = @()
        foreach ($project in $list.projects) {
            foreach ($fw in @($project.frameworks)) {
                foreach ($pkg in @($fw.topLevelPackages) + @($fw.transitivePackages)) {
                    if (-not $pkg) { continue }
                    if ($pkg.id -match '^(Microsoft\.Extensions\.|System\.Diagnostics\.EventLog$|System\.ServiceProcess\.ServiceController$)' -and $pkg.resolvedVersion -and $pkg.resolvedVersion -notmatch '^10\.') {
                        $mismatch += "$($pkg.id) $($pkg.resolvedVersion) in $(Split-Path -Leaf $project.path)"
                    }
                }
            }
        }
        $mismatch = $mismatch | Sort-Object -Unique
        if ($mismatch) { Fail ("packages not on 10.x (pin them to 10.x versions):`n        " + ($mismatch -join "`n        ")) }
        else { Pass "Microsoft.Extensions.* (and EventLog/ServiceController) packages all on 10.x" }
    } else {
        Warn "could not list packages (dotnet list package --format json)"
    }

    # known vulnerabilities (needs the NuGet vulnerability feed)
    $vulnJson = & dotnet list (Join-Path $root 'RtcCallMonitor.sln') package --vulnerable --include-transitive --format json 2>$null
    if ($LASTEXITCODE -eq 0 -and $vulnJson) {
        $vuln = ($vulnJson -join "`n") | ConvertFrom-Json
        $found = @()
        foreach ($project in $vuln.projects) {
            foreach ($fw in @($project.frameworks)) {
                foreach ($pkg in @($fw.topLevelPackages) + @($fw.transitivePackages)) {
                    if ($pkg -and $pkg.vulnerabilities) {
                        $sev = ($pkg.vulnerabilities | ForEach-Object { $_.severity }) -join '/'
                        $found += "$($pkg.id) $($pkg.resolvedVersion) ($sev)"
                    }
                }
            }
        }
        $found = $found | Sort-Object -Unique
        if ($found) { Fail ("packages with known vulnerabilities:`n        " + ($found -join "`n        ")) }
        else { Pass "no packages with known vulnerabilities" }
    } else {
        Warn "could not check for vulnerable packages (no connection to the NuGet vulnerability feed?)"
    }

    # uncommitted changes: the release would not match any commit
    $git = Get-Command git -ErrorAction SilentlyContinue
    $inRepo = $false
    if ($git) { $inRepo = ((& git -C $root rev-parse --is-inside-work-tree 2>$null) -eq 'true') }
    if (-not $inRepo) {
        Pass "not a git clone (source zip): skipping the uncommitted-changes check"
    } else {
        $dirty = & git -C $root status --porcelain 2>$null
        if ($dirty) { Warn "uncommitted changes in the repository; the release will include them" }
        else { Pass "working tree clean ($(& git -C $root rev-parse --short HEAD))" }
    }
}

if (-not $SkipChecks) {
    Invoke-PreflightChecks
    Write-Host ""
    if ($script:Failures.Count -gt 0) {
        Write-Host "Pre-flight checks failed ($($script:Failures.Count)); nothing was built." -ForegroundColor Red
        exit 1
    }
    Write-Host ("Pre-flight checks passed" + $(if ($script:Warnings.Count -gt 0) { " with $($script:Warnings.Count) warning(s)." } else { "." })) -ForegroundColor Green
    Write-Host ""
}
if ($CheckOnly) { exit 0 }

if (-not $SkipTests) {
    Write-Host "==> Running tests" -ForegroundColor Cyan
    & dotnet test (Join-Path $root 'RtcCallMonitor.sln') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed; no release built." }
}

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
if (Test-Path $zip) { Remove-Item -Force $zip }

Write-Host "==> Publishing the monitor" -ForegroundColor Cyan
& dotnet publish (Join-Path $root 'src\RtcCallMonitor.csproj') -c Release -r win-x64 --self-contained false -o (Join-Path $out 'app') --nologo
if ($LASTEXITCODE -ne 0) { throw "Publishing the monitor failed." }

Write-Host "==> Publishing the tray app" -ForegroundColor Cyan
& dotnet publish (Join-Path $root 'tray\RtcCallMonitor.Tray\RtcCallMonitor.Tray.csproj') -c Release -r win-x64 --self-contained false -o (Join-Path $out 'tray') --nologo
if ($LASTEXITCODE -ne 0) { throw "Publishing the tray app failed." }

# personal settings and downloaded lists never go into a release
foreach ($private in @('app\appsettings.Local.json', 'app\providers\auto')) {
    $path = Join-Path $out $private
    if (Test-Path $path) { Remove-Item -Recurse -Force $path }
}
# debugging symbols are not needed to run it
Get-ChildItem -Path $out -Recurse -Filter '*.pdb' | Remove-Item -Force

Write-Host "==> Adding the installer" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path (Join-Path $out 'installer') | Out-Null
Copy-Item (Join-Path $root 'installer\Install.ps1')   (Join-Path $out 'installer')
Copy-Item (Join-Path $root 'installer\Uninstall.ps1') (Join-Path $out 'installer')
Copy-Item (Join-Path $root 'installer\Install.cmd')   $out
Copy-Item (Join-Path $root 'installer\Uninstall.cmd') $out
Copy-Item (Join-Path $root 'README.md')    $out
Copy-Item (Join-Path $root 'CHANGELOG.md') $out

Write-Host "==> Zipping" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -Force

$size = '{0:N1} MB' -f ((Get-Item $zip).Length / 1MB)
Write-Host ""
Write-Host "Release ready:" -ForegroundColor Green
Write-Host "  Folder: $out"
Write-Host "  Zip:    $zip ($size)"
Write-Host "To install or upgrade: double-click Install.cmd in the folder (or in the extracted zip)."
