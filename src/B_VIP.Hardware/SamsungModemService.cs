using System.Diagnostics;
using System.IO.Ports;
using System.Management;
using System.Runtime.Versioning;
using System.Text;

namespace B_VIP.Hardware
{
    /// <summary>A COM port Windows reports for a Samsung modem interface.</summary>
    public sealed record SamsungModemPort(string PortName, string FriendlyName, string? InstanceId)
    {
        public string Summary => $"{FriendlyName} ({PortName})";
    }

    /// <summary>
    /// Opens the Samsung modem serial port and exchanges AT commands. A session is short lived: the
    /// caller opens it, sends the commands it needs, and disposes it so the port is free for odin or
    /// the next routine.
    /// </summary>
    public sealed class SamsungModemSession : IDisposable
    {
        /// <summary>The baud rate Samsung diagnostic modems answer at.</summary>
        public const int BaudRate = 115200;

        private readonly SerialPort _port;
        private bool _disposed;

        private SamsungModemSession(SerialPort port)
        {
            _port = port;
        }

        public string PortName => _port.PortName;

        /// <summary>Opens the port at 115200 8-N-1 with a short timeout so a silent modem cannot hang the UI.</summary>
        public static SamsungModemSession Open(string portName, int timeoutMilliseconds = 1500)
        {
            SerialPort port = new(portName, BaudRate, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                DtrEnable = false,
                RtsEnable = false,
                ReadTimeout = timeoutMilliseconds,
                WriteTimeout = timeoutMilliseconds,
                NewLine = "\r",
                Encoding = Encoding.ASCII
            };

            port.Open();
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
            return new SamsungModemSession(port);
        }

        /// <summary>
        /// Sends one AT command and returns everything the modem said until <c>OK</c>, <c>ERROR</c>
        /// or the read timeout. Runs on the thread pool: <see cref="SerialPort"/> has no async reads
        /// that honour a cancellation token, and the caller must stay free to cancel.
        /// </summary>
        public Task<AtCommandResult> SendAsync(string command, CancellationToken ct = default)
            => Task.Run(() => Send(command, ct), ct);

        private AtCommandResult Send(string command, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            string payload = command.EndsWith('\r') ? command : command + "\r";
            Stopwatch stopwatch = Stopwatch.StartNew();

            try
            {
                _port.Write(payload);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or UnauthorizedAccessException)
            {
                return new AtCommandResult(command, string.Empty, false, ex.Message, stopwatch.ElapsedMilliseconds);
            }

            StringBuilder response = new();
            string? error = null;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    string line = _port.ReadLine();
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    response.AppendLine(line.Trim());

                    string trimmed = line.Trim();
                    if (trimmed.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    {
                        return new AtCommandResult(command, response.ToString().Trim(), true, null, stopwatch.ElapsedMilliseconds);
                    }

                    if (trimmed.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        error = trimmed;
                        return new AtCommandResult(command, response.ToString().Trim(), false, error, stopwatch.ElapsedMilliseconds);
                    }

                    if (response.Length > 16_000)
                    {
                        break;
                    }
                }
            }
            catch (TimeoutException)
            {
                error = $"The modem did not answer '{command}' within {_port.ReadTimeout} ms.";
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException)
            {
                error = ex.Message;
            }

            ct.ThrowIfCancellationRequested();

            return new AtCommandResult(command, response.ToString().Trim(), false, error, stopwatch.ElapsedMilliseconds);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _port.Dispose();
            }
            catch (IOException)
            {
                // The cable was pulled; the handle is gone either way.
            }
        }
    }

    /// <summary>The exact response of one AT command, kept verbatim for the console log.</summary>
    public sealed record AtCommandResult(
        string Command,
        string Response,
        bool Succeeded,
        string? FailureReason,
        long DurationMs);

    /// <summary>
    /// Finds the COM interface Samsung exposes when the phone is connected in MTP mode. The modem
    /// port is the diagnostic side of the Samsung composite USB interface; it is present whenever the
    /// handset is connected with drivers installed, even with USB debugging off.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class SamsungModemLocator
    {
        private const string Query =
            "SELECT DeviceID, Name, Description, Service FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'";

        /// <summary>Returns every Samsung modem/serial port on this machine, best match first.</summary>
        public static IReadOnlyList<SamsungModemPort> FindPorts()
        {
            List<SamsungModemPort> ports = new();
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
                        string? name = device["Name"]?.ToString();
                        string? description = device["Description"]?.ToString();
                        string? service = device["Service"]?.ToString();
                        string? instanceId = device["DeviceID"]?.ToString();

                        bool looksSamsung = (name ?? string.Empty).Contains("samsung", StringComparison.OrdinalIgnoreCase) ||
                                            (description ?? string.Empty).Contains("samsung", StringComparison.OrdinalIgnoreCase) ||
                                            (instanceId ?? string.Empty).Contains("VID_04E8", StringComparison.OrdinalIgnoreCase);

                        if (!looksSamsung)
                        {
                            continue;
                        }

                        string? portName = PortClassifier.ExtractPortName(name) ?? PortClassifier.ExtractPortName(description);
                        if (portName is null)
                        {
                            continue;
                        }

                        string label = string.IsNullOrWhiteSpace(name) ? description ?? portName : name!;
                        ports.Add(new SamsungModemPort(portName, label, instanceId));
                    }
                }
            }
            catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
            {
                // WMI unavailable: no ports can be discovered, and the caller reports that.
            }

            // The modem interface answers AT commands more reliably than the plain serial port.
            return ports
                .OrderByDescending(p => Score(p.FriendlyName, null))
                .ThenBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Ranks a port: the modem interface answers AT commands, the serial one usually does too.</summary>
        private static int Score(string name, string? service)
        {
            int score = 0;
            if (name.Contains("modem", StringComparison.OrdinalIgnoreCase)) score += 3;
            if (name.Contains("serial", StringComparison.OrdinalIgnoreCase)) score += 1;
            if (string.Equals(service, "modem", StringComparison.OrdinalIgnoreCase)) score += 3;
            if (name.Contains("diagnostic", StringComparison.OrdinalIgnoreCase)) score += 2;
            return score;
        }
    }
}
