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
    /// <summary>Screen for Fastboot operations against a connected handset.</summary>
    public sealed class FastbootViewModel : DeviceScreenViewModel
    {
        public FastbootViewModel()
        {
            Output = string.Empty;
            VariableName = "version";
            PartitionName = "boot";
            ImagePath = string.Empty;
            // Every button is clickable with nothing attached: each routine waits for the bootloader
            // (up to 45 seconds) and runs the moment it answers. Only a running routine disables
            // the panel.
            ListDevicesCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.FastbootDevices), _ => !IsBusy);
            ReadInfoCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.FastbootReadInfo, prepare: FastbootListener), _ => !IsBusy);
            GetVarCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.FastbootGetVar,
                new Dictionary<string, string> { ["variable"] = VariableName }, prepare: FastbootListener), _ => !IsBusy);
            RebootSystemCommand = new AsyncRelayCommand(_ => RebootAsync("system"), _ => !IsBusy);
            RebootRecoveryCommand = new AsyncRelayCommand(_ => RebootAsync("recovery"), _ => !IsBusy);
            RebootBootloaderCommand = new AsyncRelayCommand(_ => RebootAsync("bootloader"), _ => !IsBusy);
            RebootEdlCommand = new AsyncRelayCommand(_ => RebootAsync("edl"), _ => !IsBusy);
            FlashCommand = new AsyncRelayCommand(_ => FlashAsync(), _ => !IsBusy);
            WipeCommand = new AsyncRelayCommand(_ => WipeAsync("Erase partition", PartitionName), _ => !IsBusy);
            BrowseImageCommand = new AsyncRelayCommand(_ =>
            {
                BrowseImage();
                return Task.CompletedTask;
            });
            ClearImageCommand = new RelayCommand(_ => ImagePath = string.Empty);

            // FRP and the three erases stay clickable with nothing attached: each one listens for the
            // bootloader, then asks for confirmation before it erases anything.
            ResetFrpCommand = new AsyncRelayCommand(_ => ResetFrpAsync(), _ => !IsBusy);
            EraseFrpCommand = new AsyncRelayCommand(_ => EraseAsync("frp"), _ => !IsBusy);
            EraseConfigCommand = new AsyncRelayCommand(_ => EraseAsync("config"), _ => !IsBusy);
            ErasePersistentCommand = new AsyncRelayCommand(_ => EraseAsync("persistent"), _ => !IsBusy);

            GateOnBusy(ReadInfoCommand, GetVarCommand, RebootSystemCommand, RebootRecoveryCommand,
                RebootBootloaderCommand, RebootEdlCommand, FlashCommand, WipeCommand,
                ListDevicesCommand, ResetFrpCommand, EraseFrpCommand, EraseConfigCommand, ErasePersistentCommand);
        }

        /// <summary>The prepare delegate every fastboot routine uses: press first, plug the cable in after.</summary>
        private Func<CancellationToken, Task<bool>> FastbootListener => FrpListener(FrpWaitMode.Fastboot);

        private string _output = string.Empty;

        public string Output
        {
            get => _output;
            set => SetProperty(ref _output, value);
        }

        private string _variableName = string.Empty;

        public string VariableName
        {
            get => _variableName;
            set => SetProperty(ref _variableName, value);
        }

        private string _partitionName = string.Empty;

        public string PartitionName
        {
            get => _partitionName;
            set => SetProperty(ref _partitionName, value);
        }

        private string _imagePath = string.Empty;

        /// <summary>
        /// Image written by "Flash image". The engine receives this absolute path; the screen only
        /// shows <see cref="ImageText"/>.
        /// </summary>
        public string ImagePath
        {
            get => _imagePath;
            set
            {
                if (SetProperty(ref _imagePath, value))
                {
                    OnPropertyChanged(nameof(ImageName));
                    OnPropertyChanged(nameof(ImageText));
                    OnPropertyChanged(nameof(ImageTooltip));
                }
            }
        }

        /// <summary>The image's bare file name, so no workshop folder appears on screen.</summary>
        public string ImageName => string.IsNullOrEmpty(_imagePath) ? string.Empty : Path.GetFileName(_imagePath);

        /// <summary>What the field shows: the bare name, or the guidance while it is empty.</summary>
        public string ImageText => string.IsNullOrEmpty(_imagePath)
            ? "No image selected — click Browse to choose a .img / .bin"
            : Path.GetFileName(_imagePath);

        /// <summary>The full path, offered only while the pointer rests on the field.</summary>
        public string? ImageTooltip => string.IsNullOrWhiteSpace(_imagePath) ? null : _imagePath;

        public AsyncRelayCommand BrowseImageCommand { get; }

        public RelayCommand ClearImageCommand { get; }

        public AsyncRelayCommand ListDevicesCommand { get; }

        /// <summary>fastboot getvar all: the complete bootloader variable dump.</summary>
        public AsyncRelayCommand ReadInfoCommand { get; }

        public AsyncRelayCommand GetVarCommand { get; }

        public AsyncRelayCommand RebootSystemCommand { get; }

        public AsyncRelayCommand RebootRecoveryCommand { get; }

        public AsyncRelayCommand RebootBootloaderCommand { get; }

        public AsyncRelayCommand RebootEdlCommand { get; }

        public AsyncRelayCommand FlashCommand { get; }

        public AsyncRelayCommand WipeCommand { get; }

        /// <summary>Erases frp, config and persistent in one confirmed run.</summary>
        public AsyncRelayCommand ResetFrpCommand { get; }

        public AsyncRelayCommand EraseFrpCommand { get; }

        public AsyncRelayCommand EraseConfigCommand { get; }

        public AsyncRelayCommand ErasePersistentCommand { get; }

        protected override void OnOperationCompleted(OperationExecutionResult result)
        {
            List<string> lines = new(result.Log);

            if (result.Checks.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("=== Results ===");
                lines.AddRange(result.Checks.Select(c => $"[{c.Status.ToUpperInvariant()}] {c.Name}: {c.Detail}"));
            }

            lines.AddRange(result.Warnings);
            lines.AddRange(result.Errors);
            Output = string.Join(Environment.NewLine, lines);
        }

        private Task RebootAsync(string target)
            => RunOperationAsync(OperationTypes.FastbootReboot,
                new Dictionary<string, string> { ["target"] = target },
                prepare: FastbootListener, caption: $"Reboot to {target}");

        private void BrowseImage()
        {
            Microsoft.Win32.OpenFileDialog dialog = new()
            {
                Title = "Select the image to flash",
                Filter = "Images (*.img;*.bin;*.mbn)|*.img;*.bin;*.mbn|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                ImagePath = dialog.FileName;
            }
        }

        private async Task ResetFrpAsync()
        {
            if (!Confirm("Reset FRP through fastboot?",
                    "frp, config and persistent are erased and the handset reboots. This is only accepted by an " +
                    "unlocked bootloader and cannot be undone."))
            {
                StatusMessage = "Routine cancelled.";
                return;
            }

            await RunOperationAsync(OperationTypes.FastbootResetFrp, null, confirmDataLoss: true,
                prepare: FrpListener(FrpWaitMode.Fastboot), caption: "Reset FRP (Fastboot)");
        }

        private async Task EraseAsync(string partition)
        {
            if (!Confirm($"Erase '{partition}'?",
                    $"The '{partition}' partition is erased on the connected handset. This cannot be undone."))
            {
                StatusMessage = "Routine cancelled.";
                return;
            }

            await RunOperationAsync(OperationTypes.FastbootWipe,
                new Dictionary<string, string> { ["partition"] = partition }, confirmDataLoss: true,
                prepare: FrpListener(FrpWaitMode.Fastboot), caption: $"Erase {partition}");
        }

        private async Task FlashAsync()
        {
            if (string.IsNullOrWhiteSpace(PartitionName))
            {
                ErrorMessage = "Name the target partition (for example 'boot' or 'recovery').";
                return;
            }

            if (string.IsNullOrWhiteSpace(ImagePath))
            {
                ErrorMessage = "Choose the image to flash with Browse first.";
                return;
            }

            if (!Confirm("Flash an image?",
                    $"'{Path.GetFileName(ImagePath)}' is written to partition '{PartitionName}'. A wrong or corrupt " +
                    "image can leave the handset unbootable."))
            {
                StatusMessage = "Routine cancelled.";
                return;
            }

            await RunOperationAsync(OperationTypes.FastbootFlash,
                new Dictionary<string, string> { ["partition"] = PartitionName, ["image"] = ImagePath },
                confirmDataLoss: true, prepare: FastbootListener, caption: $"Flash {PartitionName}");
        }

        private async Task WipeAsync(string title, string partition)
        {
            if (!Confirm($"{title}?", $"Partition '{partition}' is erased. This cannot be undone."))
            {
                StatusMessage = "Routine cancelled.";
                return;
            }

            await RunOperationAsync(OperationTypes.FastbootWipe,
                new Dictionary<string, string> { ["partition"] = partition }, confirmDataLoss: true,
                prepare: FastbootListener, caption: $"Erase {partition}");
        }

        /// <summary>The confirmation shared by every destructive fastboot action.</summary>
        private static bool Confirm(string title, string detail)
            => MessageBox.Show($"{title}\n\n{detail}\n\nContinue?", "B_VIP Tool",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}