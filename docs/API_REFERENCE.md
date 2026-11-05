# B_VIP Tool — API Reference

Base URL: `http(s)://<server>:5088` · JSON only · UTF-8 · all timestamps are UTC (`...Z`)
OpenAPI/Swagger UI is served at `/swagger` in Development (or when `OpenApi:ExposeInProduction` is
`true`).

---

## 1. Authentication

```
Authorization: Bearer <accessToken>
```

Sign in, then send the access token on every call:

```http
POST /api/auth/login
Content-Type: application/json

{ "userName": "admin", "password": "..." }
```

```json
{
  "tokens": {
    "accessToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9....",
    "refreshToken": "3x3i...",
    "expiresUtc": "2026-01-01T12:00:00Z",
    "refreshExpiresUtc": "2026-01-15T12:00:00Z"
  },
  "user": { "id": "...", "userName": "admin", "displayName": "Administrator", "roles": ["Super Admin"] },
  "permissions": [ "users.read", "operations.run", "..." ],
  "license": { "status": "Active", "planName": "Annual", "expiresUtc": "2027-01-01T00:00:00Z" },
  "serverTimeUtc": "2026-01-01T10:00:00Z"
}
```

- Tokens are HS256-signed JWTs with claims `sub` (user id), `sid` (session), `usr`, `rol`, `per`.
- Access tokens last 60 minutes by default; refresh tokens 14 days and **rotate on every use**.
  Reusing an old refresh token returns `401 invalid-refresh-token`; sign in again.
- `POST /api/auth/logout` accepts the refresh token (and revokes the session).

### Anonymous endpoints

`POST /api/auth/login`, `/api/auth/refresh`, `/api/auth/logout`, `/api/auth/forgot-password`,
`/api/auth/reset-password`, `GET /api/health`, `GET /api/meta`, `GET /api/bootstrap/status`,
`POST /api/bootstrap/first-admin`.

Everything else under `/api` requires a token, even routes that later check permissions.

## 2. Errors

```json
{ "error": { "code": "invalid-request", "message": "Title and description are required.",
             "correlationId": "8f3c..." } }
```

| Status | Typical codes |
|---|---|
| 400 | `invalid-request`, `unknown-operation`, `bad-request`, `too-long` |
| 401 | `unauthorized`, `invalid-token`, `invalid-refresh-token` |
| 403 | `forbidden`, `license-required`, `license-not-active`, `account-suspended` |
| 404 | `not-found` |
| 409 | `conflict`, `duplicate`, `already-bootstrapped` |
| 422 | `confirmation-required`, `validation-failed` |
| 423 | `account-locked` |
| 429 | `rate-limited`, `too-many-attempts` |
| 503 | `maintenance` |

Send `X-Correlation-ID` to trace a request end to end; the server generates one when the header is
missing and echoes it back on every response. Quote it in support tickets.

### Rate limits

| Scope | Limit |
|---|---|
| Global | 600 requests / minute / IP |
| `auth` policy (login, refresh, reset, bootstrap) | 10 requests / minute / IP |

## 3. Pagination

List endpoints accept `page` (default 1) and `pageSize` (default 25, max 100) and return:

```json
{ "items": [ ... ], "totalCount": 42, "page": 1, "pageSize": 25, "totalPages": 2 }
```

## 4. Permissions

| Permission | Grants |
|---|---|
| `users.read` / `users.write` / `users.suspend` / `users.roles.write` | Accounts |
| `roles.read` / `roles.write` | Roles and permission sets |
| `licenses.read` / `licenses.write` / `licenses.manage` | Licenses, plans, activations |
| `operations.read` / `operations.run` / `operations.cancel` | Device operations |
| `reports.read` / `reports.write` | Diagnostic reports |
| `tickets.read` / `tickets.write` / `tickets.assign` / `tickets.all` | Support |
| `admin.overview` / `admin.settings` / `admin.integrations` / `admin.audit` / `admin.health` / `admin.backups` / `admin.releases` | Administration |

## 5. Endpoints

