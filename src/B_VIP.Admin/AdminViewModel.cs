using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using B_VIP.Contracts.Common;
using B_VIP.CloudClient;
using B_VIP.Contracts.Admin;
using B_VIP.Contracts.Auth;
using B_VIP.Contracts.Licenses;
using B_VIP.Contracts.Users;

namespace B_VIP.Admin
{
    /// <summary>
    /// Backing state for the administration console: overview counters, users, roles, licenses,
    /// server settings, integrations, audit trail, backups, releases and maintenance mode.
    /// Every value comes from the backend; nothing here is fabricated.
    /// </summary>
    public sealed class AdminViewModel : AdminObject
    {
        private readonly BVipApiClient _api;
        private AdminOverviewDto? _overview;
        private SettingDto? _selectedSetting;
        private IntegrationDto? _selectedIntegration;
        private UserSummaryDto? _selectedUser;
        private BackupDto? _selectedBackup;
        private string? _statusMessage;
        private string? _errorMessage;
        private string _newBackupNote = string.Empty;
        private UpdateMaintenanceRequest _maintenance = new(false, null, null, null);
        private TechnicianRow? _selectedTechnician;
        private TechnicianMetricsDto? _metrics;
        private LicensePlanDto? _selectedPlan;
        private string _technicianSearch = string.Empty;
        private int _extendDays = 30;
        private string _generatedKey = string.Empty;
        private string _planCode = string.Empty;
        private string _planName = string.Empty;
        private string _planDescription = string.Empty;
        private int _planDurationDays = 30;
        private int _planMaxActivations = 1;
        private decimal? _planPrice;
        private string _planCurrency = "USD";
        private bool _planIsLifetime;
        private bool _planIsActive = true;

        public AdminViewModel(BVipApiClient api)
        {
            _api = api;
            RefreshCommand = new AdminCommand(_ => InitializeAsync());
            UpdateSettingCommand = new AdminCommand(_ => UpdateSettingAsync(), _ => SelectedSetting is not null);
            TestIntegrationCommand = new AdminCommand(_ => TestIntegrationAsync(), _ => SelectedIntegration is not null);
            CreateBackupCommand = new AdminCommand(_ => CreateBackupAsync());
            RestoreBackupCommand = new AdminCommand(parameter => RestoreBackupAsync(parameter), parameter => parameter is BackupDto);
            SuspendUserCommand = new AdminCommand(_ => ChangeUserStatusAsync(suspend: true), _ => SelectedUser is not null);
            ReactivateUserCommand = new AdminCommand(_ => ChangeUserStatusAsync(suspend: false), _ => SelectedUser is not null);
            SaveMaintenanceCommand = new AdminCommand(_ => SaveMaintenanceAsync());

            SearchTechniciansCommand = new AdminCommand(_ => LoadTechniciansAsync());
            ToggleStatusCommand = new AdminCommand(ToggleTechnicianStatusAsync, p => p is TechnicianRow);
            ResetHardwareIdCommand = new AdminCommand(ResetHardwareIdAsync, p => p is TechnicianRow);
            ToggleVerifiedCommand = new AdminCommand(ToggleTechnicianVerifiedAsync, p => p is TechnicianRow);
            ExtendSubscriptionCommand = new AdminCommand(ExtendSubscriptionAsync, p => p is TechnicianRow && ExtendDays > 0);
            DeleteTechnicianCommand = new AdminCommand(DeleteTechnicianAsync, p => p is TechnicianRow);
            GenerateLicenseCommand = new AdminCommand(_ => GenerateLicenseAsync(),
                _ => SelectedTechnician is not null && SelectedPlan is not null);

            CreatePlanCommand = new AdminCommand(_ => CreatePlanAsync());
            SavePlanCommand = new AdminCommand(_ => SavePlanAsync(), _ => SelectedPlan is not null);
            DeactivatePlanCommand = new AdminCommand(_ => SetPlanActiveAsync(false), _ => SelectedPlan is not null);
            ActivatePlanCommand = new AdminCommand(_ => SetPlanActiveAsync(true), _ => SelectedPlan is not null);
            DeletePlanCommand = new AdminCommand(_ => DeletePlanAsync(), _ => SelectedPlan is not null);
            ChangePlanCommand = new AdminCommand(ChangeTechnicianPlanAsync, p => p is TechnicianRow);
        }

