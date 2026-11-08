using System.Management;
using System.Runtime.Versioning;

namespace B_VIP.Hardware
{
    /// <summary>An MTP (Media Transfer Protocol) device Windows currently exposes.</summary>
    public sealed record MtpDeviceInfo(
        string InstanceId,
        string? Name,
        string? Manufacturer,
        string? Description,
        string? Model)
    {
        public bool LooksSamsung =>
            (Manufacturer ?? string.Empty).Contains("samsung", StringComparison.OrdinalIgnoreCase) ||
            (Name ?? string.Empty).StartsWith("SM-", StringComparison.OrdinalIgnoreCase) ||
            (Name ?? string.Empty).StartsWith("GT-", StringComparison.OrdinalIgnoreCase);

        public string Summary => $"{Model ?? Name ?? InstanceId}{(Manufacturer is null ? string.Empty : " — " + Manufacturer)}";
    }

    public interface IMtpProtocolService
    {
        Task<IReadOnlyList<MtpDeviceInfo>> FindDevicesAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Reads what the Windows Portable Devices stack publishes about an MTP handset: the model and
    /// the manufacturer are exposed as Plug-and-Play properties and need no USB debugging.
    ///
    /// What MTP does <b>not</b> expose is the serial number, the firmware build, the CSC or the
    /// Android version — the protocol has no such object properties, and it has no command that
    /// reboots a device or opens a URL. Those fields are read from an authorized ADB connection
    /// instead, and the callers say so rather than inventing values.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class MtpProtocolService : IMtpProtocolService
    {
        private const string Query =
            "SELECT DeviceID, Name, Manufacturer, Description, PNPClass FROM Win32_PnPEntity " +
            "WHERE PNPClass = 'WPD' OR Name LIKE '%MTP%'";

        public Task<IReadOnlyList<MtpDeviceInfo>> FindDevicesAsync(CancellationToken ct = default)
            => Task.Run(FindDevices, ct);

        public IReadOnlyList<MtpDeviceInfo> FindDevices()
        {
            List<MtpDeviceInfo> devices = new();
            if (!OperatingSystem.IsWindows())
            {
                return devices;
            }

            try
            {
                using ManagementObjectSearcher searcher = new(Query);
                using ManagementObjectCollection results = searcher.Get();

                foreach (ManagementBaseObject entry in results)
                {
                    using (entry)
                    {
                        string? instanceId = entry["DeviceID"]?.ToString();
                        if (string.IsNullOrWhiteSpace(instanceId))
                        {
                            continue;
                        }

                        string? name = entry["Name"]?.ToString();
                        string? manufacturer = entry["Manufacturer"]?.ToString();
                        string? description = entry["Description"]?.ToString();

                        // Composite parents ("SAMSUNG Mobile USB Composite Device") are not the MTP
                        // interface itself; only the portable-device node carries the model.
                        devices.Add(new MtpDeviceInfo(instanceId, name, manufacturer, description, ResolveModel(name, description)));
                    }
                }
            }
            catch (Exception)
            {
                // WMI fails in more ways than one: a provider error, a COM apartment that is busy, a
                // handle revoked while iterating. None of them should reach the dispatcher, so the
                // enumeration reports "no MTP device" and the caller says MTP cannot be read.
            }

            return devices;
        }

        /// <summary>Picks the model out of "SM-G991B" or "SAMSUNG SM-G991B (MTP)" style names.</summary>
        internal static string? ResolveModel(string? name, string? description)
        {
            string? source = !string.IsNullOrWhiteSpace(name) ? name : description;
            if (string.IsNullOrWhiteSpace(source))
            {
                return null;
            }

            string value = source!;

            foreach (string noise in new[] { "(MTP)", "(PTP)", "MTP Device", "Portable Device" })
            {
                value = value.Replace(noise, string.Empty, StringComparison.OrdinalIgnoreCase);
            }

            value = value.Replace("SAMSUNG", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();

            int dash = value.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0)
            {
                value = value[..dash];
            }

            return value.Trim().Length == 0 ? null : value.Trim();
        }
    }
}
