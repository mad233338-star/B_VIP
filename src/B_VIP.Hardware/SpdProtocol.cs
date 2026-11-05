using System.Buffers.Binary;
using System.IO.Ports;
using System.Management;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace B_VIP.Hardware
{
    /// <summary>The Unisoc (Spreadtrum) interfaces a service computer can see.</summary>
    public enum SpdPortKind
    {
        /// <summary>Boot ROM mode: the SoC waits for the download agent.</summary>
        Brom,

        /// <summary>The download-agent stage, once FDL1 is running.</summary>
        Fdl,

        /// <summary>The diagnostic port a booted handset exposes.</summary>
        Diag,

        /// <summary>A generic SPRD/Unisoc serial interface that could not be narrowed down.</summary>
        Serial,

        Unknown
    }

    /// <summary>One Unisoc USB/COM interface as Windows reports it.</summary>
    public sealed record SpdPort(
        string PortName,
        string FriendlyName,
        string InstanceId,
        SpdPortKind Kind,
        string? VendorId,
        string? ProductId)
    {
        public string KindLabel => SpdPortClassifier.Label(Kind);

        public string UsbId => VendorId is null
            ? "USB identifiers not reported"
            : ProductId is null ? $"VID_{VendorId}" : $"VID_{VendorId}&PID_{ProductId}";

        public bool IsLoaderStage => Kind is SpdPortKind.Brom or SpdPortKind.Fdl;

        public string Summary => $"{FriendlyName} [{KindLabel}] {PortName} ({UsbId})";
    }

    /// <summary>
    /// Pure classification of a Windows PnP node as a Unisoc interface. Kept free of WMI so the
    /// rules are unit-testable, and deliberately conservative: an unrelated serial port is never
    /// reported as a boot-ROM device.
    /// </summary>
    public static class SpdPortClassifier
    {
        /// <summary>Spreadtrum/Unisoc USB vendor identifier.</summary>
        public const string SpreadtrumVendorId = "1782";

        private static readonly Regex VidPattern = new(@"VID_([0-9A-Fa-f]{4})", RegexOptions.CultureInvariant);
        private static readonly Regex PidPattern = new(@"PID_([0-9A-Fa-f]{4})", RegexOptions.CultureInvariant);

        public static string? ParseVendorId(string? instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId)) return null;
            Match match = VidPattern.Match(instanceId);
            return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
        }

        public static string? ParseProductId(string? instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId)) return null;
            Match match = PidPattern.Match(instanceId);
            return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
        }

        /// <summary>Classifies one PnP node, or returns null when it is not Unisoc hardware.</summary>
        public static SpdPort? Classify(string instanceId, string? friendlyName)
        {
            string? vendorId = ParseVendorId(instanceId);
            string? productId = ParseProductId(instanceId);

            string haystack = (instanceId + " " + (friendlyName ?? string.Empty)).ToLowerInvariant();

            bool vendorMatch = string.Equals(vendorId, SpreadtrumVendorId, StringComparison.OrdinalIgnoreCase);
            bool nameMatch = haystack.Contains("spreadtrum", StringComparison.Ordinal) ||
                             haystack.Contains("sprd", StringComparison.Ordinal) ||
                             haystack.Contains("unisoc", StringComparison.Ordinal);

            if (!vendorMatch && !nameMatch)
            {
                return null;
            }

            string? portName = PortClassifier.ExtractPortName(friendlyName) ?? PortClassifier.ExtractPortName(instanceId);
            if (portName is null)
            {
                // A boot-ROM interface that has not finished installing its driver shows no COM name
                // yet; it is still surfaced so the operator sees the handset.
                portName = "(no COM port)";
            }

            SpdPortKind kind = ResolveKind(haystack);
            string label = string.IsNullOrWhiteSpace(friendlyName) ? instanceId : friendlyName;
            return new SpdPort(portName, label, instanceId, kind, vendorId, productId);
        }

        private static SpdPortKind ResolveKind(string haystack)
        {
            if (haystack.Contains("brom", StringComparison.Ordinal) ||
                haystack.Contains("boot rom", StringComparison.Ordinal) ||
                haystack.Contains("sprd u2s", StringComparison.Ordinal))
            {
                return SpdPortKind.Brom;
            }

            if (haystack.Contains("fdl", StringComparison.Ordinal))
            {
                return SpdPortKind.Fdl;
            }

            if (haystack.Contains("diag", StringComparison.Ordinal))
            {
                return SpdPortKind.Diag;
            }

            return haystack.Contains("sprd", StringComparison.Ordinal) ||
                   haystack.Contains("unisoc", StringComparison.Ordinal) ||
                   haystack.Contains("spreadtrum", StringComparison.Ordinal)
                ? SpdPortKind.Serial
                : SpdPortKind.Unknown;
        }

        public static string Label(SpdPortKind kind) => kind switch
        {
            SpdPortKind.Brom => "SPRD BROM",
            SpdPortKind.Fdl => "SPRD FDL",
            SpdPortKind.Diag => "SPRD Diag",
            SpdPortKind.Serial => "SPRD serial",
            _ => "Unisoc interface"
        };
    }

    /// <summary>Finds the Unisoc interfaces through WMI (Win32_PnPEntity and the COM names).</summary>
    [SupportedOSPlatform("windows")]
    public static class SpdPortLocator
    {
        private const string Query =
            "SELECT DeviceID, Name, Description FROM Win32_PnPEntity " +
            "WHERE DeviceID LIKE '%VID_1782%' OR Name LIKE '%SPRD%' OR Name LIKE '%Unisoc%' " +
            "OR Name LIKE '%Spreadtrum%'";

        public static IReadOnlyList<SpdPort> FindPorts()
        {
            List<SpdPort> ports = new();
            if (!OperatingSystem.IsWindows())
            {
                return ports;
            }

            try
            {
                using ManagementObjectSearcher searcher = new(Query);
                using ManagementObjectCollection devices = searcher.Get();

                foreach (ManagementBaseObject device in devices)
                {
                    using (device)
                    {
                        string instanceId = device["DeviceID"]?.ToString() ?? string.Empty;
                        string? name = device["Name"]?.ToString() ?? device["Description"]?.ToString();

                        SpdPort? port = SpdPortClassifier.Classify(instanceId, name);
                        if (port is not null)
                        {
                            ports.Add(port);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
            {
                // WMI unavailable: the caller reports that no interface could be enumerated.
            }

            return ports
                .GroupBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(p => p.Kind)
                .ThenBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Byte transport for the Spreadtrum boot ROM, abstracted so tests need no hardware.</summary>
    public interface ISpdTransport : IAsyncDisposable
    {
        string Description { get; }

        bool IsOpen { get; }

        Task OpenAsync(CancellationToken ct);

        Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

        /// <summary>Reads whatever arrived; zero means the deadline expired with no byte.</summary>
        Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);
    }

    /// <summary>Creates the transport for one port name. Tests substitute an in-memory transport.</summary>
    public interface ISpdTransportFactory
    {
        ISpdTransport Create(string portName);
    }

    /// <summary>
    /// Serial transport over the SPRD COM port. The USB link ignores the baud rate, but the driver
    /// requires one, so the boot ROM's 115200 8N1 is configured.
    /// </summary>
    public sealed class SpdSerialTransport : ISpdTransport
    {
        public const int BaudRate = 115200;

        private readonly SerialPort _port;

        public SpdSerialTransport(string portName, int readTimeoutMilliseconds = 3000)
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

            try
            {
                int read = _port.Read(buffer.Span.ToArray(), 0, buffer.Length);
                return Task.FromResult(read);
            }
            catch (TimeoutException)
            {
                return Task.FromResult(0);
            }
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

    /// <summary>Production factory: a serial connection to the SPRD COM port.</summary>
    public sealed class SpdSerialTransportFactory : ISpdTransportFactory
    {
        public ISpdTransport Create(string portName) => new SpdSerialTransport(portName);
    }

    /// <summary>
    /// The SPRD download protocol's byte framing. Spreadtrum frames payloads between 0x7E flags,
    /// escaping 0x7E and 0x7D with 0x7D and XOR 0x20, and appends a CRC16-CCITT of the raw payload.
    /// </summary>
    public static class SpdFraming
    {
        public const byte Flag = 0x7E;
        public const byte Escape = 0x7D;
        public const byte EscapeMask = 0x20;

        public static ushort Crc16(ReadOnlySpan<byte> data)
        {
            ushort crc = 0xFFFF;
            foreach (byte value in data)
            {
                crc ^= (ushort)(value << 8);
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
                }
            }

            return crc;
        }

        /// <summary>Wraps a payload: flag, escaped payload, escaped CRC, flag.</summary>
        public static byte[] Encode(ReadOnlySpan<byte> payload)
        {
            ushort crc = Crc16(payload);
            byte[] body = new byte[payload.Length + 2];
            payload.CopyTo(body);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(payload.Length, 2), crc);

            List<byte> framed = new(body.Length * 2 + 2) { Flag };
            foreach (byte value in body)
            {
                if (value is Flag or Escape)
                {
                    framed.Add(Escape);
                    framed.Add((byte)(value ^ EscapeMask));
                }
                else
                {
                    framed.Add(value);
                }
            }

            framed.Add(Flag);
            return framed.ToArray();
        }

        /// <summary>Removes the framing and escapes. Returns null when the buffer holds no frame.</summary>
        public static byte[]? Decode(ReadOnlySpan<byte> frame)
        {
            int start = frame.IndexOf(Flag);
            if (start < 0)
            {
                return null;
            }

            int end = frame[(start + 1)..].LastIndexOf(Flag);
            if (end < 0)
            {
                return null;
            }

            ReadOnlySpan<byte> body = frame.Slice(start + 1, end);
            List<byte> decoded = new(body.Length);
            for (int index = 0; index < body.Length; index++)
            {
                byte value = body[index];
                if (value == Escape)
                {
                    if (index + 1 >= body.Length)
                    {
                        return null;
                    }

                    decoded.Add((byte)(body[++index] ^ EscapeMask));
                }
                else
                {
                    decoded.Add(value);
                }
            }

            if (decoded.Count < 2)
            {
                return null;
            }

            byte[] payload = decoded.Take(decoded.Count - 2).ToArray();
            ushort declared = BinaryPrimitives.ReadUInt16LittleEndian(decoded.Skip(decoded.Count - 2).Take(2).ToArray());
            return declared == Crc16(payload) ? payload : null;
        }
    }

    /// <summary>The fixed byte sequences the SPRD boot ROM answers during the initial handshake.</summary>
    public static class SpdBsl
    {
        /// <summary>Synchronisation byte the host sends to interrupt the boot ROM.</summary>
        public const byte ConnectSync = 0x7E;

        /// <summary>The boot ROM's acknowledgement of the synchronisation byte.</summary>
        public const byte ConnectAck = 0x7E;

        /// <summary>Command the host sends to start the download-agent handshake.</summary>
        public const byte StartDataTransfer = 0x7F;

        /// <summary>The boot ROM's answer to a download-agent handshake.</summary>
        public const byte TransferAckHigh = 0x80;

        public const byte TransferAckLow = 0x7F;

        /// <summary>Checksum command sent once the download agent is running.</summary>
        public const byte CheckBaudrate = 0x7A;

        /// <summary>Largest single FDL block this build writes.</summary>
        public const int MaximumBlockSize = 0x10000;

        /// <summary>Explains a boot-ROM answer so the log can be specific instead of "no response".</summary>
        public static string Describe(byte value) => value switch
        {
            TransferAckHigh => "0x80 (download agent accepted)",
            // 0x7F doubles as the transfer request and the low acknowledgement byte.
            TransferAckLow => "0x7F (transfer acknowledged)",
            ConnectAck => "0x7E (synchronised)",
            _ => $"0x{value:X2}"
        };
    }

    /// <summary>Result of the boot-ROM handshake. <see cref="Synchronised"/> means the SoC answered.</summary>
    public sealed record SpdHandshakeResult(bool Synchronised, byte Answer, string Detail);

    /// <summary>
    /// The SPRD boot-ROM handshake: send the synchronisation byte, wait for the answer, then ask the
    /// ROM to start the download-agent transfer. Only bytes the boot ROM really returned are treated
    /// as success, so a dead port cannot be reported as a handshake.
    /// </summary>
    public sealed class SpdBromSession
    {
        private readonly ISpdTransport _transport;
        private readonly Action<string>? _log;

        public SpdBromSession(ISpdTransport transport, Action<string>? log = null)
        {
            _transport = transport;
            _log = log;
        }

        public async Task<SpdHandshakeResult> HandshakeAsync(int timeoutMilliseconds = 3000, CancellationToken ct = default)
        {
            _log?.Invoke($"Opening {_transport.Description} at {SpdSerialTransport.BaudRate} baud.");
            await _transport.OpenAsync(ct);

            await _transport.WriteAsync(new[] { SpdBsl.ConnectSync }, ct);
            _log?.Invoke("Sent 0x7E to interrupt the boot ROM.");

            byte[] buffer = new byte[16];
            byte answer = 0;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                int read = await _transport.ReadAsync(buffer, ct);
                if (read <= 0)
                {
                    continue;
                }

                answer = buffer[0];
                break;
            }

            if (answer == 0)
            {
                return new SpdHandshakeResult(false, 0,
                    "The boot ROM did not answer. Check the cable, the SPRD diag driver, and that the handset is off and held in download mode.");
            }

            _log?.Invoke($"Boot ROM answered {SpdBsl.Describe(answer)}.");

            await _transport.WriteAsync(new[] { SpdBsl.StartDataTransfer }, ct);
            _log?.Invoke("Sent 0x7F to start the download-agent handshake.");

            return new SpdHandshakeResult(true, answer,
                $"Boot ROM synchronised (answer {SpdBsl.Describe(answer)}); the port is ready for an FDL1 upload.");
        }

        /// <summary>
        /// Streams one FDL image in framed CRC-checked blocks. The boot ROM asks for no offsets: the
        /// whole file is written sequentially, which is what the FDL stage expects.
        /// </summary>
        public async Task<long> UploadAsync(ReadOnlyMemory<byte> image, IProgress<double>? progress, CancellationToken ct = default)
        {
            if (image.Length == 0)
            {
                throw new ArgumentException("The loader image is empty.", nameof(image));
            }

            long sent = 0;
            while (sent < image.Length)
            {
                ct.ThrowIfCancellationRequested();

                int size = (int)Math.Min(SpdBsl.MaximumBlockSize, image.Length - sent);
                ReadOnlyMemory<byte> block = image.Slice((int)sent, size);

                byte[] framed = SpdFraming.Encode(block.Span);
                await _transport.WriteAsync(framed, ct);

                sent += size;
                progress?.Report(sent * 100d / image.Length);
                _log?.Invoke($"FDL block written: {size} byte(s), {sent:N0}/{image.Length:N0}.");
            }

            return sent;
        }
    }

    /// <summary>
    /// Minimal AT client for the SPRD diagnostic port. Used to read the model and software version
    /// a booted handset reports, which is the part of the diag interface that is documented.
    /// </summary>
    public sealed class SpdDiagClient : IAsyncDisposable
    {
        private readonly SerialPort _port;
        private readonly Action<string>? _log;

        public SpdDiagClient(string portName, Action<string>? log = null)
        {
            _log = log;
            _port = new SerialPort(portName, SpdSerialTransport.BaudRate, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                DtrEnable = true,
                RtsEnable = true,
                ReadTimeout = 1500,
                WriteTimeout = 3000
            };
        }

        public bool IsOpen => _port.IsOpen;

        public void Open()
        {
            if (!_port.IsOpen)
            {
                _port.Open();
                _port.DiscardInBuffer();
            }
        }

        /// <summary>Sends one AT command and returns the raw answer (empty when nothing came back).</summary>
        public string Send(string command)
        {
            Open();
            _port.Write(command + "\\r\\n");
            _log?.Invoke($"AT >> {command}");

            StringBuilder reply = new();
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(1500);

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    string chunk = _port.ReadExisting();
                    if (chunk.Length > 0)
                    {
                        reply.Append(chunk);
                        deadline = DateTime.UtcNow.AddMilliseconds(400);
                    }
                    else
                    {
                        Thread.Sleep(60);
                    }
                }
                catch (TimeoutException)
                {
                    break;
                }
            }

            string answer = reply.ToString().Trim();
            _log?.Invoke(string.IsNullOrEmpty(answer) ? "AT << (no answer)" : $"AT << {answer.Replace(Environment.NewLine, " ")}");
            return answer;
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
            }

            _port.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
