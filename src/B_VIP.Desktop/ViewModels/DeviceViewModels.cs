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
    /// <summary>Shared behaviour for screens that need a connected handset.</summary>
    public abstract class DeviceScreenViewModel : ViewModelBase
    {
        protected readonly AppServices Services;

        protected DeviceScreenViewModel()
        {
            Services = AppServices.Current;
            Devices = new ObservableCollection<DeviceInfoDto>();
            ToolStatuses = new ObservableCollection<ToolStatusDto>();
            ScanCommand = new AsyncRelayCommand(ScanAsync);
        }

        public ObservableCollection<DeviceInfoDto> Devices { get; }

        public ObservableCollection<ToolStatusDto> ToolStatuses { get; }

        /// <summary>
        /// Live USB/COM ports from the watcher. The collection updates itself on connect and
        /// disconnect, so the devices screen never needs a manual refresh.
        /// </summary>
        public DeviceMonitorService Monitor => Services.Monitor;

        public AsyncRelayCommand ScanCommand { get; }

        private DeviceInfoDto? _selectedDevice;

        public DeviceInfoDto? SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                if (SetProperty(ref _selectedDevice, value))
                {
                    OnPropertyChanged(nameof(CanRunDeviceAction));
                    OnDeviceSelected(value);
                }
            }
        }

        public bool CanRunDeviceAction => _selectedDevice is not null;

        public string this[string key] => Localizer.Instance[key];

        protected virtual void OnDeviceSelected(DeviceInfoDto? device)
        {
        }

        public override async Task OnNavigatedToAsync()
        {
            await ScanAsync();
            await LoadToolStatusAsync();
        }

        protected async Task ScanAsync(object? _ = null)
        {
            IsBusy = true;
            ErrorMessage = null;
            try
            {
                // A scan runs adb and fastboot in turn and can take seconds: drive it through the
                // shell dispatcher so the status bar shows it and the operator can cancel.
                bool entered = false;
                bool completed = await Services.Progress.RunAsync("Scanning for connected devices", async (_, ct) =>
                {
                    entered = true;
                    DeviceListResponse response = await Services.Devices.ScanAsync(ct);
                    string? previous = SelectedDevice?.Serial;
                    Devices.Clear();
                    foreach (DeviceInfoDto device in response.Devices)
                    {
                        Devices.Add(device);
                    }

                    SelectedDevice = previous is null
                        ? Devices.FirstOrDefault()
                        : Devices.FirstOrDefault(d => d.Serial == previous) ?? Devices.FirstOrDefault();

                    StatusMessage = Devices.Count == 0
                        ? Localizer.Instance["devices.none"]
                        : $"{Devices.Count} device(s) detected.";
                    ErrorMessage = response.ScanError;
                }, indeterminate: true);

                if (!completed && entered)
                {
                    StatusMessage = "Scan cancelled.";
                }
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

        private async Task LoadToolStatusAsync()
        {
            try
            {
                IReadOnlyList<ToolStatusDto> tools = await Services.Devices.GetToolStatusAsync();
                ToolStatuses.Clear();
                foreach (ToolStatusDto tool in tools)
                {
                    ToolStatuses.Add(tool);
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }

        /// <summary>
        /// Runs one operation type through the shell dispatcher: the status bar shows the task, the
        /// cancel button kills the running tool, and the verdict is written to the live console next
        /// to the streamed command output.
        /// </summary>
        protected async Task RunOperationAsync(string operationType, IReadOnlyDictionary<string, string>? parameters = null, bool confirmDataLoss = false)
        {
            // A lapsed trial or subscription locks the hardware screens and sends the technician to
            // the renewal view instead of letting a command fail on the server.
            if (!LicenseGuard.CanRunOperations(Services.Session.License, out string licenseReason))
            {
                ErrorMessage = licenseReason;
                StatusMessage = "Opening License & Activation…";
                Services.Log.Warn(licenseReason);
                Services.Navigation.NavigateTo("license-activation");
                return;
            }

            IsBusy = true;
            ErrorMessage = null;

            string label = OperationTypes.Find(operationType)?.DisplayName ?? operationType;
            try
            {
                bool entered = false;
                bool completed = await Services.Progress.RunAsync(label, async (_, ct) =>
                {
                    entered = true;
                    OperationExecutionResult result = await Services.Devices.ExecuteAsync(
                        operationType,
                        SelectedDevice?.Serial,
                        SelectedDevice?.DeviceId,
                        Describe(SelectedDevice),
                        parameters,
                        confirmDataLoss,
                        ct);

                    StatusMessage = result.Summary;
                    if (result.Errors.Count > 0)
                    {
                        ErrorMessage = string.Join(Environment.NewLine, result.Errors);
                    }

                    OnOperationCompleted(result);
                    // DeviceWorkspace already writes the verdict to the console; logging it here too
                    // would double every line of the operation diary.
                }, indeterminate: true);

                if (!completed && entered)
                {
                    StatusMessage = "Operation cancelled.";
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                Services.Log.Error($"{label} failed to start: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        protected virtual void OnOperationCompleted(OperationExecutionResult result)
        {
        }

        protected static string? Describe(DeviceInfoDto? device)
            => device is null ? null : string.Join(" ", new[] { device.Manufacturer, device.Model, device.Serial }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    public sealed class DeviceManagerViewModel : DeviceScreenViewModel
    {
        public DeviceManagerViewModel()
        {
            RebootCommand = new AsyncRelayCommand(_ => RunDeviceActionAsync("reboot"));
            RebootBootloaderCommand = new AsyncRelayCommand(_ => RunDeviceActionAsync("reboot-bootloader"));
            RebootRecoveryCommand = new AsyncRelayCommand(_ => RunDeviceActionAsync("reboot-recovery"));
        }

        public AsyncRelayCommand RebootCommand { get; }

        public AsyncRelayCommand RebootBootloaderCommand { get; }

        public AsyncRelayCommand RebootRecoveryCommand { get; }

        private async Task RunDeviceActionAsync(string action)
        {
            if (SelectedDevice is not { Serial: { Length: > 0 } serial })
            {
                ErrorMessage = Localizer.Instance["devices.none"];
                return;
            }

            IsBusy = true;
            try
            {
                DeviceActionResult result = await Services.Devices.ExecuteActionAsync(serial, action, null);
                StatusMessage = result.Detail;
                if (!result.Succeeded)
                {
                    ErrorMessage = result.Detail;
                }
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

    public sealed class DiagnosticRow
    {
        public string Name { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public string? Detail { get; init; }

        public long? DurationMs { get; init; }
    }

    public sealed class DiagnosticsViewModel : DeviceScreenViewModel
    {
        private Guid? _lastReportId;

        public DiagnosticsViewModel()
        {
            RunCommand = new AsyncRelayCommand(_ => RunAsync());
            ExportCommand = new AsyncRelayCommand(_ => ExportAsync());
            Rows = new ObservableCollection<DiagnosticRow>();
        }

        public ObservableCollection<DiagnosticRow> Rows { get; }

        public AsyncRelayCommand RunCommand { get; }

        public AsyncRelayCommand ExportCommand { get; }

        public Guid? LastReportId
        {
            get => _lastReportId;
            private set => SetProperty(ref _lastReportId, value, () => OnPropertyChanged(nameof(HasReport)));
        }

        public bool HasReport => _lastReportId.HasValue;

        private async Task RunAsync()
        {
            if (SelectedDevice is not { Serial: { Length: > 0 } serial })
            {
                ErrorMessage = Localizer.Instance["devices.none"];
                return;
            }

            IsBusy = true;
            ErrorMessage = null;
            Rows.Clear();
            try
            {
                DeviceDiagnosticsResponse response = await Services.Devices.RunDiagnosticsAsync(serial);
                foreach (DiagnosticCheckDto check in response.Checks)
                {
                    Rows.Add(new DiagnosticRow
                    {
                        Name = check.Name,
                        Status = check.Status,
                        Detail = check.Detail,
                        DurationMs = check.DurationMs
                    });
                }

                foreach (string warning in response.Warnings)
                {
                    Rows.Add(new DiagnosticRow { Name = "warning", Status = "warn", Detail = warning });
                }

                string json = ReportBuilder.BuildJson(new ReportInput(
                    Module: "android-diagnostics",
                    Title: $"Diagnostics — {Describe(SelectedDevice)}",
                    DeviceId: SelectedDevice.DeviceId,
                    DeviceDescription: Describe(SelectedDevice),
                    AppVersion: typeof(DiagnosticsViewModel).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                    OsVersion: Environment.OSVersion.ToString(),
                    OperationId: null,
                    CorrelationId: Guid.NewGuid().ToString("N"),
                    Checks: response.Checks.Select(c => new ReportCheckResult(c.Name, c.Status, c.Detail, c.DurationMs)).ToList(),
                    Warnings: response.Warnings,
                    Errors: response.Errors,
                    Extra: null), DateTime.UtcNow);

                LastReportId = await Services.Devices.SaveReportAsync(
                    $"Diagnostics — {Describe(SelectedDevice)}", "android-diagnostics",
                    SelectedDevice.DeviceId, json, containsSensitive: true, redact: false);

                StatusMessage = Rows.Count == 0 ? Localizer.Instance["common.empty"] : $"{Rows.Count} result(s).";
                ErrorMessage = response.Errors.Count > 0 ? string.Join(Environment.NewLine, response.Errors) : null;
                LastJson = json;
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

        public string LastJson { get; private set; } = "{}";

        private Task ExportAsync()
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "B_VIP");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.WriteAllText(path, LastJson);
                StatusMessage = path;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }

            return Task.CompletedTask;
        }
    }

    public enum VendorKind
    {
        Samsung,
        Xiaomi,
        Infinix,
        Tecno,
        MediaTek,
        Qualcomm,
        Spreadtrum
    }

    /// <summary>One action offered by a brand tab: the operation it starts and whether it erases data.</summary>
    public sealed record VendorAction(string Title, string OperationType, bool ErasesData);

    /// <summary>
    /// A brand tab: reads the handset through its own module and offers the maintenance routines
    /// that module really implements. Destructive routines ask for confirmation first.
    /// </summary>
    public sealed class VendorViewModel : DeviceScreenViewModel
    {
        public VendorViewModel(VendorKind kind)
        {
            Kind = kind;
            Rows = new ObservableCollection<DiagnosticRow>();

            (string title, IReadOnlyList<VendorAction> actions) = kind switch
            {
                VendorKind.Samsung =>
                    ("Samsung", new[]
                    {
                        new VendorAction("Read device information", OperationTypes.SamsungDiagnostic, false)
                    }),
                VendorKind.Xiaomi =>
                    ("Xiaomi", new[]
                    {
                        new VendorAction("Read device information", OperationTypes.XiaomiReadInfo, false),
                        new VendorAction("Reset FRP & config", OperationTypes.XiaomiResetFrp, true)
                    }),
                VendorKind.Infinix =>
                    ("Infinix", new[]
                    {
                        new VendorAction("Read device information", OperationTypes.InfinixReadInfo, false),
                        new VendorAction("Reset FRP & persist", OperationTypes.InfinixResetFrp, true),
                        new VendorAction("Factory format", OperationTypes.InfinixFactoryFormat, true)
                    }),
                VendorKind.Tecno =>
                    ("Tecno", new[]
                    {
                        new VendorAction("Read device information", OperationTypes.TecnoReadInfo, false),
                        new VendorAction("Reset FRP", OperationTypes.TecnoResetFrp, true),
                        new VendorAction("Wipe userdata & cache", OperationTypes.TecnoWipe, true)
                    }),
                VendorKind.MediaTek =>
                    ("MediaTek", new[]
                    {
                        new VendorAction("Read device information", OperationTypes.MediaTekDiagnostic, false)
                    }),
                VendorKind.Qualcomm =>
                    ("Qualcomm", new[]
                    {
                        new VendorAction("Read device information", OperationTypes.QualcommDiagnostic, false)
                    }),
                _ =>
                    ("SPREADTRUM (Unisoc)", new[]
                    {
                        new VendorAction("Read device information", OperationTypes.SpreadtrumReadInfo, false),
                        new VendorAction("Reset FRP", OperationTypes.SpreadtrumResetFrp, true)
                    })
            };

            Title = title;
            Actions = actions;
            RunCommand = new AsyncRelayCommand(_ => RunAsync(actions[0].OperationType), _ => !IsBusy);
            RunActionCommand = new AsyncRelayCommand(RunActionAsync, _ => !IsBusy);
        }

        public VendorKind Kind { get; }

        public string Title { get; }

        public IReadOnlyList<VendorAction> Actions { get; }

        public ObservableCollection<DiagnosticRow> Rows { get; }

        public AsyncRelayCommand RunCommand { get; }

        public AsyncRelayCommand RunActionCommand { get; }

        protected override void OnOperationCompleted(OperationExecutionResult result) => Fill(result);

        private async Task RunActionAsync(object? parameter)
        {
            if (parameter is not VendorAction action)
            {
                return;
            }

            if (action.ErasesData)
            {
                MessageBoxResult answer = MessageBox.Show(
                    $"{action.Title} erases protected partitions on the connected handset.\n\n" +
                    "This cannot be undone. Continue?",
                    "B_VIP Tool",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);

                if (answer != MessageBoxResult.Yes)
                {
                    StatusMessage = "Routine cancelled.";
                    return;
                }
            }

            await RunAsync(action.OperationType, action.ErasesData);
        }

        private async Task RunAsync(string operationType, bool confirmDataLoss = false)
        {
            if (SelectedDevice is null)
            {
                ErrorMessage = Localizer.Instance["devices.none"];
                return;
            }

            IsBusy = true;
            ErrorMessage = null;
            Rows.Clear();
            try
            {
                // Destructive routines carry an explicit confirmation parameter so the module
                // can refuse to erase a partition when it is invoked without the operator's consent.
                IReadOnlyDictionary<string, string>? parameters = confirmDataLoss
                    ? new Dictionary<string, string> { ["confirm"] = "yes" }
                    : null;

                OperationExecutionResult result = await Services.Devices.ExecuteAsync(
                    operationType,
                    SelectedDevice.Serial,
                    SelectedDevice.DeviceId,
                    Describe(SelectedDevice),
                    parameters,
                    confirmDataLoss);

                Fill(result);

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

        private void Fill(OperationExecutionResult result)
        {
            Rows.Clear();
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
        }
    }
}
