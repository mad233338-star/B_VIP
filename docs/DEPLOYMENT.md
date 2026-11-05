# B_VIP Tool — Deployment Guide

Two components are deployed: the **backend** (ASP.NET Core service + PostgreSQL) and the
**desktop client** (installed on each technician's Windows machine).

```
technician PC ──HTTPS──► reverse proxy / IIS ──► B_VIP.Backend (Windows service) ──► PostgreSQL
        desktop client                                                                database
```

---

## 1. Server prerequisites

| Item | Minimum | Recommended |
|---|---|---|
| Windows | Windows 10 22H2 / Server 2019 | Windows 11 23H2 / Server 2022, x64 |
| CPU / RAM | 2 cores / 4 GB | 4 cores / 8 GB |
| Disk | 20 GB | 100 GB SSD (reports and logs grow) |
| PostgreSQL | 14 | 16 |
| .NET | none (self-contained build) | .NET 8 ASP.NET Core Runtime for framework-dependent builds |
| Network | LAN port 5088 | TLS-terminated public endpoint |

Write access needed: `C:\Program Files\B_VIP\Backend` (program), `C:\ProgramData\B_VIP` (data,
logs, backups, uploads).

## 2. Database

```powershell
# as a PostgreSQL superuser
psql -h 127.0.0.1 -p 5432 -U postgres -v app_password='<strong password>' `
     -f deploy\create-database.sql
```

This creates the `b_vip_app` login role and the `b_vip` database owned by it. The schema, seeds and
reference data are created by the backend on first start.

Hardening after the first start:

```sql
ALTER ROLE b_vip_app SET statement_timeout = '120s';
ALTER ROLE b_vip_app SET idle_in_transaction_session_timeout = '60s';
```

## 3. Build the release

```powershell
powershell -ExecutionPolicy Bypass -File deploy\publish.ps1
```

Output:

- `artifacts\backend` — self-contained `B_VIP.Backend.exe` plus configuration samples
- `artifacts\desktop` — self-contained `B_VIP.Desktop.exe`
- `artifacts\installer-payload` — the exact tree the installer packages

Use `-FrameworkDependent` when the servers already have the .NET 8 runtimes and you want smaller
folders.

## 4. Install the backend service

From an **elevated** PowerShell prompt:

```powershell
powershell -ExecutionPolicy Bypass -File deploy\install-backend-service.ps1 `
  -SourceDirectory .\artifacts\backend `
  -ConnectionString "Host=127.0.0.1;Port=5432;Database=b_vip;Username=b_vip_app;Password=<pw>;Pooling=true;Timeout=15" `
  -Port 5088
```

The script:

1. copies the binaries to `C:\Program Files\B_VIP\Backend`
2. creates `C:\ProgramData\B_VIP\{data,logs,backups,uploads}`
3. registers the **BVIPBackend** service (automatic start, restart on failure)
4. stores the connection string and generated secrets as service environment variables
   (`ConnectionStrings__Default`, `Auth__SigningKey`, `Security__SecretEncryptionKey`,
   `Bootstrap__FirstAdminToken`)
5. opens the firewall port for the Domain and Private profiles
6. starts the service and waits for `/api/health`
7. prints the one-time bootstrap token

Keep that token safe; it is printed once. Create the first administrator immediately (see the
[admin guide](ADMIN_GUIDE.md#2-creating-the-first-administrator)), then set
`Bootstrap:AllowFirstAdminBootstrap=false` in the service configuration and restart.

### Manual (no script)

```powershell
sc.exe create BVIPBackend binPath= "\"C:\Program Files\B_VIP\Backend\B_VIP.Backend.exe\" --contentRoot \"C:\Program Files\B_VIP\Backend\"" start= auto
sc.exe description BVIPBackend "B_VIP Tool backend"
sc.exe start BVIPBackend
```

Environment variables for a service live in
`HKLM\SYSTEM\CurrentControlSet\Services\BVIPBackend\Environment` (REG_MULTI_SZ, `NAME=value`).

## 5. TLS and reverse proxy

The service speaks HTTP; put TLS in front of it.

**IIS** — install the ASP.NET Core Hosting Bundle, create a site bound to the certificate, then use
`web.config` with `<aspNetCore processPath="B_VIP.Backend.exe" arguments="--contentRoot .\" />`
and `ANCM_ADDITIONAL_STARTUP_ERROR_PAGE` disabled in production.

**nginx**

```nginx
server {
    listen 443 ssl http2;
    server_name vip.example.com;

    ssl_certificate     C:/certs/vip.crt;
    ssl_certificate_key C:/certs/vip.key;

    client_max_body_size 12m;

    location / {
        proxy_pass         http://127.0.0.1:5088;
        proxy_http_version 1.1;
        proxy_set_header   Host $host;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_read_timeout 180s;
    }
}
```

After adding the public origin, set `Cors:AllowedOrigins` to it and restart the service.

## 6. Client installation

```powershell
iscc installer\B_VIP-Desktop.iss       # -> dist\B_VIP-Setup-<version>.exe
```

- Per-machine install into `Program Files\B_VIP Tool`; shortcuts in the Start menu (desktop
  shortcut optional).
- The installer checks for the .NET 8 Desktop Runtime when the client is framework-dependent and
  warns once.
- `deploy\appsettings.Desktop.sample.json` ships as `appsettings.json` next to the executable; the
  client uses it as the default server address until a user saves their own settings.
- Technicians still need Android platform-tools and the vendor USB drivers; point the client at the
  platform-tools folder in **Settings**.

For a fleet, pre-fill `serverUrl` in that file before packaging so nobody types the address.

## 7. Configuration reference

All keys are readable from `appsettings.json`, `appsettings.Production.json` or environment
variables (`Section__Key`).

| Key | Meaning | Default |
|---|---|---|
| `ConnectionStrings:Default` | Npgsql connection string | localhost `b_vip` |
| `Auth:SigningKey` | HS256 token key, ≥ 32 chars | none — startup fails without it |
| `Auth:AccessTokenMinutes` / `RefreshTokenDays` | Token lifetimes | 60 / 14 |
| `Auth:MaxFailedAccessAttempts` / `LockoutMinutes` | Lockout | 5 / 15 |
| `Auth:PasswordResetMinutes` / `PasswordResetPerEmailPerHour` | Reset window and quota | 30 / 5 |
| `Security:SecretEncryptionKey` | AES-256-GCM key for stored secrets | none |
| `Bootstrap:FirstAdminToken` | One-time bootstrap token | none |
| `Bootstrap:AllowFirstAdminBootstrap` | Enables the bootstrap endpoint | `true` |
| `Storage:DataDirectory` / `BackupDirectory` / `UploadDirectory` | Writable folders | `data`, `data/backups`, `data/uploads` |
| `Storage:MaxUploadBytes` | Upload limit | 10 MB |
| `Storage:ReportRetentionDays` / `AuditRetentionDays` | Retention | 180 / 730 |
| `Storage:PgDumpPath` / `PgRestorePath` | Binaries used by admin backups | empty → PATH |
| `License:OfflineGraceHours` / `AllowOfflineWork` | Offline behaviour | 72 / `false` |
| `Cors:AllowedOrigins` | Allowed browser origins | `http://localhost:5088` |
| `OpenApi:ExposeInProduction` | Serve Swagger in production | `false` |
| `Serilog:*` | Log levels, sinks and paths | console + file |

## 8. Upgrading

```powershell
# 1. Back up
powershell -File deploy\backup-database.ps1 -ConnectionString "..."

# 2. Stop the service
Stop-Service BVIPBackend

# 3. Publish the new build and re-run the installer script (it replaces the binaries)
powershell -File deploy\publish.ps1
powershell -File deploy\install-backend-service.ps1 -SourceDirectory .\artifacts\backend -ConnectionString "..."

# 4. Watch the log while migrations run
Get-Content C:\ProgramData\B_VIP\logs\backend-*.log -Tail 40 -Wait
```

Migrations run automatically at startup and are logged. Never run two backend instances with
different versions against the same database at the same time.

Rollback: restore the database backup, then re-install the previous `artifacts\backend` folder. Keep
the previous release folder until the new one has been stable for a week.

## 9. Maintenance windows

```http
PATCH /api/admin/maintenance
{ "enabled": true, "message": "Server maintenance, back at 18:00", "untilUtc": "2026-05-01T18:00:00Z" }
```

Clients receive `503` with that message on every business call; health, auth and admin endpoints
keep working so you can end the window.

## 10. Uninstalling

```powershell
# backend (elevated) — keeps data unless -RemoveFiles/-RemoveData are given
powershell -File deploy\uninstall-backend-service.ps1

# desktop
"C:\Program Files\B_VIP Tool\unins000.exe" /SILENT
```

The PostgreSQL database is never dropped by the scripts; do it explicitly when the data is no
longer needed.

## 11. Go-live checklist

- [ ] PostgreSQL on a private interface, strong `b_vip_app` password, tested restore
- [ ] `Auth:SigningKey` and `Security:SecretEncryptionKey` generated and stored in the password
      manager
- [ ] Backend served over TLS only
- [ ] First Super Admin created, bootstrap disabled, `Bootstrap:FirstAdminToken` cleared
- [ ] `Cors:AllowedOrigins` limited to the real console origin
- [ ] Nightly backup scheduled and verified (`Get-ChildItem C:\ProgramData\B_VIP\backups`)
- [ ] Monitoring polling `/api/health`
- [ ] Desktop installer tested on a clean Windows 10 and Windows 11 machine
- [ ] Platform-tools and USB drivers installed on the bench, diagnostics run on a real phone
- [ ] Roles reviewed: at least two Super Admins, no shared accounts
