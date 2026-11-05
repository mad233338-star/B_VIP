using B_VIP.Core.Operations;
using B_VIP.Hardware;
using B_VIP.Modules;
using Xunit;

namespace B_VIP.HardwareTests
{
    /// <summary>
    /// The Phase 2 ADB/Fastboot actions: detailed read info, reboot targets and the FRP erase
    /// routine. Everything runs against the fake client, so no device or tool is needed.
    /// </summary>
    public sealed class Phase2OperationTests
    {
        private static StubToolLocator Tools()
        {
            StubToolLocator tools = new();
            tools.Set(ToolLocator.Adb, "adb.exe");
            tools.Set(ToolLocator.Fastboot, "fastboot.exe");
            return tools;
        }

        private static OperationExecutionRequest Request(string type, string? serial = "SER1",
            params (string Key, string Value)[] parameters)
            => new(type, serial, parameters.ToDictionary(p => p.Key, p => p.Value), "corr-1");

        [Fact]
        public async Task Adb_read_info_combines_devices_and_getprop_into_one_report()
        {
            FakeAdbClient adb = new()
            {
                AdbHandler = args => args.Contains("getprop")
                    ? FakeAdbClient.Ok(args, "[ro.product.manufacturer]: [samsung]\n" +
                                           "[ro.product.model]: [SM-G991B]\n" +
                                           "[ro.build.version.release]: [14]\n" +
                                           "[ro.build.version.sdk]: [34]\n" +
                                           "[ro.build.version.security_patch]: [2024-09-01]\n")
                    : FakeAdbClient.Ok(args, "SER1 device product:o1s model:SM_G991B")
            };

            var executor = new AdbOperationExecutor(adb, Tools());
            OperationExecutionResult result = await executor.ExecuteAsync(Request(OperationTypes.AdbReadInfo));

            Assert.True(result.Succeeded);
            Assert.Contains("SM-G991B", result.Summary);
            Assert.Contains("Android 14", result.Summary);
            Assert.Contains(result.Log, line => line.Contains("Manufacturer") && line.Contains("samsung"));
            Assert.Contains(result.Checks, c => c.Name == "ro.build.version.security_patch" && c.Status == "pass");
            Assert.Contains(adb.AdbCalls, call => call.SequenceEqual(["-s", "SER1", "shell", "getprop"]));
            Assert.Contains(adb.AdbCalls, call => call.SequenceEqual(["devices", "-l"]));
        }

        [Fact]
        public async Task Adb_read_info_fails_when_the_device_returns_nothing()
        {
            FakeAdbClient adb = new()
            {
                AdbHandler = args => FakeAdbClient.Ok(args, string.Empty)
            };

            var executor = new AdbOperationExecutor(adb, Tools());
            OperationExecutionResult result = await executor.ExecuteAsync(Request(OperationTypes.AdbReadInfo));

            Assert.False(result.Succeeded);
            Assert.Empty(result.Checks);
        }

        [Fact]
        public async Task Adb_read_info_requires_a_serial()
        {
            var executor = new AdbOperationExecutor(new FakeAdbClient(), Tools());

            OperationExecutionResult result = await executor.ExecuteAsync(Request(OperationTypes.AdbReadInfo, serial: null));

            Assert.False(result.Succeeded);
            Assert.Contains("serial", result.Summary, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("system", new[] { "-s", "SER1", "reboot" })]
        [InlineData("recovery", new[] { "-s", "SER1", "reboot", "recovery" })]
        [InlineData("bootloader", new[] { "-s", "SER1", "reboot", "bootloader" })]
        [InlineData("edl", new[] { "-s", "SER1", "reboot", "edl" })]
        [InlineData("download", new[] { "-s", "SER1", "reboot", "download" })]
        public async Task Adb_reboot_reaches_the_requested_target(string target, string[] expected)
        {
            FakeAdbClient adb = new();
            var executor = new AdbOperationExecutor(adb, Tools());

            OperationExecutionResult result = await executor.ExecuteAsync(
                Request(OperationTypes.AdbReboot, "SER1", ("target", target)));

            Assert.True(result.Succeeded);
            Assert.Contains(adb.AdbCalls, call => call.SequenceEqual(expected));
            Assert.Contains(result.Warnings, w => w.Contains(target));
        }

        [Fact]
        public async Task Fastboot_reset_frp_erases_frp_config_and_persistent()
        {
            FakeAdbClient adb = new();
            var executor = new FastbootOperationExecutor(adb, Tools());

            OperationExecutionResult result = await executor.ExecuteAsync(Request(OperationTypes.FastbootResetFrp));

            Assert.True(result.Succeeded);
            Assert.Equal(3, adb.FastbootCalls.Count);
            Assert.Contains(adb.FastbootCalls, call => call.SequenceEqual(["-s", "SER1", "erase", "frp"]));
            Assert.Contains(adb.FastbootCalls, call => call.SequenceEqual(["-s", "SER1", "erase", "config"]));
            Assert.Contains(adb.FastbootCalls, call => call.SequenceEqual(["-s", "SER1", "erase", "persistent"]));
            Assert.All(result.Checks, c => Assert.Equal("pass", c.Status));
        }

        [Fact]
        public async Task A_partition_the_device_does_not_have_is_a_warning_not_a_failure()
        {
            FakeAdbClient adb = new()
            {
                FastbootHandler = args => args.Contains("persistent")
                    ? new ProcessResult("fastboot", args, 1, string.Empty, "FAILED (remote: 'Partition doesn't exist')",
                        false, false, true, 5, null)
                    : FakeAdbClient.Ok(args)
            };
            var executor = new FastbootOperationExecutor(adb, Tools());

            OperationExecutionResult result = await executor.ExecuteAsync(Request(OperationTypes.FastbootResetFrp));

            Assert.True(result.Succeeded);
            Assert.Contains(result.Checks, c => c.Name == "erase-persistent" && c.Status == "warn");
            Assert.Empty(result.Errors);
        }

        [Fact]
        public async Task A_refused_frp_erase_fails_the_operation()
        {
            FakeAdbClient adb = new()
            {
                FastbootHandler = args => args.Contains("frp")
                    ? new ProcessResult("fastboot", args, 1, string.Empty, "FAILED (remote: 'Erase is not allowed')",
                        false, false, true, 5, null)
                    : FakeAdbClient.Ok(args)
            };
            var executor = new FastbootOperationExecutor(adb, Tools());

            OperationExecutionResult result = await executor.ExecuteAsync(Request(OperationTypes.FastbootResetFrp));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Checks, c => c.Name == "erase-frp" && c.Status == "fail");
            Assert.NotEmpty(result.Errors);
        }

