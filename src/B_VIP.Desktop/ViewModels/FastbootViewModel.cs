using System.Collections.ObjectModel;
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
            ListDevicesCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.FastbootDevices));
            ReadInfoCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.FastbootReadInfo), DeviceReady);
            GetVarCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.FastbootGetVar,
                new Dictionary<string, string> { ["variable"] = VariableName }), DeviceReady);
            RebootSystemCommand = new AsyncRelayCommand(_ => RebootAsync("system"), DeviceReady);
            RebootRecoveryCommand = new AsyncRelayCommand(_ => RebootAsync("recovery"), DeviceReady);
            RebootBootloaderCommand = new AsyncRelayCommand(_ => RebootAsync("bootloader"), DeviceReady);
            RebootEdlCommand = new AsyncRelayCommand(_ => RebootAsync("edl"), DeviceReady);
            FlashCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.FastbootFlash,
                new Dictionary<string, string> { ["partition"] = PartitionName, ["image"] = ImagePath }, confirmDataLoss: true), DeviceReady);
            WipeCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.FastbootWipe,
                new Dictionary<string, string> { ["partition"] = PartitionName }, confirmDataLoss: true), DeviceReady);
            ResetFrpCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.FastbootResetFrp,
                confirmDataLoss: true), DeviceReady);
            EraseFrpCommand = new AsyncRelayCommand(_ => EraseAsync("frp"), DeviceReady);
            EraseConfigCommand = new AsyncRelayCommand(_ => EraseAsync("config"), DeviceReady);
            ErasePersistentCommand = new AsyncRelayCommand(_ => EraseAsync("persistent"), DeviceReady);

            GateOnDevice(ReadInfoCommand, GetVarCommand, RebootSystemCommand, RebootRecoveryCommand,
                RebootBootloaderCommand, RebootEdlCommand, FlashCommand, WipeCommand, ResetFrpCommand,
                EraseFrpCommand, EraseConfigCommand, ErasePersistentCommand);
        }

        /// <summary>Availability for every button that needs a selected handset.</summary>
        private bool DeviceReady(object? _) => !IsBusy && CanRunDeviceAction;

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

        public string ImagePath
        {
            get => _imagePath;
            set => SetProperty(ref _imagePath, value);
        }

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
        {
            if (SelectedDevice is null)
            {
                ErrorMessage = Localizer.Instance["devices.none"];
                return Task.CompletedTask;
            }

            return RunOperationAsync(OperationTypes.FastbootReboot, new Dictionary<string, string> { ["target"] = target });
        }

        private Task EraseAsync(string partition)
        {
            if (SelectedDevice is null)
            {
                ErrorMessage = Localizer.Instance["devices.none"];
                return Task.CompletedTask;
            }

            return RunOperationAsync(OperationTypes.FastbootWipe,
                new Dictionary<string, string> { ["partition"] = partition }, confirmDataLoss: true);
        }
    }
}