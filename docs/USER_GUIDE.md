# B_VIP Tool — User Guide

**For technicians using the desktop client.** Interface language can be switched at any time
between English and العربية from the header (the layout flips to right-to-left for Arabic).

---

## 1. Signing in

1. Start **B_VIP Tool** from the Start menu or desktop shortcut.
2. Enter your **user name** (or e-mail) and **password**.
3. The client remembers the server address from its settings; the first launch uses the address
   configured by your administrator. Change it in **Settings → Server address** if you run your
   own backend.

On success the shell opens: the product name and your display name sit in the header, the module
navigation is on the left and the status bar at the bottom shows what the current screen is doing.
If the server is unreachable the sign-in page tells you, and the address you are trying is shown so
you can correct it.

Security notes that affect you day to day:

- Five wrong passwords lock the account for 15 minutes; ask an administrator to unlock it sooner.
- Your session is renewed automatically while you work. **Sign out** in the header clears the
  refresh token from this computer.
- The client never stores your password; only the session token is kept, in your Windows profile.

## 2. The shell

The left navigation lists the modules you are allowed to use:

| Module | What it does |
|---|---|
| **Dashboard** | Your recent operations, open tickets, license status, quick actions |
| **Device Manager** | Detect connected phones, read model/serial/Android details, reboot to system, bootloader or recovery |
| **Android Diagnostics** | Battery, storage, memory, network, build and sensor checks with a pass/warn/fail report |
| **ADB Tools** | Device list, connection check, restart ADB server, collect logcat, run a shell command |
| **Fastboot Tools** | Fastboot device list, read variables, reboot, flash an image, erase a partition (guarded) |
| **Samsung / MediaTek / Qualcomm / Apple** | Vendor-specific connection diagnostics and guidance |
| **Google Recovery** | Step-by-step, policy-compliant account recovery guidance for your customer |
| **Service Operations** | Every operation you ran, with status, duration, exit code and output |
| **Diagnostic Reports** | Saved reports, searchable and exportable |
| **License Activation** | Your plan, expiry, activation count, verify / activate / deactivate |
| **Support Center** | Create tickets, follow replies, attach a diagnostic report |
| **Notifications** | Server announcements, license warnings, ticket updates |
| **Settings** | Server address, language, platform-tools folder, diagnostics defaults |
| **About** | Version, license holder, build target, author |

The **Admin** dashboard only appears for administrators. Every other screen is shown to all
signed-in users, but the server independently checks permissions on each call, so a screen you may
not use reports a clear "permission denied" instead of showing data.

## 3. Preparing the machine for device work

Device operations need Google **platform-tools** (`adb.exe`, `fastboot.exe`).

1. Install Android platform-tools (or copy the folder to the machine).
2. In **Settings → Platform tools folder**, select the folder that contains `adb.exe`.
3. Leave it empty to use the system `PATH`.

The client never downloads tools on its own. The **Device Manager** shows tool status (found /
missing / version) before you attempt an operation, so a missing tool is visible immediately.

USB drivers for Samsung, Qualcomm, MediaTek and Apple devices are still a Windows
responsibility — install the vendor driver once per machine.

## 4. Working with a device

### 4.1 Connect and identify

1. Enable **Developer options → USB debugging** on the phone and accept the RSA prompt.
2. Connect the cable and press **Scan** in **Device Manager**.
3. The device list shows serial, state (`device`, `unauthorized`, `offline`, `bootloader`),
   model, manufacturer, Android version and build.

If the phone appears as `unauthorized`, unlock the screen and accept the prompt. If it does not
appear at all, see [TROUBLESHOOTING.md](TROUBLESHOOTING.md).

### 4.2 Run a diagnostic

Open **Android Diagnostics**, choose the checks you need and press **Run**. Each check returns
pass, warning or fail with the raw value, and the run is saved as a **Diagnostic Report** you can
attach to a ticket or export.

Vendor modules follow the same pattern: select the connected device, press **Run**, read the
result and the guidance text. They report what the device exposes; they never claim to unlock,
bypass FRP/activation locks or change IMEI — such requests are out of scope by design.

### 4.3 ADB tools

- **List devices** — same view as Scan, including unauthorized devices.
- **Connection check** — verifies the bridge and the device answer.
- **Restart ADB server** — fixes the common "device offline" state.
- **Collect logcat** — captures the last minutes of the device log into a report.
- **Shell command** — run a single `adb shell` command; the output is stored in the operation
  history. Avoid destructive commands unless you have a backup.

### 4.4 Fastboot tools

Use **Reboot to bootloader** from Device Manager first. Fastboot operations are guarded:

- **Read variables** is safe.
- **Flash image** and **Erase partition** require an explicit confirmation and are recorded in
  the audit log with your name, the device serial and the exact command. A wrong partition name
  bricks the phone — always confirm the image and partition with the customer's device in hand.

### 4.5 Every operation is recorded

Each operation the client starts is registered on the server before the tool runs, then the real
outcome (exit code, duration, output, timeout or cancellation) is reported back. That is why the
**Service Operations** list, the audit log and support tickets always agree with what happened on
the bench.

If a tool hangs, press **Cancel** in the operation banner. The client stops the process and marks
the operation cancelled; the server refuses a late "success" for it.

## 5. Reports

- A report is created automatically for diagnostics; you can also save ad-hoc results.
- Reports are private to your account; support staff with the right permission can see them when
  you attach one to a ticket.
- **Export** writes the selected report as JSON into `Documents\B_VIP` so you can archive or send
  it.
- Tokens and paths are redacted in exported content.

## 6. License

**License Activation** shows the plan, start date, expiry, remaining days and the machines using
your seats.

- **Verify** re-checks the license with the server (done automatically at sign-in).
- **Activate** binds this machine to a free seat.
- **Deactivate** releases the seat before moving to another computer.

When a license expires or is suspended, device operations are refused by the server with a clear
message while read-only areas (reports, tickets, settings) keep working. Contact support through
the **Support Center** rather than re-installing.

## 7. Support Center

1. **New ticket** — pick a category, write a title and a description.
2. Optionally attach the diagnostic report that shows the problem.
3. Replies appear in the ticket thread; the **Notifications** module lists unread updates and the
   server also sends them to the notification area of the desktop.
4. Internal notes written by support are not visible to you.

Include the device model, Android version, the exact operation and its timestamp — the operation
history gives you all of it.

## 8. Settings

| Setting | Meaning |
|---|---|
| Server address | Backend base URL, e.g. `http://192.168.1.10:5088` |
| Language | English or العربية, applied immediately |
| Platform tools folder | Folder containing `adb.exe` / `fastboot.exe` |
| Diagnostics timeout | How long a diagnostic may run before it is stopped |

Settings are stored per Windows user in `%APPDATA%\B_VIP\settings.json`.

## 9. Window and status bar

- The window is resizable; the navigation column keeps its width and the content area scrolls.
- The bottom status bar shows the current module, a progress indicator while a screen is busy, and
  the last status or error message — read it first when something does not work.
- Every screen reloads its data when you open it, so switching modules is also a refresh.
- Errors are shown in red in the status bar; they are never hidden in a console.

## 10. When something goes wrong

1. Note the operation id (top of the operation panel) and the time.
2. Check [TROUBLESHOOTING.md](TROUBLESHOOTING.md) for the symptom.
3. If it is not listed, open a ticket with the diagnostic report attached.

Nothing on the machine is lost by restarting the client; operations that were running are marked
interrupted on the server and can be retried.
