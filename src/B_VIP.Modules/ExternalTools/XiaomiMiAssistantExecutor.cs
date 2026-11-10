using B_VIP.Contracts.Devices;
using B_VIP.Core.Operations;
using B_VIP.Hardware;

namespace B_VIP.Modules.ExternalTools
{
    /// <summary>
    /// The fallback for a Xiaomi handset whose bootloader is locked: fastboot refuses every erase, so
    /// the Google/Mi account step has to be cleared through Mi Assistant (sideload mode) with a
    /// signed firmware the account is entitled to. This routine verifies that a phone is actually in
    /// sideload mode and lays out the physical steps; it never flashes anything by itself.
    /// </summary>
    public sealed class XiaomiMiAssistantExecutor : IOperationExecutor
    {
        private readonly IAdbClient _adb;

        public XiaomiMiAssistantExecutor(IAdbClient adb) => _adb = adb;

        public IReadOnlyList<string> SupportedOperations { get; } = [OperationTypes.XiaomiMiAssistant];

        public async Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
        {
            List<DiagnosticCheckDto> checks = new();
            List<string> log = new();
            List<string> warnings = new();
            List<string> errors = new();

            request.Process("Checking for a handset in Mi Assistant / sideload mode.");

            ProcessResult devices = await _adb.AdbAsync(["devices", "-l"], 25, ct);
            string listing = devices.StandardOutput ?? string.Empty;
            log.Add("$ adb devices -l");
            foreach (string line in listing
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                log.Add("  " + VendorSupport.Truncate(line, 200));
            }

            bool sideload = listing
                .Split('\n')
                .Any(line => line.Contains("sideload", StringComparison.OrdinalIgnoreCase));

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
            log.Add("4. Select the signed firmware package for this exact model and start the sideload.");
            log.Add("5. When the phone boots, sign the required account in once so the setup wizard completes normally.");

            if (!sideload)
            {
                errors.Add("No handset is in Mi Assistant / sideload mode. Boot the phone into Mi Recovery and choose " +
                           "'Connect with MIAssistant', then run this routine again.");
                warnings.Add("A locked bootloader cannot be bypassed with fastboot: the Mi/Google account step is held in the " +
                             "signed firmware, not in an erasable partition.");
                return VendorSupport.Build(request.OperationType, "Sideload handset not detected.", checks, log, warnings, errors);
            }

            warnings.Add("The sideload itself is started from the firmware package for this model. This routine never " +
                         "flashes an image on its own, so verify the build before you send it.");
            warnings.Add("Charge the handset above 50% and do not unplug the cable during the upload.");

            return VendorSupport.Build(request.OperationType,
                "Handset is in sideload mode. Follow the procedure in the details column to apply the signed firmware.",
                checks, log, warnings, errors);
        }
    }
}