### 5.1 Authentication and identity

| Method | Path | Permission |
|---|---|---|
| POST | `/api/auth/login` | anonymous |
| POST | `/api/auth/refresh` | anonymous |
| POST | `/api/auth/logout` | anonymous |
| POST | `/api/auth/forgot-password` | anonymous |
| POST | `/api/auth/reset-password` | anonymous |
| GET | `/api/auth/me` | token |
| POST | `/api/auth/change-password` | token |
| GET | `/api/meta` | anonymous |
| GET | `/api/bootstrap/status` | anonymous |
| POST | `/api/bootstrap/first-admin` | anonymous, one time |
| GET | `/api/health` | anonymous |

### 5.2 Users, roles and permissions

| Method | Path | Permission |
|---|---|---|
| GET | `/api/users?query=&status=&role=` | `users.read` |
| GET | `/api/users/{id}` | `users.read` |
| POST | `/api/users` | `users.write` |
| PATCH | `/api/users/{id}` | `users.write` |
| POST | `/api/users/{id}/suspend` | `users.suspend` |
| POST | `/api/users/{id}/reactivate` | `users.suspend` |
| POST | `/api/users/{id}/revoke-sessions` | `users.write` |
| PUT | `/api/users/{id}/roles` | `users.roles.write` |
| GET | `/api/users/{id}/sessions` | `users.read` |
| GET | `/api/admin/roles` | `roles.read` |
| POST | `/api/admin/roles` | `roles.write` |
| PATCH | `/api/admin/roles/{id}` | `roles.write` |
| PUT | `/api/admin/roles/{id}/permissions` | `roles.write` |
| GET | `/api/admin/permissions` | `roles.read` |

Create a user:

```http
POST /api/users
{ "userName": "tech.one", "email": "tech.one@example.com", "displayName": "Tech One",
  "password": "Str0ng!Passw0rd", "roles": ["User"] }
```

### 5.3 Licenses

| Method | Path | Permission |
|---|---|---|
| GET | `/api/licenses?query=&status=&userId=` | `licenses.read` |
| GET | `/api/licenses/plans` | `licenses.read` |
| POST | `/api/licenses` | `licenses.write` |
| GET | `/api/licenses/{id}` | `licenses.read` |
| PATCH | `/api/licenses/{id}` | `licenses.write` |
| POST | `/api/licenses/{id}/suspend` `/reactivate` `/revoke` | `licenses.manage` |
| GET | `/api/licenses/{id}/activations` | `licenses.read` |
| DELETE | `/api/licenses/{id}/activations/{activationId}` | `licenses.manage` |
| GET | `/api/licenses/{id}/verification-events` | `licenses.read` |
| POST | `/api/licenses/verify` | token |
| POST | `/api/licenses/activate` | token |
| POST | `/api/licenses/deactivate` | token |

Issue and verify:

```http
POST /api/licenses
{ "userId": "8f3c...", "planCode": "annual" }
```

```http
POST /api/licenses/activate
{ "licenseKey": "ABCDE-FGHJK-MNPQR-STUVW-XYZ23", "deviceFingerprint": "SHA256-of-hardware-ids",
  "deviceName": "BENCH-01", "platform": "Windows 11", "appVersion": "1.0.0" }
```

```http
POST /api/licenses/verify
{ "licenseKey": "ABCDE-FGHJK-MNPQR-STUVW-XYZ23", "deviceFingerprint": "SHA256-of-hardware-ids" }
```

### 5.4 Device operations

| Method | Path | Permission |
|---|---|---|
| GET | `/api/device-operations?status=&type=&userId=` | `operations.read` |
| POST | `/api/device-operations` | `operations.run` |
| GET | `/api/device-operations/{id}` | `operations.read` |
| GET | `/api/device-operations/{id}/events` | `operations.read` |
| POST | `/api/device-operations/{id}/cancel` | `operations.read` (own) or `operations.cancel` |
| POST | `/api/device-operations/{id}/result` | `operations.run` |

Lifecycle:

