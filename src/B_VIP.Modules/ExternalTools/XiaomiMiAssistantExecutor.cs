using System.IO;
using B_VIP.Contracts.Devices;
using B_VIP.Core.Operations;
using B_VIP.Hardware;

namespace B_VIP.Modules.ExternalTools
{
    /// <summary>What the Mi Assistant routine is asked to do with the payload.</summary>
    public enum MiAssistantMode
    {
        /// <summary>Check the sideload link and lay out the physical procedure.</summary>
        Guidance,

        /// <summary>Send the FRP wipe payload.</summary>
        Frp,

        /// <summary>Send the factory-reset payload.</summary>
        Wipe
    }

    /// <summary>
    /// Xiaomi Mi Assistant / sideload mode. A locked bootloader refuses every fastboot erase, so the
    /// recovery-side path is the only one left: Mi Recovery 5.0 exposes "Connect with MIAssistant",
    /// which puts the phone in sideload mode, and a signed payload is then pushed with
    /// <c>adb sideload</c>.
    ///
    /// The payload is never invented here: it is whatever the technician placed in
    /// <c>Tools\Xiaomi</c> (or selected explicitly). Without a payload the routine says exactly that
    /// instead of pretending an erase happened, and the guidance mode still walks through the steps.
    /// </summary>
    public sealed class XiaomiMiAssistantExecutor : IOperationExecutor
    {
        private readonly IAdbClient _adb;
        private readonly Func<string> _payloadRoot;

        public XiaomiMiAssistantExecutor(IAdbClient adb, Func<string>? payloadRoot = null)
        {
            _adb = adb;
            _payloadRoot = payloadRoot ?? (() => DefaultPayloadRoot);
        }

        public IReadOnlyList<string> SupportedOperations { get; } =
        [
            OperationTypes.XiaomiMiAssistant,
            OperationTypes.XiaomiMiAssistantFrp,
            OperationTypes.XiaomiMiAssistantWipe
        ];

        /// <summary>Where sideload payloads are looked for: <c>Tools\Xiaomi</c> beside the executable.</summary>
        public static string DefaultPayloadRoot
            => Path.Combine(AppContext.BaseDirectory, "Tools", "Xiaomi");