        public ObservableCollection<UserSummaryDto> Users { get; } = new();

        public ObservableCollection<TechnicianRow> Technicians { get; } = new();

        public ObservableCollection<LicensePlanDto> Plans { get; } = new();

        public ObservableCollection<RoleDto> Roles { get; } = new();

        public ObservableCollection<LicenseDto> Licenses { get; } = new();

        public ObservableCollection<SettingDto> Settings { get; } = new();

        public ObservableCollection<IntegrationDto> Integrations { get; } = new();

        public ObservableCollection<AuditEventDto> AuditEvents { get; } = new();

        public ObservableCollection<BackupDto> Backups { get; } = new();

        public ObservableCollection<ReleaseDto> Releases { get; } = new();

        public AdminCommand RefreshCommand { get; }

        public AdminCommand UpdateSettingCommand { get; }

        public AdminCommand TestIntegrationCommand { get; }

        public AdminCommand CreateBackupCommand { get; }

        public AdminCommand RestoreBackupCommand { get; }

        public AdminCommand SuspendUserCommand { get; }

        public AdminCommand ReactivateUserCommand { get; }

        public AdminCommand SaveMaintenanceCommand { get; }

        public AdminCommand SearchTechniciansCommand { get; }

        public AdminCommand ToggleStatusCommand { get; }

        public AdminCommand ResetHardwareIdCommand { get; }

        public AdminCommand ToggleVerifiedCommand { get; }

        public AdminCommand ExtendSubscriptionCommand { get; }

        public AdminCommand DeleteTechnicianCommand { get; }

        public AdminCommand GenerateLicenseCommand { get; }

        public AdminCommand CreatePlanCommand { get; }

        public AdminCommand SavePlanCommand { get; }

        public AdminCommand DeactivatePlanCommand { get; }

        public AdminCommand ActivatePlanCommand { get; }

        public AdminCommand DeletePlanCommand { get; }

        public AdminCommand ChangePlanCommand { get; }

        public string PlanCode
        {
            get => _planCode;
            set => SetProperty(ref _planCode, value);
        }

        public string PlanName
        {
            get => _planName;
            set => SetProperty(ref _planName, value);
        }

        public string PlanDescription
        {
            get => _planDescription;
            set => SetProperty(ref _planDescription, value);
        }

        public int PlanDurationDays
        {
            get => _planDurationDays;
            set => SetProperty(ref _planDurationDays, value);
        }

        public int PlanMaxActivations
        {
            get => _planMaxActivations;
            set => SetProperty(ref _planMaxActivations, value);
        }

        public decimal? PlanPrice
        {
            get => _planPrice;
            set => SetProperty(ref _planPrice, value);
        }

        public string PlanCurrency
        {
            get => _planCurrency;
            set => SetProperty(ref _planCurrency, value);
        }

        public bool PlanIsLifetime
        {
            get => _planIsLifetime;
            set => SetProperty(ref _planIsLifetime, value);
        }

        public bool PlanIsActive
        {
            get => _planIsActive;
            set => SetProperty(ref _planIsActive, value);
        }

        public TechnicianMetricsDto? Metrics
        {
            get => _metrics;
            private set => SetProperty(ref _metrics, value);
        }

