<#
.SYNOPSIS
    Builds the B_VIP Tool release artifacts (backend + desktop).

.DESCRIPTION
    Produces two self-contained, win-x64 folders under artifacts\, ready to copy to a server
    or to feed into the Inno Setup installer:

        artifacts\backend   -> B_VIP.Backend.exe  (+ appsettings)
        artifacts\desktop   -> B_VIP.Desktop.exe  (WPF client)

    Self-contained output needs no .NET runtime on the target machine.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy\publish.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [switch] $FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot 'artifacts'
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }

Write-Host "B_VIP Tool - publishing $Configuration / $Runtime (self-contained=$selfContained)" -ForegroundColor Cyan

$backendOut = Join-Path $artifacts 'backend'
$desktopOut = Join-Path $artifacts 'desktop'

# A portable build carries adb.exe / fastboot.exe in a platform-tools folder beside the exe
# (the desktop searches that folder before PATH). The publish recreates the output folders, so
# stage an existing bundle and put it back afterwards instead of losing it.
$platformToolsOut = Join-Path $desktopOut 'platform-tools'
$stagedTools = Join-Path ([System.IO.Path]::GetTempPath()) ('bvip-platform-tools-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path $platformToolsOut) {
    Copy-Item $platformToolsOut $stagedTools -Recurse -Force
}

foreach ($directory in @($backendOut, $desktopOut)) {
    if (Test-Path $directory) { Remove-Item $directory -Recurse -Force }
}

function Invoke-Publish {
    param([string] $Project, [string] $Output)

    & dotnet publish $Project `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained $selfContained `
        --output $Output `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Project (exit code $LASTEXITCODE)."
    }
}

Invoke-Publish -Project (Join-Path $repoRoot 'src\B_VIP.Backend\B_VIP.Backend.csproj') -Output $backendOut
Invoke-Publish -Project (Join-Path $repoRoot 'src\B_VIP.Desktop\B_VIP.Desktop.csproj') -Output $desktopOut

# Production configuration templates: the real secrets are filled in on the server.
Copy-Item (Join-Path $PSScriptRoot 'appsettings.Production.sample.json') `
    (Join-Path $backendOut 'appsettings.Production.sample.json') -Force

# Development overrides must not travel with a release: the service runs as Production.
$developmentSettings = Join-Path $backendOut 'appsettings.Development.json'
if (Test-Path $developmentSettings) {
    Remove-Item $developmentSettings -Force
}
Copy-Item (Join-Path $PSScriptRoot 'appsettings.Desktop.sample.json') `
    (Join-Path $desktopOut 'appsettings.json') -Force

# Restore the bundled tools, or seed the bundle from the repository .tools folder.
if (Test-Path $stagedTools) {
    New-Item -ItemType Directory -Path $platformToolsOut -Force | Out-Null
    Copy-Item (Join-Path $stagedTools '*') $platformToolsOut -Recurse -Force
    Remove-Item $stagedTools -Recurse -Force
}
else {
    $repositoryTools = Join-Path $repoRoot '.tools'
    if (Test-Path $repositoryTools) {
        New-Item -ItemType Directory -Path $platformToolsOut -Force | Out-Null
        foreach ($tool in @('adb.exe', 'fastboot.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll', 'libusb-1.0.dll')) {
            $source = Join-Path $repositoryTools $tool
            if (Test-Path $source) { Copy-Item $source (Join-Path $platformToolsOut $tool) -Force }
        }
    }
}

$platformTools = @('adb.exe', 'fastboot.exe') |
    Where-Object { Test-Path (Join-Path $platformToolsOut $_) }
Write-Host "Bundled platform tools: $($platformTools.Count)/2 ($platformToolsOut)" -ForegroundColor $(if ($platformTools.Count -eq 2) { 'Green' } else { 'Yellow' })

# Keep data, logs and backups out of the installer payload.
$payload = Join-Path $artifacts 'installer-payload'
if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
New-Item -ItemType Directory -Path $payload | Out-Null
Copy-Item (Join-Path $desktopOut '*') $payload -Recurse -Force

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Backend : $backendOut"
Write-Host "  Desktop : $desktopOut"
Write-Host "  Payload : $payload"
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Deploy the backend:  powershell -File deploy\install-backend-service.ps1 -SourceDirectory '$backendOut'"
Write-Host "  2. Build the installer: iscc installer\B_VIP-Desktop.iss"
