using B_VIP.Core.Operations;
using B_VIP.Hardware;
using B_VIP.Modules;
using B_VIP.Modules.Mtk;
using Xunit;

namespace B_VIP.HardwareTests
{
    public sealed class MtkScatterTableTests
    {
        private const string ScatterXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <scatter version="1.0">
              <general>MT6789</general>
              <partition>
                <partition_name>preloader</partition_name>
                <partition_addr>0x0</partition_addr>
                <partition_size>0x40000</partition_size>
                <storage>EMMC_BOOT_1</storage>
                <file_name>preloader_k6789v1_64.bin</file_name>
              </partition>
              <partition>
                <partition_name>frp</partition_name>
                <partition_addr>0x2080000</partition_addr>
                <partition_size>0x10000</partition_size>
                <file_name>NONE</file_name>
              </partition>
              <partition>
                <partition_name>nvram</partition_name>
                <partition_addr>0x2800000</partition_addr>
                <partition_size>0x500000</partition_size>
              </partition>
            </scatter>
            """;

        private const string ScatterText = """
            - general: MT6765
            - storage: EMMC

            - partition_index: SYS0
            - partition_name: boot
            - partition_addr: 0x0000000000080000
            - partition_size: 0x0000000002000000
            - file_name: boot.img

            - partition_index: SYS1
            - partition_name: frp
            - partition_addr: 0x000000000A800000
            - partition_size: 0x0000000000010000
            - file_name: NONE