```http
POST /api/device-operations
{ "operationType": "adb.devices", "deviceId": "emulator-5554" }
```

```json
{ "id": "…", "correlationId": "…", "status": "Validating", "operationType": "adb.devices" }
```

```http
POST /api/device-operations/{id}/result
{ "exitCode": 0, "timedOut": false, "cancelled": false,
  "output": "List of devices attached…", "errorOutput": "", "summary": "One device detected.",
  "verified": true }
```

The server decides the final state (`Completed`, `Failed`, `TimedOut`, `Cancelled`) from the exit
code, the operation's success condition and the verification flag. A result for a terminal
operation is answered with the stored record and changes nothing.

Operation types accepted by the engine (18): `adb.devices`, `adb.read-properties`,
`adb.connection-check`, `adb.restart-server`, `adb.collect-logcat`, `adb.reboot`,
`fastboot.devices`, `fastboot.getvar`, `fastboot.reboot`, `fastboot.flash`, `fastboot.wipe`,
`diagnostic.android-run`, `vendor.samsung-diagnostic`, `vendor.mediatek-diagnostic`,
`vendor.qualcomm-diagnostic`, `vendor.apple-diagnostic`, `vendor.google-recovery-guidance`,
`tool.probe`.

`fastboot.flash` and `fastboot.wipe` are data-loss operations: they return `422
confirmation-required` unless the request sends `"confirmDataLoss": true`.

### 5.5 Diagnostic reports

| Method | Path | Permission |
|---|---|---|
| GET | `/api/diagnostic-reports?module=&userId=` | `reports.read` |
| POST | `/api/diagnostic-reports` | `reports.write` |
| GET | `/api/diagnostic-reports/{id}` | `reports.read` |

### 5.6 Support tickets

| Method | Path | Permission |
|---|---|---|
| GET | `/api/tickets?status=&priority=&q=&mine=` | `tickets.read` |
| GET | `/api/tickets/categories` | `tickets.read` |
| POST | `/api/tickets` | `tickets.write` |
| GET | `/api/tickets/{id}` | `tickets.read` |
| POST | `/api/tickets/{id}/messages` | `tickets.write` |
| PATCH | `/api/tickets/{id}/status` | `tickets.write` |
| POST | `/api/tickets/{id}/assign` | `tickets.assign` |

Ownership: without `tickets.all` a user only ever sees and edits their own tickets; asking for
somebody else's ticket returns `404`, not `403`, so ids cannot be probed.

### 5.7 Notifications

| Method | Path |
|---|---|
| GET | `/api/notifications?unread=true` |
| POST | `/api/notifications/{id}/read` |
| POST | `/api/notifications/read-all` |

### 5.8 Administration

| Method | Path | Permission |
|---|---|---|
| GET | `/api/admin/overview` | `admin.overview` |
| GET | `/api/admin/settings` · PATCH `/api/admin/settings/{key}` | `admin.settings` |
| GET | `/api/admin/integrations` · POST · PATCH `/{id}` · POST `/{id}/test` | `admin.integrations` |
| GET | `/api/admin/audit-events` | `admin.audit` |
| GET | `/api/admin/health` · `/api/admin/status` | `admin.health` |
| GET | `/api/admin/versions` · POST · PATCH `/{id}` | `admin.releases` |
| GET | `/api/admin/backups` · POST · POST `/{id}/restore` | `admin.backups` |
| GET | `/api/admin/maintenance` · PATCH | `admin.settings` |

## 6. Client integration checklist

1. Sign in, keep the access token in memory, the refresh token in the OS-protected store.
2. Refresh 60 seconds before `expiresUtc`; on `401 invalid-token` refresh once, then sign in again.
3. Re-read the `permissions` array after every refresh — a permission can be withdrawn mid-session.
4. Send `correlationId` on operation and ticket creation so the desktop, audit log and support
   thread agree.
5. Treat `429` as retry-after; the limit is per IP, so a whole office behind one NAT shares it.
6. Never cache license state for longer than 15 minutes (`License:VerificationCacheMinutes`).
