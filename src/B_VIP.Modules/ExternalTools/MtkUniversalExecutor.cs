using System.Globalization;
using System.IO;
using B_VIP.Contracts.Devices;
using B_VIP.Core.Operations;
using B_VIP.Hardware;

namespace B_VIP.Modules.ExternalTools
{
    /// <summary>
    /// The MediaTek boot-ROM workbench: everything on the MediaTek screen that is neither FRP nor a
    /// scatter flash. It drives the mtkclient bundle the loader manager already installs, so there is
    /// one resolver, one streaming session and one cancellation path for the whole chipset.
    ///
    /// <list type="bullet">
    ///   <item><b>Unlock</b> erases <c>seccfg</c>, the partition that carries the lock state.</item>
    ///   <item><b>Relock</b> writes a previously dumped <c>seccfg</c> image back.</item>
    ///   <item><b>Backup</b> reads nvram, nvdata, nvcfg, protect1 and protect2 into a timestamped
    ///         folder under <c>Backups\NV</c>.</item>
    ///   <item><b>Restore</b> writes those dumps back, partition by partition.</item>
    ///   <item><b>Safe format</b> erases userdata only: metadata and the persistent FRP flags survive,
    ///         which is what keeps the handset out of an encryption loop.</item>
    /// </list>
    /// </summary>
    public sealed class MtkUniversalExecutor : IOperationExecutor
    {
        /// <summary>Partitions a NVRAM backup always contains, in the order it is read.</summary>
        public static readonly IReadOnlyList<string> NvPartitions =
            ["nvram", "nvdata", "nvcfg", "protect1", "protect2"];

        private readonly IProcessRunner _runner;
        private readonly IVendorToolchain _toolchain;
        private readonly Func<string> _backupRoot;

        public MtkUniversalExecutor(IProcessRunner runner, IVendorToolchain? toolchain = null,
            Func<string>? backupRoot = null)
        {
            _runner = runner;
            _toolchain = toolchain ?? new VendorToolchain();
            _backupRoot = backupRoot ?? (() => DefaultBackupRoot);
        }

        public IReadOnlyList<string> SupportedOperations { get; } =
        [
            OperationTypes.MtkUnlockBootloader,
            OperationTypes.MtkRelockBootloader,
            OperationTypes.MtkBackupNvram,
            OperationTypes.MtkRestoreNvram,
            OperationTypes.MtkSafeFormat
        ];

        /// <summary>The documented backup location: <c>Backups\NV</c> beside the executable.</summary>
        public static string DefaultBackupRoot
            => Path.Combine(AppContext.BaseDirectory, "Backups", "NV");

