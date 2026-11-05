<#
.SYNOPSIS
    Removes the B_VIP Tool backend Windows service.

.DESCRIPTION
    Stops and deletes the service, then optionally removes the program files and the data
    directory. Requires an elevated PowerShell prompt.

.PARAMETER RemoveFiles
    Also delete the install directory.

.PARAMETER RemoveData
    Also delete the data directory (database files, logs, uploads). The PostgreSQL database
    itself is never touched by this script.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy\uninstall-backend-service.ps1
#>
[CmdletBinding()]
param(
    [string] $ServiceName = 'BVIPBackend',
    [string] $InstallDirectory = 'C:\Program Files\B_VIP\Backend',
    [string] $DataDirectory = 'C:\ProgramData\B_VIP',
    [switch] $RemoveFiles,
    [switch] $RemoveData
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must run from an elevated PowerShell prompt (Run as administrator).'
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus('Stopped', '00:01:00')
    }

    & sc.exe delete $ServiceName | Out-Null
    Write-Host "Service $ServiceName removed." -ForegroundColor Green
}
else {
    Write-Host "Service $ServiceName is not installed." -ForegroundColor Yellow
}

$firewallRule = Get-NetFirewallRule -DisplayName 'B_VIP Backend*' -ErrorAction SilentlyContinue
foreach ($rule in $firewallRule) {
    Remove-NetFirewallRule -Name $rule.Name
    Write-Host "Firewall rule '$($rule.DisplayName)' removed."
}

if ($RemoveFiles -and (Test-Path $InstallDirectory)) {
    Remove-Item $InstallDirectory -Recurse -Force
    Write-Host "Program files removed from $InstallDirectory."
}

if ($RemoveData) {
    if (Test-Path $DataDirectory) {
        Write-Host "Removing $DataDirectory in 5 seconds - press Ctrl+C to abort." -ForegroundColor Yellow
        Start-Sleep -Seconds 5
        Remove-Item $DataDirectory -Recurse -Force
        Write-Host 'Data directory removed.'
    }
}

Write-Host 'The PostgreSQL database is untouched. Drop it with: DROP DATABASE b_vip;' -ForegroundColor Yellow
