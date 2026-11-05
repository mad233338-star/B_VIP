# B_VIP Tool — Administrator Guide

**For the person running the installation:** accounts, roles, licenses, support, audit, backups,
releases and server health.

---

## 1. Roles shipped with the product

The backend seeds six system roles and 25 permissions on first start.

| Role | Intended holder | Default grants |
|---|---|---|
| **Super Admin** | You | All 25 permissions |
| **General Manager** | Operations lead | Read users/roles, manage licenses, read operations and tickets (all), admin overview, health, audit, releases |
| **Finance Manager** | Billing | Read users, manage licenses and activations, read operations, admin overview |
| **Supervisor** | Shift lead | Read users and licenses, run/cancel operations, tickets (all + assign), reports, admin overview |
| **Support Staff** | Support desk | Read licenses, run/cancel operations, reports, read/write/assign tickets |
| **User** | Technician | Read licenses, run operations, reports, read/write own tickets |

Permissions are additive and always checked on the server. A role can be edited through
`PUT /api/admin/roles/{id}/permissions` (permission `roles.write`); system roles keep their names
but their permission sets are editable.

## 2. Creating the first administrator

1. Set `Bootstrap:FirstAdminToken` (or the `Bootstrap__FirstAdminToken` environment variable) to a
   long random value before the first start.
2. Call:

```http
POST /api/bootstrap/first-admin
Content-Type: application/json

{ "token": "<the token>", "userName": "admin", "email": "admin@example.com", "password": "<strong>" }
```

3. The call succeeds once. Afterwards set `Bootstrap:AllowFirstAdminBootstrap` to `false` and
   restart the service; the endpoint then returns `409`.

The bootstrap endpoint refuses to create a second super admin, refuses weak passwords and refuses
to run when the token is missing.

## 3. Accounts

**Create** — `POST /api/users` with user name, e-mail, display name, password and optional roles.
**Edit** — `PATCH /api/users/{id}` for display name, e-mail and roles.
**Suspend / reactivate** — `POST /api/users/{id}/suspend` and `/reactivate`. A suspended account
keeps its data but cannot sign in and its tokens stop working.
**Revoke sessions** — `POST /api/users/{id}/revoke-sessions` signs the person out everywhere, for
example after a laptop was lost.
**Sessions** — `GET /api/users/{id}/sessions` lists active sessions with IP address and last seen.

Guidelines:

- One account per person, never a shared account; the audit trail is only useful that way.
- Change roles rather than creating a new account when someone changes job.
- Deactivate leavers the same day; their license seat is released from the license screen.

## 4. Licenses

**Plans** — `GET /api/licenses/plans` lists the seeded plans:

| Code | Duration | Activations | Notes |
|---|---|---|---|
| `trial` | 14 days | 1 | Evaluation |
| `monthly` | 30 days | 2 | |
| `quarterly` | 90 days | 3 | |
| `annual` | 365 days | 5 | |
| `lifetime` | never expires | 10 | |
| `custom` | set per license | 1 | Manual |

**Issue a license** — `POST /api/licenses` with `{ "userId": "...", "planCode": "annual" }`. The
response contains the license key (format `XXXXX-XXXXX-XXXXX-XXXXX-XXXXX`).

**Manage** — `PATCH /api/licenses/{id}` adjusts expiry, activation limit and features;
`/suspend`, `/reactivate` and `/revoke` change its state. Suspending blocks new operations
immediately while keeping history.

**Activations** — `GET /api/licenses/{id}/activations` shows which machines hold a seat;
`DELETE /api/licenses/{id}/activations/{activationId}` frees one (use it when a technician replaces
a computer). `GET /api/licenses/{id}/verification-events` shows every verify call, useful when a
customer claims "it worked yesterday".

Expiry semantics: an expired license keeps the account usable for reports and tickets but the
server refuses every operation with `license-not-active`.

## 5. Operations oversight

