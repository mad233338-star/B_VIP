using System;
using System.IO;
using System.Linq;
using B_VIP.Desktop.Services.Samsung;
using Xunit;

namespace B_VIP.LoaderTests
{
    /// <summary>
    /// The Odin screen shells out to whatever engine the release bundles, so the resolver, the
    /// four-slot command line and the progress parser are pinned here: a wrong flag would flash the
    /// wrong partition, and a wrong percentage would hide a failed flash behind a full bar.
    /// </summary>
    public sealed class OdinEngineTests : IDisposable
    {
        private readonly string _root;

        public OdinEngineTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "bvip-odin-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }

        private string Touch(string name)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllText(path, "stub");
            return path;
        }

        [Fact]
        public void Resolve_FindsTheBundledOdin4Engine()
        {
            string exe = Touch("odin4.exe");

            OdinEngineInfo engine = OdinEngine.Resolve(_root, _ => null);

            Assert.True(engine.Found);
            Assert.Equal(OdinEngineKind.Odin4, engine.Kind);
            Assert.Equal(exe, engine.Executable);
            // The engine's own folder is the working directory so its DLLs resolve.
            Assert.Equal(_root, engine.Folder);
            Assert.Equal("odin4", engine.Name);
        }

        [Fact]
        public void Resolve_PrefersOdin4OverHeimdall()
        {
            Touch("heimdall.exe");
            Touch("odin4.exe");

            OdinEngineInfo engine = OdinEngine.Resolve(_root, _ => null);

            Assert.Equal(OdinEngineKind.Odin4, engine.Kind);
        }

        [Fact]
        public void Resolve_FallsBackToHeimdall()
        {
            Touch("heimdall.exe");

            OdinEngineInfo engine = OdinEngine.Resolve(_root, _ => null);

            Assert.True(engine.Found);
            Assert.Equal(OdinEngineKind.Heimdall, engine.Kind);
            Assert.Equal("heimdall", engine.Name);
        }

        [Fact]
        public void Resolve_AcceptsAnEngineOnThePathVariable()
        {
            string exe = Touch("odin4.exe");

            OdinEngineInfo engine = OdinEngine.Resolve(Path.Combine(_root, "absent"), name => name == "odin4.exe" ? exe : null);

            Assert.True(engine.Found);
            Assert.Equal(exe, engine.Executable);
            Assert.Contains("PATH", engine.Detail);
        }

        [Fact]
        public void Resolve_IgnoresAGuiOnlyOdin3Window()
        {
            // A window cannot be driven headlessly: the screen must never fall back to one.
            Touch("Odin3 v3.10.0.exe");

            OdinEngineInfo engine = OdinEngine.Resolve(_root, _ => null);

            Assert.False(engine.Found);
            Assert.Equal(OdinEngineKind.None, engine.Kind);
        }

        [Fact]
        public void EveryResolvableEngineRunsSilentlyWithoutAWindow()
        {
            Touch("odin4.exe");
            Assert.True(OdinEngine.Resolve(_root, _ => null).SupportsCli);

            File.Delete(Path.Combine(_root, "odin4.exe"));
            Touch("heimdall.exe");
            Assert.True(OdinEngine.Resolve(_root, _ => null).SupportsCli);
        }

        [Fact]
        public void BuildArguments_IsEmptyForAnythingButOdin4()
        {
            Assert.Empty(OdinEngine.BuildArguments(OdinEngineKind.None, Slots(), null, true, true));
            Assert.Empty(OdinEngine.BuildArguments(OdinEngineKind.Heimdall, Slots(), null, true, true));
        }

        [Fact]
        public void Resolve_ReportsAnActionableMessageWhenNothingIsInstalled()
        {
            OdinEngineInfo engine = OdinEngine.Resolve(_root, _ => null);

            Assert.False(engine.Found);
            Assert.Equal(OdinEngineKind.None, engine.Kind);
            Assert.Contains("heimdall.exe", engine.Detail);
            Assert.Contains("odin4.exe", engine.Detail);
            Assert.Contains(_root, engine.Detail);
            Assert.Equal("not installed", engine.Name);
        }

        private static IReadOnlyList<OdinSlotFile> Slots(bool withCp = true)
        {
            List<OdinSlotFile> slots =
            [
                new("BL", @"C:\fw\BL.tar.md5"),
                new("AP", @"C:\fw\AP.tar.md5"),
                new("CSC", @"C:\fw\CSC.tar.md5")
            ];

            if (withCp)
            {
                slots.Add(new("CP", @"C:\fw\CP.tar.md5"));
            }

            return slots;
        }

