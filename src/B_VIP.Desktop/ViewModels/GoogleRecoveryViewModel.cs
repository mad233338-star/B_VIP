using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using B_VIP.Contracts.Devices;
using B_VIP.Core.Modules;
using B_VIP.Core.Operations;
using B_VIP.Desktop.Mvvm;
using B_VIP.Modules;

namespace B_VIP.Desktop.ViewModels
{
    /// <summary>A recovery destination presented as a card: title, explanation and official address.</summary>
    public sealed record RecoveryCard(string Title, string Subtitle, string Url);

    /// <summary>
    /// Google Account Recovery: official destinations as action cards, live FRP/lock diagnostics
    /// from the connected handset, and a one-click dispatch that opens a page on the device itself.
    /// </summary>
    public sealed class GoogleRecoveryViewModel : DeviceScreenViewModel
    {
        public GoogleRecoveryViewModel()
        {
            Rows = new ObservableCollection<DiagnosticRow>();

            Cards = new[]
            {
                new RecoveryCard(
                    "Account Recovery Portal",
                    "Recover a Google account and regain access to the handset.",
                    OfficialLinks.GoogleAccountRecovery),
                new RecoveryCard(
                    "Android Device Support",
                    "Official Android help centre for setup, updates and device issues.",
                    OfficialLinks.AndroidSupport),
                new RecoveryCard(
                    "Security & Account Management",
                    "Review sign-in options, trusted devices and account security.",
                    "https://myaccount.google.com/security")
            };

            OpenBrowserCommand = new RelayCommand(OpenBrowser);
            SendToDeviceCommand = new AsyncRelayCommand(SendToDeviceAsync, _ => SelectedDevice is not null && !IsBusy);
            CheckLockStatusCommand = new AsyncRelayCommand(_ => CheckLockStatusAsync(), _ => SelectedDevice is not null && !IsBusy);
        }

        public IReadOnlyList<RecoveryCard> Cards { get; }

        public ObservableCollection<DiagnosticRow> Rows { get; }

        public RelayCommand OpenBrowserCommand { get; }

        public AsyncRelayCommand SendToDeviceCommand { get; }

        public AsyncRelayCommand CheckLockStatusCommand { get; }

        protected override void OnDeviceSelected(DeviceInfoDto? device)
        {
            SendToDeviceCommand.RaiseCanExecuteChanged();
            CheckLockStatusCommand.RaiseCanExecuteChanged();
        }

        private void OpenBrowser(object? parameter)
        {
            string? url = parameter switch
            {
                RecoveryCard card => card.Url,
                string text => text,
                _ => null
            };

            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                StatusMessage = $"Opened {url} in your browser.";
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }

        /// <summary>Pushes the card's address to the handset so it opens on the device screen.</summary>
        private async Task SendToDeviceAsync(object? parameter)
        {
            if (parameter is not RecoveryCard card || SelectedDevice is null)
            {
                ErrorMessage = "Select a connected handset first.";
                return;
            }

            await RunAsync(OperationTypes.GoogleOpenDeviceUrl,
                new Dictionary<string, string> { ["url"] = card.Url });
        }

        private async Task CheckLockStatusAsync()
        {
            if (SelectedDevice is null)
            {
                ErrorMessage = "Select a connected handset first.";
                return;
            }

            await RunAsync(OperationTypes.GoogleLockStatus, null);
        }

        private async Task RunAsync(string operationType, IReadOnlyDictionary<string, string>? parameters)
        {
            IsBusy = true;
            ErrorMessage = null;
            Rows.Clear();
            try
            {
                OperationExecutionResult result = await Services.Devices.ExecuteAsync(
                    operationType,
                    SelectedDevice?.Serial,
                    SelectedDevice?.DeviceId,
                    Describe(SelectedDevice),
                    parameters,
                    confirmDataLoss: false);

                foreach (DiagnosticCheckDto check in result.Checks)
                {
                    Rows.Add(new DiagnosticRow { Name = check.Name, Status = check.Status, Detail = check.Detail });
                }

                foreach (string line in result.Log)
                {
                    Rows.Add(new DiagnosticRow { Name = "log", Status = "info", Detail = line });
                }

                foreach (string warning in result.Warnings)
                {
                    Rows.Add(new DiagnosticRow { Name = "notice", Status = "warn", Detail = warning });
                }

                StatusMessage = result.Summary;
                ErrorMessage = result.Errors.Count > 0 ? string.Join(Environment.NewLine, result.Errors) : null;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
