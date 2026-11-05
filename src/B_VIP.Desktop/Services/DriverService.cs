using System.Diagnostics;
using System.IO;
using System.Text;
using B_VIP.Desktop.Mvvm;

namespace B_VIP.Desktop.Services
{
    /// <summary>One driver package the workspace can install.</summary>
    public sealed class DriverPackage : ObservableObject
    {
        private bool _present;
        private bool _installed;
        private string? _location;
        private string _state = "Checking…";

        public DriverPackage(string key, string name, string purpose, string[] fileNames,
            string downloadUrl, string[] installedHints, string silentArguments = "")
        {
            Key = key;
            Name = name;
            Purpose = purpose;
            FileNames = fileNames;
            DownloadUrl = downloadUrl;
            InstalledHints = installedHints;
            SilentArguments = silentArguments;
        }

        public string Key { get; }

        public string Name { get; }

        public string Purpose { get; }

        /// <summary>File names looked for, in order, inside the bundle folder.</summary>
        public string[] FileNames { get; }

        public string DownloadUrl { get; }

        /// <summary>Text that identifies the vendor in <c>pnputil /enum-drivers</c>.</summary>
        public string[] InstalledHints { get; }

        /// <summary>Arguments for a setup EXE; ignored for an INF, which is installed through pnputil.</summary>
        public string SilentArguments { get; }

        public bool Present
        {
            get => _present;
            private set => SetProperty(ref _present, value);
        }

        public bool Installed
        {
            get => _installed;
            private set => SetProperty(ref _installed, value);
        }

        public string? Location
        {
            get => _location;
            private set => SetProperty(ref _location, value);
        }

        public string State
        {
            get => _state;
            private set => SetProperty(ref _state, value);
        }

        public string InstallLabel => Present ? "Install silently" : "Open download page";

        internal void Apply(bool present, bool installed, string? location)
        {
            Present = present;
            Installed = installed;
            Location = location;
            State = installed
                ? "Driver installed"
                : present ? "Package found — ready to install" : "Package not bundled yet";
            OnPropertyChanged(nameof(InstallLabel));
        }
    }

    /// <summary>
    /// Finds and installs the USB drivers the modules need.
    ///
    /// The application never downloads a vendor driver by itself: those binaries are licensed by
    /// the vendor and have to match the Windows build. An administrator drops the vendor installers
    /// (or their INF files) into a <c>drivers</c> folder beside the executable and this service
    /// installs them with one click; when nothing is bundled it opens the vendor's own download
    /// page instead of pretending an install happened.
    /// </summary>
    public sealed class DriverService
    {
        private readonly IConsoleLogger _log;

        public DriverService(IConsoleLogger log)
        {
            _log = log;
            DriversDirectory = ResolveDriversDirectory();
        }

        /// <summary>The folder searched for vendor installers, or null when none exists yet.</summary>
        public string? DriversDirectory { get; }

        public IReadOnlyList<DriverPackage> Catalog { get; } = BuildCatalog();

        private static IReadOnlyList<DriverPackage> BuildCatalog() =>
        [
            new("samsung", "Samsung USB Driver",
                "Samsung Mobile USB Composite / MTP / modem interfaces, needed for MTP identification and the test-mode FRP flow.",
                ["SAMSUNG_USB_Driver_for_Mobile_Phones.exe", "Samsung_USB_Driver_for_Mobile_Phones.exe", "samsung_usb.inf"],
                "https://developer.samsung.com/android-usb-driver",
                ["Samsung"]),

            new("mtk", "MediaTek USB VCOM Driver",
                "MTK preloader, BROM and VCOM ports, used by the MediaTek module and the port watcher.",
                ["MediaTek_USB_VCOM_drivers.zip", "MTK_USB_Driver.exe", "mtk_usb.inf", "cdc_acm.inf"],
                "https://www.mediatek.com/",
                ["MediaTek", "MTK"]),

            new("qualcomm", "Qualcomm HS-USB QDLoader 9008",
                "The emergency-download interface the Qualcomm module talks to; without it Windows shows an unknown USB device.",
                ["Qualcomm_HS-USB_QDLoader_9008.zip", "Qualcomm_USB_Driver.exe", "qcusb.inf"],
                "https://developer.qualcomm.com/",
                ["Qualcomm", "QDLoader"]),

            new("unisoc", "Unisoc / SPRD USB Driver",
                "SPRD boot-ROM, FDL and diag COM ports, used by the Unisoc module and the port watcher.",
                ["SPRD_USB_Driver.zip", "Unisoc_USB_Driver.exe", "sprd_usb.inf"],
                "https://www.unisoc.com/",
                ["Spreadtrum", "Unisoc", "SPRD"]),

            new("google", "Google ADB / Fastboot Driver",
                "The WinUSB driver that makes adb and fastboot see the handset on this workstation.",
                ["usb_driver.zip", "usb_driver", "android_winusb.inf"],
                "https://developer.android.com/studio/run/win-usb",
                ["Google", "WinUsb", "Android"])
        ];

