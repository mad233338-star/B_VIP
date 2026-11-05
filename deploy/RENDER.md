# Deploying the real B_VIP backend to Render

The desktop client has `https://b-vip.onrender.com` compiled in (`ApiConfiguration.cs`), so the
goal is to make **that hostname** serve this repository's backend. Today it answers every login
with a canned Admin session, which is why the client refuses arbitrary credentials and why no
genuine account can sign in.

## What is already in this repository

| File | Purpose |
| --- | --- |
| `Dockerfile` | Linux build of `src/B_VIP.Backend` (ASP.NET Core 8). Verified with `dotnet publish -r linux-x64`; the backend has no Windows-only dependencies. |
| `.dockerignore` | Keeps `bin/`, `obj/`, `artifacts/`, `tests/` out of the build context. |
| `render.yaml` | Blueprint: free PostgreSQL plus the Docker web service named `b-vip`, with `Database__Provider=postgres`, the database connection string, a generated `Auth__SigningKey`, CORS origin and SMTP placeholders. |
| `.gitignore` | Keeps build output and `artifacts/` out of the GitHub push. |

The backend seeds itself on first start: 6 roles, 25 permissions, 7 licence plans and the master
administrator `Admin Bashir` / `bviptool@gmail.com` with a lifetime, non-expiring licence and no
HWID binding.

## Step 1 — put this project on GitHub

The folder is not a git repository yet, so there is nothing to push. From the project root:

```bash
git init
git add .
git commit -m "B_VIP Tool: backend, desktop client and Render deployment files"
git branch -M main
git remote add origin https://github.com/<your-account>/<your-repo>.git
git push -u origin main
```

Create the empty repository on github.com first. `git status` will confirm that only sources,
`Dockerfile`, `render.yaml`, `docs/` and `deploy/` are staged — `bin`, `obj`, `artifacts` and
`.tmp` are ignored.

## Step 2 — make Render serve that repository on `b-vip.onrender.com`

Render derives the public hostname from the **service name**, so the hostname stays
`b-vip.onrender.com` only for a service named `b-vip`:

* **If `b-vip` is the current demo service:** open it in the Render dashboard →
  *Settings* → *Build & Deploy* → change *Repository* to the one you just pushed, set
  *Runtime* to **Docker** and *Dockerfile Path* to `./Dockerfile`, then *Manual Deploy →
  Deploy latest commit*. Delete the demo server code from that repository so it cannot come back.
* **If the demo responder is a different service:** delete it first (or rename the new service),
  because the hostname is reserved while another service owns the name. Then use
  *New → Blueprint* and select the repository — `render.yaml` creates the database and the
  `b-vip` web service in one step.

## Step 3 — environment variables

The blueprint sets these automatically; for a manually created service add them under
*Environment*:

| Key | Value |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `Database__Provider` | `postgres` (prevents the silent SQLite fallback, whose data is wiped on each deploy) |
| `ConnectionStrings__Default` | the *Internal Database URL* of the Render PostgreSQL instance |
| `Auth__SigningKey` | random string, at least 32 characters |
| `Cors__AllowedOrigins__0` | `https://b-vip.onrender.com` |
| `Email__PublicBaseUrl` | `https://b-vip.onrender.com` |
| `Email__Host` / `Email__Port` / `Email__UserName` / `Email__Password` / `Email__From` | the SMTP account used for technician OTP mail (587 + SSL for most providers) |

Without the `Email__*` values, registration still succeeds but the OTP mail is not delivered; the
login of the master administrator does not depend on it.

## Step 4 — verify the deployment

```bash
curl -s https://b-vip.onrender.com/api/health
# -> Healthy

curl -s -o /dev/null -w "%{http_code}\n" -X POST https://b-vip.onrender.com/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"userName":"zzz.nobody","password":"nope"}'
# -> 401 (not 200 "Login successful")

curl -s -X POST https://b-vip.onrender.com/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"userName":"bviptool@gmail.com","password":"Bashir0935784653AdminB"}'
# -> 200 with tokens.accessToken, user.roles ["Super Admin"], license.planCode "lifetime",
#    license.expiresAt null, user.hardwareId null
```

The `accessToken` payload should contain `usr` (`Admin Bashir`), `email` (`bviptool@gmail.com`)
and `rol` (`Super Admin`).

## Step 5 — desktop client

No rebuild is needed: the endpoint is compiled in and the released
`artifacts/desktop/B_VIP.Desktop.exe` already targets `https://b-vip.onrender.com`. Signing in
with the master administrator opens the Administrator Console directly.

## Notes

* Render's free PostgreSQL instance expires after 90 days; upgrade it or export the data before
  then, otherwise the accounts disappear with it.
* The first start applies the EF migrations, adds the onboarding columns for older databases and
  seeds the master administrator. If a previous database is reused, the seeder re-asserts the
  master administrator's password and clears any hardware binding.
* Keep `Auth__SigningKey` stable: changing it invalidates every issued token and forces all
  technicians to sign in again.
