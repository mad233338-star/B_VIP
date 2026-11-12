using System.IO;
using B_VIP.Core.Operations;
using B_VIP.Modules;
using Xunit;

namespace B_VIP.HardwareTests
{
    /// <summary>
    /// The Install APK / Sideload button: an .apk is installed on a booted handset, anything else is
    /// handed to the recovery. Both go through the same operation, so both are covered here.
    /// </summary>
    public sealed class AdbPackageTests
    {
        private static string TempFile(string name)
        {
            string folder = Path.Combine(Path.GetTempPath(), "BVIP_ADB_PACKAGE_TESTS");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, name);
            File.WriteAllText(path, "stub");
            return path;
        }

        private static AdbOperationExecutor Executor(FakeAdbClient adb)
            => new(adb, new StubToolLocator());

        [Fact]
        public async Task Installs_an_apk_on_the_selected_handset()
        {
            var adb = new FakeAdbClient();
            string apk = TempFile("sample.apk");

            OperationExecutionResult result = await Executor(adb).ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.AdbPackage, "SER1",
                new Dictionary<string, string> { ["path"] = apk, ["mode"] = "install" }, "corr-p1"));

            Assert.True(result.Succeeded);
            Assert.Contains(adb.AdbCalls, call => call.SequenceEqual(new[] { "-s", "SER1", "install", "-r", apk }));
            Assert.Contains(result.Checks, c => c.Name == "install" && c.Status == "pass");
        }

        [Fact]
        public async Task Sideloads_a_zip_without_dressing_it_up_as_an_install()
        {
            var adb = new FakeAdbClient();
            string zip = TempFile("payload.zip");

            OperationExecutionResult result = await Executor(adb).ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.AdbPackage, null,
                new Dictionary<string, string> { ["path"] = zip, ["mode"] = "sideload" }, "corr-p2"));

            Assert.True(result.Succeeded);
            Assert.Contains(adb.AdbCalls, call => call.SequenceEqual(new[] { "sideload", zip }));
            Assert.Contains(result.Warnings, w => w.Contains("Do not unplug the cable"));
        }

        [Fact]
        public async Task Chooses_sideload_for_a_zip_when_no_mode_is_given()
        {
            var adb = new FakeAdbClient();
            string zip = TempFile("auto.zip");

            await Executor(adb).ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.AdbPackage, null,
                new Dictionary<string, string> { ["path"] = zip }, "corr-p3"));

            Assert.Contains(adb.AdbCalls, call => call.SequenceEqual(new[] { "sideload", zip }));
        }

        [Fact]
        public async Task Refuses_a_missing_file_instead_of_running_adb()
        {
            var adb = new FakeAdbClient();
            string missing = Path.Combine(Path.GetTempPath(), "BVIP_ADB_PACKAGE_TESTS", "absent.apk");

            OperationExecutionResult result = await Executor(adb).ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.AdbPackage, "SER1",
                new Dictionary<string, string> { ["path"] = missing }, "corr-p4"));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("missing or no longer on disk"));
            Assert.Empty(adb.AdbCalls);
        }

        [Fact]
        public async Task Reports_the_tool_error_when_adb_refuses_the_package()
        {
            var adb = new FakeAdbClient
            {
                AdbHandler = args => FakeAdbClient.Failed(args, "adb: failed to install: INSTALL_FAILED_INVALID_APK")
            };
            string apk = TempFile("broken.apk");

            OperationExecutionResult result = await Executor(adb).ExecuteAsync(new OperationExecutionRequest(
                OperationTypes.AdbPackage, "SER1",
                new Dictionary<string, string> { ["path"] = apk, ["mode"] = "install" }, "corr-p5"));

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("INSTALL_FAILED_INVALID_APK"));
        }

        [Fact]
        public void The_package_routine_is_registered_and_needs_no_device_serial()
        {
            OperationTypeDefinition? definition = OperationTypes.Find(OperationTypes.AdbPackage);

            Assert.NotNull(definition);
            Assert.False(definition!.RequiresDevice);
            Assert.False(definition.DataLossRisk);

            // The shell resolves the button through the registry, so the executor must claim it.
            var registry = new OperationExecutorRegistry([new AdbOperationExecutor(new FakeAdbClient(), new StubToolLocator())]);
            Assert.True(registry.CanExecute(OperationTypes.AdbPackage));
        }
    }
}
