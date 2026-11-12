using System.Text;
using System.Xml.Linq;

namespace B_VIP.Hardware
{
    /// <summary>
    /// One Firehose exchange result. The programmer answers every command with a
    /// <c>&lt;response value="ACK" | "NAK"&gt;</c> tag, sometimes surrounded by
    /// <c>&lt;log&gt;</c> lines the device printed while working.
    /// </summary>
    public sealed record FirehoseResponse(
        bool Acknowledged,
        bool Rejected,
        string? Error,
        IReadOnlyList<string> Logs,
        string Raw)
    {
        public static FirehoseResponse Parse(string text)
        {
            bool acknowledged = false;
            bool rejected = false;
            string? error = null;
            List<string> logs = new();
            string raw = text.Trim();

            if (string.IsNullOrWhiteSpace(raw))
            {
                return new FirehoseResponse(false, false, "The programmer returned no response.", logs, raw);
            }

            // The device emits one or more XML fragments rather than a single document, so each
            // element is read on its own instead of parsing the whole buffer as one tree.
            foreach (string fragment in SplitFragments(raw))
            {
                XElement? element = TryParse(fragment);
                if (element is null)
                {
                    continue;
                }

                string name = element.Name.LocalName.ToLowerInvariant();
                string value = Attribute(element, "value")?.ToUpperInvariant() ?? string.Empty;

                switch (name)
                {
                    case "response":
                        acknowledged |= value == "ACK";
                        rejected |= value == "NAK";
                        error ??= Attribute(element, "error");
                        break;

                    case "log":
                        // The programmer writes its log lines both as element text and as a value
                        // attribute depending on the build, so both are read.
                        string message = (string.IsNullOrWhiteSpace(element.Value)
                            ? Attribute(element, "value") ?? string.Empty
                            : element.Value).Trim();
                        if (!string.IsNullOrWhiteSpace(message))
                        {
                            logs.Add(message);
                        }

                        break;

                    case "data":
                        foreach (XElement child in element.Elements())
                        {
                            if (string.Equals(child.Name.LocalName, "response", StringComparison.OrdinalIgnoreCase))
                            {
                                string inner = Attribute(child, "value")?.ToUpperInvariant() ?? string.Empty;
                                acknowledged |= inner == "ACK";
                                rejected |= inner == "NAK";
                                error ??= Attribute(child, "error");
                            }
                        }

                        break;
                }
            }

            return new FirehoseResponse(acknowledged, rejected, error ?? (rejected ? "The programmer rejected the command." : null), logs, raw);
        }

        private static IEnumerable<string> SplitFragments(string text)
        {
            // Fragments are separated by the newlines the programmer and the host use as framing.
            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return line;
            }
        }

        private static XElement? TryParse(string fragment)
        {
            try
            {
                return XElement.Parse(fragment, LoadOptions.PreserveWhitespace);
            }
            catch (System.Xml.XmlException)
            {
                return null;
            }
        }

        private static string? Attribute(XElement element, string name)
            => element.Attribute(name)?.Value ?? element.Attributes()
                .FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    /// <summary>
    /// Firehose command builders. Every command is wrapped in the <c>&lt;data&gt;</c> document the
    /// programmer expects and terminated with a newline by <see cref="FirehoseClient"/>.
    /// </summary>
    public static class FirehoseCommands
    {
        public static string Wrap(string innerXml)
            => $"<?xml version=\"1.0\" encoding=\"UTF-8\" ?><data>{innerXml}</data>";

        public static string Configure(string memoryName = "ufs", int maximumPayload = 1048576, bool skipStorageInit = false)
            => Wrap(
                $"<configure MemoryName=\"{Escape(memoryName)}\" Verbose=\"0\" AlwaysValidate=\"0\" " +
                $"MaxDigestTableSizeInBytes=\"2048\" MaxPayloadSizeToTargetInBytes=\"{maximumPayload}\" " +
                $"ZlpAwareHost=\"1\" SkipStorageInit=\"{(skipStorageInit ? 1 : 0)}\" />");

