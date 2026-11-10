using B_VIP.Contracts.Devices;
using B_VIP.Core;
using B_VIP.Core.Operations;
using B_VIP.Hardware;
using B_VIP.Modules;
using B_VIP.Modules.Samsung;
using Xunit;

namespace B_VIP.HardwareTests
{
    /// <summary>
    /// Covers the Phase 3 Samsung routines over fakes: MTP identity without USB debugging, the
    /// browser dispatch, and the test-mode FRP flow. Nothing here needs a device or the real tools.
    /// </summary>
    public sealed class SamsungServiceTests
    {
        private const string AdbDevicesHeader = "List of devices attached";

        private static FakeAdbClient AuthorizedDevice(string serial, params (string Key, string Value)[] properties)
            => new()
            {
                Properties = Props.Of(properties),
                AdbHandler = args => args.Contains("devices") && args.Contains("-l")
                    ? FakeAdbClient.Ok(args, $"{AdbDevicesHeader}\n{serial}\tdevice product:x model:SM_G991B\n")
                    : FakeAdbClient.Ok(args)
            };

        private static FakeAdbClient NoDevice()
            => new()
            {
                AdbHandler = args => args.Contains("devices") && args.Contains("-l")
                    ? FakeAdbClient.Ok(args, $"{AdbDevicesHeader}\n")
                    : FakeAdbClient.Ok(args)
            };

        private static FakeAdbClient UnauthorizedDevice(string serial)
            => new()
            {
                AdbHandler = args => args.Contains("devices") && args.Contains("-l")
                    ? FakeAdbClient.Ok(args, $"{AdbDevicesHeader}\n{serial}\tunauthorized usb:1-1\n")
                    : FakeAdbClient.Ok(args)
            };

        private static OperationExecutionRequest Request(string operation, string? serial,
            IReadOnlyDictionary<string, string>? parameters = null)
            => new(operation, serial, parameters ?? new Dictionary<string, string>(), "corr-samsung");

        [Fact]
        public async Task MTP_read_reports_the_model_without_an_adb_connection()
        {
            var mtp = new FakeMtpProtocolService();
            mtp.Devices.Add(new MtpDeviceInfo("USB#VID_04E8&PID_6860", "SM-G991B", "Samsung Electronics", "Galaxy S21", "SM-G991B"));
            var module = SamsungTestFactory.Create(NoDevice(), mtp);

            OperationExecutionResult result = await module.ExecuteAsync(Request(OperationTypes.SamsungReadInfoMtp, null));

            Assert.True(result.Succeeded);
            Assert.Contains(result.Checks, c => c.Name == "mtp-device" && c.Status == "pass");
            Assert.Contains(result.Checks, c => c.Name == "mtp-model" && c.Detail == "SM-G991B");
            // The fields MTP cannot carry are declared as skipped, never invented.
            Assert.Contains(result.Checks, c => c.Name == "adb-connection" && c.Status == "skip");
            Assert.Contains(result.Warnings, w => w.Contains("not part of MTP"));
        }

        [Fact]
        public async Task MTP_read_reads_serial_firmware_and_csc_over_an_authorized_device()
        {
            var mtp = new FakeMtpProtocolService();
            mtp.Devices.Add(new MtpDeviceInfo("USB#VID_04E8&PID_6860", "SM-G991B", "Samsung Electronics", "Galaxy S21", "SM-G991B"));

            FakeAdbClient adb = AuthorizedDevice("R58M12345AB",
                ("ro.serialno", "R58M12345AB"),
                ("ro.build.PDA", "G991BXXU7FWK1"),
                ("ro.csc.sales_code", "EUX"),
                ("ro.build.version.release", "14"));

            OperationExecutionResult result = await SamsungTestFactory.Create(adb, mtp)
                .ExecuteAsync(Request(OperationTypes.SamsungReadInfoMtp, null));

            Assert.True(result.Succeeded);
            Assert.Contains(result.Checks, c => c.Name == "mtp-device" && c.Status == "pass");
            Assert.Contains(result.Checks, c => c.Name == "ro.serialno" && c.Detail!.Contains("R58M12345AB"));
            Assert.Contains(result.Checks, c => c.Name == "ro.build.PDA" && c.Detail!.Contains("G991BXXU7FWK1"));
            Assert.Contains(result.Checks, c => c.Name == "ro.csc.sales_code" && c.Detail!.Contains("EUX"));
            Assert.Contains(result.Log, l => l.Contains("=== Samsung device information ==="));
            Assert.Empty(result.Errors);
        }