            - partition_index: SYS2
            - partition_name: nvdata
            - partition_addr: 0x000000000B000000
            - partition_size: 0x0000000004000000
            """;

        [Fact]
        public void Parses_the_xml_scatter_format_with_hex_boundaries()
        {
            IReadOnlyList<ScatterPartition> partitions = MtkScatterTable.Parse(ScatterXml);

            Assert.Equal(new[] { "preloader", "frp", "nvram" }, partitions.Select(p => p.Name).ToArray());
            ScatterPartition frp = partitions[1];
            Assert.Equal(0x2080000, frp.StartAddress);
            Assert.Equal(0x10000, frp.SizeBytes);
            Assert.Equal(0x2080000 + 0x10000, frp.EndAddress);
            Assert.Equal("preloader_k6789v1_64.bin", partitions[0].FileName);
        }

        [Fact]
        public void Parses_the_text_scatter_format_and_skips_blockless_lines()
        {
            IReadOnlyList<ScatterPartition> partitions = MtkScatterTable.Parse(ScatterText);

            Assert.Equal(3, partitions.Count);
            Assert.Equal("boot", partitions[0].Name);
            Assert.Equal(0x80000, partitions[0].StartAddress);
            Assert.Equal(0x2000000, partitions[0].SizeBytes);
            Assert.Equal("frp", partitions[1].Name);
            Assert.Null(partitions[2].FileName);
        }

        [Fact]
        public void Finds_the_frp_partition_case_insensitively()
        {
            IReadOnlyList<ScatterPartition> partitions = MtkScatterTable.Parse(ScatterXml);

            Assert.NotNull(MtkScatterTable.FindFrp(partitions));
            Assert.Null(MtkScatterTable.FindFrp(new[] { new ScatterPartition("boot", 0, 1, null, null) }));
        }

        [Theory]
        [InlineData("nvram")]
        [InlineData("NVDATA")]
        [InlineData("protect1")]
        [InlineData("protect2")]
        [InlineData("persist")]
        [InlineData("nvcfg")]
        [InlineData("seccfg")]
        [InlineData("modem")]
        [InlineData("proinfo")]
        [InlineData("nvram_backup")]
        public void Calibration_and_identifier_partitions_are_protected(string name)
        {
            Assert.True(MtkScatterTable.IsProtected(name));
        }

        [Theory]
        [InlineData("frp")]
        [InlineData("userdata")]
        [InlineData("cache")]
        [InlineData("system")]
        [InlineData("boot")]
        public void Data_and_system_partitions_are_erasable(string name)
        {
            Assert.False(MtkScatterTable.IsProtected(name));
        }

        [Fact]
        public void The_scatter_marks_calibration_partitions_as_such()
        {
            ScatterPartition nvram = MtkScatterTable.Parse(ScatterXml)[2];

            Assert.True(nvram.IsCalibration);
            Assert.False(MtkScatterTable.Parse(ScatterXml)[1].IsCalibration);
        }

        [Fact]
        public void An_empty_or_unknown_document_is_rejected()
        {
            Assert.Throws<FormatException>(() => MtkScatterTable.Parse("   "));
            Assert.Throws<FormatException>(() => MtkScatterTable.Parse("<scatter><general>x</general></scatter>"));
        }

        [Theory]
        [InlineData("0x1000", 0x1000)]
        [InlineData("4096", 4096)]
        [InlineData("  0X20  ", 32)]
        public void Numbers_are_read_in_hex_or_decimal(string text, long expected)
        {
            Assert.Equal(expected, MtkScatterTable.ParseNumber(text));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not a number")]
        [InlineData("0xZZ")]
        public void Unreadable_numbers_are_reported_as_null(string? text)
        {
            Assert.Null(MtkScatterTable.ParseNumber(text));
        }
    }

    public sealed class MtkPortClassifierTests
    {
        [Fact]
        public void The_boot_rom_is_recognised_by_vid_and_pid()
        {
            MtkPort? port = MtkPortClassifier.Classify("USB\\VID_0E8D&PID_0003\\5&1234", "MediaTek USB Port (COM5)");

            Assert.NotNull(port);
            Assert.Equal(MtkPortKind.Brom, port!.Kind);
            Assert.Equal("COM5", port.PortName);
            Assert.Equal("VID_0E8D&PID_0003", port.UsbId);
            Assert.Equal("MTK BROM", port.KindLabel);
        }

        [Fact]
        public void The_preloader_is_recognised_before_it_has_a_com_name()
        {
            MtkPort? port = MtkPortClassifier.Classify("USB\\VID_0E8D&PID_2000\\5&5678", "MediaTek PreLoader USB VCOM");

            Assert.NotNull(port);
            Assert.Equal(MtkPortKind.Preloader, port!.Kind);
            Assert.Equal("(no COM port)", port.PortName);
        }

        [Fact]
        public void A_vcom_interface_is_classified_as_vcom()
        {
            MtkPort? port = MtkPortClassifier.Classify("USB\\VID_0E8D&PID_0006\\5&9", "MediaTek USB VCOM (Android) (COM9)");

            Assert.NotNull(port);
            Assert.Equal(MtkPortKind.Vcom, port!.Kind);
            Assert.Equal("COM9", port.PortName);
        }

        [Theory]
        [InlineData("USB\\VID_18D1&PID_4EE7\\SERIAL", "Android Composite ADB Interface")]
        [InlineData("USB\\VID_04E8&PID_6860\\SERIAL", "SAMSUNG Mobile USB Composite Device")]
        [InlineData("ACPI\\PNP0501\\1", "Communications Port (COM1)")]
        [InlineData("", "MediaTek USB Port (COM5)")]
        public void Interfaces_that_are_not_mediatek_are_ignored(string instanceId, string name)
        {
            Assert.Null(MtkPortClassifier.Classify(instanceId, name));
        }

        [Fact]
        public void Vendor_and_product_ids_are_read_without_the_usb_prefix()
        {
            Assert.Equal("0E8D", MtkPortClassifier.ParseVendorId("USB\\VID_0e8d&PID_0003\\X"));
            Assert.Equal("0003", MtkPortClassifier.ParseProductId("USB\\VID_0E8D&PID_0003\\X"));
            Assert.Null(MtkPortClassifier.ParseVendorId(null));
            Assert.Null(MtkPortClassifier.ParseProductId("USB\\SERIAL"));
        }
    }

    public sealed class MtkExecutionServiceTests
    {
        private static MtkExecutionService Create(FakeAdbClient adb, StubToolLocator tools, out MtkModuleExecutor executor)
        {
            executor = new MtkModuleExecutor(adb, tools);
            return new MtkExecutionService(adb, tools);
        }

        private static OperationExecutionRequest Request(string type, params (string Key, string Value)[] parameters)
            => new(type, null,
                parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase),
                "test-correlation");

        [Fact]
        public void The_executor_claims_every_meditek_operation_type()
        {
            MtkModuleExecutor executor = new(new FakeAdbClient(), new StubToolLocator());

            Assert.Contains(OperationTypes.MtkReadInfo, executor.SupportedOperations);
            Assert.Contains(OperationTypes.MtkHandshake, executor.SupportedOperations);
            Assert.Contains(OperationTypes.MtkResetFrp, executor.SupportedOperations);
            Assert.Contains(OperationTypes.MtkFactoryReset, executor.SupportedOperations);
            Assert.Contains(OperationTypes.MtkScatter, executor.SupportedOperations);
            Assert.Contains(OperationTypes.MtkReboot, executor.SupportedOperations);
        }

        [Fact]
        public async Task An_unconfirmed_frp_reset_erases_nothing()
        {
            FakeAdbClient adb = new();
            StubToolLocator tools = new();
            tools.Set(ToolLocator.Fastboot, "fastboot.exe");

            OperationExecutionResult result = await Create(adb, tools, out _)
                .ResetFrpAsync(Request(OperationTypes.MtkResetFrp), CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Empty(adb.FastbootCalls);
            Assert.Contains(result.Warnings, w => w.Contains("did not confirm", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task An_unconfirmed_factory_reset_erases_nothing()
        {
            FakeAdbClient adb = new();
            StubToolLocator tools = new();
            tools.Set(ToolLocator.Fastboot, "fastboot.exe");

            OperationExecutionResult result = await Create(adb, tools, out _)
                .FactoryResetAsync(Request(OperationTypes.MtkFactoryReset), CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Empty(adb.FastbootCalls);
        }

        [Fact]
        public async Task A_factory_reset_never_erases_a_calibration_partition()
        {
            FakeAdbClient adb = new();
            StubToolLocator tools = new();
            tools.Set(ToolLocator.Fastboot, "fastboot.exe");

            OperationExecutionResult result = await Create(adb, tools, out _)
                .FactoryResetAsync(Request(OperationTypes.MtkFactoryReset, ("confirm", "yes"), ("partition", "nvram")),
                    CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Empty(adb.FastbootCalls);
            Assert.Contains(result.Checks, c => c.Name == "protected-partition" && c.Status == "fail");
        }

        [Fact]
        public async Task A_scatter_request_without_a_path_is_rejected_before_any_process_runs()
        {
            FakeAdbClient adb = new();

            OperationExecutionResult result = await Create(adb, new StubToolLocator(), out _)
                .ScatterAsync(Request(OperationTypes.MtkScatter), CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Empty(adb.AdbCalls);
            Assert.Empty(adb.FastbootCalls);
        }

        [Fact]
        public async Task A_scatter_table_is_parsed_and_its_frp_boundary_reported()
        {
            string path = Path.Combine(Path.GetTempPath(), $"scatter-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(path,
                "- partition_name: boot\n- partition_addr: 0x0\n- partition_size: 0x1000\n\n" +
                "- partition_name: frp\n- partition_addr: 0x1000\n- partition_size: 0x800\n");

            try
            {
                OperationExecutionResult result = await Create(new FakeAdbClient(), new StubToolLocator(), out _)
                    .ScatterAsync(Request(OperationTypes.MtkScatter, ("scatterPath", path)), CancellationToken.None);

                Assert.True(result.Succeeded);
                Assert.Contains(result.Checks, c => c.Name == "scatter-parse" && c.Status == "pass");
                Assert.Contains(result.Checks, c => c.Name == "scatter-frp" && c.Status == "pass");
                Assert.Contains(result.Log, line => line.Contains("frp", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public async Task An_unsupported_reboot_target_is_refused()
        {
            FakeAdbClient adb = new();

            OperationExecutionResult result = await Create(adb, new StubToolLocator(), out _)
                .RebootAsync(Request(OperationTypes.MtkReboot, ("target", "download")), CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Empty(adb.AdbCalls);
        }

        [Fact]
        public async Task A_reboot_to_recovery_is_sent_over_adb()
        {
            FakeAdbClient adb = new();
            adb.AdbHandler = args => args.Contains("devices")
                ? FakeAdbClient.Ok(args, "List of devices attached\nR58M1234\tdevice\n")
                : FakeAdbClient.Ok(args);

            OperationExecutionResult result = await Create(adb, new StubToolLocator(), out _)
                .RebootAsync(Request(OperationTypes.MtkReboot, ("target", "recovery")), CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Contains(adb.AdbCalls, args => args.Contains("reboot") && args.Contains("recovery"));
        }
    }
}
