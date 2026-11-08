using B_VIP.Core;
using B_VIP.Hardware;
using B_VIP.Modules;
using Xunit;

namespace B_VIP.HardwareTests
{
    public sealed class AdbOutputParsingTests
    {
        private const string Sample = """
            List of devices attached
            HT123ABC	device usb:1-2 product:panther model:Pixel_7 device:panther transport_id:3
            R58M12UNAUTH	unauthorized usb:1-3 transport_id:4
            SWMEDIA	recovery usb:1-4 transport_id:5
            * daemon started successfully *

            """;

        [Fact]
        public void ParseAdbDevices_reads_states_models_and_transport_ids()
        {
            IReadOnlyList<B_VIP.Contracts.Devices.DeviceInfoDto> devices = DeviceDetector.ParseAdbDevices(Sample);

            Assert.Equal(3, devices.Count);

            B_VIP.Contracts.Devices.DeviceInfoDto connected = devices[0];
            Assert.Equal("HT123ABC", connected.Serial);
            Assert.Equal(DeviceConnectionState.Connected, connected.State);
            Assert.Equal("Pixel_7", connected.Model);
            Assert.Equal("panther", connected.Product);
            Assert.Equal("3", connected.TransportId);
            Assert.Equal("usb:1-2", connected.InterfaceDetail);
            Assert.Equal(DeviceInterfaceType.Adb, connected.InterfaceType);
            Assert.Contains("shell", connected.Capabilities);
            Assert.Contains("logcat", connected.Capabilities);
            Assert.Equal("adb devices -l", connected.DetectedBy);

            Assert.Equal(DeviceConnectionState.Unauthorized, devices[1].State);
            Assert.Equal(new[] { "authorize_device" }, devices[1].Capabilities);

            Assert.Equal(DeviceConnectionState.Recovery, devices[2].State);
            Assert.Equal(DeviceInterfaceType.Recovery, devices[2].InterfaceType);
            Assert.Contains("sideload", devices[2].Capabilities);
        }

        [Fact]
        public void ParseAdbDevices_ignores_the_header_daemon_and_empty_lines()
        {
            IReadOnlyList<B_VIP.Contracts.Devices.DeviceInfoDto> devices = DeviceDetector.ParseAdbDevices(
                "List of devices attached\n* daemon running as pid 42\n\n");

            Assert.Empty(devices);
            Assert.Empty(DeviceDetector.ParseAdbDevices(string.Empty));
        }

        [Theory]
        [InlineData("device", DeviceConnectionState.Connected)]
        [InlineData("unauthorized", DeviceConnectionState.Unauthorized)]
        [InlineData("offline", DeviceConnectionState.Disconnected)]
        [InlineData("recovery", DeviceConnectionState.Recovery)]
        [InlineData("sideload", DeviceConnectionState.Recovery)]
        [InlineData("bootloader", DeviceConnectionState.Fastboot)]
        [InlineData("host", DeviceConnectionState.Busy)]
        [InlineData("something-new", DeviceConnectionState.Unsupported)]
        public void ParseAdbDevices_maps_every_known_state_token(string token, DeviceConnectionState expected)
        {
            IReadOnlyList<B_VIP.Contracts.Devices.DeviceInfoDto> devices =
                DeviceDetector.ParseAdbDevices($"SER123\t{token}\n");

            Assert.Single(devices);
            Assert.Equal(expected, devices[0].State);
        }

        [Fact]
        public void BuildDeviceId_normalizes_the_serial_or_falls_back_to_a_guid()
        {
            Assert.Equal("ht123abc", DeviceDetector.BuildDeviceId("HT123ABC"));
            Assert.Equal("abc123", DeviceDetector.BuildDeviceId("ab:c-123"));

            string fallback = DeviceDetector.BuildDeviceId("!!!");
            Assert.True(Guid.TryParse(fallback, out _));
        }

