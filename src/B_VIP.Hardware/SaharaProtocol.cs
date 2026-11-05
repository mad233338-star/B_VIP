using System.Buffers.Binary;
using System.IO.Ports;

namespace B_VIP.Hardware
{
    /// <summary>
    /// Byte transport for the Qualcomm boot ROM. The real 9008 device is a USB endpoint; on Windows it
    /// is usually reached through libusb, but the QDLoader driver also publishes a serial-style COM
    /// port, which is what this build drives. Everything above the transport is protocol code and is
    /// therefore transport independent.
    /// </summary>
    public interface IEdlTransport : IAsyncDisposable
    {
        /// <summary>For example <c>COM7</c>, for the operator's log.</summary>
        string Description { get; }

        bool IsOpen { get; }

        Task OpenAsync(CancellationToken ct);

        Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

        /// <summary>Waits for at least one byte and returns what arrived (up to the buffer length).</summary>
        Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);
    }

    /// <summary>Creates the transport for one port name. Tests substitute an in-memory transport.</summary>
    public interface IEdlTransportFactory
    {
        IEdlTransport Create(string portName);
    }

    /// <summary>
    /// Serial transport over the QDLoader COM port. The USB link ignores the baud rate, but the
    /// driver requires one to be configured, so 115200 8N1 is used.
    /// </summary>
    public sealed class SerialEdlTransport : IEdlTransport
    {
        public const int BaudRate = 115200;

        private readonly SerialPort _port;

        public SerialEdlTransport(string portName, int readTimeoutMilliseconds = 3000)
        {
            Description = portName;
            _port = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                DtrEnable = true,
                RtsEnable = true,
                ReadTimeout = Math.Max(200, readTimeoutMilliseconds),
                WriteTimeout = 5000
            };
        }

        public string Description { get; }

        public bool IsOpen => _port.IsOpen;

        public Task OpenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _port.Open();
            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();
            return Task.CompletedTask;
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _port.Write(data.ToArray(), 0, data.Length);
            return Task.CompletedTask;
        }

        public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            int read = _port.Read(buffer.Span.ToArray(), 0, buffer.Length);
            return Task.FromResult(read);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (_port.IsOpen)
                {
                    _port.Close();
                }
            }
            catch (IOException)
            {
                // A port that vanished with the device is already closed.
            }

            _port.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Production transport factory: a serial connection to the QDLoader COM port.</summary>
    public sealed class SerialEdlTransportFactory : IEdlTransportFactory
    {
        public IEdlTransport Create(string portName) => new SerialEdlTransport(portName);
    }

    /// <summary>Sahara command identifiers carried in the packet header.</summary>
    public static class SaharaCommand
    {
        public const uint Hello = 0x01;
        public const uint HelloResponse = 0x02;
        public const uint ReadData = 0x03;
        public const uint EndOfImage = 0x04;
        public const uint Done = 0x05;
        public const uint DoneResponse = 0x06;
        public const uint Reset = 0x07;
        public const uint ResetResponse = 0x08;
        public const uint MemoryDebug = 0x09;
        public const uint Error = 0x0A;
        public const uint ReadMemory = 0x0B;
        public const uint CommandReady = 0x0C;
        public const uint CommandExecute = 0x0D;
        public const uint CommandExecuteResponse = 0x0E;

        public static string Name(uint command) => command switch
        {
            Hello => "HELLO",
            HelloResponse => "HELLO_RESPONSE",
            ReadData => "READ_DATA",
            EndOfImage => "END_OF_IMAGE",
            Done => "DONE",
            DoneResponse => "DONE_RESPONSE",
            Reset => "RESET",
            ResetResponse => "RESET_RESPONSE",
            Error => "ERROR",
            ReadMemory => "READ_MEMORY",
            _ => $"0x{command:X2}"
        };
    }

    /// <summary>The Sahara modes a host can request in its hello response.</summary>
    public static class SaharaMode
    {
        public const uint ImageTxPending = 0x0D;
        public const uint ImageTxComplete = 0x0E;
        public const uint Command = 0x0C;
    }

    /// <summary>The protocol version this build speaks.</summary>
    public static class SaharaVersion
    {
        public const uint Version = 2;
        public const uint Minimum = 1;
        public const uint MaxCommandLength = 0x1000;
    }

    /// <summary>A Sahara packet as read from the wire: the two-word header plus what followed it.</summary>
    public sealed record SaharaPacket(uint Command, uint DeclaredLength, byte[] Payload)
    {
        /// <summary>The fixed header every Sahara packet starts with: command and length.</summary>
        public const int HeaderSize = 8;

        public string Name => SaharaCommand.Name(Command);
    }

    /// <summary>Decoded Sahara hello: the boot ROM's identity and the modes it accepts.</summary>
    public sealed record SaharaHello(uint Version, uint MinimumVersion, uint MaximumCommandLength, uint Mode);

    /// <summary>One READ_DATA request: which image, at which offset, for how many bytes.</summary>
    public sealed record SaharaDataRequest(uint ImageId, uint Offset, uint Length);

    /// <summary>A transfer result. <see cref="Uploaded"/> is true only after the device acknowledged DONE.</summary>
    public sealed record SaharaUploadResult(bool Uploaded, uint ImageId, long BytesSent, string Detail);

    /// <summary>
    /// Sahara packet encoding and decoding. Nothing is derived from the device's layout by
    /// assumption: incoming packets are framed by their own declared length, and the fixed-size
    /// records below match the documented structures used by the open-source EDL clients.
    /// </summary>
    public static class SaharaPacketCodec
    {
        /// <summary>Full header size for outgoing packets: command, length and four reserved words.</summary>
        public const int FullHeaderSize = 0x30;

        /// <summary>Largest packet this build will accept, so a corrupt length cannot exhaust memory.</summary>
        public const uint MaximumPacketLength = 0x100000;

        public static byte[] EncodeHeader(uint command, uint length, uint version = SaharaVersion.Version,
            uint minimumVersion = SaharaVersion.Minimum, uint maximumCommandLength = SaharaVersion.MaxCommandLength,
            uint mode = SaharaMode.ImageTxPending)
        {
            byte[] buffer = new byte[FullHeaderSize];
            Span<byte> span = buffer;

            BinaryPrimitives.WriteUInt32LittleEndian(span[..4], command);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4, 4), length);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, 4), version);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12, 4), minimumVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), maximumCommandLength);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(20, 4), mode);
            // Bytes 24..48 are the six reserved words and stay zero.
            return buffer;
        }

        /// <summary>The host's answer to HELLO: it asks the device to start sending the programmer.</summary>
        public static byte[] EncodeHelloResponse(uint mode = SaharaMode.ImageTxPending)
            => EncodeHeader(SaharaCommand.HelloResponse, FullHeaderSize, mode: mode);

        /// <summary>Marks one uploaded image as finished.</summary>
        public static byte[] EncodeDone(uint imageId, uint status = 0)
        {
            byte[] buffer = new byte[FullHeaderSize + 8];
            EncodeHeader(SaharaCommand.Done, (uint)buffer.Length).CopyTo(buffer, 0);

            Span<byte> span = buffer;
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(FullHeaderSize, 4), imageId);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(FullHeaderSize + 4, 4), status);
            return buffer;
        }

        public static byte[] EncodeReset()
            => EncodeHeader(SaharaCommand.Reset, FullHeaderSize);

        public static SaharaHello DecodeHello(ReadOnlySpan<byte> payload)
        {
            uint version = payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]) : 0;
            uint minimum = payload.Length >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4)) : 0;
            uint maximum = payload.Length >= 12 ? BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4)) : 0;
            uint mode = payload.Length >= 16 ? BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(12, 4)) : 0;

            return new SaharaHello(version, minimum, maximum, mode);
        }

        /// <summary>READ_DATA carries image id, data offset and requested length.</summary>
        public static SaharaDataRequest? DecodeDataRequest(ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 12)
            {
                return null;
            }

            return new SaharaDataRequest(
                BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]),
                BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4)));
        }

        /// <summary>END_OF_IMAGE carries image id and a status word (0 means success).</summary>
        public static (uint ImageId, uint Status)? DecodeEndOfImage(ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 8)
            {
                return null;
            }

            return (BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]),
                    BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4)));
        }

        /// <summary>ERROR carries an error number followed by a description.</summary>
        public static (uint Code, string Description) DecodeError(ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 4)
            {
                return (0, string.Empty);
            }

            uint code = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
            string description = payload.Length > 4
                ? System.Text.Encoding.ASCII.GetString(payload[4..]).TrimEnd('\0', '\r', '\n')
                : string.Empty;

            return (code, description);
        }
    }

    /// <summary>
    /// The Sahara handshake state machine: it reads the device's HELLO, answers it, streams the
    /// programmer binary on every READ_DATA request, acknowledges END_OF_IMAGE with DONE, and
    /// succeeds only when the device answers DONE_RESPONSE.
    /// </summary>
    public sealed class SaharaSession
    {
        /// <summary>Documents the maximum single packet this build will write back.</summary>
        public const int MaximumChunk = 0x100000;

        private readonly IEdlTransport _transport;
        private readonly Action<string>? _log;
        private readonly int _readTimeoutMilliseconds;

        public SaharaSession(IEdlTransport transport, Action<string>? log = null, int readTimeoutMilliseconds = 8000)
        {
            _transport = transport;
            _log = log;
            _readTimeoutMilliseconds = readTimeoutMilliseconds;
        }

        /// <summary>Reads the HELLO packet and answers it, moving the device into image transfer.</summary>
        public async Task<SaharaHello> HandshakeAsync(CancellationToken ct)
        {
            SaharaPacket hello = await ReadPacketAsync(ct);

            if (hello.Command == SaharaCommand.EndOfImage)
            {
                throw new InvalidOperationException(
                    "The device answered with END_OF_IMAGE instead of HELLO; the programmer was rejected before it was read.");
            }

            if (hello.Command != SaharaCommand.Hello)
            {
                throw new InvalidOperationException(
                    $"Expected a Sahara HELLO packet but received {hello.Name} ({hello.Command:X2}) of {hello.DeclaredLength} bytes.");
            }

            SaharaHello decoded = SaharaPacketCodec.DecodeHello(hello.Payload);
            _log?.Invoke($"Sahara HELLO: protocol {decoded.Version}.{decoded.MinimumVersion}, " +
                         $"max command length {decoded.MaximumCommandLength}, device mode 0x{decoded.Mode:X2}.");

            await _transport.WriteAsync(SaharaPacketCodec.EncodeHelloResponse(SaharaMode.ImageTxPending), ct);
            _log?.Invoke("Sahara HELLO_RESPONSE sent: image transfer pending.");
            return decoded;
        }

        /// <summary>
        /// Streams the programmer binary to the device. Every requested range is answered with exactly
        /// the bytes the device asked for; a partial write would corrupt the firmware.
        /// </summary>
        public async Task<SaharaUploadResult> UploadAsync(ReadOnlyMemory<byte> programmer,
            IProgress<double>? progress, CancellationToken ct)
        {
            if (programmer.Length == 0)
            {
                throw new ArgumentException("The programmer binary is empty.", nameof(programmer));
            }

            long total = programmer.Length;
            long sent = 0;
            uint imageId = 0;
            bool doneSent = false;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                SaharaPacket packet = await ReadPacketAsync(ct);

                switch (packet.Command)
                {
                    case SaharaCommand.ReadData:
                    {
                        SaharaDataRequest? request = SaharaPacketCodec.DecodeDataRequest(packet.Payload);
                        if (request is null)
                        {
                            throw new InvalidOperationException(
                                $"READ_DATA carried {packet.Payload.Length} bytes; at least 12 were expected.");
                        }

                        SaharaDataRequest range = request;
                        imageId = range.ImageId;

                        if ((long)range.Offset + range.Length > total)
                        {
                            throw new InvalidOperationException(
                                $"The device requested bytes {range.Offset}..{(long)range.Offset + range.Length} " +
                                $"of a {total} byte programmer.");
                        }

                        ReadOnlyMemory<byte> chunk = programmer.Slice((int)range.Offset, (int)range.Length);
                        await _transport.WriteAsync(chunk, ct);

                        sent += range.Length;
                        progress?.Report(Math.Min(100d, (double)((long)range.Offset + range.Length) * 100d / total));

                        if (sent <= range.Length || range.Offset == 0)
                        {
                            _log?.Invoke($"Sahara READ_DATA: image {range.ImageId}, {range.Length} bytes at offset {range.Offset}.");
                        }

                        break;
                    }

                    case SaharaCommand.EndOfImage:
                    {
                        (uint cloneId, uint status) = SaharaPacketCodec.DecodeEndOfImage(packet.Payload) ?? (imageId, 0xFFFFFFFF);
                        if (status != 0)
                        {
                            throw new InvalidOperationException(
                                $"The device rejected the programmer (END_OF_IMAGE status 0x{status:X8}).");
                        }

                        _log?.Invoke($"Sahara END_OF_IMAGE for image {cloneId}: {sent} bytes sent, status OK.");
                        await _transport.WriteAsync(SaharaPacketCodec.EncodeDone(cloneId), ct);
                        doneSent = true;
                        _log?.Invoke("Sahara DONE sent.");
                        break;
                    }

                    case SaharaCommand.DoneResponse:
                        _log?.Invoke("Sahara DONE_RESPONSE received: the programmer is running.");
                        return new SaharaUploadResult(true, imageId, sent,
                            $"Programmer accepted: {sent} byte(s) transferred.");

                    case SaharaCommand.Hello:
                    {
                        // Some boot ROM versions repeat HELLO when they do not see the answer in time.
                        _log?.Invoke("Sahara repeated HELLO; answering again.");
                        SaharaHello decoded = SaharaPacketCodec.DecodeHello(packet.Payload);
                        _log?.Invoke($"Sahara HELLO: protocol {decoded.Version}.{decoded.MinimumVersion}.");
                        await _transport.WriteAsync(SaharaPacketCodec.EncodeHelloResponse(SaharaMode.ImageTxPending), ct);
                        break;
                    }

                    case SaharaCommand.Reset:
                    case SaharaCommand.ResetResponse:
                        if (doneSent)
                        {
                            _log?.Invoke("Sahara RESET after DONE: the device is leaving the boot ROM.");
                            return new SaharaUploadResult(true, imageId, sent,
                                $"Programmer accepted: {sent} byte(s) transferred.");
                        }

                        throw new InvalidOperationException("The device reset the Sahara session before acknowledging DONE.");

                    case SaharaCommand.Error:
                    {
                        (uint code, string description) = SaharaPacketCodec.DecodeError(packet.Payload);
                        throw new InvalidOperationException(
                            $"The device reported a Sahara error {code}: {description}");
                    }

                    default:
                        throw new InvalidOperationException(
                            $"Unexpected Sahara command {packet.Name} ({packet.Command:X2}) of {packet.DeclaredLength} bytes.");
                }
            }
        }

        /// <summary>
        /// Reads one packet: the two-word framing header, then exactly the number of bytes the header
        /// declares. A declared length outside the sane range aborts instead of allocating it.
        /// </summary>
        public async Task<SaharaPacket> ReadPacketAsync(CancellationToken ct)
        {
            byte[] header = new byte[SaharaPacket.HeaderSize];
            await ReadExactlyAsync(header, ct);

            uint command = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));

            if (length < SaharaPacket.HeaderSize || length > SaharaPacketCodec.MaximumPacketLength)
            {
                throw new InvalidOperationException(
                    $"The Sahara header declares {length} bytes ({SaharaCommand.Name(command)}), which is outside the accepted range.");
            }

            int payloadLength = (int)length - SaharaPacket.HeaderSize;
            byte[] payload = new byte[payloadLength];
            if (payloadLength > 0)
            {
                await ReadExactlyAsync(payload, ct);
            }

            return new SaharaPacket(command, length, payload);
        }

        private async Task ReadExactlyAsync(byte[] buffer, CancellationToken ct)
        {
            int offset = 0;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(_readTimeoutMilliseconds);

            while (offset < buffer.Length)
            {
                ct.ThrowIfCancellationRequested();

                int read = await _transport.ReadAsync(buffer.AsMemory(offset), ct);
                if (read <= 0)
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        throw new TimeoutException(
                            $"The device sent {offset} of {buffer.Length} expected byte(s) within {_readTimeoutMilliseconds} ms.");
                    }

                    continue;
                }

                offset += read;
                deadline = DateTime.UtcNow.AddMilliseconds(_readTimeoutMilliseconds);
            }
        }
    }
}
