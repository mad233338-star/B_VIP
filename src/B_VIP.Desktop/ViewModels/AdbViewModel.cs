using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using B_VIP.Application.Services;
using B_VIP.Contracts.Devices;
using B_VIP.Core.Operations;
using B_VIP.Desktop.Localization;
using B_VIP.Desktop.Mvvm;
using B_VIP.Desktop.Services;
using B_VIP.Hardware;
using B_VIP.Modules;

namespace B_VIP.Desktop.ViewModels
{
    /// <summary>Screen for ADB operations against a connected handset.</summary>
    public sealed class AdbViewModel : DeviceScreenViewModel
    {
        public AdbViewModel()
        {
            Output = string.Empty;
            ShellCommand = "getprop ro.product.model";
            PackagePath = string.Empty;

            // Every button is bright and clickable with no cable attached: the command listens for
            // an authorized ADB interface (up to 45 seconds) and runs the moment one answers. Only a
            // running routine disables the panel.
            ListDevicesCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.AdbDevices), _ => !IsBusy);
            ReadInfoCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.AdbReadInfo, prepare: AdbListener), _ => !IsBusy);
            RestartServerCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.AdbRestartServer), _ => !IsBusy);
            ConnectionCheckCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.AdbConnectionCheck, prepare: AdbListener), _ => !IsBusy);
            CollectLogcatCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.AdbCollectLogcat, prepare: AdbListener), _ => !IsBusy);
            RunShellCommand = new AsyncRelayCommand(_ => RunShellAsync(), _ => !IsBusy);
            RebootSystemCommand = new AsyncRelayCommand(_ => RebootAsync("system"), _ => !IsBusy);
            RebootRecoveryCommand = new AsyncRelayCommand(_ => RebootAsync("recovery"), _ => !IsBusy);
            RebootBootloaderCommand = new AsyncRelayCommand(_ => RebootAsync("bootloader"), _ => !IsBusy);
            RebootEdlCommand = new AsyncRelayCommand(_ => RebootAsync("edl"), _ => !IsBusy);
            RebootDownloadCommand = new AsyncRelayCommand(_ => RebootAsync("download"), _ => !IsBusy);
            // With no handset selected the routine listens for an authorized ADB interface for up
            // to 45 seconds and then runs the setup-wizard bypass.
            ResetFrpCommand = new AsyncRelayCommand(_ => ResetFrpAsync(), _ => !IsBusy);

            // APK install / package sideload: the file comes from the picker below.
            InstallPackageCommand = new AsyncRelayCommand(_ => InstallPackageAsync(), _ => !IsBusy);
            BrowsePackageCommand = new AsyncRelayCommand(_ =>
            {
                BrowsePackage();
                return Task.CompletedTask;
            });
            ClearPackageCommand = new RelayCommand(_ => PackagePath = string.Empty);

            GateOnBusy(ReadInfoCommand, ConnectionCheckCommand, CollectLogcatCommand, RunShellCommand,
                RebootSystemCommand, RebootRecoveryCommand, RebootBootloaderCommand, RebootEdlCommand,
                RebootDownloadCommand, ListDevicesCommand, RestartServerCommand, ResetFrpCommand,
                InstallPackageCommand);
        }

        /// <summary>The prepare delegate every ADB routine uses: press first, plug the cable in after.</summary>
        private Func<CancellationToken, Task<bool>> AdbListener => FrpListener(FrpWaitMode.Adb);

        private string _output = string.Empty;

        public string Output
        {
            get => _output;
            set => SetProperty(ref _output, value);
        }

        private string _shellCommand = string.Empty;

        public string ShellCommand
        {
            get => _shellCommand;
            set => SetProperty(ref _shellCommand, value);
        }

        public AsyncRelayCommand ListDevicesCommand { get; }

        /// <summary>getprop + adb devices: the full build identity of the attached handset.</summary>
        public AsyncRelayCommand ReadInfoCommand { get; }

        public AsyncRelayCommand RestartServerCommand { get; }

        public AsyncRelayCommand ConnectionCheckCommand { get; }

        public AsyncRelayCommand CollectLogcatCommand { get; }

        public AsyncRelayCommand RunShellCommand { get; }

        public AsyncRelayCommand RebootSystemCommand { get; }

        public AsyncRelayCommand RebootRecoveryCommand { get; }

        public AsyncRelayCommand RebootBootloaderCommand { get; }

        public AsyncRelayCommand RebootEdlCommand { get; }

        public AsyncRelayCommand RebootDownloadCommand { get; }

        /// <summary>
        /// Resets FRP over ADB: writes the <c>user_setup_complete</c> secure flag, sends the
        /// MASTER_CLEAR broadcast, brings the launcher to the front and reboots. Works on handsets
        /// that are already in Android with USB debugging authorized.
        /// </summary>
        public AsyncRelayCommand ResetFrpCommand { get; }

        private string _packagePath = string.Empty;

        /// <summary>
        /// Package chosen for install/sideload. The engine receives this absolute path; the screen
        /// only ever shows <see cref="PackageName"/>.
        /// </summary>
        public string PackagePath
        {
            get => _packagePath;
            set
            {
                if (SetProperty(ref _packagePath, value))
                {
                    OnPropertyChanged(nameof(PackageName));
                    OnPropertyChanged(nameof(PackageTooltip));
                    OnPropertyChanged(nameof(PackageText));
                }
            }
        }

        /// <summary>The package's bare file name, so no workshop folder appears on screen.</summary>
        public string PackageName => string.IsNullOrEmpty(_packagePath) ? string.Empty : Path.GetFileName(_packagePath);

        /// <summary>The full path, offered only while the pointer rests on the field.</summary>
        public string? PackageTooltip => string.IsNullOrWhiteSpace(_packagePath) ? null : _packagePath;

        /// <summary>What the field shows: the bare name, or the guidance while it is empty.</summary>
        public string PackageText => string.IsNullOrEmpty(_packagePath)
            ? "No package selected — click Browse to choose a .apk or .zip"
            : Path.GetFileName(_packagePath);

        public AsyncRelayCommand InstallPackageCommand { get; }

        public AsyncRelayCommand BrowsePackageCommand { get; }

        public RelayCommand ClearPackageCommand { get; }

        protected override void OnOperationCompleted(OperationExecutionResult result)
        {
            List<string> lines = new(result.Log);
            lines.AddRange(result.Warnings);
            lines.AddRange(result.Errors);
            Output = string.Join(Environment.NewLine, lines);
        }

        private Task RebootAsync(string target)
            => RunOperationAsync(OperationTypes.AdbReboot,
                new Dictionary<string, string> { ["target"] = target },
                prepare: AdbListener, caption: $"Reboot to {target}");

        private void BrowsePackage()
        {
            Microsoft.Win32.OpenFileDialog dialog = new()
            {
                Title = "Select an APK to install or a package to sideload",
                Filter = "Packages (*.zip;*.apk)|*.zip;*.apk|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                PackagePath = dialog.FileName;
            }
        }

        /// <summary>
        /// Installs an APK on a booted handset, or hands a .zip to the recovery with adb sideload. The
        /// button listens for the ADB / recovery interface first, so it can be pressed before the
        /// cable is plugged in.
        /// </summary>
        private Task InstallPackageAsync()
        {
            if (string.IsNullOrWhiteSpace(PackagePath))
            {
                ErrorMessage = "Choose a package (.apk or .zip) with Browse first.";
                return Task.CompletedTask;
            }

            if (!File.Exists(PackagePath))
            {
                ErrorMessage = $"'{Path.GetFileName(PackagePath)}' is no longer on disk. Choose it again.";
                return Task.CompletedTask;
            }

            bool sideload = !PackagePath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);
            MessageBoxResult answer = MessageBox.Show(
                sideload
                    ? $"Sideload '{Path.GetFileName(PackagePath)}' through the recovery?\n\n" +
                      "The package is streamed to the handset's recovery, which applies it as it arrives. A package " +
                      "built for another model leaves the phone unable to boot. Continue?"
                    : $"Install '{Path.GetFileName(PackagePath)}' on the connected handset?\n\n" +
                      "adb install -r replaces an existing copy of the same package and keeps its data. Continue?",
                "B_VIP Tool",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                StatusMessage = "Install cancelled.";
                return Task.CompletedTask;
            }

            Dictionary<string, string> parameters = new()
            {
                ["path"] = PackagePath.Trim(),
                ["mode"] = sideload ? "sideload" : "install"
            };

            return RunOperationAsync(OperationTypes.AdbPackage, parameters,
                prepare: AdbListener, caption: sideload ? "Sideload package" : "Install APK");
        }

        private async Task ResetFrpAsync()
        {
            MessageBoxResult answer = MessageBox.Show(
                "Reset FRP over ADB on the connected handset?\n\n" +
                "The secure 'user_setup_complete' flag is written, the MASTER_CLEAR broadcast is sent, the launcher is " +
                "brought to the front and the phone reboots. Builds that refuse the broadcast report it as a notice; " +
                "nothing on the phone's storage is read or uploaded.\n\nContinue?",
                "B_VIP Tool",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                StatusMessage = "Routine cancelled.";
                return;
            }

            await RunOperationAsync(OperationTypes.AdbSetupWizardBypass,
                new Dictionary<string, string> { ["confirm"] = "yes" },
                confirmDataLoss: true,
                prepare: FrpListener(FrpWaitMode.Adb),
                caption: "Reset FRP (ADB)");
        }

        private async Task RunShellAsync()
        {
            IsBusy = true;
            try
            {
                // adb streams its output into the console through the process sink; the shell text
                // is also kept on screen and the status bar can cancel a hanging command. The
                // listener runs inside the same task, so pressing the button before plugging the
                // cable in simply waits for the interface.
                bool completed = await Services.Progress.RunAsync($"adb shell {ShellCommand}", async (_, ct) =>
                {
                    if (!await WaitForFrpConnectionAsync(FrpWaitMode.Adb, ct))
                    {
                        return;
                    }

                    if (SelectedDevice is not { Serial: { Length: > 0 } serial })
                    {
                        ErrorMessage = Localizer.Instance["devices.none"];
                        return;
                    }

                    ProcessResult result = await Services.Adb.ShellAsync(serial, ShellCommand, 60, ct);
                    Output = string.IsNullOrWhiteSpace(result.StandardOutput)
                        ? (result.FailureReason ?? result.StandardError)
                        : result.StandardOutput;

                    if (!result.Succeeded)
                    {
                        Services.Log.Error($"adb shell: {result.FailureReason ?? result.StandardError.Trim()}");
                    }
                }, indeterminate: true);

                if (!completed)
                {
                    StatusMessage = "Command cancelled.";
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                Services.Log.Error($"adb shell: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}