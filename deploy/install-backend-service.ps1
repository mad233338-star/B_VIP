<#
.SYNOPSIS
    Installs the B_VIP Tool backend as a Windows service.

.DESCRIPTION
    Copies the published backend to its install directory, creates the data/log/backup
    folders, registers the service (automatic start), applies the connection string and
    secrets as service environment variables, opens the firewall port and waits for
    /api/health to answer.

    Run from an elevated PowerShell prompt. Nothing is written to the project folder.

.PARAMETER SourceDirectory
    Folder produced by deploy\publish.ps1 (artifacts\backend).

.PARAMETER InstallDirectory
    Where the service files are copied. Default: C:\Program Files\B_VIP\Backend

.PARAMETER ConnectionString
    Npgsql connection string. Required unless appsettings.Production.json next to the
    published binaries already contains a real one.

.PARAMETER SigningKey
    Access-token signing key, minimum 32 characters. Generated when omitted.

.PARAMETER SecretEncryptionKey
    AES key used to encrypt stored integration secrets. Generated when omitted.

.PARAMETER FirstAdminToken
    One-time token used by POST /api/bootstrap/first-admin. Generated when omitted and
    printed once so the operator can create the first Super Admin.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy\install-backend-service.ps1 `
        -SourceDirectory .\artifacts\backend `
        -ConnectionString "Host=127.0.0.1;Port=5432;Database=b_vip;Username=b_vip_app;Password=...;Pooling=true"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $SourceDirectory,
    [string] $InstallDirectory = 'C:\Program Files\B_VIP\Backend',
    [string] $ServiceName = 'BVIPBackend',
    [int] $Port = 5088,
    [string] $ConnectionString,
    [string] $SigningKey,
    [string] $SecretEncryptionKey,
    [string] $FirstAdminToken,
    [string] $DataDirectory = 'C:\ProgramData\B_VIP'
)

$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This script must run from an elevated PowerShell prompt (Run as administrator).'
    }
}

function New-RandomSecret {
    param([int] $Bytes = 32)
    $buffer = New-Object byte[] $Bytes
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($buffer)
    return [Convert]::ToBase64String($buffer)
}

Assert-Administrator

if (-not (Test-Path (Join-Path $SourceDirectory 'B_VIP.Backend.exe'))) {
    throw "'$SourceDirectory' does not contain B_VIP.Backend.exe. Run deploy\publish.ps1 first."
}

if ([string]::IsNullOrWhiteSpace($SigningKey)) { $SigningKey = New-RandomSecret 48 }
if ([string]::IsNullOrWhiteSpace($SecretEncryptionKey)) { $SecretEncryptionKey = New-RandomSecret 32 }
if ([string]::IsNullOrWhiteSpace($FirstAdminToken)) { $FirstAdminToken = New-RandomSecret 24 }

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    $productionFile = Join-Path $SourceDirectory 'appsettings.Production.json'
    if (Test-Path $productionFile) {
        Write-Host "No -ConnectionString supplied; relying on $productionFile" -ForegroundColor Yellow
    }
    else {
        throw 'A -ConnectionString is required (or place appsettings.Production.json next to the binaries).'
    }
}

Write-Host "Installing B_VIP backend into $InstallDirectory" -ForegroundColor Cyan

# Stop and remove any previous installation so files can be replaced.
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping existing service $ServiceName" -ForegroundColor Yellow
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus('Stopped', '00:01:00')
    }
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
foreach ($folder in @($DataDirectory, (Join-Path $DataDirectory 'data'), (Join-Path $DataDirectory 'logs'),
        (Join-Path $DataDirectory 'backups'), (Join-Path $DataDirectory 'uploads'))) {
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
}

Copy-Item (Join-Path $SourceDirectory '*') $InstallDirectory -Recurse -Force

# Configuration stays next to the binaries; secrets are injected as service environment variables.
$configuration = @{
    'ASPNETCORE_ENVIRONMENT'    = 'Production'
    'ASPNETCORE_URLS'           = "http://0.0.0.0:$Port"
    'DOTNET_ENVIRONMENT'        = 'Production'
    'Auth__SigningKey'          = $SigningKey
    'Security__SecretEncryptionKey' = $SecretEncryptionKey
    'Bootstrap__FirstAdminToken' = $FirstAdminToken
    'Storage__DataDirectory'    = (Join-Path $DataDirectory 'data')
    'Storage__BackupDirectory'  = (Join-Path $DataDirectory 'backups')
    'Storage__UploadDirectory'  = (Join-Path $DataDirectory 'uploads')
}
if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) {
    $configuration['ConnectionStrings__Default'] = $ConnectionString
}

$environment = @()
foreach ($key in $configuration.Keys) {
    $environment += "$key=$($configuration[$key])"
}

$exe = Join-Path $InstallDirectory 'B_VIP.Backend.exe'
$binaryPath = "`"$exe`" --contentRoot `"$InstallDirectory`""

& sc.exe create $ServiceName binPath= $binaryPath start= auto DisplayName= 'B_VIP Tool Backend (Bashir Al-Farhan)' | Out-Null
& sc.exe description $ServiceName 'B_VIP Tool backend: licensing, users, operations, support, admin API.' | Out-Null
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/120000//

New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" `
    -Name Environment -Value ([string[]] $environment) -PropertyType MultiString -Force | Out-Null

$firewallRule = "B_VIP Backend ($Port)"
if (-not (Get-NetFirewallRule -DisplayName $firewallRule -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $firewallRule -Direction Inbound -Action Allow `
        -Protocol TCP -LocalPort $Port -Profile Domain,Private | Out-Null
    Write-Host "Firewall rule '$firewallRule' created (Domain/Private profiles)." -ForegroundColor Green
}

Write-Host "Starting service $ServiceName" -ForegroundColor Cyan
Start-Service -Name $ServiceName

$deadline = (Get-Date).AddMinutes(2)
$healthy = $false
while ((Get-Date) -lt $deadline) {
    try {
        $response = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/api/health" -UseBasicParsing -TimeoutSec 5
        if ($response.StatusCode -eq 200) { $healthy = $true; break }
    }
    catch {
        Start-Sleep -Seconds 2
    }
}

$service = Get-Service -Name $ServiceName
Write-Host ""
Write-Host "Service    : $($service.Name) [$($service.Status)]"
Write-Host "Install    : $InstallDirectory"
Write-Host "Data       : $DataDirectory"
Write-Host "Endpoint   : http://127.0.0.1:$Port"
if ($healthy) { Write-Host 'Health     : OK' } else { Write-Host "Health     : not answering yet, inspect $DataDirectory\logs" -ForegroundColor Yellow }
Write-Host ""
Write-Host 'First administrator (use this token once; it is shown only here):' -ForegroundColor Yellow
Write-Host "  POST http://127.0.0.1:$Port/api/bootstrap/first-admin"
Write-Host "  token: $FirstAdminToken"
Write-Host '  body: { "token": "<token>", "userName": "<admin>", "email": "<mail>", "password": "<password>" }'
Write-Host ""
Write-Host "After the first admin exists, set Bootstrap:AllowFirstAdminBootstrap=false and restart the service." -ForegroundColor Yellow
