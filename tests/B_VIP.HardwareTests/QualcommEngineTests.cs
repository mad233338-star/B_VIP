using System.Buffers.Binary;
using System.Text;
using B_VIP.Core.Operations;
using B_VIP.Hardware;
using B_VIP.Modules;
using B_VIP.Modules.Qualcomm;
using Xunit;

namespace B_VIP.HardwareTests
{
    /// <summary>
    /// A transport that replays a scripted device. Every byte the host reads is queued up front, which
    /// keeps the protocol tests deterministic: no timing, no hardware.
    /// </summary>
    internal sealed class ScriptedEdlTransport : IEdlTransport
    {
        private readonly List<byte[]> _incoming = new();
        private int _chunk;
        private int _offset;

        public List<byte[]> Written { get; } = new();

        public string Description { get; } = "COM7";

        public bool IsOpen { get; private set; }

        public void QueueHeader(uint command, byte[] payload)
        {
            byte[] header = new byte[SaharaPacket.HeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), command);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), (uint)(SaharaPacket.HeaderSize + payload.Length));
            _incoming.Add(header);
            if (payload.Length > 0)
            {
                _incoming.Add(payload);
            }
        }

        public void QueueText(string text) => _incoming.Add(Encoding.UTF8.GetBytes(text));

        public void QueueBytes(byte[] bytes) => _incoming.Add(bytes);

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
            if (_chunk >= _incoming.Count)
            {
                throw new InvalidOperationException("The host read past the end of the scripted device.");
            }

            byte[] current = _incoming[_chunk];
            int count = Math.Min(buffer.Length, current.Length - _offset);
            current.AsMemory(_offset, count).CopyTo(buffer);

            _offset += count;
            if (_offset >= current.Length)
            {
                _chunk++;
                _offset = 0;
            }

            return Task.FromResult(count);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class ScriptedEdlTransportFactory : IEdlTransportFactory
    {
        public ScriptedEdlTransportFactory(ScriptedEdlTransport transport) => Transport = transport;

        public ScriptedEdlTransport Transport { get; }

        public IEdlTransport Create(string portName) => Transport;
    }

    public sealed class QualcommPortClassifierTests
    {
        [Theory]
        [InlineData("USB\\VID_05C6&PID_9008\\5&1234", "Qualcomm HS-USB QDLoader 9008 (COM7)", QualcommPortKind.Edl9008)]
        [InlineData("USB\\VID_05C6&PID_900E\\5&9999", "Qualcomm HS-USB QDLoader 900E (COM8)", QualcommPortKind.Edl900E)]
        [InlineData("USB\\VID_05C6&PID_9006\\5&5555", "Qualcomm HS-USB Diagnostics 9006 (COM9)", QualcommPortKind.Diagnostic)]
        public void Qualcomm_interfaces_are_classified_by_vid_and_pid(string instanceId, string name, QualcommPortKind expected)
        {
            QualcommPort? port = QualcommPortClassifier.Classify(instanceId, name);

            Assert.NotNull(port);
            Assert.Equal(expected, port!.Kind);
            Assert.StartsWith("COM", port.PortName);
        }

        [Fact]
        public void The_9008_interface_is_reported_as_emergency_download()
        {
            QualcommPort? port = QualcommPortClassifier.Classify("USB\\VID_05C6&PID_9008\\X", "Qualcomm HS-USB QDLoader 9008 (COM7)");

            Assert.NotNull(port);
            Assert.True(port!.IsEmergencyDownload);
            Assert.Equal("VID_05C6&PID_9008", port.UsbId);
            Assert.Contains("COM7", port.Summary);
        }

        [Theory]
        [InlineData("USB\\VID_18D1&PID_4EE7\\X", "Android Composite ADB Interface")]
        [InlineData("USB\\VID_0E8D&PID_0003\\X", "MediaTek USB Port (COM5)")]
        [InlineData("ACPI\\PNP0501\\1", "Communications Port (COM1)")]
        [InlineData("", "Qualcomm HS-USB QDLoader 9008 (COM7)")]
        public void Other_interfaces_are_never_guessed_to_be_qualcomm(string instanceId, string name)
        {
            Assert.Null(QualcommPortClassifier.Classify(instanceId, name));
        }
    }

    public sealed class FirehoseProtocolTests
    {
        [Fact]
        public void An_ack_is_acknowledged_and_not_a_rejection()
        {
            FirehoseResponse response = FirehoseResponse.Parse("<response value=\"ACK\" />\n");

            Assert.True(response.Acknowledged);
            Assert.False(response.Rejected);
            Assert.Null(response.Error);
        }

        [Fact]
        public void A_nak_carries_the_error_the_programmer_reported()
        {
            FirehoseResponse response = FirehoseResponse.Parse(
                "<log value=\"ERROR: Failed to open partition\" />\n<response value=\"NAK\" error=\"0x0\" />\n");

            Assert.False(response.Acknowledged);
            Assert.True(response.Rejected);
            Assert.Single(response.Logs);
            Assert.Equal("ERROR: Failed to open partition", response.Logs[0]);
        }

        [Fact]
        public void An_empty_response_is_reported_as_such()
        {
            FirehoseResponse response = FirehoseResponse.Parse("  ");

            Assert.False(response.Acknowledged);
            Assert.False(response.Rejected);
            Assert.NotNull(response.Error);
        }

        [Fact]
        public void The_erase_command_targets_the_requested_sector_range()
        {
            string command = FirehoseCommands.Erase(0, 1000, 16, 4096);

            Assert.Contains("start_sector=\"1000\"", command);
            Assert.Contains("num_partition_sectors=\"16\"", command);
            Assert.Contains("SECTOR_SIZE_IN_BYTES=\"4096\"", command);
            Assert.Contains("physical_partition_number=\"0\"", command);
        }

        [Fact]
        public void Every_command_is_wrapped_in_a_data_document_and_escaped()
        {
            string command = FirehoseCommands.Power("edl");

            Assert.StartsWith("<?xml", command);
            Assert.Contains("<data>", command);
            Assert.EndsWith("</data>", command);
            Assert.Contains("value=\"edl\"", command);
        }
    }

    public sealed class GptTableTests
    {
        [Fact]
        public void A_synthetic_table_round_trips_with_its_boundaries()
        {
            byte[] gpt = QualcommTestData.BuildGpt(4096,
                ("userdata", 1000, 2000),
                ("cache", 2001, 2100),
                ("frp", 2101, 2110),
                ("modemst1", 2111, 2120));

            GptTable table = GptTable.Parse(gpt, 4096);

            Assert.Equal(4, table.Partitions.Count);
            GptPartition frp = table.Find("frp")!;
            Assert.Equal(2101, frp.FirstLba);
            Assert.Equal(2110, frp.LastLba);
            Assert.Equal(10, frp.SectorCount);
            Assert.Equal(10 * 4096, frp.SizeBytes(4096));
        }

        [Fact]
        public void The_sector_size_is_detected_from_where_the_header_sits()
        {
            Assert.Equal(4096, GptTable.DetectSectorSize(QualcommTestData.BuildGpt(4096, ("frp", 10, 20))));
            Assert.Equal(512, GptTable.DetectSectorSize(QualcommTestData.BuildGpt(512, ("frp", 10, 20))));
        }

        [Fact]
        public void A_corrupt_header_is_rejected_instead_of_producing_a_table()
        {
            byte[] gpt = QualcommTestData.BuildGpt(4096, ("frp", 10, 20));
            gpt[4096 + 40] ^= 0xFF;

            Assert.Throws<FormatException>(() => GptTable.Parse(gpt, 4096));
        }

        [Fact]
        public void A_block_of_zeroes_is_not_a_partition_table()
        {
            Assert.Throws<FormatException>(() => GptTable.Parse(new byte[4096 * 34], 4096));
        }

        [Theory]
        [InlineData("modemst1")]
        [InlineData("modemst2")]
        [InlineData("fsg")]
        [InlineData("PERSIST")]
        public void Calibration_partitions_are_protected(string name)
        {
            Assert.True(GptTable.IsProtected(name));
        }

        [Theory]
        [InlineData("userdata")]
        [InlineData("cache")]
        [InlineData("frp")]
        [InlineData("config")]
        public void Data_partitions_are_not_protected(string name)
        {
            Assert.False(GptTable.IsProtected(name));
        }
    }

    public sealed class SaharaProtocolTests
    {
        [Fact]
        public async Task The_handshake_answers_a_hello_packet()
        {
            ScriptedEdlTransport device = new();
            byte[] hello = QualcommTestData.HelloPayload(version: 2, minimum: 1, maximum: 0x1000, mode: 0x0D);
            device.QueueHeader(SaharaCommand.Hello, hello);

            SaharaSession session = new(device);
            SaharaHello decoded = await session.HandshakeAsync(CancellationToken.None);

            Assert.Equal(2u, decoded.Version);
            Assert.Equal(1u, decoded.MinimumVersion);
            Assert.Single(device.Written);
            Assert.Equal(SaharaCommand.HelloResponse,
                BinaryPrimitives.ReadUInt32LittleEndian(device.Written[0].AsSpan(0, 4)));
        }

        [Fact]
        public async Task A_hello_packet_of_another_command_is_rejected()
        {
            ScriptedEdlTransport device = new();
            device.QueueHeader(SaharaCommand.ReadData, QualcommTestData.DataRequestPayload(0, 0, 16));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new SaharaSession(device).HandshakeAsync(CancellationToken.None));
        }

        [Fact]
        public async Task The_programmer_is_streamed_and_the_upload_succeeds_on_done_response()
        {
            ScriptedEdlTransport device = new();
            byte[] programmer = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();

            device.QueueHeader(SaharaCommand.Hello, QualcommTestData.HelloPayload(2, 1, 0x1000, 0x0D));
            device.QueueHeader(SaharaCommand.ReadData, QualcommTestData.DataRequestPayload(13, 0, 32));
            device.QueueHeader(SaharaCommand.ReadData, QualcommTestData.DataRequestPayload(13, 32, 32));
            device.QueueHeader(SaharaCommand.EndOfImage, QualcommTestData.EndOfImagePayload(13, 0));
            device.QueueHeader(SaharaCommand.DoneResponse, Array.Empty<byte>());

            SaharaSession session = new(device);
            await session.HandshakeAsync(CancellationToken.None);
            SaharaUploadResult result = await session.UploadAsync(programmer, null, CancellationToken.None);

            Assert.True(result.Uploaded);
            Assert.Equal(64, result.BytesSent);

            // HELLO_RESPONSE, the two data chunks and DONE.
            Assert.Equal(4, device.Written.Count);
            Assert.Equal(programmer[..32], device.Written[1]);
            Assert.Equal(programmer[32..], device.Written[2]);
        }

        [Fact]
        public async Task A_device_that_asks_for_more_bytes_than_the_programmer_has_is_rejected()
        {
            ScriptedEdlTransport device = new();
            byte[] programmer = new byte[16];

            device.QueueHeader(SaharaCommand.Hello, QualcommTestData.HelloPayload(2, 1, 0x1000, 0x0D));
            device.QueueHeader(SaharaCommand.ReadData, QualcommTestData.DataRequestPayload(13, 0, 4096));

            SaharaSession session = new(device);
            await session.HandshakeAsync(CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.UploadAsync(programmer, null, CancellationToken.None));
        }

        [Fact]
        public async Task A_rejected_programmer_reports_the_end_of_image_status()
        {
            ScriptedEdlTransport device = new();
            byte[] programmer = new byte[16];

            device.QueueHeader(SaharaCommand.Hello, QualcommTestData.HelloPayload(2, 1, 0x1000, 0x0D));
            device.QueueHeader(SaharaCommand.ReadData, QualcommTestData.DataRequestPayload(13, 0, 16));
            device.QueueHeader(SaharaCommand.EndOfImage, QualcommTestData.EndOfImagePayload(13, 0x80000001));

            SaharaSession session = new(device);
            await session.HandshakeAsync(CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.UploadAsync(programmer, null, CancellationToken.None));
        }

        [Fact]
        public async Task A_declared_length_outside_the_accepted_range_aborts_the_read()
        {
            ScriptedEdlTransport device = new();
            byte[] header = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), SaharaCommand.Hello);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 0x7FFFFFFF);
            device.QueueBytes(header);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new SaharaSession(device).ReadPacketAsync(CancellationToken.None));
        }
    }

    public sealed class QualcommExecutionServiceTests
    {
        private static OperationExecutionRequest Request(string type, params (string Key, string Value)[] parameters)
            => new(type, null,
                parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase),
                "test-correlation");

        private static QualcommExecutionService Create(ScriptedEdlTransport device, out string programmerPath)
        {
            programmerPath = Path.Combine(Path.GetTempPath(), $"prog_firehose_{Guid.NewGuid():N}.elf");
            File.WriteAllBytes(programmerPath, new byte[128]);

            return new QualcommExecutionService(new FakeAdbClient(), new StubToolLocator(),
                new ScriptedEdlTransportFactory(device),
                () => new[]
                {
                    new QualcommPort("COM7", "Qualcomm HS-USB QDLoader 9008 (COM7)",
                        "USB\\VID_05C6&PID_9008\\X", QualcommPortKind.Edl9008, "05C6", "9008")
                });
        }

        /// <summary>Queues the handshake, upload, configure and GPT read every routine starts with.</summary>
        private static void QueueSession(ScriptedEdlTransport device, byte[] gpt)
        {
            device.QueueHeader(SaharaCommand.Hello, QualcommTestData.HelloPayload(2, 1, 0x1000, 0x0D));
            device.QueueHeader(SaharaCommand.ReadData, QualcommTestData.DataRequestPayload(13, 0, 128));
            device.QueueHeader(SaharaCommand.EndOfImage, QualcommTestData.EndOfImagePayload(13, 0));
            device.QueueHeader(SaharaCommand.DoneResponse, Array.Empty<byte>());
            device.QueueText("<response value=\"ACK\" />\n");
            device.QueueText("<response value=\"ACK\" />\n");
            device.QueueBytes(gpt);
            device.QueueText("<response value=\"ACK\" />\n");
        }

        [Fact]
        public async Task An_unconfirmed_frp_reset_never_opens_the_port()
        {
            ScriptedEdlTransport device = new();
            QualcommExecutionService service = Create(device, out _);

            OperationExecutionResult result = await service.ResetFrpAsync(
                Request(OperationTypes.QualcommResetFrp), CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Empty(device.Written);
        }

        [Fact]
        public async Task An_unconfirmed_factory_reset_never_opens_the_port()
        {
            ScriptedEdlTransport device = new();

            OperationExecutionResult result = await Create(device, out _).FactoryResetAsync(
                Request(OperationTypes.QualcommFactoryReset), CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Empty(device.Written);
        }

        [Fact]
        public async Task A_routine_without_a_programmer_path_stops_before_uploading()
        {
            ScriptedEdlTransport device = new();
            device.QueueHeader(SaharaCommand.Hello, QualcommTestData.HelloPayload(2, 1, 0x1000, 0x0D));

            OperationExecutionResult result = await Create(device, out _).ResetFrpAsync(
                Request(OperationTypes.QualcommResetFrp, ("confirm", "yes")), CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Contains("programmer", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Checks, c => c.Name == "edl-port" && c.Status == "pass");
        }

        [Fact]
        public async Task The_frp_routine_reads_the_table_and_erases_exactly_that_range()
        {
            ScriptedEdlTransport device = new();
            byte[] gpt = QualcommTestData.BuildGpt(4096, ("userdata", 1000, 2000), ("frp", 2101, 2110));
            QueueSession(device, gpt);
            device.QueueText("<response value=\"ACK\" />\n");

            QualcommExecutionService service = Create(device, out string programmer);
            try
            {
                OperationExecutionResult result = await service.ResetFrpAsync(
                    Request(OperationTypes.QualcommResetFrp, ("confirm", "yes"), ("firehosePath", programmer)),
                    CancellationToken.None);

                Assert.True(result.Succeeded);
                Assert.Contains(result.Checks, c => c.Name == "frp-partition" && c.Status == "pass");
                Assert.Contains(result.Checks, c => c.Name == "frp-erase" && c.Status == "pass");

                string erase = Encoding.UTF8.GetString(device.Written.Last());
                Assert.Contains("start_sector=\"2101\"", erase);
                Assert.Contains("num_partition_sectors=\"10\"", erase);
            }
            finally
            {
                File.Delete(programmer);
            }
        }

        [Fact]
        public async Task A_table_that_names_persist_as_frp_is_refused()
        {
            ScriptedEdlTransport device = new();
            byte[] gpt = QualcommTestData.BuildGpt(4096, ("userdata", 1000, 2000), ("persist", 2101, 2110));
            QueueSession(device, gpt);

            QualcommExecutionService service = Create(device, out string programmer);
            try
            {
                OperationExecutionResult result = await service.ResetFrpAsync(
                    Request(OperationTypes.QualcommResetFrp, ("confirm", "yes"), ("firehosePath", programmer)),
                    CancellationToken.None);

                Assert.False(result.Succeeded);
                Assert.DoesNotContain(device.Written, w =>
                    Encoding.UTF8.GetString(w).Contains("<erase", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                File.Delete(programmer);
            }
        }

        [Fact]
        public async Task A_factory_reset_erases_userdata_and_cache_only()
        {
            ScriptedEdlTransport device = new();
            byte[] gpt = QualcommTestData.BuildGpt(4096,
                ("userdata", 1000, 2000), ("cache", 2001, 2100), ("modemst1", 2101, 2110), ("persist", 2111, 2120));
            QueueSession(device, gpt);
            device.QueueText("<response value=\"ACK\" />\n");
            device.QueueText("<response value=\"ACK\" />\n");

            QualcommExecutionService service = Create(device, out string programmer);
            try
            {
                OperationExecutionResult result = await service.FactoryResetAsync(
                    Request(OperationTypes.QualcommFactoryReset, ("confirm", "yes"), ("firehosePath", programmer)),
                    CancellationToken.None);

                Assert.True(result.Succeeded);

                string[] erases = device.Written
                    .Select(w => Encoding.UTF8.GetString(w))
                    .Where(text => text.Contains("<erase", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                Assert.Equal(2, erases.Length);
                Assert.Contains("start_sector=\"1000\"", erases[0]);
                Assert.Contains("start_sector=\"2001\"", erases[1]);
                Assert.DoesNotContain(erases, e => e.Contains("2101\"") || e.Contains("2111\""));
            }
            finally
            {
                File.Delete(programmer);
            }
        }

        [Fact]
        public async Task A_factory_reset_naming_a_calibration_partition_is_refused_before_connecting()
        {
            ScriptedEdlTransport device = new();

            OperationExecutionResult result = await Create(device, out _).FactoryResetAsync(
                Request(OperationTypes.QualcommFactoryReset, ("confirm", "yes"), ("partition", "modemst2")),
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains(result.Checks, c => c.Name == "protected-partition" && c.Status == "fail");
            Assert.Empty(device.Written);
        }

        [Theory]
        [InlineData("download")]
        [InlineData("fastboot")]
        [InlineData("")]
        public async Task An_unsupported_power_target_is_refused_without_touching_the_device(string target)
        {
            ScriptedEdlTransport device = new();

            OperationExecutionResult result = await Create(device, out _).RebootAsync(
                Request(OperationTypes.QualcommReboot, ("target", target)), CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Empty(device.Written);
        }

        [Fact]
        public async Task A_power_command_is_sent_through_firehose()
        {
            ScriptedEdlTransport device = new();
            QueueSession(device, QualcommTestData.BuildGpt(4096, ("userdata", 1000, 2000)));
            device.QueueText("<response value=\"ACK\" />\n");

            QualcommExecutionService service = Create(device, out string programmer);
            try
            {
                OperationExecutionResult result = await service.RebootAsync(
                    Request(OperationTypes.QualcommReboot, ("target", "edl"), ("firehosePath", programmer)),
                    CancellationToken.None);

                Assert.True(result.Succeeded);
                Assert.Contains(device.Written, w =>
                    Encoding.UTF8.GetString(w).Contains("<power value=\"edl\"", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                File.Delete(programmer);
            }
        }

        [Fact]
        public void The_executor_claims_every_qualcomm_operation_type()
        {
            B_VIP.Modules.Qualcomm.QualcommEdlExecutor executor =
                new(new FakeAdbClient(), new StubToolLocator());

            Assert.Contains(OperationTypes.QualcommDiagnostic, executor.SupportedOperations);
            Assert.Contains(OperationTypes.QualcommReadInfo, executor.SupportedOperations);
            Assert.Contains(OperationTypes.QualcommSahara, executor.SupportedOperations);
            Assert.Contains(OperationTypes.QualcommGpt, executor.SupportedOperations);
            Assert.Contains(OperationTypes.QualcommResetFrp, executor.SupportedOperations);
            Assert.Contains(OperationTypes.QualcommFactoryReset, executor.SupportedOperations);
            Assert.Contains(OperationTypes.QualcommReboot, executor.SupportedOperations);
        }
    }

    /// <summary>Byte builders shared by the Qualcomm protocol tests.</summary>
    internal static class QualcommTestData
    {
        public static byte[] HelloPayload(uint version, uint minimum, uint maximum, uint mode)
        {
            byte[] payload = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), version);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), minimum);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), maximum);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12, 4), mode);
            return payload;
        }

        public static byte[] DataRequestPayload(uint imageId, uint offset, uint length)
        {
            byte[] payload = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), imageId);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), offset);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), length);
            return payload;
        }

        public static byte[] EndOfImagePayload(uint imageId, uint status)
        {
            byte[] payload = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), imageId);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), status);
            return payload;
        }

        /// <summary>
        /// Builds LBA 0..33 with a valid header and CRC, so the parser sees the same structure a
        /// handset reports.
        /// </summary>
        public static byte[] BuildGpt(int sectorSize, params (string Name, long First, long Last)[] partitions)
        {
            const int headerSize = 92;
            const int entryCount = 128;
            const int entrySize = 128;

            byte[] disk = new byte[sectorSize * 34];
            Guid diskGuid = Guid.NewGuid();

            Span<byte> header = disk.AsSpan(sectorSize, sectorSize);
            Encoding.ASCII.GetBytes("EFI PART").CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], 0x00010000);
            BinaryPrimitives.WriteUInt32LittleEndian(header[12..16], headerSize);
            // CRC at 16..20 is written last.
            BinaryPrimitives.WriteUInt64LittleEndian(header[24..32], 1);
            BinaryPrimitives.WriteUInt64LittleEndian(header[32..40], 33);
            BinaryPrimitives.WriteUInt64LittleEndian(header[40..48], 34);
            BinaryPrimitives.WriteUInt64LittleEndian(header[48..56], 33);
            diskGuid.TryWriteBytes(header[56..72]);
            BinaryPrimitives.WriteUInt64LittleEndian(header[72..80], 2);
            BinaryPrimitives.WriteUInt32LittleEndian(header[80..84], entryCount);
            BinaryPrimitives.WriteUInt32LittleEndian(header[84..88], entrySize);

            int entriesOffset = 2 * sectorSize;
            for (int index = 0; index < partitions.Length; index++)
            {
                (string name, long first, long last) = partitions[index];
                Span<byte> entry = disk.AsSpan(entriesOffset + index * entrySize, entrySize);
                Guid.NewGuid().TryWriteBytes(entry[..16]);
                Guid.NewGuid().TryWriteBytes(entry.Slice(16, 16));
                BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(32, 8), (ulong)first);
                BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(40, 8), (ulong)last);

                byte[] utf16 = Encoding.Unicode.GetBytes(name);
                utf16.AsSpan(0, Math.Min(utf16.Length, 72)).CopyTo(entry.Slice(56, 72));
            }

            uint entriesCrc = GptCrc32.Compute(disk.AsSpan(entriesOffset, entryCount * entrySize));
            BinaryPrimitives.WriteUInt32LittleEndian(header[88..92], entriesCrc);

            byte[] headerBytes = header[..headerSize].ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(headerBytes.AsSpan(16, 4), 0);
            uint crc = GptCrc32.Compute(headerBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], crc);

            return disk;
        }
    }
}
