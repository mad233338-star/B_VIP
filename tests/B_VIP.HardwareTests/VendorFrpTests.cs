using System.IO;
using B_VIP.Core.Operations;
using B_VIP.Hardware;
using B_VIP.Modules;
using B_VIP.Modules.ExternalTools;
using Xunit;

namespace B_VIP.HardwareTests
{
    /// <summary>A toolchain that reports the loader bundles as installed, without touching the disk.</summary>
    public sealed class StubToolchain : IVendorToolchain
    {
        public VendorToolResolution Mtk { get; set; } = new(
            true, @"C:\Python\python.exe", @"C:\Loaders\MTK\mtk.py", "mtkclient: stub");

        public VendorToolResolution FhLoader { get; set; } = new(
            true, @"C:\Loaders\Qualcomm\fh_loader.exe", null, "fh_loader: stub");

        public VendorToolResolution SpdDump { get; set; } = new(
            true, @"C:\Loaders\SPD\spd_dump.exe", null, "spd_dump: stub");

        public string? SamsungDa { get; set; }

        public string? Programmer { get; set; } = @"C:\Loaders\Qualcomm\prog_firehose_sdm660.mbn";

        public VendorToolResolution ResolveMtkClient() => Mtk;

        public VendorToolResolution ResolveFhLoader() => FhLoader;

        public VendorToolResolution ResolveSpdDump() => SpdDump;

        public string? FindSamsungDa() => SamsungDa;

        public string? FindFirehoseProgrammer() => Programmer;
    }