        [Fact]
        public void BuildArguments_Odin4_PassesEverySlotWithItsFlag()
        {
            var arguments = OdinEngine.BuildArguments(
                OdinEngineKind.Odin4, Slots(), @"C:\fw\phone.pit", autoReboot: true, resetTime: true);

            Assert.Equal(
                ["-b", @"C:\fw\BL.tar.md5", "-a", @"C:\fw\AP.tar.md5", "-c", @"C:\fw\CP.tar.md5",
                 "-s", @"C:\fw\CSC.tar.md5", "--pit", @"C:\fw\phone.pit"],
                arguments);
        }

        [Fact]
        public void BuildArguments_Odin4_OnlyPassesTheSlotsThatAreFilled()
        {
            var arguments = OdinEngine.BuildArguments(
                OdinEngineKind.Odin4, [new("AP", @"C:\fw\AP.tar.md5")], pitPath: null,
                autoReboot: false, resetTime: false);

            Assert.Equal(["-a", @"C:\fw\AP.tar.md5", "--no-reboot"], arguments);
            Assert.DoesNotContain("-b", arguments);
            Assert.DoesNotContain("--pit", arguments);
        }

        [Fact]
        public void BuildHeimdallArguments_WritesPartitionImagesWithThePit()
        {
            List<OdinPartitionImage> images =
            [
                new("BOOT", "boot.img", @"C:\tmp\boot.img", "AP"),
                new("RECOVERY", "recovery.img", @"C:\tmp\recovery.img", "AP")
            ];

            var arguments = OdinEngine.BuildHeimdallArguments(images, @"C:\fw\phone.pit", autoReboot: true);

            Assert.Equal("flash", arguments[0]);
            Assert.Equal(
                ["flash", "--BOOT", @"C:\tmp\boot.img", "--RECOVERY", @"C:\tmp\recovery.img",
                 "--pit", @"C:\fw\phone.pit", "--verbose", "--stdout-errors"],
                arguments);
        }

        [Fact]
        public void BuildHeimdallArguments_HonoursTheNoRebootOption()
        {
            var arguments = OdinEngine.BuildHeimdallArguments(
                [new("BOOT", "boot.img", @"C:\tmp\boot.img", "AP")], pitPath: null, autoReboot: false);

            Assert.Contains("--no-reboot", arguments);
            Assert.DoesNotContain("--pit", arguments);
        }

        [Theory]
        [InlineData("Flashing BOOTLOADER 42%", 42)]
        [InlineData("<ID:0/003> 100%", 100)]
        [InlineData("AP 7 %", 7)]
        [InlineData("SetupConnection succeeded", null)]
        [InlineData("250%", 100)]
        [InlineData("", null)]
        public void ParsePercent_ReadsTheEnginePercentage(string line, int? expected)
            => Assert.Equal(expected, OdinEngine.ParsePercent(line));

        [Theory]
        [InlineData("Flashing BOOTLOADER 10%", 0)]
        [InlineData("SYSTEM 30%", 1)]
        [InlineData("MODEM 10%", 2)]
        [InlineData("CSC 90%", 3)]
        [InlineData("SetupConnection", -1)]
        public void StageIndex_PlacesAPercentageOnTheRightSlot(string line, int expected)
            => Assert.Equal(expected, OdinEngine.StageIndex(line));

        [Theory]
        [InlineData("PASS!", true)]
        [InlineData("RST_OK", true)]
        [InlineData("FAIL!", false)]
        [InlineData("", false)]
        public void LooksSuccessful_OnlyAcceptsARealVerdict(string line, bool expected)
            => Assert.Equal(expected, OdinEngine.LooksSuccessful(line));

        [Theory]
        [InlineData("FAIL! Complete(Write) operation failed.", true)]
        [InlineData("ERROR: Failed to open the device", true)]
        [InlineData("PASS!", false)]
        [InlineData("Flashing AP 30%", false)]
        public void LooksFailed_RecognisesTheEngineVerdict(string line, bool expected)
            => Assert.Equal(expected, OdinEngine.LooksFailed(line));

        [Fact]
        public void DefaultFolder_IsToolsSamsungBesideTheExecutable()
        {
            string expected = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "Samsung");
            Assert.Equal(expected, OdinEngine.DefaultFolder);
        }
    }
}
