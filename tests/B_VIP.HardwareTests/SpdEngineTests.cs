using System.Text;
using B_VIP.Core.Operations;
using B_VIP.Hardware;
using B_VIP.Modules;
using B_VIP.Modules.Spreadtrum;
using Xunit;

namespace B_VIP.HardwareTests
{
    /// <summary>A scripted SPRD device: the test queues exactly the bytes the boot ROM would send.</summary>
    internal sealed class ScriptedSpdTransport : ISpdTransport
    {
        private readonly Queue<byte[]> _incoming = new();

        public List<byte[]> Written { get; } = new();

        public string Description { get; } = "COM9";

        public bool IsOpen { get; private set; }

        public void Queue(params byte[] bytes) => _incoming.Enqueue(bytes);

        public Task OpenAsync(CancellationToken ct)
        {
            IsOpen = true;
            return Task.CompletedTask;
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            lock (Written)
            {
                Written.Add(data.ToArray());
            }

            return Task.CompletedTask;
        }

        public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (_incoming.Count == 0)
            {
                return Task.FromResult(0);
            }

            byte[] next = _incoming.Dequeue();
            int count = Math.Min(buffer.Length, next.Length);
            next.AsMemory(0, count).CopyTo(buffer);
            return Task.FromResult(count);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class ScriptedSpdTransportFactory : ISpdTransportFactory
    {
        public ScriptedSpdTransportFactory(ScriptedSpdTransport transport) => Transport = transport;

        public ScriptedSpdTransport Transport { get; }

        public ISpdTransport Create(string portName) => Transport;
    }

    public sealed class SpdPortClassifierTests
    {
        [Fact]
        public void The_boot_rom_is_recognised_by_the_spreadtrum_vendor_id()
        {
            SpdPort? port = SpdPortClassifier.Classify("USB\\VID_1782&PID_4D00\\5&1234", "SPRD U2S Diag (COM9)");

            Assert.NotNull(port);
            Assert.Equal(SpdPortKind.Brom, port!.Kind);
            Assert.Equal("COM9", port.PortName);
            Assert.Equal("VID_1782&PID_4D00", port.UsbId);
            Assert.True(port.IsLoaderStage);
        }

        [Fact]
        public void A_diag_interface_is_classified_as_diag()
        {
            SpdPort? port = SpdPortClassifier.Classify("USB\\VID_1782&PID_4D01\\5&9", "Unisoc Diag Port (COM11)");

            Assert.NotNull(port);
            Assert.Equal(SpdPortKind.Diag, port!.Kind);
            Assert.False(port.IsLoaderStage);
        }

        [Fact]
        public void An_interface_without_a_com_name_is_still_surfaced()
        {
            SpdPort? port = SpdPortClassifier.Classify("USB\\VID_1782&PID_5D05\\5&77", "Spreadtrum USB Device");

            Assert.NotNull(port);
            Assert.Equal("(no COM port)", port!.PortName);
        }

        [Theory]
        [InlineData("USB\\VID_05C6&PID_9008\\X", "Qualcomm HS-USB QDLoader 9008 (COM7)")]
        [InlineData("USB\\VID_0E8D&PID_0003\\X", "MediaTek USB Port (COM5)")]
        [InlineData("ACPI\\PNP0501\\1", "Communications Port (COM1)")]
        [InlineData("", "Communications Port (COM2)")]
        public void Other_interfaces_are_never_guessed_to_be_unisoc(string instanceId, string name)
        {
            Assert.Null(SpdPortClassifier.Classify(instanceId, name));
        }
    }

    public sealed class SpdFramingTests
    {
        [Fact]
        public void A_payload_round_trips_through_the_framing()
        {
            byte[] payload = [0x01, 0x02, 0x7E, 0x7D, 0x03];

            byte[] framed = SpdFraming.Encode(payload);
            byte[]? decoded = SpdFraming.Decode(framed);

            Assert.Equal(SpdFraming.Flag, framed[0]);
            Assert.Equal(SpdFraming.Flag, framed[^1]);
            Assert.Equal(payload, decoded);
        }

        [Fact]
        public void Reserved_bytes_are_escaped_on_the_wire()
        {
            byte[] framed = SpdFraming.Encode([0x7E, 0x7D]);

            Assert.Contains(SpdFraming.Escape, framed);
            Assert.Equal(2, framed.Count(b => b == SpdFraming.Flag));
        }

        [Fact]
        public void A_corrupted_crc_is_rejected()
        {
            byte[] framed = SpdFraming.Encode([0x10, 0x20, 0x30]);
            framed[2] ^= 0xFF;

            Assert.Null(SpdFraming.Decode(framed));
        }