        /// <summary>
        /// The bundle folder: <c>drivers</c> next to the executable, otherwise the repository folder
        /// while running from a build output.
        /// </summary>
        private static string? ResolveDriversDirectory()
        {
            string baseDirectory = AppContext.BaseDirectory;
            string local = Path.Combine(baseDirectory, "drivers");
            if (Directory.Exists(local))
            {
                return local;
            }

            DirectoryInfo? current = new(baseDirectory);
            for (int level = 0; level < 4 && current?.Parent is not null; level++)
            {
                current = current.Parent;
                string candidate = Path.Combine(current.FullName, "drivers");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>Finds each package in the bundle and reads the installed driver list once.</summary>
        public async Task RefreshAsync(CancellationToken ct = default)
        {
            string installed = await ReadInstalledDriversAsync(ct);

            foreach (DriverPackage package in Catalog)
            {
                string? location = FindPackageFile(package);
                bool present = location is not null;
                bool isInstalled = package.InstalledHints.Any(hint =>
                    installed.Contains(hint, StringComparison.OrdinalIgnoreCase));

                package.Apply(present, isInstalled, location);
            }
        }

        private string? FindPackageFile(DriverPackage package)
        {
            if (DriversDirectory is null)
            {
                return null;
            }

            foreach (string name in package.FileNames)
            {
                string candidate = Path.Combine(DriversDirectory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private async Task<string> ReadInstalledDriversAsync(CancellationToken ct)
        {
            try
            {
                using Process process = new()
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "pnputil.exe",
                        Arguments = "/enum-drivers",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                return output;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                or IOException or OperationCanceledException)
            {
                _log.Warn($"The installed driver list could not be read: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Installs one package with User Account Control. An INF goes through pnputil; a setup EXE
        /// runs with the vendor's silent switch. Returns the message to show the operator.
        /// </summary>
        public string Install(DriverPackage package)
        {
            if (package.Location is null)
            {
                OpenDownloadPage(package);
                return $"No package is bundled for {package.Name}; the vendor download page was opened. " +
                       $"Place the installer in {DriversDirectory ?? "the drivers folder"} and refresh.";
            }

            string? arguments;
            string fileName;

            if (string.Equals(Path.GetExtension(package.Location), ".inf", StringComparison.OrdinalIgnoreCase))
            {
                fileName = "pnputil.exe";
                arguments = $"/add-driver \"{package.Location}\" /install";
            }
            else if (Directory.Exists(package.Location))
            {
                // a driver folder: install every INF it contains through pnputil
                string? inf = Directory.EnumerateFiles(package.Location, "*.inf", SearchOption.AllDirectories).FirstOrDefault();
                if (inf is null)
                {
                    return $"{package.Name}: the folder {package.Location} contains no INF file.";
                }

                fileName = "pnputil.exe";
                arguments = $"/add-driver \"{inf}\" /install";
            }
            else
            {
                fileName = package.Location;
                arguments = package.SilentArguments;
            }

            try
            {
                using Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = true,
                    Verb = "runas"
                }) ?? throw new InvalidOperationException("The installer could not be started.");

                _log.Info($"{package.Name}: installer started ({(Path.GetFileName(fileName))}).");
                return $"{package.Name}: the installer is running. Approve the Windows prompt, finish the wizard, " +
                       "then refresh this page.";
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // 1223 = the operator declined the elevation prompt.
                return $"{package.Name}: installation was cancelled at the Windows permission prompt.";
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                _log.Error($"{package.Name}: the installer could not run ({ex.Message}).");
                return $"{package.Name}: the installer could not run — {ex.Message}";
            }
        }

        public void OpenDownloadPage(DriverPackage package)
        {
            try
            {
                Process.Start(new ProcessStartInfo(package.DownloadUrl) { UseShellExecute = true });
                _log.Info($"{package.Name}: opened {package.DownloadUrl}.");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _log.Warn($"{package.Name}: the browser could not be opened ({ex.Message}).");
            }
        }

        /// <summary>Opens the platform-tools folder so the operator can check the bundled adb/fastboot.</summary>
        public string DescribeBundle()
        {
            StringBuilder builder = new();
            builder.AppendLine($"Bundle folder: {DriversDirectory ?? "(none — create a 'drivers' folder beside B_VIP.Desktop.exe)"}");

            foreach (DriverPackage package in Catalog)
            {
                builder.AppendLine($"{package.Name,-34} {package.State}");
            }

            return builder.ToString().TrimEnd();
        }
    }
}
