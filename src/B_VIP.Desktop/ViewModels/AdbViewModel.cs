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
    /// <summary>Screen for ADB operations against a connected handset.</summary>
    public sealed class AdbViewModel : DeviceScreenViewModel
    {
        public AdbViewModel()
        {
            Output = string.Empty;
            ShellCommand = "getprop ro.product.model";
            ListDevicesCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.AdbDevices));
            ReadInfoCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.AdbReadInfo), DeviceReady);
            RestartServerCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.AdbRestartServer));
            ConnectionCheckCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.AdbConnectionCheck), DeviceReady);
            CollectLogcatCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.AdbCollectLogcat), DeviceReady);
            RunShellCommand = new AsyncRelayCommand(_ => RunShellAsync(), DeviceReady);
            RebootSystemCommand = new AsyncRelayCommand(_ => RebootAsync("system"), DeviceReady);
            RebootRecoveryCommand = new AsyncRelayCommand(_ => RebootAsync("recovery"), DeviceReady);
            RebootBootloaderCommand = new AsyncRelayCommand(_ => RebootAsync("bootloader"), DeviceReady);
            RebootEdlCommand = new AsyncRelayCommand(_ => RebootAsync("edl"), DeviceReady);
            RebootDownloadCommand = new AsyncRelayCommand(_ => RebootAsync("download"), DeviceReady);

            GateOnDevice(ReadInfoCommand, ConnectionCheckCommand, CollectLogcatCommand, RunShellCommand,
                RebootSystemCommand, RebootRecoveryCommand, RebootBootloaderCommand, RebootEdlCommand, RebootDownloadCommand);
        }

        /// <summary>Availability for every button that needs a selected handset.</summary>
        private bool DeviceReady(object? _) => !IsBusy && CanRunDeviceAction;

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

        protected override void OnOperationCompleted(OperationExecutionResult result)
        {
            List<string> lines = new(result.Log);
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

            return RunOperationAsync(OperationTypes.AdbReboot, new Dictionary<string, string> { ["target"] = target });
        }

        private async Task RunShellAsync()
        {
            if (SelectedDevice is not { Serial: { Length: > 0 } serial })
            {
                ErrorMessage = Localizer.Instance["devices.none"];
                return;
            }

            IsBusy = true;
            try
            {
                // adb streams its output into the console through the process sink; the shell text
                // is also kept on screen and the status bar can cancel a hanging command.
                bool completed = await Services.Progress.RunAsync($"adb shell {ShellCommand}", async (_, ct) =>
                {
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