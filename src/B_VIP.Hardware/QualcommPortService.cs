using System.IO.Ports;
using System.Management;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace B_VIP.Hardware
{
    /// <summary>The Qualcomm USB interfaces a service computer can see.</summary>
    public enum QualcommPortKind
    {
        /// <summary>Emergency Download: the SoC is in the boot ROM's programmer-loading mode.</summary>
        Edl9008,

        /// <summary>The second EDL personality some SoCs present once a programmer is running.</summary>
        Edl900E,

        /// <summary>The Qualcomm diagnostic port exposed by an Android handset with a working system.</summary>
        Diagnostic,

        Unknown
    }

    /// <summary>One Qualcomm USB/COM interface with the identifiers Windows reports for it.</summary>
    public sealed record QualcommPort(
        string PortName,
        string FriendlyName,
        string InstanceId,
        QualcommPortKind Kind,
        string? VendorId,
        string? ProductId)
    {
        public string KindLabel => QualcommPortClassifier.Label(Kind);

        /// <summary>The USB identifier pair every Qualcomm tool shows, for example <c>VID_05C6&PID_9008</c>.</summary>
        public string UsbId => VendorId is null
            ? "USB identifiers not reported"
            : ProductId is null ? $"VID_{VendorId}" : $"VID_{VendorId}&PID_{ProductId}";

        /// <summary>True for the 9008 / 900E personalities that carry the Sahara protocol.</summary>
        public bool IsEmergencyDownload => Kind is QualcommPortKind.Edl9008 or QualcommPortKind.Edl900E;

        public string Summary => $"{FriendlyName} [{KindLabel}] {PortName} ({UsbId})";
    }

    /// <summary>
    /// Pure classification of a Windows PnP node as a Qualcomm interface. Kept free of WMI so the
    /// rules are unit-testable: a plain serial port is never guessed to be a 9008 device.
    /// </summary>
    public static class QualcommPortClassifier
    {
        public const string QualcommVendorId = "05C6";
        public const string EdlProductId = "9008";
        public const string EdlAlternateProductId = "900E";
        public const string DiagnosticProductId = "9006";

        private static readonly Regex VidPattern = new(@"VID_([0-9A-Fa-f]{4})", RegexOptions.CultureInvariant);
        private static readonly Regex PidPattern = new(@"PID_([0-9A-Fa-f]{4})", RegexOptions.CultureInvariant);

        /// <summary>Extracts the USB vendor identifier from a PnP instance id, if it has one.</summary>
        public static string? ParseVendorId(string? instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                return null;
            }

            Match match = VidPattern.Match(instanceId);
            return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
        }

        /// <summary>Extracts the USB product identifier from a PnP instance id, if it has one.</summary>
        public static string? ParseProductId(string? instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                return null;
            }

            Match match = PidPattern.Match(instanceId);
            return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
        }

        /// <summary>
        /// Returns the Qualcomm interface this node represents, or null when the node is not a
        /// Qualcomm device. VID_05C6 is the Qualcomm vendor id; PID_9008 is the emergency download
        /// personality and PID_900E the second EDL personality used after a programmer is loaded.
        /// </summary>
        public static QualcommPort? Classify(string instanceId, string? name, string? description = null, string? service = null)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                return null;
            }

            string? vendorId = ParseVendorId(instanceId);
            string? productId = ParseProductId(instanceId);

            string haystack = string.Join(' ',
                new[] { name, description, service }.Where(s => !string.IsNullOrWhiteSpace(s)))
                .ToLowerInvariant();

            bool vendorMatch = string.Equals(vendorId, QualcommVendorId, StringComparison.OrdinalIgnoreCase);
            bool nameMatch = haystack.Contains("qualcomm", StringComparison.Ordinal) ||
                             haystack.Contains("qdloader", StringComparison.Ordinal) ||
                             haystack.Contains("hs-usb qdloader", StringComparison.Ordinal) ||
                             haystack.Contains("9008", StringComparison.Ordinal);

            if (!vendorMatch && !nameMatch)
            {
                return null;
            }

            QualcommPortKind kind = ResolveKind(productId, haystack);
            string? portName = PortClassifier.ExtractPortName(name) ?? PortClassifier.ExtractPortName(description);

            if (portName is null && kind == QualcommPortKind.Edl9008)
            {
                // A 9008 device that has not finished installing its driver reports no COM name yet;
                // it is still surfaced so the operator sees the handset instead of an empty list.
                portName = "(no COM port)";
            }

            if (portName is null)
            {
                return null;
            }

            string label = string.IsNullOrWhiteSpace(name) ? description ?? instanceId : name!;
            return new QualcommPort(portName, label, instanceId, kind, vendorId, productId);
        }

        private static QualcommPortKind ResolveKind(string? productId, string haystack)
        {
            // The product identifier is authoritative. Only when Windows does not report one do the
            // friendly name and service hints decide, and 900E / 9006 are tested before the generic
            // "QDLoader" word that every one of these drivers shares.
            if (!string.IsNullOrWhiteSpace(productId))
            {
                if (string.Equals(productId, EdlProductId, StringComparison.OrdinalIgnoreCase))
                {
                    return QualcommPortKind.Edl9008;
                }

                if (string.Equals(productId, EdlAlternateProductId, StringComparison.OrdinalIgnoreCase))
                {
                    return QualcommPortKind.Edl900E;
                }

                if (string.Equals(productId, DiagnosticProductId, StringComparison.OrdinalIgnoreCase))
                {
                    return QualcommPortKind.Diagnostic;
                }
            }

            if (haystack.Contains("9008", StringComparison.Ordinal))
            {
                return QualcommPortKind.Edl9008;
            }

            if (haystack.Contains("900e", StringComparison.Ordinal))
            {
                return QualcommPortKind.Edl900E;
            }

            if (haystack.Contains("diagnostic", StringComparison.Ordinal))
            {
                return QualcommPortKind.Diagnostic;
            }

            if (haystack.Contains("qdloader", StringComparison.Ordinal))
            {
                return QualcommPortKind.Edl9008;
            }

            return QualcommPortKind.Unknown;
        }

        public static string Label(QualcommPortKind kind) => kind switch
        {
            QualcommPortKind.Edl9008 => "Qualcomm EDL 9008",
            QualcommPortKind.Edl900E => "Qualcomm EDL 900E",
            QualcommPortKind.Diagnostic => "Qualcomm Diagnostics",
            _ => "Qualcomm interface"
        };
    }

    /// <summary>
    /// Finds the Qualcomm interfaces on this computer through WMI. What is discoverable here is the
    /// USB endpoint and its COM name; the programmer itself has to be supplied by the operator.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class QualcommPortLocator
    {
        private const string Query =
            "SELECT DeviceID, Name, Description, Service FROM Win32_PnPEntity " +
            "WHERE DeviceID LIKE '%VID_05C6%' OR Name LIKE '%QDLoader%' OR Name LIKE '%Qualcomm%' " +
            "OR Name LIKE '%9008%' OR Name LIKE '%900E%'";

        /// <summary>Returns every Qualcomm interface, emergency download mode first.</summary>
        public static IReadOnlyList<QualcommPort> FindPorts()
        {
            List<QualcommPort> ports = new();
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
                        QualcommPort? port = QualcommPortClassifier.Classify(
                            device["DeviceID"]?.ToString() ?? string.Empty,
                            device["Name"]?.ToString(),
                            device["Description"]?.ToString(),
                            device["Service"]?.ToString());

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
                .OrderBy(p => p.Kind switch
                {
                    QualcommPortKind.Edl9008 => 0,
                    QualcommPortKind.Edl900E => 1,
                    QualcommPortKind.Diagnostic => 2,
                    _ => 3
                })
                .ThenBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>What a short read of a 9008 port produced.</summary>
    public sealed record QualcommProbeResult(
        string PortName,
        bool Opened,
        int BytesRead,
        uint? SaharaCommand,
        uint? SaharaLength,
        string Banner,
        string? FailureReason);

    /// <summary>
    /// Opens a 9008 port and reads the Sahara hello the boot ROM sends as soon as it enumerates.
    /// A device that answers with a hello packet is genuinely reachable: the command and length
    /// words are decoded, not guessed.
    /// </summary>
    public static class QualcommEdlProbe
    {
        public const int BaudRate = 115200;

        public static QualcommProbeResult Probe(string portName, int readMilliseconds = 1500)
        {
            if (!portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                return new QualcommProbeResult(portName, false, 0, null, null, string.Empty,
                    "The interface has no COM name yet; wait for Windows to finish installing the QDLoader driver.");
            }

            try
            {
                using SerialPort port = new(portName, BaudRate, Parity.None, 8, StopBits.One)
                {
                    Handshake = Handshake.None,
                    DtrEnable = true,
                    RtsEnable = true,
                    ReadTimeout = Math.Max(200, readMilliseconds),
                    WriteTimeout = 1000,
                    Encoding = Encoding.ASCII
                };

                port.Open();
                port.DiscardInBuffer();

                List<byte> received = new(512);
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(200, readMilliseconds));

                while (DateTime.UtcNow < deadline && received.Count < 512)
                {
                    try
                    {
                        int available = port.BytesToRead;
                        if (available <= 0)
                        {
                            Thread.Sleep(60);
                            continue;
                        }

                        byte[] chunk = new byte[Math.Min(available, 512 - received.Count)];
                        int read = port.Read(chunk, 0, chunk.Length);
                        if (read > 0)
                        {
                            received.AddRange(chunk.Take(read));
                        }
                    }
                    catch (TimeoutException)
                    {
                        // Nothing arrived in this window; the deadline ends the read.
                    }
                }

                byte[] buffer = received.ToArray();
                uint? command = null;
                uint? length = null;
                if (buffer.Length >= 8)
                {
                    command = BitConverter.ToUInt32(buffer, 0);
                    length = BitConverter.ToUInt32(buffer, 4);
                }

                return new QualcommProbeResult(portName, true, buffer.Length, command, length,
                    Describe(buffer), null);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException
                or InvalidOperationException or TimeoutException)
            {
                return new QualcommProbeResult(portName, false, 0, null, null, string.Empty, ex.Message);
            }
        }

        private static string Describe(byte[] buffer)
        {
            if (buffer.Length == 0)
            {
                return "The port opened but sent nothing; the host has to speak first or the device is not in EDL.";
            }

            StringBuilder builder = new();
            builder.Append(BitConverter.ToString(buffer, 0, Math.Min(buffer.Length, 32)));

            string text = new(buffer.Take(buffer.Length)
                .Where(b => b >= 0x20 && b < 0x7F)
                .Select(b => (char)b)
                .ToArray());

            if (text.Length >= 4)
            {
                builder.Append("  '").Append(text[..Math.Min(text.Length, 64)]).Append('\'');
            }

            return builder.ToString();
        }
    }
}
