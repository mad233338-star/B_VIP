<#
.SYNOPSIS
    Creates a compressed pg_dump backup of the B_VIP database.

.DESCRIPTION
    Writes a custom-format dump (restorable with pg_restore) into the backup directory,
    then deletes dumps older than the retention window. Meant for a daily Task Scheduler job.

.PARAMETER ConnectionString
    Npgsql/PostgreSQL connection string. Host/Port/Database/Username are read from it;
    the password is passed to pg_dump through PGPASSWORD.

.PARAMETER PgDumpPath
    Path to pg_dump.exe. Default: pg_dump from PATH.

.PARAMETER RetentionDays
    How many days of backups to keep. Default: 30.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy\backup-database.ps1 `
        -ConnectionString "Host=127.0.0.1;Port=5432;Database=b_vip;Username=b_vip_app;Password=..."

.EXAMPLE
    # Daily at 02:00 (elevated):
    schtasks /Create /TN "B_VIP database backup" /SC DAILY /ST 02:00 ^
        /TR "powershell -ExecutionPolicy Bypass -File C:\B_VIP\deploy\backup-database.ps1 -ConnectionString \"...\""
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $ConnectionString,
    [string] $BackupDirectory = 'C:\ProgramData\B_VIP\backups',
    [string] $PgDumpPath = 'pg_dump',
    [int] $RetentionDays = 30
)

$ErrorActionPreference = 'Stop'

function ConvertFrom-ConnectionString {
    param([string] $Text)

    $values = @{}
    foreach ($part in $Text.Split(';')) {
        if ([string]::IsNullOrWhiteSpace($part)) { continue }
        $pair = $part.Split('=', 2)
        if ($pair.Count -eq 2) { $values[$pair[0].Trim().ToLowerInvariant()] = $pair[1].Trim() }
    }
    return $values
}

$settings = ConvertFrom-ConnectionString $ConnectionString
foreach ($required in @('host', 'database', 'username')) {
    if (-not $settings.ContainsKey($required)) {
        throw "The connection string must contain '$required'."
    }
}

$port = if ($settings.ContainsKey('port')) { $settings['port'] } else { '5432' }

if (-not (Get-Command $PgDumpPath -ErrorAction SilentlyContinue)) {
    throw "pg_dump was not found at '$PgDumpPath'. Install the PostgreSQL client tools or pass -PgDumpPath."
}

New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$target = Join-Path $BackupDirectory ("b_vip-{0}.dump" -f $stamp)

$previousPassword = $env:PGPASSWORD
try {
    if ($settings.ContainsKey('password')) { $env:PGPASSWORD = $settings['password'] }

    & $PgDumpPath --host $settings['host'] --port $port --username $settings['username'] `
        --dbname $settings['database'] --format custom --compress 9 --no-owner --file $target

    if ($LASTEXITCODE -ne 0) { throw "pg_dump failed with exit code $LASTEXITCODE." }
}
finally {
    $env:PGPASSWORD = $previousPassword
}

$size = [math]::Round((Get-Item $target).Length / 1MB, 2)
Write-Host "Backup written: $target ($size MB)" -ForegroundColor Green

$cutoff = (Get-Date).AddDays(-$RetentionDays)
Get-ChildItem -Path $BackupDirectory -Filter 'b_vip-*.dump' |
    Where-Object { $_.LastWriteTime -lt $cutoff } |
    ForEach-Object {
        Remove-Item $_.FullName -Force
        Write-Host "Removed expired backup $($_.Name)"
    }

Write-Host 'Restore with:'
Write-Host "  pg_restore --host <host> --port $port --username <user> --dbname <db> --no-owner --clean --if-exists `"$target`""