        public async Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
        {
            VendorCliSession session = new(_runner, request);

            if (RequiresConfirmation(request.OperationType) &&
                !string.Equals(request.GetParameter("confirm"), "yes", StringComparison.OrdinalIgnoreCase))
            {
                session.Errors.Add("This routine changes the handset and must be confirmed explicitly before it runs.");
                session.Checks.Add(new DiagnosticCheckDto("confirm", "fail", "The routine was not confirmed.", null));
                return VendorSupport.Build(request.OperationType, "Confirmation required.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            VendorToolResolution tool = await Task.Run(() => _toolchain.ResolveMtkClient(), ct);
            if (!tool.Found || tool.EntryScript is null)
            {
                session.Errors.Add(tool.Detail);
                session.Checks.Add(new DiagnosticCheckDto("mtkclient", "fail", tool.Detail, null));
                return VendorSupport.Build(request.OperationType, "mtkclient is not available.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            session.Check("mtkclient", true, tool.Detail);

            string workingDirectory = tool.WorkingDirectory ?? Directory.GetCurrentDirectory();

            return request.OperationType.ToLowerInvariant() switch
            {
                OperationTypes.MtkUnlockBootloader => await UnlockAsync(tool, session, workingDirectory, ct),
                OperationTypes.MtkRelockBootloader => await RelockAsync(request, tool, session, workingDirectory, ct),
                OperationTypes.MtkBackupNvram => await BackupAsync(tool, session, workingDirectory, ct),
                OperationTypes.MtkRestoreNvram => await RestoreAsync(request, tool, session, workingDirectory, ct),
                _ => await SafeFormatAsync(tool, session, workingDirectory, ct)
            };
        }

        private static bool RequiresConfirmation(string operationType)
            => operationType.Equals(OperationTypes.MtkUnlockBootloader, StringComparison.OrdinalIgnoreCase)
               || operationType.Equals(OperationTypes.MtkRelockBootloader, StringComparison.OrdinalIgnoreCase)
               || operationType.Equals(OperationTypes.MtkRestoreNvram, StringComparison.OrdinalIgnoreCase)
               || operationType.Equals(OperationTypes.MtkSafeFormat, StringComparison.OrdinalIgnoreCase);

        private async Task<OperationExecutionResult> UnlockAsync(VendorToolResolution tool, VendorCliSession session,
            string workingDirectory, CancellationToken ct)
        {
            session.Stage("Power the handset off, then hold Vol+ and Vol- while connecting the cable to enter BROM mode.");
            session.Stage("Erasing 'seccfg': the boot loader lock state lives there.");

            ProcessResult? erase = await session.RunAsync("unlock-seccfg", tool.Executable!,
                BuildEraseArguments(tool.EntryScript!, "seccfg"), workingDirectory, 300, ct);

            if (erase is null || !erase.Succeeded || session.Cancelled)
            {
                return VendorSupport.Build(OperationTypes.MtkUnlockBootloader,
                    session.Cancelled ? "Cancelled." : "The bootloader could not be unlocked.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            await ResetAsync(tool, session, workingDirectory, ct);
            session.Warn("An unlocked bootloader wipes the handset the first time it boots: warn the customer before you continue.");

            return VendorSupport.Build(OperationTypes.MtkUnlockBootloader,
                "Bootloader unlocked through the boot ROM (seccfg erased).",
                session.Checks, session.Log, session.Warnings, session.Errors);
        }

        private async Task<OperationExecutionResult> RelockAsync(OperationExecutionRequest request,
            VendorToolResolution tool, VendorCliSession session, string workingDirectory, CancellationToken ct)
        {
            string? seccfg = FirstExisting(request.GetParameter("seccfgPath"));
            if (seccfg is null)
            {
                const string detail = "A relock writes the 'seccfg' image this handset had before it was unlocked. " +
                                      "Select that backup file (Backups\\NV\\...\\seccfg.bin) and retry. There is no way to " +
                                      "invent a valid lock state for another handset.";
                session.Errors.Add(detail);
                session.Checks.Add(new DiagnosticCheckDto("seccfg", "fail", detail, null));
                return VendorSupport.Build(OperationTypes.MtkRelockBootloader, "A seccfg backup is required.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            session.Check("seccfg", true, $"Lock image: {Path.GetFileName(seccfg)}");
            session.Stage("Writing 'seccfg' back through the boot ROM.");

            List<string> arguments = BuildWriteArguments(tool.EntryScript!, "seccfg", seccfg);
            ProcessResult? write = await session.RunAsync("relock-seccfg", tool.Executable!, arguments,
                workingDirectory, 600, ct);

            if (write is null || !write.Succeeded || session.Cancelled)
            {
                return VendorSupport.Build(OperationTypes.MtkRelockBootloader,
                    session.Cancelled ? "Cancelled." : "The bootloader could not be relocked.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            await ResetAsync(tool, session, workingDirectory, ct);
            return VendorSupport.Build(OperationTypes.MtkRelockBootloader,
                "Bootloader relocked from the seccfg backup.",
                session.Checks, session.Log, session.Warnings, session.Errors);
        }

        private async Task<OperationExecutionResult> BackupAsync(VendorToolResolution tool, VendorCliSession session,
            string workingDirectory, CancellationToken ct)
        {
            string folder = Path.Combine(_backupRoot(), DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                session.Errors.Add($"The backup folder could not be created: {ex.Message}");
                return VendorSupport.Build(OperationTypes.MtkBackupNvram, "Backup folder unavailable.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            session.Check("backup-folder", true, $"Backups are written to {folder}");
            session.Stage("Power the handset off, then hold Vol+ and Vol- while connecting the cable to enter BROM mode.");

            int written = 0;
            foreach (string partition in NvPartitions)
            {
                ct.ThrowIfCancellationRequested();

                string target = Path.Combine(folder, partition + ".bin");
                session.Stage($"Reading '{partition}'.");

                ProcessResult? read = await session.RunAsync($"read-{partition}", tool.Executable!,
                    BuildReadArguments(tool.EntryScript!, partition, target), workingDirectory, 600, ct);

                if (read is null || !read.Succeeded || session.Cancelled)
                {
                    // A missing protect2 on some SoCs is normal; the others are required.
                    if (partition is "protect2" or "nvcfg")
                    {
                        session.Warn($"'{partition}' could not be read on this handset; the rest of the backup continues.");
                        continue;
                    }

                    session.Errors.Add($"The '{partition}' dump failed, so the backup is incomplete.");
                    return VendorSupport.Build(OperationTypes.MtkBackupNvram, "The NVRAM backup did not complete.",
                        session.Checks, session.Log, session.Warnings, session.Errors);
                }

                if (!File.Exists(target))
                {
                    session.Warn($"'{partition}' reported success but produced no file; skipping it.");
                    continue;
                }

                written++;
                session.Check($"dump-{partition}", true,
                    $"{partition}.bin · {new FileInfo(target).Length:N0} bytes");
            }

            if (written == 0)
            {
                session.Errors.Add("No partition could be dumped, so nothing was backed up.");
                return VendorSupport.Build(OperationTypes.MtkBackupNvram, "The NVRAM backup is empty.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            session.Warn("Keep this folder: it holds the radio calibration and IMEI data for this handset only.");

            return VendorSupport.Build(OperationTypes.MtkBackupNvram,
                $"Backed up {written} partition(s) to {folder}.",
                session.Checks, session.Log, session.Warnings, session.Errors);
        }

        private async Task<OperationExecutionResult> RestoreAsync(OperationExecutionRequest request,
            VendorToolResolution tool, VendorCliSession session, string workingDirectory, CancellationToken ct)
        {
            // The caller may pass one file, a folder, or nothing (the newest backup is then used).
            string? folder = ResolveBackupFolder(request.GetParameter("backupPath"));
            if (folder is null)
            {
                session.Errors.Add("No NVRAM backup was selected. Back up the handset first, or choose the folder " +
                                   "that holds nvram.bin / nvdata.bin / nvcfg.bin / protect1.bin / protect2.bin.");
                session.Checks.Add(new DiagnosticCheckDto("backup", "fail", "No backup folder selected.", null));
                return VendorSupport.Build(OperationTypes.MtkRestoreNvram, "No backup selected.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            session.Check("backup", true, $"Restoring from {folder}");
            session.Stage("Power the handset off, then hold Vol+ and Vol- while connecting the cable to enter BROM mode.");

            int restored = 0;
            foreach (string partition in NvPartitions)
            {
                ct.ThrowIfCancellationRequested();

                string source = Path.Combine(folder, partition + ".bin");
                if (!File.Exists(source))
                {
                    continue;
                }

                session.Stage($"Writing '{partition}' ({new FileInfo(source).Length:N0} bytes).");
                ProcessResult? write = await session.RunAsync($"write-{partition}", tool.Executable!,
                    BuildWriteArguments(tool.EntryScript!, partition, source), workingDirectory, 600, ct);

                if (write is null || !write.Succeeded || session.Cancelled)
                {
                    session.Errors.Add($"The '{partition}' restore failed; the handset now holds a mixed set of partitions.");
                    return VendorSupport.Build(OperationTypes.MtkRestoreNvram, "The NVRAM restore did not complete.",
                        session.Checks, session.Log, session.Warnings, session.Errors);
                }

                restored++;
            }

            if (restored == 0)
            {
                session.Errors.Add("The selected folder contains none of nvram.bin, nvdata.bin, nvcfg.bin, protect1.bin or protect2.bin.");
                return VendorSupport.Build(OperationTypes.MtkRestoreNvram, "Nothing to restore.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            await ResetAsync(tool, session, workingDirectory, ct);
            session.Warn("Restore a backup only onto the handset it came from: these dumps carry that phone's calibration and IMEI.");

            return VendorSupport.Build(OperationTypes.MtkRestoreNvram,
                $"Restored {restored} partition(s) from {folder}.",
                session.Checks, session.Log, session.Warnings, session.Errors);
        }

        private async Task<OperationExecutionResult> SafeFormatAsync(VendorToolResolution tool, VendorCliSession session,
            string workingDirectory, CancellationToken ct)
        {
            session.Stage("Power the handset off, then hold Vol+ and Vol- while connecting the cable to enter BROM mode.");
            session.Stage("Erasing 'userdata' only: metadata, persistent flags and FRP are left in place.");

            ProcessResult? erase = await session.RunAsync("safe-format", tool.Executable!,
                BuildEraseArguments(tool.EntryScript!, "userdata"), workingDirectory, 600, ct);

            if (erase is null || !erase.Succeeded || session.Cancelled)
            {
                return VendorSupport.Build(OperationTypes.MtkSafeFormat,
                    session.Cancelled ? "Cancelled." : "The userdata format did not complete.",
                    session.Checks, session.Log, session.Warnings, session.Errors);
            }

            await ResetAsync(tool, session, workingDirectory, ct);
            session.Warn("The handset boots into setup with the user data gone. The FRP state is unchanged by design.");

            return VendorSupport.Build(OperationTypes.MtkSafeFormat,
                "userdata formatted; metadata and persistent FRP flags preserved.",
                session.Checks, session.Log, session.Warnings, session.Errors);
        }

        // ---- command construction (kept public for the tests) --------------------------------

        /// <summary><c>mtk.py e &lt;partitions&gt;</c>.</summary>
        public static List<string> BuildEraseArguments(string entryScript, string partitions)
            => [entryScript, "e", partitions];

        /// <summary><c>mtk.py r &lt;partition&gt; &lt;file&gt;</c>.</summary>
        public static List<string> BuildReadArguments(string entryScript, string partition, string targetFile)
            => [entryScript, "r", partition, targetFile];

        /// <summary><c>mtk.py w &lt;partition&gt; &lt;file&gt;</c>.</summary>
        public static List<string> BuildWriteArguments(string entryScript, string partition, string sourceFile)
            => [entryScript, "w", partition, sourceFile];

        /// <summary>
        /// Resolves what the operator picked: a folder, or a file inside one (its parent is used). A
        /// null answer falls back to the newest backup under <c>Backups\NV</c>.
        /// </summary>
        public static string? ResolveBackupFolder(string? selection, string? backupRoot = null)
        {
            if (!string.IsNullOrWhiteSpace(selection))
            {
                string path = selection.Trim();
                if (Directory.Exists(path))
                {
                    return Path.GetFullPath(path);
                }

                if (File.Exists(path) && Path.GetDirectoryName(path) is { Length: > 0 } parent)
                {
                    return Path.GetFullPath(parent);
                }

                return null;
            }

            string root = backupRoot ?? DefaultBackupRoot;
            try
            {
                if (!Directory.Exists(root))
                {
                    return null;
                }

                return Directory.GetDirectories(root)
                    .OrderByDescending(folder => folder, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private async Task ResetAsync(VendorToolResolution tool, VendorCliSession session,
            string workingDirectory, CancellationToken ct)
        {
            session.Stage("Resetting the handset.");
            ProcessResult? reset = await session.RunAsync("reset", tool.Executable!, [tool.EntryScript!, "reset"],
                workingDirectory, 120, ct);

            if (reset is null || !reset.Succeeded)
            {
                session.Warn("The routine succeeded but the reset command did not. Unplug the cable and hold Power for ten seconds.");
            }
        }

        private static string? FirstExisting(string? path)
            => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? Path.GetFullPath(path) : null;
    }
}
