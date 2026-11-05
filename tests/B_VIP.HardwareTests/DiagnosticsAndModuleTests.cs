using B_VIP.Contracts.Devices;
using B_VIP.Core;
using B_VIP.Core.Operations;
using B_VIP.Hardware;
using B_VIP.Modules;
using Xunit;

namespace B_VIP.HardwareTests
{
    public sealed class DeviceDiagnosticsTests
    {
        private static FakeAdbClient ConnectedDevice(params (string Key, string Value)[] properties)
        {
            var adb = new FakeAdbClient
            {
                Properties = Props.Of(properties),
                AdbHandler = args => args.Contains("get-state")
                    ? FakeAdbClient.Ok(args, "device")
                    : FakeAdbClient.Ok(args)
            };
            return adb;
        }

        [Fact]
        public async Task Reports_an_error_when_the_device_does_not_answer()
        {
            var adb = new FakeAdbClient
            {
                AdbHandler = args => FakeAdbClient.Failed(args, "device offline")
            };
            var diagnostics = new DeviceDiagnostics(adb);

            DeviceDiagnosticsResponse response = await diagnostics.RunNamedAsync("SER1", [DeviceDiagnostics.CheckIdentity]);

            Assert.Empty(response.Checks);
            Assert.Single(response.Errors);
            Assert.Contains("device offline", response.Errors[0]);
            Assert.Null(response.DeviceDescription);
        }

        [Fact]
        public async Task Reads_the_device_identity_and_android_version_from_real_properties()
        {
            FakeAdbClient adb = ConnectedDevice(
                ("ro.product.manufacturer", "samsung"),
                ("ro.product.model", "SM-G991B"),
                ("ro.build.version.release", "14"));
            var diagnostics = new DeviceDiagnostics(adb);

            DeviceDiagnosticsResponse response = await diagnostics.RunNamedAsync("SER1",
                [DeviceDiagnostics.CheckIdentity, DeviceDiagnostics.CheckAndroidVersion]);

            Assert.Equal("samsung SM-G991B", response.DeviceDescription);
            Assert.Equal(2, response.Checks.Count);
            Assert.Equal("pass", response.Checks[0].Status);
            Assert.Contains("samsung SM-G991B", response.Checks[0].Detail);
            Assert.Equal("pass", response.Checks[1].Status);
            Assert.Contains("Android version: 14", response.Checks[1].Detail);
            Assert.Empty(response.Errors);
        }

        [Fact]
        public async Task A_missing_property_is_reported_as_skip_not_as_a_value()
        {
            FakeAdbClient adb = ConnectedDevice(("ro.product.manufacturer", "samsung"));
            var diagnostics = new DeviceDiagnostics(adb);

            DeviceDiagnosticsResponse response = await diagnostics.RunNamedAsync("SER1",
                [DeviceDiagnostics.CheckAndroidVersion]);

            DiagnosticCheckDto check = Assert.Single(response.Checks);
            Assert.Equal("skip", check.Status);
            Assert.Contains("not reported", check.Detail);
        }

        [Fact]
        public async Task An_outdated_security_patch_raises_a_warning()
        {
            FakeAdbClient adb = ConnectedDevice(("ro.build.version.security_patch", "2020-01-01"));
            var diagnostics = new DeviceDiagnostics(adb);

            DeviceDiagnosticsResponse response = await diagnostics.RunNamedAsync("SER1",
                [DeviceDiagnostics.CheckSecurityPatch]);

            DiagnosticCheckDto check = Assert.Single(response.Checks);
            Assert.Equal("warn", check.Status);
            Assert.Contains(response.Warnings, w => w.Contains("older than six months"));
        }

        [Fact]
        public async Task Root_detection_reports_a_real_answer_from_the_device()
        {
            FakeAdbClient adb = ConnectedDevice();
            adb.ShellHandler = (_, command) => command.Contains("su")
                ? FakeAdbClient.Ok([], "/system/xbin/su")
                : FakeAdbClient.Ok([]);
            var diagnostics = new DeviceDiagnostics(adb);

            DeviceDiagnosticsResponse rooted = await diagnostics.RunNamedAsync("SER1", [DeviceDiagnostics.CheckRoot]);
            Assert.Equal("warn", Assert.Single(rooted.Checks).Status);
            Assert.Contains("su", Assert.Single(rooted.Checks).Detail);

            adb.ShellHandler = (_, _) => FakeAdbClient.Ok([]);
            DeviceDiagnosticsResponse clean = await diagnostics.RunNamedAsync("SER1", [DeviceDiagnostics.CheckRoot]);
            Assert.Equal("pass", Assert.Single(clean.Checks).Status);
        }