    /// <summary>Creates throwaway files for the routines that check their inputs exist.</summary>
    internal sealed class TempFile : IDisposable
    {
        public TempFile(string extension = ".bin")
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bvip-{Guid.NewGuid():N}{extension}");
            File.WriteAllText(Path, "stub");
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }
    }

    public sealed class MtkClientFrpTests
    {
        private static StubProcessRunner Runner() => new();

        [Fact]
        public async Task Refuses_without_confirmation_and_starts_nothing()
        {
            var runner = Runner();
            var executor = new MtkClientFrpExecutor(runner, new StubToolchain());

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.MtkFrpClient, null, new Dictionary<string, string>(), "corr-f1"));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("confirmed"));
            Assert.Empty(runner.Requests);
        }

        [Fact]
        public async Task Erases_frp_and_persistent_through_the_boot_rom_then_resets()
        {
            var runner = Runner();
            var executor = new MtkClientFrpExecutor(runner, new StubToolchain());

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.MtkFrpClient, null,
                new Dictionary<string, string> { ["confirm"] = "yes" }, "corr-f2"));

            Assert.True(result.Succeeded);
            Assert.Equal(2, runner.Requests.Count);

            ProcessRequest erase = runner.Requests[0];
            Assert.EndsWith("python.exe", erase.Executable, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(@"C:\Loaders\MTK\mtk.py", erase.Arguments[0]);
            Assert.Equal("e", erase.Arguments[1]);
            Assert.Equal("frp,persistent", erase.Arguments[2]);
            Assert.Equal(@"C:\Loaders\MTK", erase.WorkingDirectory);

            Assert.Equal(new[] { "reset" }, runner.Requests[1].Arguments.Skip(1));
        }

        [Fact]
        public async Task Passes_the_samsung_download_agent_to_mtkclient()
        {
            using TempFile da = new();
            var runner = Runner();
            var toolchain = new StubToolchain { SamsungDa = da.Path };
            var executor = new MtkClientFrpExecutor(runner, toolchain);

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.SamsungFrpPreloader, null,
                new Dictionary<string, string> { ["confirm"] = "yes" }, "corr-f3"));

            Assert.True(result.Succeeded);

            string[] arguments = runner.Requests[0].Arguments.ToArray();
            Assert.Contains("--da-file", arguments);
            int index = Array.IndexOf(arguments, "--da-file");
            Assert.Equal(da.Path, arguments[index + 1]);
            Assert.Contains("frp,persistent", arguments);
        }

        [Fact]
        public async Task Drops_a_partition_that_is_not_part_of_an_frp_routine()
        {
            var runner = Runner();
            var executor = new MtkClientFrpExecutor(runner, new StubToolchain());

            await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.MtkFrpClient, null,
                new Dictionary<string, string> { ["confirm"] = "yes", ["partitions"] = "frp,userdata,modem" },
                "corr-f4"));

            Assert.Equal("frp", runner.Requests[0].Arguments[2]);
        }

        [Fact]
        public async Task Reports_a_missing_mtkclient_instead_of_failing_obscurely()
        {
            var runner = Runner();
            var toolchain = new StubToolchain
            {
                Mtk = VendorToolResolution.Missing("mtkclient (mtk.py) was not found in the loader bundle.")
            };
            var executor = new MtkClientFrpExecutor(runner, toolchain);

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.MtkFrpClient, null,
                new Dictionary<string, string> { ["confirm"] = "yes" }, "corr-f5"));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("mtk.py", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(runner.Requests);
        }

        [Fact]
        public async Task Stops_after_a_failed_erase_and_does_not_reset()
        {
            var runner = new StubProcessRunner
            {
                Handler = request => request.Arguments.Contains("e")
                    ? new ProcessResult(request.Executable, request.Arguments, 1, string.Empty,
                        "BROM command failed", false, false, true, 12, null)
                    : new ProcessResult(request.Executable, request.Arguments, 0, "reset", string.Empty,
                        false, false, true, 5, null)
            };

            var executor = new MtkClientFrpExecutor(runner, new StubToolchain());

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.MtkFrpClient, null,
                new Dictionary<string, string> { ["confirm"] = "yes" }, "corr-f6"));

            Assert.False(result.Succeeded);
            Assert.Single(runner.Requests);
            Assert.Contains(result.Errors, e => e.Contains("BROM command failed"));
        }
    }

    public sealed class FhLoaderFrpTests
    {
        [Fact]
        public async Task Erases_frp_and_config_on_the_selected_edl_port()
        {
            var runner = new StubProcessRunner();
            var executor = new FhLoaderFrpExecutor(runner, new StubToolchain());

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.QualcommFrpLoader, "COM7",
                new Dictionary<string, string> { ["confirm"] = "yes" }, "corr-q1"));

            Assert.True(result.Succeeded);

            string[] arguments = runner.Requests[0].Arguments.ToArray();
            Assert.Contains(@"--port=\\.\COM7", arguments);
            Assert.Contains("--erase=frp", arguments);
            Assert.Contains("--erase=config", arguments);
            Assert.Contains("--reset", arguments);
            Assert.Contains("--noprompt", arguments);
            Assert.Contains(arguments, a => a.StartsWith("--search_path=", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Refuses_when_no_edl_port_is_available()
        {
            var runner = new StubProcessRunner();
            var executor = new FhLoaderFrpExecutor(runner, new StubToolchain());

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.QualcommFrpLoader, null,
                new Dictionary<string, string> { ["confirm"] = "yes" }, "corr-q2"));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("9008 COM port"));
            Assert.Empty(runner.Requests);
        }

        [Fact]
        public async Task Never_erases_a_partition_outside_the_frp_set()
        {
            var runner = new StubProcessRunner();
            var executor = new FhLoaderFrpExecutor(runner, new StubToolchain());

            await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.QualcommFrpLoader, @"\\.\COM3",
                new Dictionary<string, string> { ["confirm"] = "yes", ["partitions"] = "frp,modemst1" },
                "corr-q3"));

            string[] arguments = runner.Requests[0].Arguments.ToArray();
            Assert.Contains("--erase=frp", arguments);
            Assert.DoesNotContain("--erase=modemst1", arguments);
        }

        [Theory]
        [InlineData("COM3", "COM3")]
        [InlineData(@"\\.\COM12", "COM12")]
        [InlineData("Qualcomm HS-USB QDLoader 9008 (COM5)", "COM5")]
        [InlineData("usb:1-2", null)]
        public void Normalises_the_port_names_windows_reports(string input, string? expected)
            => Assert.Equal(expected, FhLoaderFrpExecutor.NormalisePort(input));
    }

    public sealed class SpdDumpFrpTests
    {
        [Fact]
        public async Task Runs_the_documented_fdl_erase_sequence()
        {
            using TempFile fdl1 = new();
            using TempFile fdl2 = new();

            var runner = new StubProcessRunner();
            var executor = new SpdDumpFrpExecutor(runner, new StubToolchain());

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.SpdFrpDump, null,
                new Dictionary<string, string>
                {
                    ["confirm"] = "yes",
                    ["fdl1Path"] = fdl1.Path,
                    ["fdl2Path"] = fdl2.Path
                }, "corr-s1"));

            Assert.True(result.Succeeded);

            string[] arguments = runner.Requests[0].Arguments.ToArray();
            Assert.Equal(
                new[]
                {
                    "fdl", fdl1.Path, "0x50000000",
                    "fdl", fdl2.Path, "0x9efffe00",
                    "erase_partition", "frp",
                    "reset"
                }, arguments);
        }

        [Fact]
        public async Task Refuses_without_the_fdl_stages()
        {
            using TempFile fdl1 = new();

            var runner = new StubProcessRunner();
            var executor = new SpdDumpFrpExecutor(runner, new StubToolchain());

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.SpdFrpDump, null,
                new Dictionary<string, string> { ["confirm"] = "yes", ["fdl1Path"] = fdl1.Path }, "corr-s2"));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("FDL2"));
            Assert.Empty(runner.Requests);
        }

        [Theory]
        [InlineData("frp", "frp")]
        [InlineData("userdata", "frp")]
        [InlineData("persist", "persist")]
        public void Only_a_protected_partition_name_is_accepted(string requested, string expected)
            => Assert.Equal(expected, SpdDumpFrpExecutor.NormalisePartition(requested));
    }

    public sealed class SetupWizardBypassTests
    {
        [Fact]
        public async Task Clears_the_setup_flags_and_reboots()
        {
            var adb = new FakeAdbClient();
            var executor = new SetupWizardBypassExecutor(adb);

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.AdbSetupWizardBypass, "SER1",
                new Dictionary<string, string> { ["confirm"] = "yes" }, "corr-w1"));

            Assert.True(result.Succeeded);
            Assert.Contains("content insert --uri content://settings/secure --bind name:s:user_setup_complete --bind value:s:1",
                adb.ShellCommands);
            Assert.Contains("am broadcast -a android.intent.action.MASTER_CLEAR", adb.ShellCommands);
            Assert.Contains(adb.AdbCalls, call => call.SequenceEqual(new[] { "-s", "SER1", "reboot" }));
        }

        [Fact]
        public async Task Requires_the_operator_to_confirm()
        {
            var adb = new FakeAdbClient();
            var executor = new SetupWizardBypassExecutor(adb);

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.AdbSetupWizardBypass, "SER1", new Dictionary<string, string>(), "corr-w2"));

            Assert.False(result.Succeeded);
            Assert.Empty(adb.ShellCommands);
        }

        [Fact]
        public async Task A_refused_master_clear_broadcast_is_a_notice_not_a_failure()
        {
            var adb = new FakeAdbClient
            {
                ShellHandler = (_, command) => command.Contains("MASTER_CLEAR", StringComparison.Ordinal)
                    ? FakeAdbClient.Failed(Array.Empty<string>(), "Permission Denial: not allowed to send broadcast")
                    : FakeAdbClient.Ok(Array.Empty<string>())
            };

            var executor = new SetupWizardBypassExecutor(adb);

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.AdbSetupWizardBypass, "SER1",
                new Dictionary<string, string> { ["confirm"] = "yes" }, "corr-w4"));

            Assert.True(result.Succeeded);
            Assert.Contains(result.Warnings, w => w.Contains("MASTER_CLEAR"));
            Assert.Contains(adb.AdbCalls, call => call.SequenceEqual(new[] { "-s", "SER1", "reboot" }));
        }

        [Fact]
        public async Task Reports_a_refused_setup_flag_as_the_failure()
        {
            var adb = new FakeAdbClient
            {
                ShellHandler = (_, _) => FakeAdbClient.Failed(Array.Empty<string>(), "Error: no permission")
            };

            var executor = new SetupWizardBypassExecutor(adb);

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.AdbSetupWizardBypass, "SER1",
                new Dictionary<string, string> { ["confirm"] = "yes" }, "corr-w3"));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("no permission"));
            Assert.DoesNotContain(adb.AdbCalls, call => call.Contains("reboot"));
        }
    }

    public sealed class FrpRouterTests
    {
        [Theory]
        [InlineData("MT6765", "mtk")]
        [InlineData("mt6789", "mtk")]
        [InlineData("ums512", "spd")]
        [InlineData("sp9820e", "spd")]
        [InlineData("sdm660", "fastboot")]
        [InlineData("taro", "fastboot")]
        [InlineData("qualcomm", "qualcomm")]
        [InlineData("", "fastboot")]
        public void Maps_a_chipset_onto_an_engine(string chipset, string expected)
            => Assert.Equal(expected, FrpRouterExecutor.RouteFor(chipset, "infinix"));

        [Fact]
        public async Task Routes_a_mediatek_handset_to_mtkclient()
        {
            var runner = new StubProcessRunner();
            var adb = new FakeAdbClient
            {
                Properties = Props.Of(("ro.board.platform", "MT6765"))
            };

            var executor = new FrpRouterExecutor(adb, runner, new StubToolchain());

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.FrpAutoReset, "SER1",
                new Dictionary<string, string>
                {
                    ["confirm"] = "yes",
                    ["brand"] = "infinix",
                    ["partitions"] = "frp,persist"
                }, "corr-r1"));

            Assert.True(result.Succeeded);
            Assert.Equal(OperationTypes.FrpAutoReset, result.OperationType);
            Assert.EndsWith("python.exe", runner.Requests[0].Executable, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("frp,persist", runner.Requests[0].Arguments);
        }

        [Fact]
        public async Task Erases_through_fastboot_and_reboots_for_a_qualcomm_handset()
        {
            var adb = new FakeAdbClient
            {
                Properties = Props.Of(("ro.board.platform", "sdm660")),
                FastbootHandler = args => new ProcessResult("fastboot", args, 0,
                    args.Contains("devices") ? "SER1\tfastboot" : "OKAY", string.Empty, false, false, true, 3, null)
            };

            var executor = new FrpRouterExecutor(adb, new StubProcessRunner(), new StubToolchain());

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.FrpAutoReset, "SER1",
                new Dictionary<string, string> { ["confirm"] = "yes", ["brand"] = "tecno" }, "corr-r2"));

            Assert.True(result.Succeeded);
            Assert.Contains(adb.FastbootCalls, call => call.SequenceEqual(new[] { "-s", "SER1", "erase", "frp" }));
            Assert.Contains(adb.FastbootCalls, call => call.SequenceEqual(new[] { "-s", "SER1", "erase", "frp_p" }));
            Assert.Contains(adb.FastbootCalls, call => call.SequenceEqual(new[] { "-s", "SER1", "reboot" }));
        }

        [Fact]
        public async Task Sends_a_locked_xiaomi_to_mi_assistant_without_erasing_anything()
        {
            var adb = new FakeAdbClient
            {
                Properties = Props.Of(("ro.board.platform", "sdm660")),
                FastbootHandler = args => new ProcessResult("fastboot", args, 0,
                    args.Contains("devices") ? "SER1\tfastboot" : "unlocked: no", string.Empty,
                    false, false, true, 3, null)
            };

            var executor = new FrpRouterExecutor(adb, new StubProcessRunner(), new StubToolchain());

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.FrpAutoReset, "SER1",
                new Dictionary<string, string> { ["confirm"] = "yes", ["brand"] = "xiaomi" }, "corr-r3"));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("Mi Assistant"));
            Assert.DoesNotContain(adb.FastbootCalls, call => call.Contains("erase"));
        }

        [Fact]
        public async Task Honors_an_explicitly_requested_engine()
        {
            using TempFile fdl1 = new();
            using TempFile fdl2 = new();

            var runner = new StubProcessRunner();
            var adb = new FakeAdbClient();

            var executor = new FrpRouterExecutor(adb, runner, new StubToolchain());

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.FrpAutoReset, null,
                new Dictionary<string, string>
                {
                    ["confirm"] = "yes",
                    ["brand"] = "infinix",
                    ["mode"] = "spd",
                    ["fdl1Path"] = fdl1.Path,
                    ["fdl2Path"] = fdl2.Path
                }, "corr-r4"));

            Assert.True(result.Succeeded);
            Assert.EndsWith("spd_dump.exe", runner.Requests[0].Executable, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Never_widens_the_partition_list()
        {
            Assert.Equal(new[] { "frp", "config" }, FrpRouterExecutor.PartitionList(null, "xiaomi"));
            Assert.Equal(new[] { "frp", "persist" }, FrpRouterExecutor.PartitionList(null, "infinix"));
            Assert.Equal(new[] { "frp", "frp_p" }, FrpRouterExecutor.PartitionList(null, "tecno"));
        }
    }

    public sealed class XiaomiMiAssistantTests
    {
        [Fact]
        public async Task Reports_the_sideload_procedure_when_no_device_is_waiting()
        {
            var adb = new FakeAdbClient
            {
                AdbHandler = args => new ProcessResult("adb", args, 0, "List of devices attached\n", string.Empty,
                    false, false, true, 2, null)
            };

            var executor = new XiaomiMiAssistantExecutor(adb);

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.XiaomiMiAssistant, null, new Dictionary<string, string>(), "corr-a1"));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Log, line => line.Contains("Mi Recovery"));
            Assert.Contains(result.Errors, e => e.Contains("sideload"));
        }

        [Fact]
        public async Task Recognises_a_handset_in_sideload_mode()
        {
            var adb = new FakeAdbClient
            {
                AdbHandler = args => new ProcessResult("adb", args, 0,
                    "List of devices attached\nABC123\tsideload\n", string.Empty, false, false, true, 2, null)
            };

            var executor = new XiaomiMiAssistantExecutor(adb);

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.XiaomiMiAssistant, "ABC123", new Dictionary<string, string>(), "corr-a2"));

            Assert.True(result.Succeeded);
            Assert.Contains(result.Checks, c => c.Name == "sideload-mode" && c.Status == "pass");
        }
    }
}
