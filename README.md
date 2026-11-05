# B_VIP Tool

**Professional mobile-phone repair, diagnostics and GSM service platform**
Brand: **Bashir Al-Farhan** · Platform: **Windows 10 / 11 (64-bit)** · UI languages: **English / العربية**
Design language: **black, gold and white**

B_VIP Tool is a complete, buildable product: a WPF desktop client for technicians, an
ASP.NET Core backend that owns accounts, licenses, operations, support and audit records, a
PostgreSQL database with EF Core migrations, and an installer plus deployment scripts.

---

## 1. What is in the box

| Area | Contents |
|---|---|
| Desktop client (`src/B_VIP.Desktop`) | Black/gold WPF shell, English + Arabic (full RTL), 18 modules, local ADB/Fastboot engine, diagnostics, reports, support centre, license status |
| Admin console (`src/B_VIP.Admin`) | Users, roles & permissions, licenses and plans, operations oversight, audit, integrations, releases, backups, maintenance, health |
| Backend (`src/B_VIP.Backend`) | ASP.NET Core 8 minimal API, deny-by-default authentication, per-endpoint permissions, rate limiting, health, OpenAPI |
| Domain + rules (`src/B_VIP.Core`, `src/B_VIP.Contracts`) | Entities, operation state machine, license rules, permissions, DTOs shared by all layers |
| Services (`src/B_VIP.Application`, `src/B_VIP.Infrastructure`) | Application interfaces, EF Core (PostgreSQL) mapping, migrations, seeding, security primitives |
| Hardware + modules (`src/B_VIP.Hardware`, `src/B_VIP.Modules`) | ADB/Fastboot process wrappers, device detection, diagnostics, vendor diagnostic modules |
| API client (`src/B_VIP.CloudClient`) | Typed HTTP client used by the desktop and admin UI |
| Tests (`tests/`) | 218 automated tests: unit, hardware, API, integration, security |
| Deployment (`deploy/`, `installer/`) | Release publishing, PostgreSQL bootstrap, Windows service install/uninstall, database backup, Inno Setup installer |
| Docs (`docs/`) | User, admin, API, database, security, deployment, troubleshooting and Arabic guides |

## 2. Requirements

**Runtime (target machine)**

- Windows 10 22H2 or Windows 11, x64
- PostgreSQL 14+ (16 recommended)
- Android platform-tools (`adb`, `fastboot`) for device work — not bundled for licensing reasons
- .NET 8 Desktop Runtime for the client (or use the self-contained build)

**Development**

- .NET SDK 8.0.400+
- PostgreSQL 14+ reachable from the machine
- Inno Setup 6 (only to build the installer)

## 3. Repository layout

```
B_VIP_Software.sln
src/
  B_VIP.Core            domain entities, rules, permissions, security primitives
  B_VIP.Contracts       request/response DTOs shared by every layer
  B_VIP.Application     service interfaces, report builder
  B_VIP.Infrastructure  EF Core, PostgreSQL, migrations, seeding, services
  B_VIP.Backend         ASP.NET Core 8 Web API (the server)
  B_VIP.Hardware        adb/fastboot process layer, device detection, diagnostics
  B_VIP.Modules         diagnostic + service modules built on the hardware layer
  B_VIP.CloudClient     typed API client
  B_VIP.Desktop         WPF client (net8.0-windows)
  B_VIP.Admin           admin console views/view-models
tests/
  B_VIP.UnitTests B_VIP.HardwareTests B_VIP.ApiTests B_VIP.IntegrationTests B_VIP.SecurityTests B_VIP.TestSupport
deploy/                 publish, database bootstrap, service install, backup scripts
installer/              Inno Setup script + license page
docs/                   product documentation
```

## 4. Quick start (development)

```powershell
# 1. Database (any PostgreSQL instance)
psql -h 127.0.0.1 -p 5432 -U postgres -v app_password='dev-password' `
     -v db_name=b_vip_dev -v role_name=b_vip_app -f deploy/create-database.sql

# 2. Backend configuration: src/B_VIP.Backend/appsettings.Development.json already points at
#    127.0.0.1:5433 / b_vip_dev and contains development-only keys. Adjust the connection string
#    to your instance, then:

dotnet run --project src/B_VIP.Backend          # http://127.0.0.1:5088  (Swagger UI at /swagger)

# 3. First administrator (one time)
#    POST /api/bootstrap/first-admin
#    { "token": "<Bootstrap:FirstAdminToken>", "userName": "admin", "email": "admin@example.com",
#      "password": "<strong password>" }

# 4. Desktop client
dotnet run --project src/B_VIP.Desktop
```

The backend migrates the schema and seeds roles, permissions, license plans, support categories,
settings and integrations on startup, so an empty database is all it needs.

## 5. Build and test

```powershell
dotnet build B_VIP_Software.sln -c Release
dotnet test  B_VIP_Software.sln -c Release
```

The API, integration and security suites boot the real backend against a disposable PostgreSQL
database. They expect PostgreSQL on `127.0.0.1:5433` with a passwordless `postgres` role
(see [docs/TESTING.md](docs/TESTING.md) to point them somewhere else).

## 6. Publish, deploy and install

```powershell
# Release artifacts (self-contained win-x64)
powershell -ExecutionPolicy Bypass -File deploy\publish.ps1

# Backend as a Windows service (elevated)
powershell -ExecutionPolicy Bypass -File deploy\install-backend-service.ps1 `
    -SourceDirectory .\artifacts\backend `
    -ConnectionString "Host=127.0.0.1;Port=5432;Database=b_vip;Username=b_vip_app;Password=...;Pooling=true"

# Desktop installer
iscc installer\B_VIP-Desktop.iss        # -> dist\B_VIP-Setup-<version>.exe

# Daily database backups
powershell -ExecutionPolicy Bypass -File deploy\backup-database.ps1 -ConnectionString "..."
```

Full instructions, TLS setup and upgrade steps: [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

## 7. Security model in one paragraph

Every `/api` route requires a signed bearer token except login, refresh, password reset, health,
metadata and the one-time first-admin bootstrap. Tokens are HS256-signed with a server key, carry
the user, session, roles and permissions, and are re-checked per endpoint against the permission
matrix. Passwords are PBKDF2-SHA256 (210 000 iterations). Integration secrets are AES-encrypted at
rest with a server key. Every privileged action writes an audit record. Details:
[docs/SECURITY.md](docs/SECURITY.md).

## 8. Documentation

| Document | Audience |
|---|---|
| [docs/USER_GUIDE.md](docs/USER_GUIDE.md) | Technicians using the desktop client |
| [docs/ADMIN_GUIDE.md](docs/ADMIN_GUIDE.md) | Administrators running accounts, licenses and support |
| [docs/API_REFERENCE.md](docs/API_REFERENCE.md) | Integrators and client developers |
| [docs/DATABASE.md](docs/DATABASE.md) | Database administrators |
| [docs/SECURITY.md](docs/SECURITY.md) | Security reviewers and operators |
| [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) | Installations, upgrades, backups |
| [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) | Support staff diagnosing problems |
| [docs/TESTING.md](docs/TESTING.md) | Developers running the test suites |
| [docs/ARABIC_GUIDE.md](docs/ARABIC_GUIDE.md) | دليل عربي مختصر للمستخدم والمدير |

## 9. License and credits

Proprietary software. See [installer/license.txt](installer/license.txt) for the end-user terms.

Designed and built for **Bashir Al-Farhan**.