        [Fact]
        public async Task MTP_read_fails_when_nothing_answers()
        {
            FakeAdbClient adb = NoDevice();
            var module = SamsungTestFactory.Create(adb, new FakeMtpProtocolService());

            OperationExecutionResult result = await module.ExecuteAsync(Request(OperationTypes.SamsungReadInfoMtp, null));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Warnings, w => w.Contains("no MTP portable device"));
            Assert.Contains(result.Checks, c => c.Name == "adb-connection" && c.Status == "skip");
        }

        [Fact]
        public async Task Reboot_to_download_explains_that_mtp_has_no_reboot_verb()
        {
            OperationExecutionResult result = await SamsungTestFactory.Create(NoDevice())
                .ExecuteAsync(Request(OperationTypes.SamsungRebootDownload, null));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("MTP has no reboot command"));
            Assert.Contains(result.Checks, c => c.Name == "adb-connection" && c.Status == "fail");
        }

        [Fact]
        public async Task Reboot_to_download_sends_the_download_target_over_adb()
        {
            FakeAdbClient adb = AuthorizedDevice("R58M12345AB");

            OperationExecutionResult result = await SamsungTestFactory.Create(adb)
                .ExecuteAsync(Request(OperationTypes.SamsungRebootDownload, "R58M12345AB"));

            Assert.True(result.Succeeded);
            Assert.Contains(adb.AdbCalls, call => call.SequenceEqual(new[] { "-s", "R58M12345AB", "reboot", "download" }));
        }

        [Fact]
        public async Task Reboot_to_download_falls_back_to_bootloader_when_the_bootloader_refuses_it()
        {
            FakeAdbClient adb = AuthorizedDevice("R58M12345AB");
            adb.AdbHandler = args => args.Contains("download")
                ? FakeAdbClient.Failed(args, "error: closed")
                : FakeAdbClient.Ok(args);

            OperationExecutionResult result = await SamsungTestFactory.Create(adb)
                .ExecuteAsync(Request(OperationTypes.SamsungRebootDownload, "R58M12345AB"));

            Assert.True(result.Succeeded);
            Assert.Contains(adb.AdbCalls, call => call.SequenceEqual(new[] { "-s", "R58M12345AB", "reboot", "bootloader" }));
            Assert.Contains(result.Warnings, w => w.Contains("bootloader"));
        }

        [Fact]
        public async Task Reboot_to_download_reports_both_refused_modes_as_the_failure()
        {
            FakeAdbClient adb = AuthorizedDevice("R58M12345AB");
            adb.AdbHandler = args => FakeAdbClient.Failed(args, "error: closed");

            OperationExecutionResult result = await SamsungTestFactory.Create(adb)
                .ExecuteAsync(Request(OperationTypes.SamsungRebootDownload, "R58M12345AB"));

            Assert.False(result.Succeeded);
            Assert.Contains(adb.AdbCalls, call => call.SequenceEqual(new[] { "-s", "R58M12345AB", "reboot", "download" }));
            Assert.Contains(adb.AdbCalls, call => call.SequenceEqual(new[] { "-s", "R58M12345AB", "reboot", "bootloader" }));
            Assert.Contains(result.Checks, c => c.Name == "reboot" && c.Status == "fail");
        }

        [Fact]
        public async Task Browser_dispatch_refuses_without_an_authorized_device()
        {
            FakeAdbClient adb = NoDevice();

            OperationExecutionResult result = await SamsungTestFactory.Create(adb)
                .ExecuteAsync(Request(OperationTypes.SamsungOpenBrowser, null,
                    new Dictionary<string, string> { ["target"] = "youtube" }));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("MTP itself cannot dispatch a URL"));
            Assert.DoesNotContain(adb.AdbCalls, call => call.Contains("am"));
        }

        [Fact]
        public async Task Browser_dispatch_starts_the_chosen_package_with_the_url()
        {
            FakeAdbClient adb = AuthorizedDevice("R58M12345AB");

            OperationExecutionResult result = await SamsungTestFactory.Create(adb)
                .ExecuteAsync(Request(OperationTypes.SamsungOpenBrowser, "R58M12345AB",
                    new Dictionary<string, string> { ["target"] = "chrome" }));

            Assert.True(result.Succeeded);
            string[] call = Assert.Single(adb.AdbCalls, c => c.Contains("am"));
            Assert.Equal(new[] { "-s", "R58M12345AB", "shell", "am", "start", "-a", "android.intent.action.VIEW", "-d", "https://www.google.com/", "-p", "com.android.chrome" }, call);
        }

        [Fact]
        public async Task A_custom_browser_target_must_be_an_absolute_http_address()
        {
            FakeAdbClient adb = AuthorizedDevice("R58M12345AB");

            OperationExecutionResult result = await SamsungTestFactory.Create(adb)
                .ExecuteAsync(Request(OperationTypes.SamsungOpenBrowser, "R58M12345AB",
                    new Dictionary<string, string> { ["target"] = "custom", ["url"] = "javascript:alert(1)" }));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("not an absolute http/https address"));
        }

        [Fact]
        public async Task The_FRP_routine_does_nothing_without_confirmation()
        {
            FakeAdbClient adb = AuthorizedDevice("R58M12345AB");

            OperationExecutionResult result = await SamsungTestFactory.Create(adb)
                .ExecuteAsync(Request(OperationTypes.SamsungFrpTestMode, "R58M12345AB",
                    new Dictionary<string, string> { ["waitSeconds"] = "0" }));

            Assert.False(result.Succeeded);
            Assert.Equal(OperationOutcome.RequiresAttention, result.Outcome);
            Assert.DoesNotContain(adb.AdbCalls, call => call.Contains("content"));
        }

        [Fact]
        public async Task The_FRP_routine_stops_when_no_device_is_authorized()
        {
            FakeAdbClient adb = UnauthorizedDevice("R58M12345AB");

            OperationExecutionResult result = await SamsungTestFactory.Create(adb)
                .ExecuteAsync(Request(OperationTypes.SamsungFrpTestMode, null, new Dictionary<string, string>
                {
                    ["confirm"] = "yes",
                    ["waitSeconds"] = "0"
                }));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("cannot enable USB debugging"));
            Assert.Contains(result.Checks, c => c.Name == "adb-connection" && c.Status == "fail");
            Assert.Contains(result.Warnings, w => w.Contains("RSA prompt"));
        }

        [Fact]
        public async Task The_FRP_routine_clears_the_setup_flags_and_reboots_over_adb()
        {
            FakeAdbClient adb = AuthorizedDevice("R58M12345AB");

            OperationExecutionResult result = await SamsungTestFactory.Create(adb)
                .ExecuteAsync(Request(OperationTypes.SamsungFrpTestMode, "R58M12345AB", new Dictionary<string, string>
                {
                    ["confirm"] = "yes",
                    ["waitSeconds"] = "0"
                }));

            Assert.True(result.Succeeded);
            Assert.Contains(adb.AdbCalls, c => c.Contains("content") &&
                c.Any(a => a.EndsWith("user_setup_complete", StringComparison.Ordinal)));
            Assert.Contains(adb.AdbCalls, c => c.SequenceEqual(new[]
            {
                "-s", "R58M12345AB", "shell", "settings", "put", "global", "device_provisioned", "1"
            }));
            Assert.Contains(adb.AdbCalls, c => c.SequenceEqual(new[] { "-s", "R58M12345AB", "shell", "reboot" }));
            Assert.Contains(result.Checks, c => c.Name == "reboot" && c.Status == "pass");
            Assert.Empty(result.Errors);
        }
    }
}
