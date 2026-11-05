using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using B_VIP.CloudClient;
using B_VIP.Contracts.Auth;
using B_VIP.Contracts.Licenses;
using B_VIP.Desktop.Mvvm;
using B_VIP.Desktop.Services;

namespace B_VIP.Desktop.ViewModels
{
    /// <summary>
    /// One pricing card. The Telegram link is compiled in per plan so the operator reaches support
    /// with the exact plan and price already written in the message.
    /// </summary>
    public sealed record PricingPlan(
        string Title,
        string Price,
        string Term,
        string Summary,
        IReadOnlyList<string> Features,
        string? Badge,
        bool IsFeatured,
        string TelegramUrl)
    {
        public bool HasBadge => !string.IsNullOrWhiteSpace(Badge);
    }

    /// <summary>
    /// Subscription screen: three direct Telegram purchase cards plus the manual 16-character
    /// license key activation. Nothing is simulated: the cards open the support chat and the key
    /// is activated against the server with this workstation's fingerprint.
    /// </summary>
    public sealed class SubscriptionViewModel : ViewModelBase
    {
        public const string TelegramContact = "@B_Vip_0";

        private readonly AppServices _services;
        private string _licenseKey = string.Empty;

        public SubscriptionViewModel()
        {
            _services = AppServices.Current;

            Plans = new ObservableCollection<PricingPlan>(BuildPlans());

            OpenTelegramCommand = new RelayCommand(parameter =>
            {
                if (parameter is PricingPlan plan)
                {
                    Open(plan.TelegramUrl, plan.Title);
                }
            });

            ContactCommand = new RelayCommand(_ =>
                Open($"https://t.me/{TelegramContact.TrimStart('@')}", "B_VIP Support"));

            ActivateLicenseCommand = new AsyncRelayCommand(_ => ActivateAsync(),
                _ => !IsBusy && LicenseKey.Length == 16);

            RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        }

        public ObservableCollection<PricingPlan> Plans { get; }

        public RelayCommand OpenTelegramCommand { get; }

        public RelayCommand ContactCommand { get; }

        public AsyncRelayCommand ActivateLicenseCommand { get; }

        public AsyncRelayCommand RefreshCommand { get; }

        /// <summary>The 16-character activation key, restricted to the characters keys are made of.</summary>
        public string LicenseKey
        {
            get => _licenseKey;
            set
            {
                string cleaned = new((value ?? string.Empty)
                    .ToUpperInvariant()
                    .Where(c => char.IsAsciiLetterOrDigit(c) || c == '-')
                    .Take(16)
                    .ToArray());

                if (SetProperty(ref _licenseKey, cleaned))
                {
                    OnPropertyChanged(nameof(KeyHint));
                    ActivateLicenseCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public bool IsKeyComplete => LicenseKey.Length == 16;

        public string KeyHint => IsKeyComplete
            ? "Ready to activate."
            : $"{16 - LicenseKey.Length} character(s) remaining.";

        public string CurrentPlan => _services.Session.License is null
            ? "No active subscription"
            : $"{_services.Session.License.PlanName} · {_services.Session.License.Status}";

        public string CurrentExpiry => _services.Session.License?.ExpiresAt is null
            ? "—"
            : _services.Session.License.ExpiresAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        public string SupportHandle => TelegramContact;

        public override async Task OnNavigatedToAsync() => await RefreshAsync();

        private async Task RefreshAsync()
        {
            try
            {
                MeResponse me = await _services.Api.MeAsync();
                _services.Session.License = me.License;
                OnPropertyChanged(nameof(CurrentPlan));
                OnPropertyChanged(nameof(CurrentExpiry));
            }
            catch (ApiException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (HttpRequestException)
            {
                ErrorMessage = "The server is not reachable; the current plan could not be refreshed.";
            }
        }

        private async Task ActivateAsync()
        {
            if (!IsKeyComplete)
            {
                ErrorMessage = "An activation key is exactly 16 characters.";
                return;
            }

            IsBusy = true;
            ErrorMessage = null;
            try
            {
                ActivateLicenseResponse response = await _services.Api.ActivateLicenseAsync(new ActivateLicenseRequest(
                    LicenseKey,
                    LicenseViewModel.Fingerprint(),
                    Environment.MachineName,
                    "windows-x64",
                    LicenseViewModel.ClientVersion()));

                StatusMessage = response.Message;
                if (!response.Success)
                {
                    ErrorMessage = response.Message;
                    return;
                }

                await RefreshAsync();
                LicenseKey = string.Empty;
            }
            catch (ApiException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (HttpRequestException)
            {
                ErrorMessage = "The server is not reachable; the key was not activated.";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void Open(string url, string what)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                StatusMessage = $"Opening Telegram for {what}…";
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ErrorMessage = $"The browser could not be opened: {ex.Message}";
            }
        }

        private static IReadOnlyList<PricingPlan> BuildPlans() =>
        [
            new("3 Months Plan", "$20", "3 months",
                "A quarter of full access for a single bench.",
                [
                    "Every vendor and chipset module",
                    "ADB, Fastboot and partition maintenance",
                    "Live console and diagnostic reports",
                    "3 concurrent workstations"
                ],
                null,
                false,
                "https://t.me/B_Vip_0?text=Hello%20B_VIP%20Support%2C%20I%20want%20to%20activate%20the%203-Month%20Plan%20(%2420)%20for%20my%20account."),

            new("6 Months Plan", "$35", "6 months",
                "The workshop favourite: half a year at the lowest monthly rate.",
                [
                    "Everything in the 3-month plan",
                    "Priority support on the Telegram channel",
                    "HWID reset on request",
                    "3 concurrent workstations"
                ],
                "Most Popular",
                true,
                "https://t.me/B_Vip_0?text=Hello%20B_VIP%20Support%2C%20I%20want%20to%20activate%20the%206-Month%20Plan%20(%2435)%20for%20my%20account."),

            new("1 Year Plan", "$50", "12 months",
                "The best value: a full year of every module and update.",
                [
                    "Everything in the 6-month plan",
                    "Every future chipset module",
                    "Highest concurrent-PC limit",
                    "Direct line to the developer"
                ],
                "Best Value",
                false,
                "https://t.me/B_Vip_0?text=Hello%20B_VIP%20Support%2C%20I%20want%20to%20activate%20the%201-Year%20Plan%20(%2450)%20for%20my%20account.")
        ];
    }
}