        [Fact]
        public void ParseFastbootDevices_reads_bootloader_devices_and_skips_permission_noise()
        {
            const string output = """
                ABC123	fastboot
                XYZ789	fastboot
                no permissions (user in plugdev group); see [http://developer.android.com/tools/device.html]
                """;

            IReadOnlyList<B_VIP.Contracts.Devices.DeviceInfoDto> devices = DeviceDetector.ParseFastbootDevices(output);

            Assert.Equal(2, devices.Count);
            Assert.All(devices, d =>
            {
                Assert.Equal(DeviceConnectionState.Fastboot, d.State);
                Assert.Equal(DeviceInterfaceType.Fastboot, d.InterfaceType);
                Assert.Equal("fastboot devices", d.DetectedBy);
                Assert.Contains("flash", d.Capabilities);
            });
            Assert.Equal("ABC123", devices[0].Serial);
            Assert.Equal("fastboot", devices[0].InterfaceDetail);
            Assert.Empty(DeviceDetector.ParseFastbootDevices(string.Empty));
        }
    }

    /// <summary>
    /// The getprop dump that a real handset prints: 1300+ lines of <c>[key]: [value]</c>. Nothing in
    /// it may be dropped, because the read-info table is built from these entries.
    /// </summary>
    public sealed class GetpropParsingTests
    {
        [Fact]
        public void Parses_the_standard_bracketed_pairs()
        {
            const string output = """
                [ro.product.model]: [M2101K6G]
                [ro.product.brand]: [Redmi]
                [ro.miui.ui.version.name]: [V14.0.5.0.TKHMIXM]
                [ro.mi.os.version.name]: [OS1.0.4.0.UKHMIXM]
                [ro.build.version.release]: [13]
                [ro.build.version.security_patch]: [2023-05-01]
                [ro.serialno]: [9A1B2C3D]
                """;

            IReadOnlyList<Dictionary<string, string>> props = AdbClient.ParseGetprop(output);

            Assert.Equal(7, props.Count);
            Assert.Equal("M2101K6G", First(props, "ro.product.model"));
            Assert.Equal("Redmi", First(props, "ro.product.brand"));
            Assert.Equal("V14.0.5.0.TKHMIXM", First(props, "ro.miui.ui.version.name"));
            Assert.Equal("OS1.0.4.0.UKHMIXM", First(props, "ro.mi.os.version.name"));
            Assert.Equal("13", First(props, "ro.build.version.release"));
            Assert.Equal("2023-05-01", First(props, "ro.build.version.security_patch"));
            Assert.Equal("9A1B2C3D", First(props, "ro.serialno"));
        }

        [Fact]
        public void Keeps_empty_values_and_values_containing_colons_or_brackets()
        {
            const string output = """
                [persist.sys.blank]: []
                [ro.build.description]: [redmi:user:13/TKQ1:1234/release-keys]
                [ro.url]: [https://example.invalid/a?b=c]
                [dalvik.vm.args]: [-Xmx[1g]]
                """;

            IReadOnlyList<Dictionary<string, string>> props = AdbClient.ParseGetprop(output);

            Assert.Equal(4, props.Count);
            Assert.Equal(string.Empty, First(props, "persist.sys.blank"));
            Assert.Equal("redmi:user:13/TKQ1:1234/release-keys", First(props, "ro.build.description"));
            Assert.Equal("https://example.invalid/a?b=c", First(props, "ro.url"));
            Assert.Equal("-Xmx[1g]", First(props, "dalvik.vm.args"));
        }

        [Fact]
        public void Accepts_bracketless_and_windows_line_endings_and_ignores_noise()
        {
            string output = "ro.product.model: Redmi Note 12\r\nro.product.brand=Poco\r\n\r\nrandom noise without a separator\r\n";

            IReadOnlyList<Dictionary<string, string>> props = AdbClient.ParseGetprop(output);

            Assert.Equal(2, props.Count);
            Assert.Equal("Redmi Note 12", First(props, "ro.product.model"));
            Assert.Equal("Poco", First(props, "ro.product.brand"));
            Assert.Empty(AdbClient.ParseGetprop(null));
            Assert.Empty(AdbClient.ParseGetprop("   \n\n"));
        }

