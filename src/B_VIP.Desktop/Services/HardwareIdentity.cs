using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using B_VIP.Hardware;

namespace B_VIP.Desktop.Services
{
    /// <summary>
    /// Stable workstation fingerprint used for HWID binding. It hashes the motherboard serial/UUID
    /// with the CPU identifier; when WMI is unavailable it falls back to the Windows MachineGuid and
    /// the processor identification string, so a fingerprint is always produced.
    /// </summary>
    public static class HardwareIdentity
    {
        private const string Script =
            "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
            "try { (Get-CimInstance Win32_BaseBoard -ErrorAction Stop | Select-Object -First 1 -ExpandProperty SerialNumber) } catch { '' }; " +
            "(Get-CimInstance Win32_ComputerSystemProduct -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty UUID); " +
            "(Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty ProcessorId)";

        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static string? _cached;

        /// <summary>The fingerprint of this workstation, computed once per process.</summary>
        public static async Task<string> GetAsync(IProcessRunner runner, CancellationToken ct = default)
        {
            if (_cached is not null)
            {
                return _cached;
            }

            await Gate.WaitAsync(ct);
            try
            {
                if (_cached is not null)
                {
                    return _cached;
                }

                string components = await ReadWmiAsync(runner, ct);

                if (string.IsNullOrWhiteSpace(components) || components.Replace(";", string.Empty).Trim().Length < 8)
                {
                    components = FallbackComponents();
                }

                _cached = Hash(components);
                return _cached;
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>Synchronous variant used where an await cannot be introduced.</summary>
        public static string Get()
        {
            if (_cached is not null)
            {
                return _cached;
            }

            _cached = Hash(FallbackComponents());
            return _cached;
        }

        private static async Task<string> ReadWmiAsync(IProcessRunner runner, CancellationToken ct)
        {
            try
            {
                ProcessResult result = await runner.RunAsync(new ProcessRequest(
                    "powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-Command", Script],
                    TimeoutSeconds: 20), ct);

                if (!result.Succeeded)
                {
                    return string.Empty;
                }

                string[] lines = result.StandardOutput
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                return string.Join(";", lines);
            }
            catch (Exception)
            {
                // WMI can be disabled by policy: the fallback below still produces a fingerprint.
                return string.Empty;
            }
        }

        private static string FallbackComponents()
        {
            string machineGuid = string.Empty;

            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                machineGuid = key?.GetValue("MachineGuid") as string ?? string.Empty;
            }
            catch (Exception)
            {
                // The registry is not readable on this machine; the environment still identifies it.
            }

            string cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? string.Empty;
            string architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? string.Empty;

            return $"{machineGuid};{cpu};{architecture};{Environment.MachineName}";
        }

        private static string Hash(string components)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(components.Trim().ToUpperInvariant()));
            return Convert.ToHexString(hash)[..32];
        }
    }
}
