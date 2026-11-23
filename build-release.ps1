# build-release.ps1
# Version: 1.0.0
# Changelog:
#   1.0.0 - Initial version. Runs the tests, publishes the monitor and the tray
#           app, and builds release\RtcCallMonitor-<version>\ plus a .zip with
#           Install.cmd / Uninstall.cmd. Never packages appsettings.Local.json.
#
# Usage (from the repository root):  .\build-release.ps1   [-SkipTests]

[CmdletBinding()]
param(
    [switch]$SkipTests
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