        [Fact]
        public void A_full_dump_is_parsed_completely_and_in_order()
        {
            const int lines = 1349;
            var builder = new System.Text.StringBuilder(lines * 40);
            for (int i = 0; i < lines; i++)
            {
                builder.Append("[ro.test.key").Append(i).Append("]: [value").Append(i).Append("]\n");
            }

            IReadOnlyList<Dictionary<string, string>> props = AdbClient.ParseGetprop(builder.ToString());

            Assert.Equal(lines, props.Count);
            Assert.Equal("value0", First(props, "ro.test.key0"));
            Assert.Equal("value1348", First(props, "ro.test.key1348"));
        }

        [Fact]
        public async Task Xiaomi_read_info_fills_rows_from_a_hyperos_dump()
        {
            const string dump = """
                [ro.product.model]: [2312DRA50G]
                [ro.product.vendor.model]: [2312DRA50G]
                [ro.product.brand]: [Redmi]
                [ro.product.manufacturer]: [Xiaomi]
                [ro.mi.os.version.name]: [OS1.0.8.0.UMOMIXM]
                [ro.build.version.incremental]: [OS1.0.8.0.UMOMIXM]
                [ro.build.version.release]: [14]
                [ro.build.version.security_patch]: [2024-06-01]
                [ro.serialno]: [4f9c1d2a]
                [ro.miui.region]: [IN]
                [ro.board.platform]: [taro]
                """;

            var adb = new FakeAdbClient { Properties = AdbClient.ParseGetprop(dump) };
            var module = new B_VIP.Modules.BrandModuleExecutor(
                B_VIP.Modules.BrandProfiles.Xiaomi, adb, new StubToolLocator(), new ProcessRunner(),
                [B_VIP.Core.Operations.OperationTypes.XiaomiReadInfo]);

            OperationExecutionResult result = await module.ExecuteAsync(new OperationExecutionRequest(
                B_VIP.Core.Operations.OperationTypes.XiaomiReadInfo, "SER1", new Dictionary<string, string>(), "corr-x"));

            Assert.True(result.Succeeded, string.Join("; ", result.Errors));
            Assert.Contains(result.Checks, c => c.Name == "ro.product.model" && c.Status == "pass" && c.Detail!.Contains("2312DRA50G"));
            Assert.Contains(result.Checks, c => c.Name == "ro.product.brand" && c.Detail!.Contains("Redmi"));
            Assert.Contains(result.Checks, c => c.Name == "ro.mi.os.version.name" && c.Detail!.Contains("UMOMIXM"));
            Assert.Contains(result.Checks, c => c.Name == "ro.build.version.release" && c.Detail!.Contains("14"));
            Assert.Contains(result.Checks, c => c.Name == "ro.build.version.security_patch" && c.Detail!.Contains("2024-06-01"));
            Assert.Contains(result.Checks, c => c.Name == "ro.serialno" && c.Detail!.Contains("4f9c1d2a"));
            Assert.Contains(result.Checks, c => c.Name == "ro.miui.region" && c.Detail!.Contains("IN"));
        }

        [Fact]
        public async Task Xiaomi_read_info_falls_back_to_the_vendor_and_odm_model_keys()
        {
            var adb = new FakeAdbClient
            {
                Properties = AdbClient.ParseGetprop("""
                    [ro.product.odm.model]: [22111317G]
                    [ro.product.manufacturer]: [Xiaomi]
                    [ro.build.product]: [fuxi]
                    [ro.board.platform]: [taro]
                    """)
            };

            var module = new B_VIP.Modules.BrandModuleExecutor(
                B_VIP.Modules.BrandProfiles.Xiaomi, adb, new StubToolLocator(), new ProcessRunner(),
                [B_VIP.Core.Operations.OperationTypes.XiaomiReadInfo]);

            OperationExecutionResult result = await module.ExecuteAsync(new OperationExecutionRequest(
                B_VIP.Core.Operations.OperationTypes.XiaomiReadInfo, "SER1", new Dictionary<string, string>(), "corr-y"));

            Assert.True(result.Succeeded, string.Join("; ", result.Errors));
            Assert.Contains(result.Checks, c => c.Name == "ro.product.model" && c.Status == "pass" && c.Detail!.Contains("22111317G"));
            Assert.Contains(result.Checks, c => c.Name == "ro.product.brand" && c.Detail!.Contains("Xiaomi"));
        }

