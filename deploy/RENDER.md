# Deploying the real B_VIP backend to Render

The desktop client has `https://b-vip.onrender.com` compiled in (`ApiConfiguration.cs`), so the
goal is to make **that hostname** serve a backend that really authenticates. There are two
implementations in this repository and only one should be deployed:

| Runtime | Entry point | Notes |
| --- | --- | --- |
| Node.js | `server.js` (`npm start`) | The service that has been running on `b-vip.onrender.com`. Real PostgreSQL + BCrypt authentication, HWID binding, OTP mail, remote-support WebSocket relay. |
| .NET | `Dockerfile` → `src/B_VIP.Backend` | The full ASP.NET Core API (users, licences, tickets, audit). Verified with `dotnet publish -r linux-x64`. |

Deploy one of them. The verification steps at the end apply to both.

## What is already in this repository

- `server.js` — the Node service. Its login endpoint verifies the BCrypt password, refuses unknown
  accounts and wrong passwords with `401 invalid-credentials`, binds a technician account to the
  first workstation that signs in, refuses any other machine with
  `403 device-mismatch` ("This account is bound to another computer. Please contact administrator
  to reset HWID."), exempts administrators from both the HWID check and the e-mail OTP step, and
  returns role-based JWT claims. When the database is unreachable it answers
  `503 database-unavailable` — it never answers a login with a canned session.
- `server.js` also migrates an older `users` table on start: a legacy integer `id` column is widened
  to `varchar(100)` (this was the `invalid input syntax for type integer` error), missing
  account/licence columns are added, and the expiry column is copied into the licence column.
- It seeds the master administrator `Admin Bashir` / `bviptool@gmail.com` with the BCrypt hash of
  its password, role `lifetime_admin`, a lifetime non-expiring licence and no HWID binding. The
  password is re-asserted on every start, so a lost admin password is recoverable by redeploying.
- `tests/node/` — 24 authentication tests (`npm test`), including a run of the real SQL through
  a PostgreSQL-compatible engine.
- `Dockerfile`, `.dockerignore`, `render.yaml` — the .NET deployment path.
- `.gitignore` — keeps `bin/`, `obj/`, `artifacts/`, `.tmp/` and `node_modules/` out of the push.

## Step 1 — the repository

Already pushed to `https://github.com/mad233338-star/B_VIP.git` (`main`). To work on it elsewhere:

```bash
git clone https://github.com/mad233338-star/B_VIP.git
cd B_VIP
```

## Step 2 — make Render serve this code on `b-vip.onrender.com`

Render derives the public hostname from the **service name**, so the hostname stays
`b-vip.onrender.com` only for a service named `b-vip`:

* **If `b-vip` is the current service:** open it in the Render dashboard → *Settings* →
  *Build & Deploy* → confirm *Repository* is this one and the *Branch* is `main`. Keep
  *Runtime* **Node** with *Build Command* `npm install` and *Start Command* `npm start`
  (or switch *Runtime* to **Docker** with *Dockerfile Path* `./Dockerfile` to run the .NET API).
  Then *Manual Deploy → Deploy latest commit*.
* **If the responder lives in a different service:** delete it first (or rename the new service),
  because the hostname is reserved while another service owns the name. Then use
  *New → Blueprint* and select the repository — `render.yaml` creates PostgreSQL and the `b-vip`
  web service in one step.

## Step 3 — environment variables

| Key | Used by | Value |
| --- | --- | --- |
| `DATABASE_URL` | Node | the *Internal Database URL* of the Render PostgreSQL instance (or let the Blueprint inject it) |
| `Auth__SigningKey` | Node + .NET | random string, at least 32 characters; keep it stable, it signs every JWT |
| `Email__Host` / `Email__Port` / `Email__UserName` / `Email__Password` / `Email__From` | both | SMTP account for the technician verification code (587 with SSL for most providers) |
| `Database__Provider` | .NET only | `postgres` (prevents the silent SQLite fallback, whose data is wiped on each deploy) |
| `ConnectionStrings__Default` | .NET only | same database as `DATABASE_URL` |
| `Cors__AllowedOrigins__0` | .NET only | `https://b-vip.onrender.com` |

Without the `Email__*` values the server logs that verification codes cannot be delivered;
registration still succeeds and the account stays unverified, but the master administrator login
does not depend on e-mail at all.

## Step 4 — verify the deployment

```bash
curl -s https://b-vip.onrender.com/api/health
# Node: {"status":"online", ..., "database":"connected"}   .NET: "Healthy"

curl -s -o /dev/null -w "%{http_code}\n" -X POST https://b-vip.onrender.com/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"userName":"zzz.nobody","password":"nope"}'
# -> 401 (not 200 "Login successful")

curl -s -X POST https://b-vip.onrender.com/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"userName":"bviptool@gmail.com","password":"Bashir0935784653AdminB"}'
# -> 200 with tokens.accessToken, user.roles ["Admin","lifetime_admin"], license.status "Active",
#    license.expiresAt null, user.hardwareId null
```

A login with the wrong password for a real account must be `401 invalid-credentials`, and a
technician signing in from a second computer must be `403 device-mismatch`.

## Step 5 — desktop client

No rebuild is needed: the endpoint is compiled in and the released
`artifacts/desktop/B_VIP.Desktop.exe` already targets `https://b-vip.onrender.com`. Signing in
with the master administrator opens the Administrator Console directly.

## Notes

* Render's free PostgreSQL instance expires after 90 days; upgrade it or export the data before
  then, otherwise the accounts disappear with it.
* Accounts created by the previous stub stored their password verbatim. On the first successful
  sign-in after this change such a password is accepted once and immediately replaced with a
  BCrypt hash.
* Keep `Auth__SigningKey` stable: changing it invalidates every issued token and forces all
  technicians to sign in again.
