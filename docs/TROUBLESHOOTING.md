# B_VIP Tool — Troubleshooting

Find your symptom, apply the check, and quote the operation or correlation id when you open a
ticket.

---

## 1. Signing in

| Symptom | Cause | Fix |
|---|---|---|
| "The server at … is not reachable" | Wrong address, service down, firewall | `Get-Service BVIPBackend`; `curl http://<host>:5088/api/health`; check the address in **Settings** |
| `401` with "user name or password is incorrect" | Wrong credentials (the message never says which) | Re-type both; user names are case-insensitive |
| `423` account locked | Five failed attempts | Wait 15 minutes or have an admin reset the lock |
| `403` account suspended | Account status is not Active | Reactivate it from the admin console |
| Sign-in works, then everything is `401` | Clock skew or rotated signing key | Check server time; if `Auth:SigningKey` changed, everyone must sign in again |
| `429` too many attempts | Auth rate limit (10/min per IP) | Wait a minute; do not retry in a loop |

## 2. Client does not start

| Symptom | Cause | Fix |
|---|---|---|
| "You must install .NET Desktop Runtime" | Framework-dependent build without the runtime | Install the .NET 8 Desktop Runtime or use the self-contained build |
| Window opens then closes instantly | Unhandled startup exception | Run from a console: `B_VIP.Desktop.exe` shows the error; check `%APPDATA%\B_VIP\settings.json` is valid JSON |
| White/blank window | Theme resource missing after a partial copy | Reinstall; verify `Assets` and the theme dictionary shipped with the binary |
| Arabic text shows in the wrong direction | Old settings file with a stale language value | **Settings → Language**, save, restart |

## 3. Device not detected

1. **ADB Tools → List devices.** No device at all means the cable, driver or USB mode.
2. `unauthorized` → unlock the phone and accept the RSA dialog. If no dialog appears:
   `adb kill-server`, `adb start-server`, replug.
3. `offline` → **ADB Tools → Restart ADB server**, then replug.
4. Nothing in `adb devices` but Windows sees the phone → **driver**. Install the vendor driver
   (Samsung, Qualcomm, MediaTek, Apple) and check Device Manager for a yellow triangle.
5. Windows 11 with USB 3 issues → try another port, a powered hub, or a USB 2 port.
6. Phone in Fastboot → it will not appear in `adb`; use **Fastboot Tools → List devices**.
7. `adb.exe` missing → set **Settings → Platform tools folder**; the Device Manager tool list shows
   whether the client found it.

## 4. Operations

| Symptom | Meaning | Action |
|---|---|---|
| `license-required` / `license-not-active` | No active license for the account | License screen → verify; admin reactivates the license |
| `confirmation-required` (422) | Data-loss operation without confirmation | Confirm in the dialog; never confirm for an unknown partition |
| `unknown-operation` (400) | Client and server versions disagree | Update the client and the backend together |
| Operation stuck in `Validating` | No result was reported back | Cancel it; retry; if it repeats, check the tool path and the log |
| `TimedOut` | Tool exceeded its timeout | Increase the timeout for that operation or check the cable/device state |
| `RequiresAttention` | Output could not be classified | Read the operation output and decide manually |
| Result arrives but nothing changes | The operation was already terminal (cancelled/finished) | Expected; the server keeps the first final state |

## 5. Fastboot problems

- "waiting for device": the phone is not in bootloader mode — reboot to bootloader first.
- `FAILED (remote: 'Flashing is not allowed')`: locked bootloader; the device must be unlocked by
  its owner through the vendor's official procedure.
- `FAILED (remote: 'unknown partition')`: wrong partition name for that model. Stop; verify the
  firmware package.
- Flashing stops halfway: **do not unplug**. Let it fail completely, then re-flash from a known good
  image; a half-written boot partition needs a full firmware restore.

## 6. Backend service

| Symptom | Cause | Fix |
|---|---|---|
| Service starts then stops | Startup exception (missing key, bad connection string) | `Get-Content C:\ProgramData\B_VIP\logs\backend-*.log -Tail 50` |
| `Auth:SigningKey is missing or too short` | No signing key | Set `Auth__SigningKey` (≥ 32 chars) in the service environment and restart |
| `/api/health` refuses connection | Not listening / firewall | `netstat -ano | findstr :5088`; check the firewall rule created by the installer |
| `28P01` password authentication failed | Wrong database password | Fix `ConnectionStrings__Default`; restart |
| `42P01 relation ... does not exist` | Migrations did not run | Start the service and read the log; run `dotnet ef database update` |
| Port already in use | Another instance | `netstat -ano` → stop the other process or change `-Port` |
| All business calls return `503 maintenance` | Maintenance mode is on | `GET /api/admin/maintenance`, then `PATCH` with `enabled:false` |
| Restore ran but clients see old data | Restored into a different database or the service cached connections | Restart the service and verify `ConnectionStrings__Default` |

## 7. Licenses

| Symptom | Cause | Fix |
|---|---|---|
| "The activation limit for this license has been reached." | All seats are in use | Delete an unused activation in the admin console, or extend the plan's activation count |
| A replaced machine still consumes a seat | The old activation was never released | Delete the old activation (`DELETE /api/licenses/{id}/activations/{activationId}`), then activate the new machine |
| License expires immediately | Issued with a past start date or a zero-day plan | Re-issue with the right plan |
| Two machines claim the same fingerprint | Cloned VM or copied fingerprint | Revoke the license and investigate; do not raise the seat count |

## 8. Support centre and reports

- "Report not found" → the report id belongs to another account; export it and send the file with
  the ticket description instead.
- Export writes nothing → `Documents\B_VIP` is not writable (redirected or locked-down profile);
  free up the folder or export from another Windows account.
- Ticket replies not visible → a reply marked as an internal note is only shown to support staff.

## 9. Logs and what to send to support

| What | Where |
|---|---|
| Backend log | `C:\ProgramData\B_VIP\logs\backend-YYYYMMDD.log` |
| Windows service events | Event Viewer → Windows Logs → Application (source `BVIPBackend`) |
| Client errors | Status bar of the window, then a diagnostic report |
| Operation evidence | `POST /api/device-operations/{id}` and `/events` |
| Correlation id | Response header `X-Correlation-ID`, or the error body field |

When you open a ticket, include: user name, machine name, device model and Android version, the
operation type and id, the exact time (with time zone) and the error code.

## 10. Emergency actions

| Situation | Action |
|---|---|
| Suspected account compromise | Admin console → revoke sessions → reset password |
| Suspected key leak | Rotate `Auth:SigningKey` (all sessions die) and delete/rotate integration secrets |
| Database corruption | Stop the service → restore the latest `pg_dump` → start → verify `/api/health` |
| Runaway disk usage | Check `backups`, `uploads`, and the log folder; tighten retention settings |
| Lost bootstrap token before the first admin | Set a new `Bootstrap__FirstAdminToken` and restart the service (the endpoint still works while no super admin exists) |