        private static string? First(IReadOnlyList<Dictionary<string, string>> props, string key)
        {
            foreach (Dictionary<string, string> entry in props)
            {
                if (entry.TryGetValue(key, out string? value))
                {
                    return value;
                }
            }

            return null;
        }
    }

    public sealed class ToolLocatorTests
    {
        [Fact]
        public void Find_returns_the_path_of_a_tool_in_the_configured_directory()
        {
            string directory = Path.Combine(Path.GetTempPath(), "bvip-tools-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string adbPath = Path.Combine(directory, "adb.exe");
                File.WriteAllText(adbPath, "#!/bin/sh\n");

                var locator = new ToolLocator(new ToolLocatorOptions { PlatformToolsPath = directory }, new StubProcessRunner());

                Assert.Equal(Path.GetFullPath(adbPath), locator.Find("adb"));
                Assert.Equal(Path.GetFullPath(adbPath), locator.Find("adb.exe"));
                Assert.Null(locator.Find("fastboot"));
                Assert.Null(locator.Find("tool-that-does-not-exist-xyz"));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void Find_falls_back_to_the_additional_search_paths()
        {
            string first = Path.Combine(Path.GetTempPath(), "bvip-a-" + Guid.NewGuid().ToString("N"));
            string second = Path.Combine(Path.GetTempPath(), "bvip-b-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            try
            {
                string toolPath = Path.Combine(second, "fastboot.exe");
                File.WriteAllText(toolPath, "#!/bin/sh\n");

                var locator = new ToolLocator(new ToolLocatorOptions
                {
                    PlatformToolsPath = first,
                    AdditionalSearchPaths = [second]
                }, new StubProcessRunner());

                Assert.Equal(Path.GetFullPath(toolPath), locator.Find("fastboot"));
            }
            finally
            {
                Directory.Delete(first, recursive: true);
                Directory.Delete(second, recursive: true);
            }
        }

        [Fact]
        public void Find_prefers_the_bundled_platform_tools_folder_before_PATH()
        {
            string application = Path.Combine(Path.GetTempPath(), "bvip-app-" + Guid.NewGuid().ToString("N"));
            string bundled = Path.Combine(application, "platform-tools");
            Directory.CreateDirectory(bundled);
            try
            {
                string adbPath = Path.Combine(bundled, "adb.exe");
                string fastbootPath = Path.Combine(bundled, "fastboot.exe");
                File.WriteAllText(adbPath, "#!/bin/sh\n");
                File.WriteAllText(fastbootPath, "#!/bin/sh\n");

                var locator = new ToolLocator(new ToolLocatorOptions { BaseDirectory = application }, new StubProcessRunner());

                Assert.Equal(Path.GetFullPath(adbPath), locator.Find("adb"));
                Assert.Equal(Path.GetFullPath(fastbootPath), locator.Find("fastboot.exe"));
            }
            finally
            {
                Directory.Delete(application, recursive: true);
            }
        }

        [Fact]
        public void Find_uses_a_tool_that_sits_directly_beside_the_executable()
        {
            string application = Path.Combine(Path.GetTempPath(), "bvip-app-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(application);
            try
            {
                string adbPath = Path.Combine(application, "adb.exe");
                File.WriteAllText(adbPath, "#!/bin/sh\n");

                var locator = new ToolLocator(new ToolLocatorOptions { BaseDirectory = application }, new StubProcessRunner());

                Assert.Equal(Path.GetFullPath(adbPath), locator.Find("adb"));
            }
            finally
            {
                Directory.Delete(application, recursive: true);
            }
        }

        [Fact]
        public void Find_walks_up_to_the_development_tools_folder()
        {
            string repository = Path.Combine(Path.GetTempPath(), "bvip-repo-" + Guid.NewGuid().ToString("N"));
            string output = Path.Combine(repository, "src", "bin", "Debug", "net8.0");
            string toolsFolder = Path.Combine(repository, ".tools");
            Directory.CreateDirectory(output);
            Directory.CreateDirectory(toolsFolder);
            try
            {
                string adbPath = Path.Combine(toolsFolder, "adb.exe");
                string fastbootPath = Path.Combine(toolsFolder, "fastboot.exe");
                File.WriteAllText(adbPath, "#!/bin/sh\n");
                File.WriteAllText(fastbootPath, "#!/bin/sh\n");

                var locator = new ToolLocator(new ToolLocatorOptions { BaseDirectory = output }, new StubProcessRunner());

                Assert.Equal(Path.GetFullPath(adbPath), locator.Find("adb"));
                Assert.Equal(Path.GetFullPath(fastbootPath), locator.Find("fastboot"));
            }
            finally
            {
                Directory.Delete(repository, recursive: true);
            }
        }

        [Fact]
        public async Task GetStatusAsync_reports_both_tools_when_both_are_bundled()
        {
            string application = Path.Combine(Path.GetTempPath(), "bvip-app-" + Guid.NewGuid().ToString("N"));
            string bundled = Path.Combine(application, "platform-tools");
            Directory.CreateDirectory(bundled);
            try
            {
                File.WriteAllText(Path.Combine(bundled, "adb.exe"), "#!/bin/sh\n");
                File.WriteAllText(Path.Combine(bundled, "fastboot.exe"), "#!/bin/sh\n");

                var locator = new ToolLocator(new ToolLocatorOptions { BaseDirectory = application }, new StubProcessRunner());

                IReadOnlyList<B_VIP.Contracts.Devices.ToolStatusDto> statuses = await locator.GetAllStatusAsync();

                Assert.Equal(2, statuses.Count);
                Assert.All(statuses, status => Assert.True(status.Found));
                Assert.Equal(2, statuses.Count(status => status.Found));
            }
            finally
            {
                Directory.Delete(application, recursive: true);
            }
        }

        [Fact]
        public async Task GetStatusAsync_runs_the_resolved_full_path_instead_of_the_bare_tool_name()
        {
            string application = Path.Combine(Path.GetTempPath(), "bvip-app-" + Guid.NewGuid().ToString("N"));
            string bundled = Path.Combine(application, "platform-tools");
            Directory.CreateDirectory(bundled);
            try
            {
                string adbPath = Path.GetFullPath(Path.Combine(bundled, "adb.exe"));
                File.WriteAllText(adbPath, "#!/bin/sh\n");

                var runner = new StubProcessRunner("Android Debug Bridge version 35.0.2");
                var locator = new ToolLocator(new ToolLocatorOptions { BaseDirectory = application }, runner);

                B_VIP.Contracts.Devices.ToolStatusDto status = await locator.GetStatusAsync("adb");

                Assert.True(status.Found);
                Assert.Equal(adbPath, status.Path);
                Assert.Equal(adbPath, Assert.Single(runner.Requests).Executable);
                Assert.Equal(new[] { "version" }, runner.Requests[0].Arguments.ToArray());
            }
            finally
            {
                Directory.Delete(application, recursive: true);
            }
        }

        [Fact]
        public async Task GetStatusAsync_reports_a_missing_tool_instead_of_failing()
        {
            var locator = new ToolLocator(new ToolLocatorOptions(), new StubProcessRunner());

            B_VIP.Contracts.Devices.ToolStatusDto status = await locator.GetStatusAsync("tool-that-does-not-exist-xyz");

            Assert.False(status.Found);
            Assert.Null(status.Path);
            Assert.NotNull(status.Error);
            Assert.Contains("not found", status.Error);
        }
    }

    public sealed class ProcessRunnerTests
    {
        private static (string Executable, string[] Arguments) EchoCommand(string text)
            => OperatingSystem.IsWindows()
                ? ("cmd.exe", ["/c", $"echo {text}"])
                : ("/bin/echo", [text]);

        private static (string Executable, string[] Arguments) SleepCommand(int seconds)
            => OperatingSystem.IsWindows()
                ? ("cmd.exe", ["/c", $"ping -n {seconds + 1} 127.0.0.1 >nul"])
                : ("/bin/sleep", [seconds.ToString()]);

        [Fact]
        public async Task Runs_a_process_and_captures_its_output()
        {
            var runner = new ProcessRunner();
            (string executable, string[] arguments) = EchoCommand("hello-bvip");

            ProcessResult result = await runner.RunAsync(new ProcessRequest(executable, arguments, TimeoutSeconds: 15));

            Assert.True(result.Started, result.FailureReason);
            Assert.True(result.Succeeded);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("hello-bvip", result.StandardOutput.Trim());
            Assert.False(result.TimedOut);
            Assert.False(result.Cancelled);
            Assert.Null(result.FailureReason);
        }

        [Fact]
        public async Task Passes_arguments_without_a_shell()
        {
            var runner = new ProcessRunner();
            (string executable, string[] arguments) = EchoCommand("two words kept together");

            ProcessResult result = await runner.RunAsync(new ProcessRequest(executable, arguments, TimeoutSeconds: 15));

            Assert.True(result.Succeeded);
            Assert.Contains("two words kept together", result.StandardOutput);
        }

        [Fact]
        public async Task Reports_a_missing_executable_without_throwing()
        {
            var runner = new ProcessRunner();

            ProcessResult result = await runner.RunAsync(new ProcessRequest(
                Path.Combine(Path.GetTempPath(), "definitely-missing-tool-xyz"), ["--version"], TimeoutSeconds: 5));

            Assert.False(result.Started);
            Assert.False(result.Succeeded);
            Assert.Contains("not found", result.FailureReason);
        }

        [Fact]
        public async Task Refuses_to_execute_batch_scripts()
        {
            string batch = Path.Combine(Path.GetTempPath(), $"bvip-{Guid.NewGuid():N}.bat");
            await File.WriteAllTextAsync(batch, "@echo off\r\necho hi\r\n");
            try
            {
                var runner = new ProcessRunner();
                ProcessResult result = await runner.RunAsync(new ProcessRequest(batch, [], TimeoutSeconds: 5));

                Assert.False(result.Started);
                Assert.Contains("Batch scripts are not executed", result.FailureReason);
            }
            finally
            {
                File.Delete(batch);
            }
        }

        [Fact]
        public async Task Terminates_a_process_that_exceeds_its_timeout()
        {
            var runner = new ProcessRunner();
            (string executable, string[] arguments) = SleepCommand(20);

            ProcessResult result = await runner.RunAsync(new ProcessRequest(executable, arguments, TimeoutSeconds: 1));

            Assert.True(result.Started);
            Assert.True(result.TimedOut);
            Assert.False(result.Succeeded);
            Assert.Contains("timeout", result.FailureReason);
            Assert.True(result.DurationMs < 15_000, $"the process was not terminated promptly ({result.DurationMs} ms)");
        }

        [Fact]
        public async Task Cancelling_the_token_cancels_the_process()
        {
            var runner = new ProcessRunner();
            (string executable, string[] arguments) = SleepCommand(20);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            ProcessResult result = await runner.RunAsync(
                new ProcessRequest(executable, arguments, TimeoutSeconds: 30), cancellation.Token);

            Assert.True(result.Cancelled);
            Assert.False(result.TimedOut);
            Assert.False(result.Succeeded);
            Assert.Contains("cancelled", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task An_empty_executable_path_fails_cleanly()
        {
            var runner = new ProcessRunner();

            ProcessResult result = await runner.RunAsync(new ProcessRequest("   ", [], TimeoutSeconds: 5));

            Assert.False(result.Started);
            Assert.Contains("empty", result.FailureReason);
        }
    }
}