        [Fact]
        public async Task ExecuteActionAsync_rejects_unknown_or_argument_less_actions()
        {
            var diagnostics = new DeviceDiagnostics(new FakeAdbClient());

            DeviceActionResult unknown = await diagnostics.ExecuteActionAsync("SER1", "launch-missiles", null);
            Assert.False(unknown.Succeeded);
            Assert.Contains("unknown", unknown.Detail);

            DeviceActionResult missingArgument = await diagnostics.ExecuteActionAsync("SER1", "clear-app", null);
            Assert.False(missingArgument.Succeeded);
        }

        [Fact]
        public async Task ExecuteActionAsync_runs_a_known_action()
        {
            var adb = new FakeAdbClient();
            var diagnostics = new DeviceDiagnostics(adb);

            DeviceActionResult reboot = await diagnostics.ExecuteActionAsync("SER1", "reboot", null);

            Assert.True(reboot.Succeeded);
            Assert.Empty(adb.ShellCommands); // a plain reboot needs no shell round-trip
        }
    }

    public sealed class VendorModuleTests
    {
        private static FakeAdbClient WithProperties(params (string Key, string Value)[] properties)
            => new() { Properties = Props.Of(properties) };

        [Fact]
        public async Task Samsung_module_accepts_a_samsung_handset()
        {
            FakeAdbClient adb = WithProperties(
                ("ro.product.manufacturer", "samsung"),
                ("ro.product.model", "SM-G991B"),
                ("ro.csc.product_code", "EUX"),
                ("ro.build.display.id", "G991BXXU"),
                ("ro.boot.warranty_bit", "0"));
            var module = SamsungTestFactory.Create(adb);

            OperationExecutionResult result = await module.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.SamsungDiagnostic, "SER1", new Dictionary<string, string>(), "corr-1"));