        public static string Read(int physicalPartition, long startSector, long sectorCount, int sectorSize, string? fileName = null)
            => Wrap(
                $"<read SECTOR_SIZE_IN_BYTES=\"{sectorSize}\" num_partition_sectors=\"{sectorCount}\" " +
                $"physical_partition_number=\"{physicalPartition}\" start_sector=\"{startSector}\" " +
                (string.IsNullOrWhiteSpace(fileName) ? string.Empty : $"filename=\"{Escape(fileName)}\" ") + "/>");

        public static string Erase(int physicalPartition, long startSector, long sectorCount, int sectorSize)
            => Wrap(
                $"<erase SECTOR_SIZE_IN_BYTES=\"{sectorSize}\" physical_partition_number=\"{physicalPartition}\" " +
                $"start_sector=\"{startSector}\" num_partition_sectors=\"{sectorCount}\" />");

        public static string Program(int physicalPartition, long startSector, long sectorCount, int sectorSize, string fileName)
            => Wrap(
                $"<program SECTOR_SIZE_IN_BYTES=\"{sectorSize}\" num_partition_sectors=\"{sectorCount}\" " +
                $"physical_partition_number=\"{physicalPartition}\" start_sector=\"{startSector}\" " +
                $"filename=\"{Escape(fileName)}\" />");

        /// <summary>Power actions: <c>reset</c> (normal boot), <c>off</c>, or <c>edl</c>.</summary>
        public static string Power(string value)
            => Wrap($"<power value=\"{Escape(value)}\" />");

        private static string Escape(string value)
            => value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    /// <summary>
    /// Talks Firehose to a device that is already running the uploaded programmer. Framing is
    /// newline terminated XML; the response is read until the device's closing tag arrives.
    /// </summary>
    public sealed class FirehoseClient
    {
        private readonly IEdlTransport _transport;
        private readonly Action<string>? _log;
        private readonly int _responseTimeoutMilliseconds;

        public FirehoseClient(IEdlTransport transport, Action<string>? log = null, int responseTimeoutMilliseconds = 30000)
        {
            _transport = transport;
            _log = log;
            _responseTimeoutMilliseconds = Math.Max(1000, responseTimeoutMilliseconds);
        }

        public IEdlTransport Transport => _transport;

        /// <summary>Sends one command and returns the programmer's verdict.</summary>
        public async Task<FirehoseResponse> SendAsync(string command, CancellationToken ct)
        {
            _log?.Invoke($"Firehose >> {Summarize(command)}");

            byte[] payload = Encoding.UTF8.GetBytes(command + "\n");
            await _transport.WriteAsync(payload, ct);

            string response = await ReadResponseAsync(ct);
            FirehoseResponse parsed = FirehoseResponse.Parse(response);

            foreach (string line in parsed.Logs)
            {
                _log?.Invoke($"Firehose   · {line}");
            }

            _log?.Invoke(parsed.Rejected
                ? $"Firehose << NAK {(parsed.Error is null ? string.Empty : parsed.Error)}"
                : parsed.Acknowledged ? "Firehose << ACK" : "Firehose << (no verdict)");

            return parsed;
        }

        /// <summary>
        /// Reads a sector range: the programmer acknowledges, streams the raw sectors, then closes the
        /// exchange. The byte count is deterministic, so the read cannot hang on a missing terminator.
        /// </summary>
        public async Task<byte[]> ReadSectorsAsync(int physicalPartition, long startSector, long sectorCount,
            int sectorSize, CancellationToken ct)
        {
            long expected = sectorCount * sectorSize;
            if (expected <= 0 || expected > 16 * 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(sectorCount),
                    $"A single read of {sectorCount} sector(s) at {sectorSize} bytes is outside the accepted range.");
            }

            FirehoseResponse ack = await SendAsync(
                FirehoseCommands.Read(physicalPartition, startSector, sectorCount, sectorSize), ct);

            if (ack.Rejected)
            {
                throw new InvalidOperationException($"The programmer refused the read: {ack.Error}");
            }

            byte[] data = new byte[expected];
            await ReadExactlyAsync(data, ct);

