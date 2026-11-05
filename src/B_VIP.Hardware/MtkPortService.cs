using System.IO.Ports;
using System.Management;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace B_VIP.Hardware
{
    /// <summary>The MediaTek USB interfaces a service computer can see.</summary>
    public enum MtkPortKind
    {
        /// <summary>Boot ROM: the handset is powered off and the SoC asks for a download agent.</summary>
        Brom,

        /// <summary>Preloader: the stage between the boot ROM and Android.</summary>
        Preloader,

        /// <summary>Virtual COM port exposed by the MediaTek USB driver.</summary>
        Vcom,

        Unknown
    }

    /// <summary>One MediaTek USB/COM interface with the identifiers Windows reports for it.</summary>
    public sealed record MtkPort(
        string PortName,
        string FriendlyName,
        string InstanceId,
        MtkPortKind Kind,
        string? VendorId,
        string? ProductId)
    {
        public string KindLabel => MtkPortClassifier.Label(Kind);

        /// <summary>The USB identifier pair every MTK tool shows, for example <c>VID_0E8D&PID_0003</c>.</summary>
        public string UsbId => VendorId is null
            ? "USB identifiers not reported"
            : ProductId is null ? $"VID_{VendorId}" : $"VID_{VendorId}&PID_{ProductId}";

        public string Summary => $"{FriendlyName} [{KindLabel}] {PortName} ({UsbId})";
    }

    /// <summary>What a short read of the port produced.</summary>
    public sealed record MtkProbeResult(
        string PortName,
        bool Opened,
        bool ReceivedData,
        string Banner,
        string? FailureReason);

    /// <summary>
    /// Pure classification of a Windows PnP node as a MediaTek interface. Kept free of WMI so the
    /// rules are unit-testable: no port is ever guessed from the COM number alone.
    /// </summary>
    public static class MtkPortClassifier
    {
        public const string MediaTekVendorId = "0E8D";
        public const string BromProductId = "0003";
        public const string PreloaderProductId = "2000";
        public const string VcomProductId = "0006";

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
        /// Returns the MediaTek interface this node represents, or null when the node is not a
        /// MediaTek device. VID_0E8D is the MediaTek vendor id; the preloader and boot ROM use the
        /// fixed product ids 2000 and 0003.
        /// </summary>
        public static MtkPort? Classify(string instanceId, string? name, string? description = null, string? service = null)
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

            bool vendorMatch = string.Equals(vendorId, MediaTekVendorId, StringComparison.OrdinalIgnoreCase);
            bool nameMatch = haystack.Contains("mediatek", StringComparison.Ordinal) ||
                             haystack.Contains("mtk", StringComparison.Ordinal) ||
                             haystack.Contains("preloader", StringComparison.Ordinal) ||
                             haystack.Contains("vcom", StringComparison.Ordinal) ||
                             haystack.Contains("mt6", StringComparison.Ordinal) ||
                             haystack.Contains("mt8", StringComparison.Ordinal);

            if (!vendorMatch && !nameMatch)
            {
                return null;
            }

            MtkPortKind kind = ResolveKind(productId, haystack);
            string? portName = PortClassifier.ExtractPortName(name) ?? PortClassifier.ExtractPortName(description);

            if (portName is null && productId == PreloaderProductId)
            {
                // A preloader that has not finished enumerating has no COM name yet; it is still
                // reported so the operator sees the handset rather than an empty list.
                portName = "(no COM port)";
            }

            if (portName is null)
            {
                return null;
            }

            string label = string.IsNullOrWhiteSpace(name) ? description ?? instanceId : name!;
            return new MtkPort(portName, label, instanceId, kind, vendorId, productId);
        }

        private static MtkPortKind ResolveKind(string? productId, string haystack)
        {
            if (string.Equals(productId, BromProductId, StringComparison.OrdinalIgnoreCase) ||
                haystack.Contains("boot rom", StringComparison.Ordinal) ||
                haystack.Contains("brom", StringComparison.Ordinal))
            {
                return MtkPortKind.Brom;
            }

            if (string.Equals(productId, PreloaderProductId, StringComparison.OrdinalIgnoreCase) ||
                haystack.Contains("preloader", StringComparison.Ordinal))
            {
                return MtkPortKind.Preloader;
            }

            if (string.Equals(productId, VcomProductId, StringComparison.OrdinalIgnoreCase) ||
                haystack.Contains("vcom", StringComparison.Ordinal) ||
                haystack.Contains("android", StringComparison.Ordinal) ||
                haystack.Contains("da usb", StringComparison.Ordinal))
            {
                return MtkPortKind.Vcom;
            }

            return MtkPortKind.Unknown;
        }

        public static string Label(MtkPortKind kind) => kind switch
        {
            MtkPortKind.Brom => "MTK BROM",
            MtkPortKind.Preloader => "MTK Preloader",
            MtkPortKind.Vcom => "MTK VCOM",
            _ => "MTK interface"
        };
    }

    /// <summary>
    /// Finds the MediaTek interfaces on this computer through WMI. The download agent itself is not
    /// part of Windows: what is discoverable here is the USB endpoint and its COM name.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class MtkPortLocator
    {
        private const string Query =
            "SELECT DeviceID, Name, Description, Service FROM Win32_PnPEntity " +
            "WHERE DeviceID LIKE '%VID_0E8D%' OR Name LIKE '%MediaTek%' OR Name LIKE '%Preloader%' " +
            "OR Name LIKE '%VCOM%' OR Name LIKE '%MTK%' OR Name LIKE '%(COM%'";

        /// <summary>Returns every MediaTek interface, boot ROM and preloader first.</summary>
        public static IReadOnlyList<MtkPort> FindPorts()
        {
            List<MtkPort> ports = new();
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
                        MtkPort? port = MtkPortClassifier.Classify(
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
                // WMI unavailable: the caller reports that no port could be enumerated.
            }

            return ports
                .GroupBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(p => p.Kind switch
                {
                    MtkPortKind.Brom => 0,
                    MtkPortKind.Preloader => 1,
                    MtkPortKind.Vcom => 2,
                    _ => 3
                })
                .ThenBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// Opens a MediaTek COM port for a short period and reads whatever the interface sends. The
    /// boot ROM speaks a binary protocol that needs a download agent, so this probe reports only
    /// what is observable without one: whether the port opens and whether it emits any banner.
    /// </summary>
    public static class MtkSerialProbe
    {
        public const int BaudRate = 115200;

        public static MtkProbeResult Probe(string portName, int readMilliseconds = 1200)
        {
            if (!portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                return new MtkProbeResult(portName, false, false, string.Empty,
                    "The interface has no COM name yet; wait for Windows to finish installing the driver.");
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

                StringBuilder banner = new();
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(200, readMilliseconds));

                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        string chunk = port.ReadExisting();
                        if (!string.IsNullOrEmpty(chunk))
                        {
                            banner.Append(chunk);
                        }
                        else
                        {
                            Thread.Sleep(100);
                        }
                    }
                    catch (TimeoutException)
                    {
                        // Nothing arrived in this window; the deadline ends the read.
                    }

                    if (banner.Length > 4000)
                    {
                        break;
                    }
                }

                string text = Sanitize(banner.ToString());
                return new MtkProbeResult(portName, true, text.Length > 0, text, null);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException
                or InvalidOperationException or TimeoutException)
            {
                return new MtkProbeResult(portName, false, false, string.Empty, ex.Message);
            }
        }

        private static string Sanitize(string value)
        {
            StringBuilder builder = new(value.Length);
            foreach (char c in value)
            {
                builder.Append(char.IsControl(c) && c != '\n' && c != '\r' ? '.' : c);
            }

            return builder.ToString().Trim();
        }
    }
}