            Assert.True(result.Succeeded);
            Assert.Contains(result.Checks, c => c.Name == "samsung-identity" && c.Status == "pass");
            Assert.Contains(result.Checks, c => c.Name == "samsung-model" && c.Detail!.Contains("SM-G991B"));
            Assert.Contains(result.Checks, c => c.Name == "knox-warranty-flag" && c.Status == "pass");
            Assert.Contains(result.Warnings, w => w.Contains("Odin"));
        }

        [Fact]
        public async Task Samsung_module_refuses_a_non_samsung_device()
        {
            FakeAdbClient adb = WithProperties(
                ("ro.product.manufacturer", "Xiaomi"),
                ("ro.product.model", "Mi 11"));
            var module = SamsungTestFactory.Create(adb);

            OperationExecutionResult result = await module.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.SamsungDiagnostic, "SER1", new Dictionary<string, string>(), "corr-2"));

            Assert.False(result.Succeeded);
            Assert.Equal(OperationOutcome.Failed, result.Outcome);
            Assert.Contains(result.Errors, e => e.Contains("not a Samsung"));
            Assert.Contains(result.Checks, c => c.Name == "samsung-identity" && c.Status == "fail");
        }

        [Fact]
        public async Task Vendor_modules_fail_cleanly_when_no_device_is_supplied()
        {
            var samsung = SamsungTestFactory.Create(WithProperties());
            OperationExecutionResult result = await samsung.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.SamsungDiagnostic, null, new Dictionary<string, string>(), "corr-3"));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("No device serial"));
        }

        [Fact]
        public async Task Vendor_modules_report_a_dead_connection()
        {
            var mediatek = new MediaTekModuleExecutor(new FakeAdbClient()); // no properties

            OperationExecutionResult result = await mediatek.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.MediaTekDiagnostic, "SER1", new Dictionary<string, string>(), "corr-4"));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("did not return any properties"));
        }

        [Fact]
        public async Task MediaTek_module_identifies_an_mt_platform()
        {
            FakeAdbClient adb = WithProperties(
                ("ro.board.platform", "mt6893"),
                ("ro.soc.model", "MT6893"));
            var module = new MediaTekModuleExecutor(adb);

            OperationExecutionResult result = await module.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.MediaTekDiagnostic, "SER1", new Dictionary<string, string>(), "corr-5"));

            Assert.True(result.Succeeded);
            Assert.Contains(result.Checks, c => c.Name == "mediatek-platform" && c.Status == "pass");
            Assert.Contains(result.Warnings, w => w.Contains("SP Flash Tool"));
        }

        [Fact]
        public async Task MediaTek_module_rejects_a_qualcomm_platform()
        {
            var module = new MediaTekModuleExecutor(WithProperties(("ro.board.platform", "sm8450")));

            OperationExecutionResult result = await module.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.MediaTekDiagnostic, "SER1", new Dictionary<string, string>(), "corr-6"));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("MediaTek"));
        }

        [Fact]
        public async Task Qualcomm_module_identifies_an_sm_platform_and_reports_fastboot_availability()
        {
            FakeAdbClient adb = WithProperties(("ro.board.platform", "sm8450"), ("ro.soc.model", "SM8450"));
            var tools = new StubToolLocator();
            var module = new QualcommModuleExecutor(adb, tools);

            OperationExecutionResult result = await module.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.QualcommDiagnostic, "SER1", new Dictionary<string, string>(), "corr-7"));

            Assert.True(result.Succeeded);
            Assert.Contains(result.Checks, c => c.Name == "qualcomm-platform" && c.Status == "pass");
            // fastboot is not installed in the stub locator, so the check reports it honestly.
            Assert.Contains(result.Checks, c => c.Name == "fastboot-availability" && c.Status == "fail");
            Assert.Contains(result.Warnings, w => w.Contains("platform-tools"));
            Assert.Contains(result.Warnings, w => w.Contains("QDLoader"));
        }

        [Fact]
        public async Task Xiaomi_module_verifies_the_brand_and_reads_the_reported_information()
        {
            FakeAdbClient adb = WithProperties(
                ("ro.product.manufacturer", "Xiaomi"),
                ("ro.product.model", "M2101K6G"),
                ("ro.board.platform", "sm6150"),
                ("ro.build.version.release", "13"),
                ("ro.build.version.security_patch", "2024-01-01"),
                ("ro.frp.pst", "/dev/block/bootdevice/by-name/frp"));

            var module = new BrandModuleExecutor(BrandProfiles.Xiaomi, adb, new StubToolLocator(), new StubProcessRunner(),
                [OperationTypes.XiaomiReadInfo, OperationTypes.XiaomiResetFrp]);

            OperationExecutionResult result = await module.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.XiaomiReadInfo, "SER9", new Dictionary<string, string>(), "corr-8"));

            Assert.True(result.Succeeded);
            Assert.Contains(result.Checks, c => c.Name == "brand-identity" && c.Status == "pass");
            Assert.Contains(result.Checks, c => c.Name == "ro.product.model" && c.Detail!.Contains("M2101K6G"));
            Assert.Contains(result.Checks, c => c.Name == "frp-state" && c.Status == "pass");
            Assert.Empty(result.Errors);
        }

        [Fact]
        public async Task Xiaomi_FRP_reset_requires_confirmation_and_then_runs_the_erase_sequence()
        {
            var tools = new StubToolLocator();
            tools.Set(ToolLocator.Fastboot, "C:\\tools\\fastboot.exe");

            var runner = new StubProcessRunner("SER9\tfastboot");
            runner.Handler = request => request.Arguments.FirstOrDefault() == "getvar"
                ? new ProcessResult(request.Executable, request.Arguments, 0, "product: xiaomi", string.Empty, false, false, true, 1, null)
                : new ProcessResult(request.Executable, request.Arguments, 0, "SER9\tfastboot", "Finished.", false, false, true, 1, null);

            var module = new BrandModuleExecutor(BrandProfiles.Xiaomi, new FakeAdbClient(), tools, runner,
                [OperationTypes.XiaomiReadInfo, OperationTypes.XiaomiResetFrp]);

            OperationExecutionResult unconfirmed = await module.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.XiaomiResetFrp, "SER9", new Dictionary<string, string>(), "corr-8a"));

            Assert.False(unconfirmed.Succeeded);
            Assert.Contains(unconfirmed.Errors, e => e.Contains("confirmed"));
            Assert.Empty(runner.Requests);

            OperationExecutionResult confirmed = await module.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.XiaomiResetFrp, "SER9", new Dictionary<string, string> { ["confirm"] = "yes" }, "corr-8b"));

            Assert.True(confirmed.Succeeded);
            Assert.Contains(runner.Requests, r => r.Arguments.SequenceEqual(new[] { "erase", "frp" }));
            Assert.Contains(runner.Requests, r => r.Arguments.SequenceEqual(new[] { "erase", "config" }));
            Assert.Contains(confirmed.Log, l => l.Contains("fastboot erase frp"));
        }

        [Fact]
        public async Task Google_recovery_module_only_publishes_official_links()
        {
            var module = new GoogleRecoveryExecutor(new FakeAdbClient());

            OperationExecutionResult result = await module.ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.GoogleRecoveryGuidance, null, new Dictionary<string, string>(), "corr-9"));

            Assert.True(result.Succeeded);
            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Empty(result.Errors);
            Assert.Equal(3, result.Checks.Count);
            Assert.Contains(result.Checks, c => c.Detail == "https://accounts.google.com/signin/recovery");
            Assert.Contains(result.Log, l => l.Contains("No lock is bypassed"));
        }
    }

    public sealed class OperationExecutorRegistryTests
    {
        private sealed class StubExecutor : IOperationExecutor
        {
            private readonly Func<OperationExecutionRequest, CancellationToken, Task<OperationExecutionResult>> _handler;

            public StubExecutor(string operation, Func<OperationExecutionRequest, CancellationToken, Task<OperationExecutionResult>> handler)
            {
                SupportedOperations = [operation];
                _handler = handler;
            }

            public IReadOnlyList<string> SupportedOperations { get; }

            public Task<OperationExecutionResult> ExecuteAsync(OperationExecutionRequest request, CancellationToken ct = default)
                => _handler(request, ct);
        }

        private static OperationExecutionRequest Request(string type)
            => new(type, null, new Dictionary<string, string>(), "corr");

        [Fact]
        public async Task Routes_to_the_registered_executor()
        {
            var executor = new StubExecutor(OperationTypes.AdbDevices, (_, _) =>
                Task.FromResult(OperationExecutionResult.Failure(OperationTypes.AdbDevices, "ok")));
            var registry = new OperationExecutorRegistry([executor]);

            Assert.True(registry.CanExecute(OperationTypes.AdbDevices));
            Assert.Contains(OperationTypes.AdbDevices, registry.RegisteredOperationTypes);

            OperationExecutionResult result = await registry.ExecuteAsync(Request(OperationTypes.AdbDevices));
            Assert.Equal("ok", result.Summary);
        }

        [Fact]
        public async Task An_unregistered_operation_type_fails_with_a_clear_message()
        {
            var registry = new OperationExecutorRegistry(Array.Empty<IOperationExecutor>());

            OperationExecutionResult result = await registry.ExecuteAsync(Request("no.such.operation"));

            Assert.False(result.Succeeded);
            Assert.Equal(OperationOutcome.Failed, result.Outcome);
            Assert.Contains("No executor is registered", result.Errors[0]);
        }

        [Fact]
        public async Task An_executor_that_throws_is_converted_into_a_failed_result()
        {
            var executor = new StubExecutor(OperationTypes.ToolProbe, (_, _) => throw new InvalidOperationException("boom"));
            var registry = new OperationExecutorRegistry([executor]);

            OperationExecutionResult result = await registry.ExecuteAsync(Request(OperationTypes.ToolProbe));

            Assert.False(result.Succeeded);
            Assert.Contains("unexpected error", result.Errors[0]);
        }

        [Fact]
        public async Task Cancellation_is_reported_as_cancelled_not_as_a_failure()
        {
            var executor = new StubExecutor(OperationTypes.AdbDevices, (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(OperationExecutionResult.Failure(OperationTypes.AdbDevices, "unreachable"));
            });
            var registry = new OperationExecutorRegistry([executor]);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            OperationExecutionResult result = await registry.ExecuteAsync(Request(OperationTypes.AdbDevices), cancellation.Token);

            Assert.False(result.Succeeded);
            Assert.Equal(OperationOutcome.Cancelled, result.Outcome);
        }

        [Fact]
        public void The_same_key_registered_twice_is_overridden_by_the_last_executor()
        {
            var first = new StubExecutor(OperationTypes.AdbDevices, (_, _) => Task.FromResult(
                OperationExecutionResult.Failure("first", "first")));
            var second = new StubExecutor(OperationTypes.AdbDevices, (_, _) => Task.FromResult(
                OperationExecutionResult.Failure("second", "second")));
            var registry = new OperationExecutorRegistry([first, second]);

            Assert.Single(registry.RegisteredOperationTypes);
        }
    }
}