        public TechnicianRow? SelectedTechnician
        {
            get => _selectedTechnician;
            set
            {
                if (SetProperty(ref _selectedTechnician, value))
                {
                    GenerateLicenseCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public LicensePlanDto? SelectedPlan
        {
            get => _selectedPlan;
            set
            {
                if (SetProperty(ref _selectedPlan, value))
                {
                    GenerateLicenseCommand.RaiseCanExecuteChanged();
                    SavePlanCommand.RaiseCanExecuteChanged();
                    DeactivatePlanCommand.RaiseCanExecuteChanged();
                    ActivatePlanCommand.RaiseCanExecuteChanged();
                    DeletePlanCommand.RaiseCanExecuteChanged();

                    if (value is not null)
                    {
                        // Load the tier into the editor so an edit starts from real values.
                        PlanCode = value.Code;
                        PlanName = value.Name;
                        PlanDescription = value.Description;
                        PlanDurationDays = value.DurationDays ?? 0;
                        PlanMaxActivations = value.MaxActivations;
                        PlanPrice = value.Price;
                        PlanCurrency = value.Currency ?? "USD";
                        PlanIsLifetime = value.IsLifetime;
                        PlanIsActive = value.IsActive;
                    }
                }
            }
        }

        public string TechnicianSearch
        {
            get => _technicianSearch;
            set => SetProperty(ref _technicianSearch, value);
        }

        public int ExtendDays
        {
            get => _extendDays;
            set
            {
                if (SetProperty(ref _extendDays, value))
                {
                    ExtendSubscriptionCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string GeneratedKey
        {
            get => _generatedKey;
            private set => SetProperty(ref _generatedKey, value);
        }

        public AdminOverviewDto? Overview
        {
            get => _overview;
            private set => SetProperty(ref _overview, value);
        }

        public SettingDto? SelectedSetting
        {
            get => _selectedSetting;
            set => SetProperty(ref _selectedSetting, value);
        }

        public IntegrationDto? SelectedIntegration
        {
            get => _selectedIntegration;
            set => SetProperty(ref _selectedIntegration, value);
        }

        public UserSummaryDto? SelectedUser
        {
            get => _selectedUser;
            set => SetProperty(ref _selectedUser, value);
        }

        public BackupDto? SelectedBackup
        {
            get => _selectedBackup;
            set => SetProperty(ref _selectedBackup, value);
        }

        public string NewBackupNote
        {
            get => _newBackupNote;
            set => SetProperty(ref _newBackupNote, value);
        }

        public UpdateMaintenanceRequest Maintenance
        {
            get => _maintenance;
            set => SetProperty(ref _maintenance, value);
        }

        public string? StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        public string? ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                if (SetProperty(ref _errorMessage, value))
                {
                    OnPropertyChanged(nameof(HasError));
                }
            }
        }

        public bool HasError => !string.IsNullOrEmpty(_errorMessage);

        public async Task InitializeAsync()
        {
            ErrorMessage = null;

            // Every section loads on its own: one unavailable endpoint, or a database with no rows
            // yet, must never blank the console or surface an exception dialog.
            await LoadSectionAsync(Users, () => _api.SearchUsersAsync(null, null, null, 1, 100), r => r.Items);
            await LoadSectionAsync(Roles, () => _api.GetRolesAsync(), r => r);
            await LoadSectionAsync(Licenses, () => _api.SearchLicensesAsync(null, null, null, 1, 100), r => r.Items);
            await LoadSectionAsync(Settings, () => _api.GetSettingsAsync(), r => r.Settings);
            await LoadSectionAsync(Integrations, () => _api.GetIntegrationsAsync(), r => r);
            await LoadSectionAsync(AuditEvents, () => _api.GetAuditEventsAsync(null, null, null, null, 1, 100), r => r.Items);
            await LoadSectionAsync(Backups, () => _api.GetBackupsAsync(), r => r);
            await LoadSectionAsync(Releases, () => _api.GetReleasesAsync(), r => r);
            await LoadSectionAsync(Plans, () => _api.GetPlansAsync(), r => r);

            SelectedPlan ??= Plans.FirstOrDefault(p => p.Code == "monthly") ?? Plans.FirstOrDefault();

            try
            {
                MaintenanceDto state = await _api.GetMaintenanceAsync();
                Maintenance = new UpdateMaintenanceRequest(state.Enabled, state.Message, state.StartsAt, state.EndsAt);
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException)
            {
                // The maintenance switch keeps its current defaults when the endpoint is unavailable.
                ErrorMessage ??= FriendlyError(ex);
            }

            try
            {
                Overview = await _api.GetAdminOverviewAsync();
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException)
            {
                ErrorMessage ??= FriendlyError(ex);
            }

            await LoadTechniciansAsync();

            StatusMessage = $"Updated {DateTime.Now:HH:mm:ss}";
        }

        /// <summary>
        /// Loads one collection. A transport failure only records a message: the remaining sections
        /// still load, which is what keeps a fresh or partially reachable backend from breaking the
        /// whole page.
        /// </summary>
        private async Task LoadSectionAsync<T, TResult>(
            ObservableCollection<T> target,
            Func<Task<TResult>> load,
            Func<TResult, IReadOnlyList<T>?> selector)
        {
            try
            {
                TResult result = await load();
                target.Clear();

                if (result is null)
                {
                    return;
                }

                foreach (T item in selector(result) ?? Array.Empty<T>())
                {
                    if (item is not null)
                    {
                        target.Add(item);
                    }
                }
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException)
            {
                ErrorMessage ??= FriendlyError(ex);
            }
        }

        private static string FriendlyError(Exception ex) =>
            ex is ApiException api ? api.Message : "The backend is not reachable.";

        private async Task UpdateSettingAsync()
        {
            if (SelectedSetting is null)
            {
                return;
            }

            try
            {
                SettingDto updated = await _api.UpdateSettingAsync(SelectedSetting.Key, SelectedSetting.Value);
                int index = Settings.IndexOf(SelectedSetting);
                if (index >= 0)
                {
                    Settings[index] = updated;
                    SelectedSetting = updated;
                }

                StatusMessage = $"Setting '{updated.Key}' saved.";
            }
            catch (ApiException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (HttpRequestException)
            {
                ErrorMessage = "The backend is not reachable.";
            }
        }

        private async Task TestIntegrationAsync()
        {
            if (SelectedIntegration is null)
            {
                return;
            }

            try
            {
                TestIntegrationResponse response = await _api.TestIntegrationAsync(SelectedIntegration.Id);
                StatusMessage = $"{SelectedIntegration.DisplayName}: {response.Message}";
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }

        private async Task CreateBackupAsync()
        {
            try
            {
                BackupActionResponse response = await _api.CreateBackupAsync(new CreateBackupRequest(
                    string.IsNullOrWhiteSpace(NewBackupNote) ? null : NewBackupNote));
                StatusMessage = response.Message;
                await LoadSectionAsync(Backups, () => _api.GetBackupsAsync(), r => r);
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }

        private async Task RestoreBackupAsync(object? parameter)
        {
            if (parameter is not BackupDto backup)
            {
                return;
            }

            try
            {
                BackupActionResponse response = await _api.RestoreBackupAsync(backup.Id);
                StatusMessage = response.Message;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }

        private async Task ChangeUserStatusAsync(bool suspend)
        {
            if (SelectedUser is null)
            {
                return;
            }

            try
            {
                if (suspend)
                {
                    await _api.SuspendUserAsync(SelectedUser.Id, "Suspended from the admin console.");
                }
                else
                {
                    await _api.ReactivateUserAsync(SelectedUser.Id);
                }

                await LoadSectionAsync(Users, () => _api.SearchUsersAsync(null, null, null, 1, 100), r => r.Items);
                StatusMessage = suspend ? $"{SelectedUser.UserName} suspended." : $"{SelectedUser.UserName} reactivated.";
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }

        // ------------------------------------------------------------ technicians

        private async Task LoadTechniciansAsync()
        {
            try
            {
                PagedResponse<UserSummaryDto>? users = await _api.SearchTechniciansAsync(TechnicianSearch, 1, 200);
                PagedResponse<LicenseDto>? licenses = await _api.SearchLicensesAsync(null, null, null, 1, 200);

                // An empty directory and an empty licence list are normal states, never errors.
                IReadOnlyList<UserSummaryDto> directory = users?.Items ?? Array.Empty<UserSummaryDto>();
                IReadOnlyList<LicenseDto> licenseRows = licenses?.Items ?? Array.Empty<LicenseDto>();

                Technicians.Clear();
                foreach (UserSummaryDto user in directory)
                {
                    if (user is null)
                    {
                        continue;
                    }

                    LicenseDto? license = licenseRows
                        .Where(l => l is not null && l.UserId == user.Id)
                        .OrderByDescending(l => l.CreatedUtc)
                        .FirstOrDefault();

                    Technicians.Add(new TechnicianRow(user, license));
                }

                try
                {
                    Metrics = await _api.GetTechnicianMetricsAsync();
                }
                catch (Exception ex) when (ex is ApiException or HttpRequestException)
                {
                    // The counters stay empty; the technician rows are still usable.
                    ErrorMessage ??= FriendlyError(ex);
                }

                StatusMessage = $"{Technicians.Count} technician(s) loaded.";
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException)
            {
                ErrorMessage = FriendlyError(ex);
            }
        }

        private async Task ToggleTechnicianStatusAsync(object? parameter)
        {
            if (parameter is not TechnicianRow row)
            {
                return;
            }

            try
            {
                if (row.IsSuspended)
                {
                    await _api.ReactivateUserAsync(row.Id);
                }
                else
                {
                    await _api.SuspendUserAsync(row.Id, "Suspended from the technician console.");
                }

                await LoadTechniciansAsync();
                StatusMessage = row.IsSuspended ? $"{row.UserName} activated." : $"{row.UserName} suspended.";
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }

        /// <summary>Decouples the workstation so the technician can bind the new machine on next sign-in.</summary>
        private async Task ResetHardwareIdAsync(object? parameter)
        {
            if (parameter is not TechnicianRow row)
            {
                return;
            }

            if (MessageBox.Show(
                    $"Are you sure you want to decouple the current device license for {row.UserName}?",
                    "Reset hardware ID",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                ResetHardwareIdResponse response = await _api.ResetHardwareIdAsync(row.Id);
                StatusMessage = response.Message;
                await LoadTechniciansAsync();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }

        private async Task ToggleTechnicianVerifiedAsync(object? parameter)
        {
            if (parameter is not TechnicianRow row)
            {
                return;
            }

            try
            {
                VerifyEmailResponseDto response = await _api.SetEmailVerifiedAsync(row.Id, !row.User.EmailVerified);
                StatusMessage = response.Message;
                await LoadTechniciansAsync();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }

        private async Task ExtendSubscriptionAsync(object? parameter)
        {
            if (parameter is not TechnicianRow row)
            {
                return;
            }

            try
            {
                ExtendSubscriptionResponse response = await _api.ExtendSubscriptionAsync(row.Id, ExtendDays,
                    "Extended from the technician console.");
                StatusMessage = $"{row.UserName}: {response.Message} New expiry {response.ExpiresAt:yyyy-MM-dd}.";
                await LoadTechniciansAsync();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }

        private async Task DeleteTechnicianAsync(object? parameter)
        {
            if (parameter is not TechnicianRow row)
            {
                return;
            }

            if (MessageBox.Show(
                    $"Delete the account for {row.UserName}?\n\nThe account is removed from the directory and all sessions are revoked. This cannot be undone.",
                    "Delete technician",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                await _api.DeleteTechnicianAsync(row.Id);
                StatusMessage = $"{row.UserName} removed.";
                await LoadTechniciansAsync();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }

        /// <summary>Issues a BVIP-XXXX-XXXX-XXXX activation key to the selected technician.</summary>
        private async Task GenerateLicenseAsync()
        {
            if (SelectedTechnician is null || SelectedPlan is null)
            {
                ErrorMessage = "Select a technician and a plan first.";
                return;
            }

            try
            {
                LicenseDto created = await _api.CreateLicenseAsync(new CreateLicenseRequest(
                    SelectedTechnician.Id,
                    SelectedPlan.Code,
                    MaxActivations: SelectedPlan.MaxActivations,
                    Notes: "Issued from the license generator."));

                GeneratedKey = created.LicenseKey;
                StatusMessage = $"Key {created.LicenseKey} issued to {SelectedTechnician.UserName} ({created.PlanName}).";
                await LoadSectionAsync(Licenses, () => _api.SearchLicensesAsync(null, null, null, 1, 100), r => r.Items);
                await LoadTechniciansAsync();
            }
            catch (ApiException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (HttpRequestException)
            {
                ErrorMessage = "The backend is not reachable.";
            }
        }

        // ------------------------------------------------------------ subscription tiers

        private async Task CreatePlanAsync()
        {
            ErrorMessage = null;
            try
            {
                LicensePlanDto created = await _api.CreatePlanAsync(new CreatePlanRequest(
                    PlanCode, PlanName, null, PlanDescription, PlanDurationDays, PlanMaxActivations,
                    PlanPrice, PlanCurrency, PlanIsLifetime, 0, PlanIsActive));

                StatusMessage = $"Plan '{created.Name}' ({created.Code}) created.";
                await InitializeAsync();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }

        private async Task SavePlanAsync()
        {
            if (SelectedPlan is null)
            {
                ErrorMessage = "Select a plan to edit first.";
                return;
            }

            ErrorMessage = null;
            try
            {
                LicensePlanDto updated = await _api.UpdatePlanAsync(SelectedPlan.Id, new UpdatePlanRequest(
                    PlanName, null, PlanDescription, PlanDurationDays, PlanMaxActivations,
                    PlanPrice, PlanCurrency, PlanIsLifetime, null, PlanIsActive));

                StatusMessage = $"Plan '{updated.Name}' saved.";
                await InitializeAsync();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }

        private async Task SetPlanActiveAsync(bool active)
        {
            if (SelectedPlan is null)
            {
                return;
            }

            try
            {
                LicensePlanDto updated = await _api.UpdatePlanAsync(
                    SelectedPlan.Id, new UpdatePlanRequest(IsActive: active));
                StatusMessage = $"Plan '{updated.Name}' is now {(active ? "active" : "deactivated")}.";
                await InitializeAsync();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }

        private async Task DeletePlanAsync()
        {
            if (SelectedPlan is null)
            {
                return;
            }

            if (MessageBox.Show(
                    $"Delete the plan '{SelectedPlan.Name}'?\n\nA plan that already has licenses cannot be deleted; deactivate it instead.",
                    "Delete plan",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                await _api.DeletePlanAsync(SelectedPlan.Id);
                StatusMessage = "Plan deleted.";
                await InitializeAsync();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }

        /// <summary>Moves one technician onto the tier selected in their row.</summary>
        private async Task ChangeTechnicianPlanAsync(object? parameter)
        {
            if (parameter is not TechnicianRow row)
            {
                return;
            }

            if (row.PlanChoice is null)
            {
                ErrorMessage = $"Pick a plan for {row.UserName} in the row first.";
                return;
            }

            try
            {
                LicenseDto license = await _api.ChangeTechnicianPlanAsync(row.Id, row.PlanChoice.Id);
                StatusMessage = $"{row.UserName} moved to {license.PlanName}; expires {license.ExpiresAt:yyyy-MM-dd}.";
                await LoadTechniciansAsync();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }

        private async Task SaveMaintenanceAsync()
        {
            try
            {
                MaintenanceDto state = await _api.SetMaintenanceAsync(Maintenance);
                Maintenance = new UpdateMaintenanceRequest(state.Enabled, state.Message, state.StartsAt, state.EndsAt);
                StatusMessage = state.Enabled ? "Maintenance mode enabled." : "Maintenance mode disabled.";
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }
    }
}
