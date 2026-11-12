using System.IO;
using B_VIP.Core.Operations;
using B_VIP.Hardware;
using B_VIP.Modules;
using B_VIP.Modules.ExternalTools;
using Xunit;

namespace B_VIP.HardwareTests
{
    /// <summary>
    /// The MediaTek boot-ROM workbench: lock state, NVRAM dumps and the safe userdata format. The
    /// command lines are pinned here because a wrong mtkclient verb writes a partition instead of
    /// reading it, and a wrong partition name can cost a handset its calibration data.
    /// </summary>
    public sealed class MtkUniversalTests
    {
        private static StubProcessRunner Runner() => new();

        [Theory]
        [InlineData(OperationTypes.MtkUnlockBootloader)]
        [InlineData(OperationTypes.MtkRelockBootloader)]
        [InlineData(OperationTypes.MtkRestoreNvram)]
        [InlineData(OperationTypes.MtkSafeFormat)]
        public async Task Destructive_routines_refuse_to_run_without_confirmation(string operationType)
        {
            var runner = Runner();
            var executor = new MtkUniversalExecutor(runner, new StubToolchain());

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                operationType, null, new Dictionary<string, string>(), "corr-u1"));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("confirmed"));
            Assert.Empty(runner.Requests);
        }

        [Fact]
        public async Task Unlock_erases_seccfg_through_the_boot_rom_then_resets()
        {
            var runner = Runner();
            var executor = new MtkUniversalExecutor(runner, new StubToolchain());

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.MtkUnlockBootloader, null,
                new Dictionary<string, string> { ["confirm"] = "yes" }, "corr-u2"));

            Assert.True(result.Succeeded);
            Assert.Equal(2, runner.Requests.Count);

            ProcessRequest erase = runner.Requests[0];
            Assert.Equal(@"C:\Loaders\MTK\mtk.py", erase.Arguments[0]);
            Assert.Equal("e", erase.Arguments[1]);
            Assert.Equal("seccfg", erase.Arguments[2]);
            Assert.Equal(@"C:\Loaders\MTK", erase.WorkingDirectory);

            Assert.Equal(new[] { "reset" }, runner.Requests[1].Arguments.Skip(1));
        }

        [Fact]
        public async Task Safe_format_erases_userdata_only()
        {
            var runner = Runner();
            var executor = new MtkUniversalExecutor(runner, new StubToolchain());

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.MtkSafeFormat, null,
                new Dictionary<string, string> { ["confirm"] = "yes" }, "corr-u3"));

            Assert.True(result.Succeeded);
            Assert.Equal("userdata", runner.Requests[0].Arguments[2]);
            // metadata and the persistent FRP flags are never named, which is the whole point.
            Assert.DoesNotContain("metadata", runner.Requests[0].Arguments);
            Assert.DoesNotContain("frp", runner.Requests[0].Arguments);
        }

        [Fact]
        public async Task Relock_requires_a_seccfg_backup_and_writes_it_back()
        {
            using TempFile seccfg = new();

            var runner = Runner();
            var executor = new MtkUniversalExecutor(runner, new StubToolchain());

            OperationExecutionResult missing = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.MtkRelockBootloader, null,
                new Dictionary<string, string> { ["confirm"] = "yes" }, "corr-u4"));

            Assert.False(missing.Succeeded);
            Assert.Empty(runner.Requests);

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.MtkRelockBootloader, null,
                new Dictionary<string, string> { ["confirm"] = "yes", ["seccfgPath"] = seccfg.Path }, "corr-u5"));

            Assert.True(result.Succeeded);
            Assert.Equal("w", runner.Requests[0].Arguments[1]);
            Assert.Equal("seccfg", runner.Requests[0].Arguments[2]);
            Assert.Equal(seccfg.Path, runner.Requests[0].Arguments[3]);
        }

        [Fact]
        public async Task Backup_dumps_every_nv_partition_into_a_timestamped_folder()
        {
            string root = Path.Combine(Path.GetTempPath(), "bvip-nv-" + Guid.NewGuid().ToString("N"));
            var runner = new StubProcessRunner
            {
                // mtkclient writes the dump itself; the stub creates the file the routine then checks.
                Handler = request =>
                {
                    File.WriteAllText(request.Arguments[3], "dump");
                    return new ProcessResult(request.Executable, request.Arguments, 0, "done", string.Empty,
                        false, false, true, 5, null);
                }
            };

            try
            {
                var executor = new MtkUniversalExecutor(runner, new StubToolchain(), () => root);

                OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                    OperationTypes.MtkBackupNvram, null, new Dictionary<string, string>(), "corr-u6"));

                Assert.True(result.Succeeded);
                Assert.Equal(MtkUniversalExecutor.NvPartitions.Count, runner.Requests.Count);

                string folder = Path.GetDirectoryName(runner.Requests[0].Arguments[3])!;
                Assert.StartsWith(root, folder, StringComparison.OrdinalIgnoreCase);
                foreach (string partition in MtkUniversalExecutor.NvPartitions)
                {
                    Assert.True(File.Exists(Path.Combine(folder, partition + ".bin")), partition);
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        [Fact]
        public void ResolveBackupFolder_accepts_a_folder_a_file_or_the_newest_backup()
        {
            string root = Path.Combine(Path.GetTempPath(), "bvip-nv-pick-" + Guid.NewGuid().ToString("N"));
            string older = Path.Combine(root, "20240101-000000");
            string newer = Path.Combine(root, "20250101-000000");
            Directory.CreateDirectory(older);
            Directory.CreateDirectory(newer);
            string file = Path.Combine(newer, "nvram.bin");
            File.WriteAllText(file, "dump");

            try
            {
                Assert.Equal(Path.GetFullPath(newer), MtkUniversalExecutor.ResolveBackupFolder(newer));
                Assert.Equal(Path.GetFullPath(newer), MtkUniversalExecutor.ResolveBackupFolder(file));
                Assert.Equal(Path.GetFullPath(newer), MtkUniversalExecutor.ResolveBackupFolder(null, root));
                Assert.Null(MtkUniversalExecutor.ResolveBackupFolder(Path.Combine(root, "absent")));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Command_builders_use_the_documented_mtkclient_verbs()
        {
            Assert.Equal(new[] { "mtk.py", "e", "seccfg" },
                MtkUniversalExecutor.BuildEraseArguments("mtk.py", "seccfg"));
            Assert.Equal(new[] { "mtk.py", "r", "nvram", @"C:\b\nvram.bin" },
                MtkUniversalExecutor.BuildReadArguments("mtk.py", "nvram", @"C:\b\nvram.bin"));
            Assert.Equal(new[] { "mtk.py", "w", "nvdata", @"C:\b\nvdata.bin" },
                MtkUniversalExecutor.BuildWriteArguments("mtk.py", "nvdata", @"C:\b\nvdata.bin"));
        }
    }

    /// <summary>
    /// Mi Assistant / sideload: the payload is never invented, so the tests pin both the detection
    /// and the refusal to send anything when no signed package is present.
    /// </summary>
    public sealed class XiaomiSideloadTests
    {
        private static FakeAdbClient SideloadAdb() => new()
        {
            AdbHandler = args => args.Contains("sideload")
                ? FakeAdbClient.Ok(args, "serving: 'payload.zip'  (~47%)")
                : FakeAdbClient.Ok(args, "List of devices attached\nABC123\tsideload\n")
        };

        [Fact]
        public void ResolvePayload_prefers_the_explicit_file_then_the_matching_zip()
        {
            string root = Path.Combine(Path.GetTempPath(), "bvip-mi-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string frp = Path.Combine(root, "mi_frp_payload.zip");
            string wipe = Path.Combine(root, "mi_wipe_payload.zip");
            File.WriteAllText(frp, "zip");
            File.WriteAllText(wipe, "zip");

            try
            {
                Assert.Equal(frp, XiaomiMiAssistantExecutor.ResolvePayload(null, MiAssistantMode.Frp, root));
                Assert.Equal(wipe, XiaomiMiAssistantExecutor.ResolvePayload(null, MiAssistantMode.Wipe, root));
                Assert.Equal(wipe, XiaomiMiAssistantExecutor.ResolvePayload(wipe, MiAssistantMode.Frp, root));
                Assert.Null(XiaomiMiAssistantExecutor.ResolvePayload(null, MiAssistantMode.Guidance, root));
                Assert.Null(XiaomiMiAssistantExecutor.ResolvePayload(null, MiAssistantMode.Frp,
                    Path.Combine(root, "absent")));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void IsSideloadListing_reads_the_adb_devices_output()
        {
            Assert.True(XiaomiMiAssistantExecutor.IsSideloadListing("List of devices attached\nABC\tsideload\n"));
            Assert.False(XiaomiMiAssistantExecutor.IsSideloadListing("List of devices attached\nABC\tdevice\n"));
            Assert.False(XiaomiMiAssistantExecutor.IsSideloadListing(null));
        }

        [Fact]
        public async Task Frp_without_a_payload_reports_it_and_sends_nothing()
        {
            var adb = SideloadAdb();
            var executor = new XiaomiMiAssistantExecutor(adb, () => Path.Combine(Path.GetTempPath(), "bvip-mi-absent"));

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.XiaomiMiAssistantFrp, "ABC123",
                new Dictionary<string, string> { ["confirm"] = "yes" }, "corr-x1"));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("payload"));
            Assert.DoesNotContain(adb.AdbCalls, call => call.Contains("sideload"));
        }

        [Fact]
        public async Task Frp_sends_the_payload_over_sideload_when_one_is_present()
        {
            string root = Path.Combine(Path.GetTempPath(), "bvip-mi-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string payload = Path.Combine(root, "mi_frp_payload.zip");
            File.WriteAllText(payload, "zip");

            try
            {
                var adb = SideloadAdb();
                var executor = new XiaomiMiAssistantExecutor(adb, () => root);

                OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                    OperationTypes.XiaomiMiAssistantFrp, "ABC123",
                    new Dictionary<string, string> { ["confirm"] = "yes" }, "corr-x2"));

                Assert.True(result.Succeeded);
                Assert.Contains(adb.AdbCalls, call => call.Length == 2 && call[0] == "sideload" && call[1] == payload);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task Sideload_routines_require_confirmation()
        {
            var adb = SideloadAdb();
            var executor = new XiaomiMiAssistantExecutor(adb);

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.XiaomiMiAssistantWipe, "ABC123", new Dictionary<string, string>(), "corr-x3"));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("confirmed"));
            Assert.Empty(adb.AdbCalls);
        }
    }

    /// <summary>
    /// The EDL firmware flasher: fh_loader must receive the program XML, the patch XML and the folder
    /// the images live in, in that order, or a package flashes half way and stops.
    /// </summary>
    public sealed class QualcommFirmwareTests
    {
        [Fact]
        public void BuildArguments_sends_both_xml_files_with_the_shared_search_path()
        {
            List<string> arguments = QualcommFirmwareExecutor.BuildArguments(
                "COM5", @"C:\fw\rawprogram0.xml", @"C:\fw\patch0.xml", @"C:\fw",
                @"C:\fw\prog_firehose_sdm660.mbn");

            Assert.Equal(@"--port=\\.\COM5", arguments[0]);
            Assert.Equal("--sendxml=\"rawprogram0.xml\"", arguments[1]);
            Assert.Equal("--search_path=\"C:\\fw\"", arguments[2]);
            Assert.Contains("--reset", arguments);
            Assert.Contains("--noprompt", arguments);
            Assert.Contains("--showpercentagecomplete", arguments);

            // patch0.xml is applied after the program XML, so it must come later in the list.
            int program = arguments.IndexOf("--sendxml=\"rawprogram0.xml\"");
            int patch = arguments.IndexOf("--sendxml=\"patch0.xml\"");
            Assert.True(patch > program);
            Assert.Contains("--memoryname=ufs", arguments);
        }

        [Fact]
        public void BuildArguments_omits_the_patch_when_none_was_selected()
        {
            List<string> arguments = QualcommFirmwareExecutor.BuildArguments(
                "COM3", @"C:\fw\rawprogram0.xml", null, @"C:\fw", null);

            Assert.DoesNotContain(arguments, a => a.Contains("patch0.xml"));
            Assert.DoesNotContain(arguments, a => a.StartsWith("--memoryname=", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(@"C:\fw\prog_firehose_emmc.mbn", "emmc")]
        [InlineData(@"C:\fw\prog_firehose_sdm660.mbn", "ufs")]
        [InlineData(null, "ufs")]
        public void MemoryNameFor_matches_the_programmer(string? programmer, string expected)
            => Assert.Equal(expected, QualcommFirmwareExecutor.MemoryNameFor(programmer));

        [Fact]
        public async Task Refuses_to_flash_without_confirmation()
        {
            var runner = new StubProcessRunner();
            var executor = new QualcommFirmwareExecutor(runner, new StubToolchain());

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.QualcommFlashFirmware, null, new Dictionary<string, string>(), "corr-q1"));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("confirmed"));
            Assert.Empty(runner.Requests);
        }

        [Fact]
        public async Task Refuses_to_flash_without_a_rawprogram_xml()
        {
            var runner = new StubProcessRunner();
            var executor = new QualcommFirmwareExecutor(runner, new StubToolchain());

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.QualcommFlashFirmware, null,
                new Dictionary<string, string> { ["confirm"] = "yes", ["port"] = "COM5" }, "corr-q2"));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("rawprogram"));
            Assert.Empty(runner.Requests);
        }
    }
}