        public async Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
        {
            MiAssistantMode mode = request.OperationType.ToLowerInvariant() switch
            {
                OperationTypes.XiaomiMiAssistantFrp => MiAssistantMode.Frp,
                OperationTypes.XiaomiMiAssistantWipe => MiAssistantMode.Wipe,
                _ => MiAssistantMode.Guidance
            };

            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            if (mode != MiAssistantMode.Guidance &&
                !string.Equals(request.GetParameter("confirm"), "yes", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Sending a Mi Assistant payload must be confirmed explicitly before it runs.");
                checks.Add(new DiagnosticCheckDto("confirm", "fail", "The routine was not confirmed.", null));
                return VendorSupport.Build(request.OperationType, "Confirmation required.", checks, log, warnings, errors);
            }

            request.Process("Checking for a handset in Mi Assistant / sideload mode.");

            ProcessResult devices = await _adb.AdbAsync(["devices", "-l"], 25, ct);
            string listing = devices.StandardOutput ?? string.Empty;
            log.Add("$ adb devices -l");
            foreach (string line in listing
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                log.Add("  " + VendorSupport.Truncate(line, 200));
            }

            bool sideload = IsSideloadListing(listing);

            checks.Add(new DiagnosticCheckDto("sideload-mode", sideload ? "pass" : "warn",
                sideload
                    ? "A handset answered in sideload mode."
                    : "No handset is in sideload mode. Mi Assistant only appears once the phone is booted into Mi Recovery.",
                devices.DurationMs));

            // The steps are the same whether or not the phone is attached yet: they are the procedure.
            log.Add(string.Empty);
            log.Add("=== Mi Assistant / sideload procedure ===");
            log.Add("1. Power the phone off, then hold Vol+ and Power to boot Mi Recovery.");
            log.Add("2. Choose 'Connect with MIAssistant' (or 'Apply update from ADB'). The screen then waits for a sideload.");
            log.Add("3. Plug the cable in. Run this routine again: it will confirm the sideload link is up.");
            log.Add("4. Select the signed firmware or wipe payload for this exact model and start the sideload.");
            log.Add("5. When the phone boots, sign the required account in once so the setup wizard completes normally.");

            if (!sideload)
            {
                errors.Add("No handset is in Mi Assistant / sideload mode. Boot the phone into Mi Recovery and choose " +
                           "'Connect with MIAssistant', then run this routine again.");
                warnings.Add("A locked bootloader cannot be bypassed with fastboot: the Mi/Google account step is held in the " +
                             "signed firmware, not in an erasable partition.");

                if (mode != MiAssistantMode.Guidance)
                {
                    warnings.Add("The button listens for the handset: enter Mi Recovery first and the routine picks it up on the next attempt.");
                }

                return VendorSupport.Build(request.OperationType, "Sideload handset not detected.", checks, log, warnings, errors);
            }

            if (mode == MiAssistantMode.Guidance)
            {
                warnings.Add("The sideload itself is started from the firmware package for this model. This routine never " +
                             "flashes an image on its own, so verify the build before you send it.");
                warnings.Add("Charge the handset above 50% and do not unplug the cable during the upload.");

                return VendorSupport.Build(request.OperationType,
                    "Handset is in sideload mode. Follow the procedure in the details column to apply the signed firmware.",
                    checks, log, warnings, errors);
            }

            // A signed payload is required; use the selected one, else the matching file in Tools\Xiaomi.
            string? payload = ResolvePayload(request.GetParameter("payloadPath"), mode, _payloadRoot());
            if (payload is null)
            {
                string kind = mode == MiAssistantMode.Frp ? "FRP" : "wipe";
                string detail = $"No {kind} payload was found. Place the signed {kind} package (a .zip) in " +
                                $"'{_payloadRoot()}' or select it explicitly, then run the routine again. B_VIP does not " +
                                "ship or fabricate account-attestation payloads.";
                errors.Add(detail);
                checks.Add(new DiagnosticCheckDto("payload", "fail", detail, null));
                return VendorSupport.Build(request.OperationType, $"No {kind} payload.", checks, log, warnings, errors);
            }

            checks.Add(new DiagnosticCheckDto("payload", "pass", $"Payload: {Path.GetFileName(payload)}", null));
            warnings.Add("Do not unplug the cable: the recovery applies the payload as it streams and may reboot mid-transfer.");

            request.Process($"Sideloading {Path.GetFileName(payload)}.");
            ProcessResult result = await _adb.AdbAsync(["sideload", payload], 1800, ct);

            log.Add("$ adb sideload " + Path.GetFileName(payload));
            foreach (string line in (result.StandardOutput ?? string.Empty)
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .TakeLast(20))
            {
                log.Add("  " + VendorSupport.Truncate(line, 200));
            }

            if (result.Cancelled)
            {
                errors.Add("The sideload was cancelled.");
                return VendorSupport.Build(request.OperationType, "Cancelled.", checks, log, warnings, errors);
            }

            if (!result.Succeeded)
            {
                string reason = VendorSupport.FirstLine((result.StandardError ?? string.Empty) + " " + (result.StandardOutput ?? string.Empty));
                errors.Add(reason.Length > 0
                    ? $"The sideload failed: {VendorSupport.Truncate(reason, 220)}"
                    : $"The sideload exited with code {result.ExitCode}.");
                checks.Add(new DiagnosticCheckDto("sideload", "fail", errors[^1], result.DurationMs));
                return VendorSupport.Build(request.OperationType, "The sideload did not complete.", checks, log, warnings, errors);
            }

            checks.Add(new DiagnosticCheckDto("sideload", "pass", "The recovery accepted the payload.", result.DurationMs));
            warnings.Add("Wait for the recovery to finish and reboot by itself; the first boot after a payload can take five minutes.");

            return VendorSupport.Build(request.OperationType,
                mode == MiAssistantMode.Frp
                    ? "Mi Assistant FRP payload applied."
                    : "Mi Assistant wipe payload applied.",
                checks, log, warnings, errors);
        }

        /// <summary>True when <c>adb devices -l</c> lists a recovery waiting for a sideload.</summary>
        public static bool IsSideloadListing(string? listing)
            => !string.IsNullOrWhiteSpace(listing) &&
               listing.Split('\n').Any(line => line.Contains("sideload", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Picks the payload to send: the explicit selection when it exists, otherwise the first .zip in
        /// <paramref name="root"/> whose name mentions the mode. Returns null when nothing matches.
        /// </summary>
        public static string? ResolvePayload(string? explicitPath, MiAssistantMode mode, string root)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            {
                return Path.GetFullPath(explicitPath);
            }

            if (mode == MiAssistantMode.Guidance)
            {
                return null;
            }

            try
            {
                if (!Directory.Exists(root))
                {
                    return null;
                }

                string marker = mode == MiAssistantMode.Frp ? "frp" : "wipe";
                return Directory.EnumerateFiles(root, "*.zip", SearchOption.TopDirectoryOnly)
                    .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(file => Path.GetFileName(file).Contains(marker, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