            // The closing response is optional in raw mode; a missing one is not an error.
            try
            {
                await ReadResponseAsync(ct, tolerateSilence: true);
            }
            catch (TimeoutException)
            {
                _log?.Invoke("Firehose: no closing response after the data stream (normal in raw mode).");
            }

            return data;
        }

        /// <summary>
        /// Writes a sector range: the host sends the program command, waits for the raw-mode ACK,
        /// streams the payload and then collects the final verdict. The chunk size is bounded so a
        /// partition write cannot exhaust memory on a large image.
        /// </summary>
        public async Task<FirehoseResponse> ProgramSectorsAsync(int physicalPartition, long startSector,
            long sectorCount, int sectorSize, byte[] data, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(data);

            long expected = sectorCount * sectorSize;
            if (expected <= 0 || data.LongLength != expected)
            {
                throw new ArgumentOutOfRangeException(nameof(data),
                    $"The image is {data.LongLength:N0} bytes but the sector range needs {expected:N0}.");
            }

            // The filename is informational for the programmer; the payload itself is sent raw.
            FirehoseResponse ack = await SendAsync(
                FirehoseCommands.Program(physicalPartition, startSector, sectorCount, sectorSize, "raw.bin"), ct);

            if (ack.Rejected)
            {
                throw new InvalidOperationException($"The programmer refused the write: {ack.Error}");
            }

            _log?.Invoke($"Firehose >> {data.LongLength:N0} byte(s) of raw data");

            const int ChunkSize = 1024 * 1024;
            for (int offset = 0; offset < data.Length; offset += ChunkSize)
            {
                ct.ThrowIfCancellationRequested();
                int length = Math.Min(ChunkSize, data.Length - offset);
                await _transport.WriteAsync(data.AsMemory(offset, length), ct);
            }

            // The closing verdict arrives after the data; a silent programmer in raw mode is not a failure.
            string closing = await ReadResponseAsync(ct, tolerateSilence: true);
            FirehoseResponse verdict = FirehoseResponse.Parse(closing);
            _log?.Invoke(verdict.Rejected
                ? $"Firehose << NAK {(verdict.Error is null ? string.Empty : verdict.Error)}"
                : verdict.Acknowledged ? "Firehose << ACK" : "Firehose << (no verdict)");
            return verdict;
        }

        /// <summary>Collects bytes until the closing <c>&lt;/response&gt;</c>, <c>&lt;/data&gt;</c> or self-closing tag.</summary>
        private async Task<string> ReadResponseAsync(CancellationToken ct, bool tolerateSilence = false)
        {
            StringBuilder builder = new();
            byte[] buffer = new byte[4096];
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(_responseTimeoutMilliseconds);

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (DateTime.UtcNow >= deadline)
                {
                    if (tolerateSilence)
                    {
                        return builder.ToString();
                    }

                    throw new TimeoutException(
                        $"The programmer sent no response within {_responseTimeoutMilliseconds} ms.");
                }

                int read = await _transport.ReadAsync(buffer, ct);
                if (read <= 0)
                {
                    continue;
                }

                builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
                deadline = DateTime.UtcNow.AddMilliseconds(_responseTimeoutMilliseconds);

                string text = builder.ToString();
                if (text.Contains("</response>", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("</data>", StringComparison.OrdinalIgnoreCase))
                {
                    return text;
                }

                if (text.TrimEnd().EndsWith("/>", StringComparison.Ordinal) &&
                    text.Contains("<response", StringComparison.OrdinalIgnoreCase))
                {
                    return text;
                }

                if (builder.Length > 64 * 1024)
                {
                    return text;
                }
            }
        }

        private async Task ReadExactlyAsync(byte[] buffer, CancellationToken ct)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                ct.ThrowIfCancellationRequested();

                int read = await _transport.ReadAsync(buffer.AsMemory(offset), ct);
                if (read <= 0)
                {
                    throw new IOException(
                        $"The data stream ended after {offset} of {buffer.Length} byte(s).");
                }

                offset += read;
            }
        }

        private static string Summarize(string command)
        {
            string compact = command.Replace("?>", "?> ").Trim();
            return compact.Length <= 200 ? compact : compact[..200] + "…";
        }
    }
}
