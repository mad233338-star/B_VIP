using System.IO;
using B_VIP.Contracts.Devices;
using B_VIP.Core.Operations;
using B_VIP.Hardware;

namespace B_VIP.Modules.ExternalTools
{
    /// <summary>
    /// Unisoc/Spreadtrum FRP through <c>spd_dump</c>: FDL1 is uploaded to its base address, FDL2 to
    /// its own, and the tool then erases the FRP partition and resets the handset:
    ///
    /// <code>
    /// spd_dump fdl fdl1-dl.bin 0x50000000 fdl fdl2-dl.bin 0x9efffe00 erase_partition frp reset
    /// </code>
    ///
    /// The FDL images come from the loader bundle or from the <c>.pac</c> packet the operator
    /// selected on the Unisoc screen, so the routine works on a locked bootloader.
    /// </summary>
    public sealed class SpdDumpFrpExecutor : IOperationExecutor
    {
        internal const string Fdl1Address = "0x50000000";
        internal const string Fdl2Address = "0x9efffe00";

        private readonly IProcessRunner _runner;
        private readonly IVendorToolchain _toolchain;

        public SpdDumpFrpExecutor(IProcessRunner runner, IVendorToolchain? toolchain = null)
        {
            _runner = runner;
            _toolchain = toolchain ?? new VendorToolchain();
        }

        public IReadOnlyList<string> SupportedOperations { get; } = [OperationTypes.SpdFrpDump];

        public async Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
        {
            VendorCliSession session = new(_runner, request);

            if (!string.Equals(request.GetParameter("confirm"), "yes", StringComparison.OrdinalIgnoreCase))
            {
                session.Errors.Add("Erasing FRP through the Unisoc boot ROM must be confirmed explicitly before it runs. " +
                                   "Confirm the warning in the client and retry.");
                session.Checks.Add(new DiagnosticCheckDto("confirm", "fail", "The routine was not confirmed.", null));
                return VendorSupport.Build(request.OperationType, "Confirmation required.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            VendorToolResolution tool = await Task.Run(() => _toolchain.ResolveSpdDump(), ct);
            if (!tool.Found)
            {
                session.Errors.Add(tool.Detail);
                session.Checks.Add(new DiagnosticCheckDto("spd_dump", "fail", tool.Detail, null));
                return VendorSupport.Build(request.OperationType, "spd_dump is not available.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            session.Check("spd_dump", true, tool.Detail);

            string? fdl1 = FirstExisting(request.GetParameter("fdl1Path"));
            string? fdl2 = FirstExisting(request.GetParameter("fdl2Path"));

            if (fdl1 is null)
            {
                const string detail = "FDL1 was not found. Select it on the Unisoc screen (or extract a .pac packet) before running the routine.";
                session.Errors.Add(detail);
                session.Checks.Add(new DiagnosticCheckDto("fdl1", "fail", detail, null));
                return VendorSupport.Build(request.OperationType, "FDL1 missing.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            session.Check("fdl1", true, $"FDL1: {Path.GetFileName(fdl1)}");

            if (fdl2 is null)
            {
                const string detail = "FDL2 was not found. spd_dump must upload both stages before it can reach the partitions: " +
                                      "select the FDL2 image (or extract a .pac packet) and retry.";
                session.Errors.Add(detail);
                session.Checks.Add(new DiagnosticCheckDto("fdl2", "fail", detail, null));
                return VendorSupport.Build(request.OperationType, "FDL2 missing.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            session.Check("fdl2", true, $"FDL2: {Path.GetFileName(fdl2)}");

            string fdl1Address = Address(request.GetParameter("fdl1Address"), Fdl1Address);
            string fdl2Address = Address(request.GetParameter("fdl2Address"), Fdl2Address);
            string partition = NormalisePartition(request.GetParameter("partition"));

            List<string> arguments =
            [
                "fdl", fdl1, fdl1Address,
                "fdl", fdl2, fdl2Address,
                "erase_partition", partition,
                "reset"
            ];

            if (NormalisePort(request.GetParameter("port")) is { } port)
            {
                session.Note($"The boot ROM port was pinned to {port}; spd_dump opens the first matching interface.");
            }

            int timeout = ParseTimeout(request.GetParameter("timeoutSeconds"), 600);

            session.Stage("Hold Vol+ (or the boot-ROM key) while connecting the cable, then keep the phone still.");
            session.Stage($"Uploading FDL1 at {fdl1Address}, FDL2 at {fdl2Address} and erasing '{partition}'.");

            ProcessResult? result = await session.RunAsync("spd-erase", tool.Executable, arguments,
                Path.GetDirectoryName(tool.Executable), timeout, ct);

            if (result is null || !result.Succeeded)
            {
                return VendorSupport.Build(request.OperationType,
                    session.Cancelled ? "Cancelled." : "The Unisoc boot-ROM routine did not complete.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            session.Warn("The handset resets and boots into setup. Confirm that the Google account step no longer appears.");

            return VendorSupport.Build(request.OperationType,
                $"FRP erased through spd_dump ('{partition}') and the handset was reset.",
                session.Checks, session.Log, session.Warnings, session.Errors);
        }

        /// <summary>The base addresses are fixed for this SoC family; a caller may override them.</summary>
        internal static string Address(string? value, string fallback)
            => !string.IsNullOrWhiteSpace(value) && value.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? value.Trim()
                : fallback;

        internal static string NormalisePartition(string? value)
        {
            string[] allowed = ["frp", "persistent", "persist", "frp_p"];
            string requested = (value ?? "frp").Trim().ToLowerInvariant();
            return allowed.Contains(requested) ? requested : "frp";
        }

        internal static int ParseTimeout(string? value, int fallback)
            => int.TryParse(value, out int seconds) && seconds is >= 30 and <= 3600 ? seconds : fallback;

        internal static string? NormalisePort(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string text = value.Trim();
            int marker = text.IndexOf("COM", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
            {
                return null;
            }

            string tail = text[marker..];
            int end = 3;
            while (end < tail.Length && char.IsDigit(tail[end]))
            {
                end++;
            }

            return end > 3 ? tail[..end].ToUpperInvariant() : null;
        }

        private static string? FirstExisting(string? path)
            => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? Path.GetFullPath(path) : null;
    }
}