        [Fact]
        public async Task Fastboot_read_info_parses_the_variable_dump_from_stderr()
        {
            FakeAdbClient adb = new()
            {
                FastbootHandler = args => new ProcessResult("fastboot", args, 0, string.Empty,
                    "(bootloader) product: sunfish\n(bootloader) serialno: 12345678\n(bootloader) secure: yes\n" +
                    "(bootloader) current-slot: a\nFinished. Total time: 0.041s",
                    false, false, true, 40, null)
            };
            var executor = new FastbootOperationExecutor(adb, Tools());

            OperationExecutionResult result = await executor.ExecuteAsync(Request(OperationTypes.FastbootReadInfo));

            Assert.True(result.Succeeded);
            Assert.Contains("sunfish", result.Summary);
            Assert.Contains(result.Log, line => line.Contains("Product") && line.Contains("sunfish"));
            Assert.Contains(result.Checks, c => c.Name == "serialno" && c.Detail == "12345678");
            Assert.Contains(result.Warnings, w => w.Contains("secure boot"));
        }

        [Fact]
        public async Task Fastboot_read_info_fails_when_no_variable_comes_back()
        {
            FakeAdbClient adb = new()
            {
                FastbootHandler = args => new ProcessResult("fastboot", args, 1, string.Empty,
                    "fastboot: error: Device does not support slots", false, false, true, 9, null)
            };
            var executor = new FastbootOperationExecutor(adb, Tools());

            OperationExecutionResult result = await executor.ExecuteAsync(Request(OperationTypes.FastbootReadInfo));

            Assert.False(result.Succeeded);
            Assert.NotEmpty(result.Errors);
        }

        [Theory]
        [InlineData("system", new[] { "-s", "SER1", "reboot" })]
        [InlineData("recovery", new[] { "-s", "SER1", "reboot", "recovery" })]
        [InlineData("bootloader", new[] { "-s", "SER1", "reboot", "bootloader" })]
        [InlineData("edl", new[] { "-s", "SER1", "oem", "edl" })]
        public async Task Fastboot_reboot_reaches_the_requested_target(string target, string[] expected)
        {
            FakeAdbClient adb = new();
            var executor = new FastbootOperationExecutor(adb, Tools());

            OperationExecutionResult result = await executor.ExecuteAsync(
                Request(OperationTypes.FastbootReboot, "SER1", ("target", target)));

            Assert.True(result.Succeeded);
            Assert.Contains(adb.FastbootCalls, call => call.SequenceEqual(expected));
        }

        [Fact]
        public async Task Fastboot_operations_refuse_to_run_without_the_tool()
        {
            StubToolLocator tools = new();
            tools.Set(ToolLocator.Fastboot, null);
            var executor = new FastbootOperationExecutor(new FakeAdbClient(), tools);

            OperationExecutionResult result = await executor.ExecuteAsync(Request(OperationTypes.FastbootResetFrp));

            Assert.False(result.Succeeded);
            Assert.Contains("fastboot", result.Summary, StringComparison.OrdinalIgnoreCase);
        }
    }
}
