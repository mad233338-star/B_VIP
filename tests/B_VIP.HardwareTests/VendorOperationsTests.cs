using B_VIP.Core.Operations;
using B_VIP.Hardware;
using B_VIP.Modules;
using Xunit;

namespace B_VIP.HardwareTests
{
    public sealed class VendorRebootTests
    {
        [Fact]
        public async Task Reboots_through_adb_when_the_device_is_awake()
        {
            var adb = new FakeAdbClient();
            var executor = new VendorRebootExecutor(adb);

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.VendorReboot, "SER1", new Dictionary<string, string> { ["target"] = "edl" }, "corr-r1"));

            Assert.True(result.Succeeded);
            Assert.Contains(adb.AdbCalls, call => call.SequenceEqual(new[] { "-s", "SER1", "reboot", "edl" }));
            Assert.Empty(adb.FastbootCalls);
            Assert.Contains(result.Warnings, w => w.Contains("USB debugging is dropped"));
        }

        [Fact]
        public async Task Falls_back_to_fastboot_when_adb_refuses()
        {
            var adb = new FakeAdbClient
            {
                AdbHandler = args => FakeAdbClient.Failed(args, "error: device unauthorized"),
                FastbootHandler = args => new ProcessResult("fastboot", args, 0, "OKAY", string.Empty, false, false, true, 1, null)
            };
            var executor = new VendorRebootExecutor(adb);

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.VendorReboot, "SER1", new Dictionary<string, string> { ["target"] = "edl" }, "corr-r2"));

            Assert.True(result.Succeeded);
            Assert.Contains(adb.FastbootCalls, call => call.SequenceEqual(new[] { "-s", "SER1", "oem", "edl" }));
            Assert.Contains(result.Checks, c => c.Name == "reboot-fastboot" && c.Status == "pass");
        }

        [Fact]
        public async Task Reports_an_honest_failure_when_neither_transport_accepts_the_target()
        {
            var adb = new FakeAdbClient
            {
                AdbHandler = args => FakeAdbClient.Failed(args, "device offline"),
                FastbootHandler = args => FakeAdbClient.Failed(args, "unknown command")
            };
            var executor = new VendorRebootExecutor(adb);

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.VendorReboot, "SER1", new Dictionary<string, string> { ["target"] = "edl" }, "corr-r3"));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("EDL needs an unlocked bootloader"));
        }

        [Fact]
        public async Task Refuses_to_run_without_a_serial()
        {
            var executor = new VendorRebootExecutor(new FakeAdbClient());

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.VendorReboot, null, new Dictionary<string, string>(), "corr-r4"));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("No device serial"));
        }
    }

    public sealed class XiaomiMiAccountTests
    {
        private static BrandModuleExecutor Create(FakeAdbClient adb) => new(
            BrandProfiles.Xiaomi, adb, new StubToolLocator(), new StubProcessRunner(),
            [OperationTypes.XiaomiReadInfo, OperationTypes.XiaomiResetFrp, OperationTypes.XiaomiBypassMiAccount]);

        [Fact]
        public async Task Disables_the_find_device_package_over_adb()
        {
            var adb = new FakeAdbClient
            {
                Properties = Props.Of(("ro.product.manufacturer", "Xiaomi"), ("ro.product.model", "M2101K6G"))
            };

            OperationExecutionResult result = await Create(adb).ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.XiaomiBypassMiAccount, "SER1", new Dictionary<string, string>(), "corr-m1"));

            Assert.True(result.Succeeded);
            Assert.Contains("pm disable-user --user 0 com.xiaomi.finddevice", adb.ShellCommands);
            Assert.Contains("pm clear com.xiaomi.finddevice", adb.ShellCommands);
            Assert.Contains(result.Warnings, w => w.Contains("Xiaomi's servers"));
        }

        [Fact]
        public async Task Refuses_a_device_that_is_not_xiaomi()
        {
            var adb = new FakeAdbClient
            {
                Properties = Props.Of(("ro.product.manufacturer", "samsung"))
            };

            OperationExecutionResult result = await Create(adb).ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.XiaomiBypassMiAccount, "SER1", new Dictionary<string, string>(), "corr-m2"));

            Assert.False(result.Succeeded);
            Assert.Empty(adb.ShellCommands);
            Assert.Contains(result.Errors, e => e.Contains("does not identify itself as Xiaomi"));
        }
    }

    public sealed class FastbootEraseBatchTests
    {
        private static (FastbootOperationExecutor Executor, FakeAdbClient Adb) Create()
        {
            var adb = new FakeAdbClient
            {
                FastbootHandler = args => new ProcessResult("fastboot", args, 0,
                    args.Contains("devices") ? "SER1\tfastboot" : "Erasing... OKAY",
                    string.Empty, false, false, true, 1, null)
            };

            var tools = new StubToolLocator();
            tools.Set(ToolLocator.Fastboot, "C:\\tools\\fastboot.exe");
            return (new FastbootOperationExecutor(adb, tools), adb);
        }

        [Fact]
        public async Task Erases_the_requested_partitions_after_confirmation()
        {
            (FastbootOperationExecutor executor, FakeAdbClient adb) = Create();

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.FastbootEraseBatch, "SER1",
                new Dictionary<string, string> { ["partitions"] = "frp,config", ["confirm"] = "yes" }, "corr-e1"));

            Assert.True(result.Succeeded);
            Assert.Contains(adb.FastbootCalls, call => call.SequenceEqual(new[] { "-s", "SER1", "erase", "frp" }));
            Assert.Contains(adb.FastbootCalls, call => call.SequenceEqual(new[] { "-s", "SER1", "erase", "config" }));
        }

        [Fact]
        public async Task Refuses_calibration_partitions_without_touching_the_device()
        {
            (FastbootOperationExecutor executor, FakeAdbClient adb) = Create();

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.FastbootEraseBatch, "SER1",
                new Dictionary<string, string> { ["partitions"] = "frp,modemst1", ["confirm"] = "yes" }, "corr-e2"));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("protected partition"));
            Assert.DoesNotContain(adb.FastbootCalls, call => call.Contains("erase"));
        }

        [Fact]
        public async Task Requires_explicit_confirmation()
        {
            (FastbootOperationExecutor executor, FakeAdbClient adb) = Create();

            OperationExecutionResult result = await executor.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.FastbootEraseBatch, "SER1",
                new Dictionary<string, string> { ["partitions"] = "frp" }, "corr-e3"));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("confirmed"));
            Assert.Empty(adb.FastbootCalls);
        }
    }
}