        [Fact]
        public void A_buffer_without_flags_is_not_a_frame()
        {
            Assert.Null(SpdFraming.Decode([0x01, 0x02, 0x03]));
        }

        [Fact]
        public void The_crc_matches_the_ccitt_reference_value()
        {
            // CRC16-CCITT (0x1021, init 0xFFFF) of "123456789" is 0x29B1.
            Assert.Equal(0x29B1, SpdFraming.Crc16(Encoding.ASCII.GetBytes("123456789")));
        }
    }

    public sealed class SpdBromSessionTests
    {
        [Fact]
        public async Task The_handshake_succeeds_when_the_boot_rom_answers()
        {
            ScriptedSpdTransport device = new();
            device.Queue(SpdBsl.ConnectAck);

            SpdHandshakeResult result = await new SpdBromSession(device).HandshakeAsync(500, CancellationToken.None);

            Assert.True(result.Synchronised);
            Assert.Equal(SpdBsl.ConnectAck, result.Answer);
            Assert.Equal(2, device.Written.Count);
            Assert.Equal(SpdBsl.ConnectSync, device.Written[0][0]);
            Assert.Equal(SpdBsl.StartDataTransfer, device.Written[1][0]);
        }

        [Fact]
        public async Task A_silent_port_is_reported_as_no_handshake()
        {
            ScriptedSpdTransport device = new();

            SpdHandshakeResult result = await new SpdBromSession(device).HandshakeAsync(150, CancellationToken.None);

            Assert.False(result.Synchronised);
            Assert.Contains("did not answer", result.Detail, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task The_loader_is_written_in_framed_blocks()
        {
            ScriptedSpdTransport device = new();
            byte[] image = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();

            long sent = await new SpdBromSession(device).UploadAsync(image, null, CancellationToken.None);

            Assert.Equal(image.Length, sent);
            Assert.Single(device.Written);
            Assert.Equal(SpdFraming.Flag, device.Written[0][0]);
            Assert.Equal(image, SpdFraming.Decode(device.Written[0]));
        }

        [Fact]
        public async Task An_empty_loader_is_refused()
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                new SpdBromSession(new ScriptedSpdTransport()).UploadAsync(Array.Empty<byte>(), null, CancellationToken.None));
        }
    }

    public sealed class SpdExecutionServiceTests
    {
        private static OperationExecutionRequest Request(string type, params (string Key, string Value)[] parameters)
            => new(type, null,
                parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase),
                "test-correlation");

        private static SpdExecutionService Create(StubProcessRunner runner, ScriptedSpdTransport? device = null)
            => new(new FakeAdbClient(), new StubToolLocator(), runner,
                device is null ? null : new ScriptedSpdTransportFactory(device),
                () => new[]
                {
                    new SpdPort("COM9", "SPRD U2S Diag (COM9)", "USB\\VID_1782&PID_4D00\\X",
                        SpdPortKind.Brom, "1782", "4D00")
                });

        private static StubProcessRunner FastbootRunner()
        {
            StubProcessRunner runner = new();
            runner.Handler = request =>
            {
                string joined = string.Join(' ', request.Arguments);
                string output = joined.Contains("devices", StringComparison.OrdinalIgnoreCase)
                    ? "SPRD_1234\tfastboot"
                    : "OKAY";

                return new ProcessResult(request.Executable, request.Arguments, 0, output, string.Empty,
                    false, false, true, 1, null);
            };
            return runner;
        }

