using System.Collections.ObjectModel;
using System.Windows;
using B_VIP.Contracts.Devices;
using B_VIP.Core.Operations;
using B_VIP.Desktop.Localization;
using B_VIP.Desktop.Mvvm;
using B_VIP.Modules;
using B_VIP.Modules.Samsung;

namespace B_VIP.Desktop.ViewModels
{
    /// <summary>
    /// Samsung screen: reads the handset's identity (MTP first, adb when it is authorized), sends the
    /// reboot commands, dispatches a browser to the device screen and runs the test-mode FRP routine.
    ///
    /// Every button routes through <see cref="DeviceScreenViewModel.RunOperationAsync"/>, so the
    /// operation is streamed into the live console, the status bar shows progress and the cancel
    /// button can abort it — the UI thread never waits on a modem or a CLI process.
    /// </summary>
    public sealed class SamsungViewModel : DeviceScreenViewModel
    {
        private const int DefaultFrpAuthorizationWaitSeconds = 45;

        public SamsungViewModel()
        {
            Rows = new ObservableCollection<DiagnosticRow>();
            BrowserChoices = [.. BrowserTarget.Presets, BrowserTarget.Custom(string.Empty)];
            _selectedBrowser = BrowserChoices[0];
            _customUrl = "https://accounts.google.com/signin/recovery";

            // The MTP identity read works without an adb device; everything below it needs one.
            ReadInfoCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.SamsungReadInfoMtp));
            RebootDownloadCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.SamsungRebootDownload), DeviceReady);
            RebootSystemCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.SamsungRebootSystem), DeviceReady);
            RebootRecoveryCommand = new AsyncRelayCommand(_ => RunOperationAsync(OperationTypes.VendorReboot,
                new Dictionary<string, string> { ["target"] = "recovery" }), DeviceReady);
            OpenBrowserCommand = new AsyncRelayCommand(_ => OpenBrowserAsync(), DeviceReady);
            ResetFrpCommand = new AsyncRelayCommand(_ => ResetFrpAsync(), DeviceReady);

            GateOnDevice(RebootDownloadCommand, RebootSystemCommand, RebootRecoveryCommand, OpenBrowserCommand, ResetFrpCommand);
        }

        /// <summary>Availability for every button that needs a selected handset.</summary>
        private bool DeviceReady(object? _) => !IsBusy && CanRunDeviceAction;

        public ObservableCollection<DiagnosticRow> Rows { get; }

        /// <summary>The browsers offered on the screen; the last entry accepts a custom address.</summary>
        public IReadOnlyList<BrowserTarget> BrowserChoices { get; }

        private BrowserTarget _selectedBrowser;

        public BrowserTarget SelectedBrowser
        {
            get => _selectedBrowser;
            set
            {
                if (SetProperty(ref _selectedBrowser, value))
                {
                    OnPropertyChanged(nameof(CanEditCustomUrl));
                }
            }
        }

        /// <summary>True when the operator picked the custom entry and must type an address.</summary>
        public bool CanEditCustomUrl => string.Equals(_selectedBrowser.Key, "custom", StringComparison.OrdinalIgnoreCase);

        private string _customUrl;

        public string CustomUrl
        {
            get => _customUrl;
            set => SetProperty(ref _customUrl, value);
        }

        public AsyncRelayCommand ReadInfoCommand { get; }

        public AsyncRelayCommand RebootDownloadCommand { get; }

        public AsyncRelayCommand RebootSystemCommand { get; }

        /// <summary>Reboots into recovery over adb, falling back to fastboot when it refuses.</summary>
        public AsyncRelayCommand RebootRecoveryCommand { get; }

        public AsyncRelayCommand OpenBrowserCommand { get; }

        public AsyncRelayCommand ResetFrpCommand { get; }

        protected override void OnOperationCompleted(OperationExecutionResult result)
        {
            // The rows are built into a plain list and swapped in on the dispatcher: the MTP/adb read
            // completes on a worker, and clearing a bound DataGrid row by row there is what crashed
            // the table (and its console) the moment "Read Info (MTP)" produced output.
            ApplyRows(Rows, result);
        }

        private Task OpenBrowserAsync()
        {
            BrowserTarget target = CanEditCustomUrl ? BrowserTarget.Custom(CustomUrl.Trim()) : SelectedBrowser;

            if (target.Key == "custom" &&
                (!Uri.TryCreate(target.Url, UriKind.Absolute, out Uri? uri) ||
                 (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
            {
                ErrorMessage = "Enter an absolute http:// or https:// address for the custom target.";
                return Task.CompletedTask;
            }

            Dictionary<string, string> parameters = new() { ["target"] = target.Key };
            if (target.Key == "custom")
            {
                parameters["url"] = target.Url;
            }

            return RunOperationAsync(OperationTypes.SamsungOpenBrowser, parameters);
        }

        private async Task ResetFrpAsync()
        {
            MessageBoxResult answer = MessageBox.Show(
                "The FRP routine clears the setup-complete flags on the connected Samsung handset and reboots it.\n\n" +
                "It needs an authorized adb connection: the modem sequence cannot approve the USB debugging prompt by itself.\n\n" +
                "Continue?",
                "B_VIP Tool",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                StatusMessage = "FRP routine cancelled.";
                return;
            }

            await RunOperationAsync(OperationTypes.SamsungFrpTestMode, new Dictionary<string, string>
            {
                ["confirm"] = "yes",
                ["waitSeconds"] = DefaultFrpAuthorizationWaitSeconds.ToString()
            }, confirmDataLoss: true);
        }
    }
}