`GET /api/device-operations` lists every operation with status, type, user, device and timings.
Useful filters: `status`, `type`, `userId`.

- States: `Queued → Validating → Running → Completed | Failed | Cancelled | TimedOut |
  RequiresAttention`.
- `POST /api/device-operations/{id}/cancel` stops a stuck operation. Owners can always cancel their
  own; `operations.cancel` extends this to other users' operations.
- Destructive operations (`fastboot.flash`, `fastboot.wipe`) are flagged `DataLossRisk` in the
  registry and require an explicit confirmation from the technician; the audit record keeps the
  details.

Review `RequiresAttention` operations daily — they are the ones where the tool output could not be
interpreted and a human must decide.

## 6. Support

- Categories are seeded (activation, device not detected, tool/driver, account recovery, billing,
  other) and can be extended in the database.
- `POST /api/tickets/{id}/assign` assigns a ticket to support staff; assignment is recorded.
- `PATCH /api/tickets/{id}/status` moves a ticket between Open, InProgress, WaitingForUser,
  WaitingForSupport, Resolved, Closed and Reopened.
- Users only ever see their own tickets; `tickets.all` lets support see every ticket.
- Internal notes (messages with the internal flag) are never returned to customers.

## 7. Audit log

`GET /api/admin/audit-events` (permission `admin.audit`) returns every privileged action: sign-in
success and failure, user changes, license changes, operation results, ticket changes, settings and
integration changes, backup and restore. Each record carries actor, action, entity, outcome, IP
address and correlation id.

The API refuses to delete audit records. Retention is controlled by
`Storage:AuditRetentionDays` (default 730) for maintenance jobs.

## 8. Settings and integrations

**Settings** (`GET /api/admin/settings`, `PATCH /api/admin/settings/{key}`) — branding, support
contacts, default language, retention windows, maintenance text. Secrets are stored encrypted
(`IsSecret = true`) and never returned in clear text.

**Integrations** (`/api/admin/integrations`) — endpoints for external services (payment, SMS,
e-mail). Create one, store its secret, then `POST /api/admin/integrations/{id}/test` to check it;
the result is written to the integration health table.

## 9. Releases

`GET/POST/PATCH /api/admin/versions` manages client releases: version, channel, notes, download
URL, checksum, enabled flag. Disabled releases stop being offered to clients.

## 10. Backups and restore

- `POST /api/admin/backups` records a backup (with the configured `pg_dump` path the backend can
  run it directly; otherwise run `deploy/backup-database.ps1` and record the note).
- `GET /api/admin/backups` lists history, size and outcome.
- `POST /api/admin/backups/{id}/restore` restores a backup. **This replaces live data** — take a
  fresh backup first and expect connected clients to be signed out.

Recommended routine: nightly `pg_dump` through Task Scheduler, 30-day retention on disk, monthly
copy off the machine, and a restore rehearsal twice a year.

## 11. Maintenance mode and health

`PATCH /api/admin/maintenance` turns maintenance mode on with a message and optional window. While
it is on, clients receive `503` with that message on every API call except `/api/health`,
`/api/admin` and `/api/auth`, so an administrator can still sign in and switch it off, and nobody
starts a repair in the middle of a restore.

`GET /api/admin/status` and `GET /api/health` report database reachability, seeded data, disk paths
and version. Point your monitoring at `/api/health` — it needs no token.

## 12. Routine checklist

| Frequency | Task |
|---|---|
| Daily | Review `RequiresAttention` operations and failed sign-ins |
| Daily | Check `/api/health` from monitoring |
| Weekly | Verify backups exist and are not zero-length |
| Weekly | Review suspensions, expiring licenses (next 14 days) |
| Monthly | Review roles and permissions; remove unused accounts |
| Quarterly | Restore a backup into a scratch database |
| Yearly | Rotate `Auth:SigningKey` and `Security:SecretEncryptionKey` with a planned outage |
