B_VIP Tool — Samsung Odin engine folder
=======================================

The "Samsung Odin" screen flashes BL / AP / CP / CSC firmware through a command-line
engine that must sit in THIS folder. The folder is copied next to B_VIP.Desktop.exe on
build, and the installer ships it inside B_VIP_Tool_Setup.exe.

The screen runs the engine fully silently: no window is ever opened, output is streamed
into the on-screen console, and the progress bar and PASS!/FAIL! come from that output.

Supported engines (first match wins)
------------------------------------
1. odin4.exe   — the CLI build of Odin4. Put odin4.exe and every DLL it needs in this
                 folder. Command line used by the app:

                   odin4 -b <BL> -a <AP> -c <CP> -s <CSC> [--pit <file.pit>] [--no-reboot]

2. heimdall.exe — Heimdall, the open-source Odin-compatible flasher. Bundled in this
                  release together with libusb-1.0.dll. Heimdall writes partitions, not
                  Odin packages, so the app unpacks the selected packages into
                  %TEMP%\BVIP_ODIN, then runs:

                   heimdall flash --BOOT boot.img --RECOVERY recovery.img ...
                                  [--pit <file.pit>] [--no-reboot] --verbose --stdout-errors

                  Every image in the package is mapped to its PIT partition name. The
                  temporary folder is deleted as soon as the flash ends.

GUI flashers are deliberately NOT supported: a window cannot be supervised, so B_VIP
never claims progress or a verdict it cannot observe.

If no engine is present the screen says so, still validates the firmware picks, and
offers "Open engine folder" so one can be dropped in. No engine is downloaded at
runtime: everything in this folder travels inside B_VIP_Tool_Setup.exe, so the tab
works offline on a workshop machine.

Bundled in this release
-----------------------
heimdall.exe (Heimdall CLI for Windows) + libusb-1.0.dll. The engine badge reads
"Engine ready · CLI (Silent) · Heimdall". Dropping an odin4.exe (with its DLLs) into
this folder upgrades the screen to package-level flashing, which skips the unpack step.

Windows driver note
-------------------
Heimdall talks to the handset through libusb, so the Samsung Download Mode interface
must be bound to WinUSB/libusb. The Drivers & Prerequisites screen installs the Samsung
USB driver; the Zadig tool can rebind the interface to WinUSB if Heimdall reports the
device as busy or not found.

Firmware notes
--------------
* BL accepts *.tar, *.md5 and *.bin (bootloader packages are sometimes plain binaries).
* AP, CP and CSC accept *.tar and *.md5.
* Only the slots that are filled are passed to the engine, so a single-package flash
  (for example AP only) works exactly as it does in Odin.
* "Auto Reboot" maps to the engine's own reboot switch. "F. Reset Time" is accepted by
  the UI; the CLI engines rewrite the timestamps unconditionally.