        [Fact]
        public async Task An_unconfirmed_frp_reset_erases_nothing()
        {
            StubProcessRunner runner = FastbootRunner();
            StubToolLocator tools = new();
            tools.Set(ToolLocator.Fastboot, "fastboot.exe");

            SpdExecutionService service = new(new FakeAdbClient(), tools, runner);
            OperationExecutionResult result = await service.ResetFrpAsync(
                Request(OperationTypes.SpdResetFrp), CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Empty(runner.Requests);
        }

        [Fact]
        public async Task A_factory_reset_naming_a_calibration_partition_is_refused()
        {
            StubProcessRunner runner = FastbootRunner();
            StubToolLocator tools = new();
            tools.Set(ToolLocator.Fastboot, "fastboot.exe");

            SpdExecutionService service = new(new FakeAdbClient(), tools, runner);
            OperationExecutionResult result = await service.FactoryResetAsync(
                Request(OperationTypes.SpdFactoryReset, ("confirm", "yes"), ("partition", "prodnv")),
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains(result.Checks, c => c.Name == "protected-partition" && c.Status == "fail");
            Assert.Empty(runner.Requests);
        }

        [Fact]
        public async Task A_factory_reset_erases_userdata_and_cache_only()
        {
            StubProcessRunner runner = FastbootRunner();
            StubToolLocator tools = new();
            tools.Set(ToolLocator.Fastboot, "fastboot.exe");

            SpdExecutionService service = new(new FakeAdbClient(), tools, runner);
            OperationExecutionResult result = await service.FactoryResetAsync(
                Request(OperationTypes.SpdFactoryReset, ("confirm", "yes")), CancellationToken.None);

            Assert.True(result.Succeeded);

            string[] erases = runner.Requests
                .Where(r => r.Arguments.Contains("erase"))
                .Select(r => string.Join(' ', r.Arguments))
                .ToArray();

            Assert.Equal(2, erases.Length);
            Assert.Contains("erase userdata", erases[0]);
            Assert.Contains("erase cache", erases[1]);
            Assert.DoesNotContain(erases, e => e.Contains("persist") || e.Contains("prodnv"));
        }

        [Fact]
        public async Task The_frp_routine_erases_frp_and_persistent()
        {
            StubProcessRunner runner = FastbootRunner();
            StubToolLocator tools = new();
            tools.Set(ToolLocator.Fastboot, "fastboot.exe");

            SpdExecutionService service = new(new FakeAdbClient(), tools, runner);
            OperationExecutionResult result = await service.ResetFrpAsync(
                Request(OperationTypes.SpdResetFrp, ("confirm", "yes")), CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Contains(runner.Requests, r => r.Arguments.Contains("erase") && r.Arguments.Contains("frp"));
            Assert.Contains(runner.Requests, r => r.Arguments.Contains("erase") && r.Arguments.Contains("persistent"));
        }

        [Fact]
        public async Task A_handshake_without_a_boot_rom_port_fails_cleanly()
        {
            SpdExecutionService service = new(new FakeAdbClient(), new StubToolLocator(), new StubProcessRunner(),
                null, () => Array.Empty<SpdPort>());

            OperationExecutionResult result = await service.HandshakeAsync(
                Request(OperationTypes.SpdHandshake), CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains(result.Checks, c => c.Name == "spd-port" && c.Status == "fail");
        }

        [Fact]
        public async Task A_handshake_reports_the_boot_rom_answer()
        {
            ScriptedSpdTransport device = new();
            device.Queue(SpdBsl.ConnectAck);

            OperationExecutionResult result = await Create(new StubProcessRunner(), device).HandshakeAsync(
                Request(OperationTypes.SpdHandshake), CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Contains(result.Checks, c => c.Name == "spd-handshake" && c.Status == "pass");
        }

        [Fact]
        public async Task An_fdl_upload_without_an_image_is_refused()
        {
            OperationExecutionResult result = await Create(new StubProcessRunner()).UploadFdlAsync(
                Request(OperationTypes.SpdFdl), CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains(result.Checks, c => c.Name == "fdl1" && c.Status == "fail");
        }

        [Fact]
        public async Task An_fdl_upload_streams_the_image_over_the_boot_rom()
        {
            string path = Path.Combine(Path.GetTempPath(), $"fdl1-{Guid.NewGuid():N}.bin");
            await File.WriteAllBytesAsync(path, Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());

            try
            {
                ScriptedSpdTransport device = new();
                device.Queue(SpdBsl.ConnectAck);

                OperationExecutionResult result = await Create(new StubProcessRunner(), device).UploadFdlAsync(
                    Request(OperationTypes.SpdFdl, ("fdl1Path", path)), CancellationToken.None);

                Assert.True(result.Succeeded);
                Assert.Contains(result.Checks, c => c.Name == "fdl1-upload" && c.Status == "pass");
                Assert.Contains(device.Written, w => SpdFraming.Decode(w) is { Length: 64 });
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void The_executor_claims_every_spd_operation_type()
        {
            SpdModuleExecutor executor = new(new FakeAdbClient(), new StubToolLocator(), new StubProcessRunner());

            Assert.Contains(OperationTypes.SpdReadInfo, executor.SupportedOperations);
            Assert.Contains(OperationTypes.SpdHandshake, executor.SupportedOperations);
            Assert.Contains(OperationTypes.SpdFdl, executor.SupportedOperations);
            Assert.Contains(OperationTypes.SpdResetFrp, executor.SupportedOperations);
            Assert.Contains(OperationTypes.SpdFactoryReset, executor.SupportedOperations);
            Assert.Contains(OperationTypes.SpdReboot, executor.SupportedOperations);
        }
    }
}
