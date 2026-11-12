B_VIP Tool — Xiaomi Mi Assistant payload folder
===============================================

The Xiaomi tab's two sideload buttons ("Mi Assistant FRP reset" and "Mi Assistant wipe
data") send a payload to a handset that is in Mi Recovery 5.0 / Mi Assistant mode. The
payload is not shipped inside B_VIP_Tool_Setup.exe: it is model-specific and signed, and
B_VIP never fabricates or distributes account-attestation packages.

How to use it
-------------
1. Boot the handset into Mi Recovery (Power off, then hold Vol+ and Power).
2. Choose "Connect with MIAssistant" (on some builds: "Apply update from ADB").
3. Plug the USB cable in. `adb devices` must list the phone with state `sideload`.
4. Put the signed payload for that exact model in THIS folder, named so it contains
   "frp" for the FRP routine or "wipe" for the factory-reset routine, for example:

       mi_frp_payload.zip
       mi_wipe_payload.zip

5. Press the matching button on the Xiaomi tab. B_VIP streams `adb sideload` output into
   the console, reports the result, and stops on cancel.

A file chosen explicitly by the caller always wins over the folder convention, so a
one-off package can be used without renaming it.

What happens without a payload
------------------------------
The button still detects the sideload link and prints the procedure, then reports that no
payload was found. Nothing is flashed, and no success is claimed.

Note
----
The ADB-side "Bypass Mi Account (ADB)" action is separate: it freezes
com.xiaomi.finddevice and com.miui.cloudservice over an authorized ADB connection after
setup, and needs USB debugging rather than recovery.
