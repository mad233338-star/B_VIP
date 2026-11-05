# B_VIP Tool — Security Guide

What the product protects, how, and what the operator must do. Read this before exposing the
backend beyond `127.0.0.1`.

---

## 1. Trust boundaries

| Boundary | Trusted? | Notes |
|---|---|---|
| Technician workstation | No | Runs user-supplied shell commands; the token is kept per Windows user |
| Backend API | Yes | Validates every request, owns all state |
| PostgreSQL | Yes | Only the backend connects; the app role owns the schema |
| Android device over USB | No | Data coming from `adb`/`fastboot` is untrusted text and is parsed defensively |
| Browser / admin console | No | Same API as the desktop, same checks |

The client is never trusted to enforce licensing or permissions; it merely reflects what the
server allows.

## 2. Authentication

- **Deny by default.** `AuthenticationMiddleware` requires a valid bearer token on every `/api`
  route; only the endpoints listed in `AnonymousPrefixes` (login, refresh, logout, password reset,
  health, meta, bootstrap) pass without one.
- **Tokens** are HS256-signed with `Auth:SigningKey` (minimum 32 characters, validated at startup),
  carry `sub`, `sid`, `usr`, `rol`, `per`, and expire after `Auth:AccessTokenMinutes` (60 by default).
  The key never leaves the server.
- **Sessions** are rows in `user_sessions`; revoking a user's sessions stops refresh immediately and
  the access token dies with its 60-minute lifetime.
- **Refresh tokens** are random 256-bit values, stored only as SHA-256 hashes, rotated on every
  use. Reuse of a rotated token is refused (`invalid-refresh-token`) and audited.
- **Passwords** are PBKDF2-SHA256 with 210 000 iterations and a per-hash random salt, stored as
  `PBKDF2$SHA256$<iterations>$<salt>$<hash>`; verification uses a fixed-time comparison.
- **Password policy**: 10–128 characters, at least one letter, one digit and one symbol; user names
  are 3–64 characters of letters, digits, `.`, `_`, `-`, and reserved names (`admin`, `root`,
  `system`, …) are rejected.
- **Lockout**: 5 failed attempts lock the account for `Auth:LockoutMinutes` (15). Login attempts are
  throttled per identifier and IP, and every attempt is recorded in `login_attempts`.
- **Password reset**: single-use token, hashed at rest, expires after `Auth:PasswordResetMinutes`
  (30), limited to `Auth:PasswordResetPerEmailPerHour` (5) requests per address per hour.

## 3. Authorization

- Every protected endpoint declares a permission via `RequirePermission(...)`.
- Permissions are recomputed on refresh from the database, so revoking a role takes effect without
  waiting for the old token to expire (worst case one access-token lifetime).
- **Ownership scoping** is enforced in the service layer, not the UI: a user who lacks
  `tickets.all` / support permissions receives `404` for other users' tickets, reports and
  operations, which also prevents id probing.
- **Object-level checks** exist even for data a permission allows: recording an operation result
  requires owning the operation (or support rights).
- **Privilege escalation** is blocked by `PermissionRules`: a non-super-admin can only grant
  permissions they hold themselves, only a Super Admin may modify an account that holds the Super
  Admin role, the last active Super Admin cannot be suspended or demoted, and nobody can change
  their own account through the admin endpoints.
- The client dashboard only hides the admin module for non-admins; server-side checks are the real
  boundary.

## 4. Secrets at rest

| Secret | Storage | Key |
|---|---|---|
| User passwords | `users.password_hash` | none (PBKDF2, one-way) |
| Refresh tokens, reset tokens | SHA-256 hash only | none |
| Integration secrets, settings marked secret | AES-256-GCM (`AesSecretProtector`, `v1.<nonce>.<tag>.<cipher>` format) | `Security:SecretEncryptionKey` |
| Signing key | configuration / environment | — |

`AesSecretProtector` accepts a base64 32-byte key; any other value is hashed with SHA-256 so a
passphrase still works. **The key must be backed up with the database**: without it the encrypted
columns cannot be read.

Configuration precedence is environment variables → `appsettings.{Environment}.json` →
`appsettings.json`. In production, put secrets in the service environment (the install script does
this) or in a secrets vault, never in the repository.

## 5. Transport and hosting

- Terminate TLS at IIS, nginx, Caddy or the Windows HTTP.sys binding; never expose plain HTTP on a
  public interface.
- Restrict `Cors:AllowedOrigins` to the real admin console origin; the default only allows
  `http://localhost:5088`.
- The API listens on `0.0.0.0:<port>` only when configured that way; bind to a specific interface on
  multi-homed machines.
- Keep `OpenApi:ExposeInProduction` **false** unless you intentionally publish the schema.
- Put the database on a private network; the app role needs only `CONNECT`, `USAGE` and `CREATE` on
  its schema.

## 6. Abuse resistance

| Control | Value |
|---|---|
| Global rate limit | 600 requests/minute per IP |
| Auth rate limit | 10 requests/minute per IP on login, refresh, reset, bootstrap |
| Account lockout | 5 failures → 15 minutes |
| Reset throttling | 5 requests/hour per e-mail address |
| Request body limit | Kestrel defaults; uploads capped by `Storage:MaxUploadBytes` (10 MB) |
| Bootstrap | one-time token, refuses a second super admin, rate limited |
| Maintenance mode | blocks all business endpoints with `503` |

## 7. Audit and monitoring

`audit_events` records actor, action, entity, outcome, message, IP and correlation id for sign-in,
user, role, license, operation, ticket, settings, integration, backup and restore actions.

Watch these signals:

- Spike in `auth.login` failures for one identifier → credential stuffing.
- `license-verification` events with unknown fingerprints → shared or cloned installations.
- `operation.result` with `RequiresAttention` → tooling problem or unreadable device output.
- `admin.*` changes outside working hours.

Ship the log file (`Serilog` file sink) and the database to your monitoring if you have one; the
`/api/health` endpoint is intentionally anonymous and cheap to poll.

## 8. Device operations safety

- Destructive operations (`fastboot.flash`, `fastboot.wipe`) are flagged in the registry and require
  `"confirmDataLoss": true`; without it the API answers `422`.
- Every operation stores the exact command, tool path, tool version, output and exit code, so a
  device damage claim can be investigated with facts.
- The engine runs tools with absolute paths and argument lists (no shell interpolation), so a
  device serial or file name containing spaces or quotes cannot inject commands.
- Output is truncated (200 kB / 100 kB) before it is stored.

## 9. What the product deliberately does not do

B_VIP Tool performs **diagnostics, maintenance and standard service operations**. It does not
unlock FRP/activation locks, change IMEI or serial numbers, bypass carrier or vendor protections,
or "unlock" SIM locks. Requests for those features are out of scope and are not implemented in any
layer; the Google module only shows policy-compliant recovery guidance.

## 10. Incident checklist

1. Put the service in maintenance mode (`PATCH /api/admin/maintenance`).
2. Revoke the affected users' sessions and rotate their passwords.
3. Rotate `Auth:SigningKey` (all tokens become invalid) and `Security:SecretEncryptionKey`
   (re-encrypt secrets by re-saving them).
4. Export `audit_events`, `login_attempts` and the log files for the incident window.
5. Restore from backup only if data integrity is in doubt, then re-apply steps 2–3.
6. Record what happened, who was affected and what changed.
