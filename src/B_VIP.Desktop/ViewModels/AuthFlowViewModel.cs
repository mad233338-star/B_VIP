using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using B_VIP.CloudClient;
using B_VIP.Contracts.Auth;
using B_VIP.Contracts.Licenses;
using B_VIP.Desktop.Localization;
using B_VIP.Desktop.Mvvm;
using B_VIP.Desktop.Services;

namespace B_VIP.Desktop.ViewModels
{
    /// <summary>
    /// The four onboarding panels the main container switches between. Nothing opens a second OS
    /// window: sign-in, registration, e-mail verification and plan selection are states of one view.
    /// </summary>
    public enum AuthPanel
    {
        SignIn,
        Register,
        Verify,
        Plans
    }

    /// <summary>
    /// Owns the onboarding state machine. Each panel is a plain view model; the window binds a
    /// ContentControl to <see cref="CurrentPanel"/> and the matching DataTemplate renders it.
    /// </summary>
    public sealed class AuthFlowViewModel : ViewModelBase
    {
        private ViewModelBase _currentPanel;
        private AuthPanel _panel;

        public AuthFlowViewModel()
        {
            SignIn = new SignInPanelViewModel(this);
            Register = new RegisterPanelViewModel(this);
            Verify = new VerifyPanelViewModel(this);
            Plans = new PlansPanelViewModel(this);

            SignIn.SignedIn += userName =>
            {
                // The shell takes over from here; the flow itself only reports the event.
                SignedIn?.Invoke(userName);
            };

            _currentPanel = SignIn;
            _panel = AuthPanel.SignIn;
        }

        /// <summary>Raised with the technician's user name once the server accepted the credentials.</summary>
        public event Action<string>? SignedIn;

        public SignInPanelViewModel SignIn { get; }

        public RegisterPanelViewModel Register { get; }

        public VerifyPanelViewModel Verify { get; }

        public PlansPanelViewModel Plans { get; }

        public ViewModelBase CurrentPanel
        {
            get => _currentPanel;
            private set => SetProperty(ref _currentPanel, value);
        }

        public AuthPanel Panel
        {
            get => _panel;
            private set => SetProperty(ref _panel, value);
        }

        public string this[string key] => Localizer.Instance[key];

        /// <summary>Sign-in is the entry state; used by the window when it opens.</summary>
        public Task InitializeAsync() => SignIn.InitializeAsync();

        internal void ShowSignIn(string? prefillUserName = null)
        {
            if (!string.IsNullOrWhiteSpace(prefillUserName))
            {
                SignIn.UserName = prefillUserName;
            }

            Panel = AuthPanel.SignIn;
            CurrentPanel = SignIn;
        }

        internal void ShowRegister()
        {
            Register.Reset();
            Panel = AuthPanel.Register;
            CurrentPanel = Register;
        }

        internal void ShowVerify(string email, string? verificationLink, bool emailSent, string? userName = null)
        {
            Verify.Prepare(email, verificationLink, emailSent, userName);
            Panel = AuthPanel.Verify;
            CurrentPanel = Verify;
        }

        internal async Task ShowPlansAsync(string? activationNote = null)
        {
            Panel = AuthPanel.Plans;
            CurrentPanel = Plans;
            await Plans.LoadAsync(activationNote);
        }
    }

    /// <summary>Panel 1: technician sign-in. The server address is compiled in, never typed.</summary>
    public sealed class SignInPanelViewModel : ViewModelBase
    {
        private readonly AuthFlowViewModel _flow;
        private readonly AppServices _services;
        private string _userName = string.Empty;
        private string _email = string.Empty;
        private string _password = string.Empty;
        private bool _rememberMe = true;
        private bool _requiresBootstrap;
        private string _bootstrapToken = string.Empty;
        private string _bootstrapEmail = string.Empty;

        public SignInPanelViewModel(AuthFlowViewModel flow)
        {
            _flow = flow;
            _services = AppServices.Current;
            LoginCommand = new AsyncRelayCommand(_ => LoginAsync(), _ => !IsBusy);
        }

        public event Action<string>? SignedIn;

        public AsyncRelayCommand LoginCommand { get; }

        public RelayCommand ShowRegisterCommand => _showRegister ??= new RelayCommand(_ => _flow.ShowRegister());

        private RelayCommand? _showRegister;

        public string this[string key] => Localizer.Instance[key];

        public string UserName
        {
            get => _userName;
            set => SetProperty(ref _userName, value);
        }

        /// <summary>
        /// Optional: when the account is still pending, the activation panel needs the address the
        /// link went to. It is never sent as a credential.
        /// </summary>
        public string Email
        {
            get => _email;
            set => SetProperty(ref _email, value, () =>
            {
                OnPropertyChanged(nameof(EmailError));
                OnPropertyChanged(nameof(HasEmailError));
            });
        }

        /// <summary>Why the typed address is not accepted, or an empty string while it is.</summary>
        public string EmailError => Email.Length > 0 && !Core.Rules.ValidationRules.IsValidEmail(Email)
            ? "Enter a valid e-mail address, for example technician@example.com."
            : string.Empty;

        public bool HasEmailError => EmailError.Length > 0;

        public string Password
        {
            get => _password;
            set => SetProperty(ref _password, value);
        }

        public bool RememberMe
        {
            get => _rememberMe;
            set => SetProperty(ref _rememberMe, value);
        }

        public bool RequiresBootstrap
        {
            get => _requiresBootstrap;
            private set => SetProperty(ref _requiresBootstrap, value);
        }

        public string BootstrapToken
        {
            get => _bootstrapToken;
            set => SetProperty(ref _bootstrapToken, value);
        }

        public string BootstrapEmail
        {
            get => _bootstrapEmail;
            set => SetProperty(ref _bootstrapEmail, value);
        }

        public async Task InitializeAsync()
        {
            try
            {
                BootstrapStatusDto status = await _services.Api.GetBootstrapStatusAsync();
                RequiresBootstrap = status.RequiresFirstAdmin;
            }
            catch (Exception)
            {
                // Offline: the sign-in attempt reports the real connectivity problem.
            }
        }

        private async Task LoginAsync()
        {
            ErrorMessage = null;

            // The address is optional here, but when one is typed it has to be a real address: the
            // activation panel sends the code there.
            if (HasEmailError)
            {
                ErrorMessage = EmailError;
                return;
            }

            StatusMessage = Localizer.Instance["login.connecting"];
            IsBusy = true;

            try
            {
                if (RequiresBootstrap)
                {
                    if (string.IsNullOrWhiteSpace(BootstrapToken))
                    {
                        ErrorMessage = "The bootstrap token from the server configuration is required to create the first administrator.";
                        return;
                    }

                    await _services.Api.CreateFirstAdminAsync(new FirstAdminRequest(
                        BootstrapToken.Trim(), "admin", BootstrapEmail.Trim(), Password));
                    RequiresBootstrap = false;
                }

                string hardwareId = await HardwareIdentity.GetAsync(_services.ProcessRunner);
                SessionResponse session = await _services.Api.LoginAsync(new LoginRequest(
                    UserName.Trim(), Password, hardwareId, ClientVersion(), RememberMe));

                // Navigation is only ever the result of a session the server actually issued.
                if (session?.User is null || string.IsNullOrWhiteSpace(session.Tokens?.AccessToken))
                {
                    ErrorMessage = "Invalid username or password.";
                    StatusMessage = string.Empty;
                    return;
                }

                _services.Session.User = session.User;
                _services.Session.Permissions = session.Permissions;
                _services.Session.License = session.License;
                _services.Session.IsSignedIn = true;

                StatusMessage = string.Empty;
                Password = string.Empty;
                SignedIn?.Invoke(session.User.UserName);
            }
            catch (ApiException ex)
            {
                switch (ex.Code)
                {
                    case "email-not-verified":
                        // Hand the technician straight to the verification panel, no dialog.
                        ErrorMessage = null;
                        _flow.ShowVerify(
                            string.IsNullOrWhiteSpace(Email) ? string.Empty : Email.Trim(), null, false, UserName.Trim());
                        break;

                    case "invalid-credentials":
                        ErrorMessage = "Invalid username or password.";
                        break;
                    case "account-locked":
                        ErrorMessage = "This account is temporarily locked after too many failed attempts.";
                        break;
                    case "account-suspended":
                        ErrorMessage = "This account is suspended. Contact your administrator.";
                        break;
                    case "device-mismatch":
                        ErrorMessage = "This account is bound to another computer. Please contact administrator to reset HWID.";
                        break;
                    case "rate-limited" or "too-many-attempts":
                        ErrorMessage = "Too many attempts. Wait a moment and try again.";
                        break;
                    case var _ when ex.StatusCode == HttpStatusCode.Unauthorized:
                        // Any other 401 is still a rejected credential pair.
                        ErrorMessage = "Invalid username or password.";
                        break;
                    case var _ when ex.StatusCode == HttpStatusCode.Forbidden:
                        // A 403 that is not the e-mail verification state means the hardware binding
                        // refused this machine.
                        ErrorMessage = "This account is bound to another computer. Please contact administrator to reset HWID.";
                        break;
                    default:
                        ErrorMessage = ex.Message;
                        break;
                }

                StatusMessage = string.Empty;
            }
            catch (HttpRequestException)
            {
                ErrorMessage = "The cloud service is unreachable. Check this computer's internet connection.";
                StatusMessage = string.Empty;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private static string ClientVersion()
            => typeof(SignInPanelViewModel).Assembly.GetName().Version?.ToString() ?? "1.0.0";
    }

    /// <summary>Panel 2: technician registration (username, e-mail, password, terms).</summary>
    public sealed class RegisterPanelViewModel : ViewModelBase
    {
        private static readonly Regex UserNamePattern = new(@"^[A-Za-z0-9._-]{4,64}$", RegexOptions.Compiled);

        private readonly AuthFlowViewModel _flow;
        private readonly AppServices _services;
        private string _userName = string.Empty;
        private string _email = string.Empty;
        private string _password = string.Empty;
        private string _confirmPassword = string.Empty;
        private bool _acceptedTerms;

        public RegisterPanelViewModel(AuthFlowViewModel flow)
        {
            _flow = flow;
            _services = AppServices.Current;

            SubmitCommand = new AsyncRelayCommand(_ => SubmitAsync(), _ => !IsBusy && CanSubmit);
            BackToSignInCommand = new RelayCommand(_ => _flow.ShowSignIn());
        }

        public AsyncRelayCommand SubmitCommand { get; }

        public RelayCommand BackToSignInCommand { get; }

        public string UserName
        {
            get => _userName;
            set => SetProperty(ref _userName, value, RefreshValidation);
        }

        public string Email
        {
            get => _email;
            set => SetProperty(ref _email, value, () =>
            {
                OnPropertyChanged(nameof(EmailError));
                OnPropertyChanged(nameof(HasEmailError));
                RefreshValidation();
            });
        }

        /// <summary>Immediate feedback while the technician types the address.</summary>
        public string EmailError => _email.Length > 0 && !Core.Rules.ValidationRules.IsValidEmail(_email)
            ? "Enter a valid e-mail address, for example technician@example.com."
            : string.Empty;

        public bool HasEmailError => EmailError.Length > 0;

        public string Password
        {
            get => _password;
            set => SetProperty(ref _password, value, RefreshValidation);
        }

        public string ConfirmPassword
        {
            get => _confirmPassword;
            set => SetProperty(ref _confirmPassword, value, RefreshValidation);
        }

        public bool AcceptedTerms
        {
            get => _acceptedTerms;
            set => SetProperty(ref _acceptedTerms, value, RefreshValidation);
        }

        public bool PasswordsMatch => _confirmPassword.Length > 0 && _password == _confirmPassword;

        public bool PasswordsMismatch => _confirmPassword.Length > 0 && _password != _confirmPassword;

        public bool CanSubmit =>
            UserNamePattern.IsMatch(_userName) &&
            Core.Rules.ValidationRules.IsValidEmail(_email) &&
            _password.Length >= 10 &&
            _password == _confirmPassword &&
            _acceptedTerms;

        internal void Reset()
        {
            Password = string.Empty;
            ConfirmPassword = string.Empty;
            ErrorMessage = null;
            StatusMessage = null;
            RefreshValidation();
        }

        private void RefreshValidation()
        {
            OnPropertyChanged(nameof(PasswordsMatch));
            OnPropertyChanged(nameof(PasswordsMismatch));
            OnPropertyChanged(nameof(CanSubmit));
            SubmitCommand.RaiseCanExecuteChanged();
        }

        private async Task SubmitAsync()
        {
            ErrorMessage = null;
            StatusMessage = null;

            if (!CanSubmit)
            {
                ErrorMessage = "Check the username, e-mail address and password (at least 10 characters, both passwords identical) and accept the terms.";
                return;
            }

            IsBusy = true;
            try
            {
                string hardwareId = await HardwareIdentity.GetAsync(_services.ProcessRunner);

                RegisterTechnicianResponse response = await _services.Api.RegisterTechnicianAsync(new RegisterTechnicianRequest(
                    UserName.Trim(), Email.Trim(), Password, AcceptedTerms, hardwareId));

                Password = string.Empty;
                ConfirmPassword = string.Empty;

                _flow.ShowVerify(response.Email, response.VerificationLink, response.VerificationEmailSent, UserName.Trim());
            }
            catch (ApiException ex)
            {
                ErrorMessage = ex.Code switch
                {
                    "invalid-user-name" => "The username needs at least 4 alphanumeric characters.",
                    "invalid-email" => "Enter a valid e-mail address.",
                    "weak-password" => ex.Message,
                    "account-exists" => "A technician account with this username or e-mail already exists.",
                    "terms-not-accepted" => "Accept the terms of service to continue.",
                    _ => ex.Message
                };
            }
            catch (HttpRequestException)
            {
                ErrorMessage = "The cloud service is unreachable. Check this computer's internet connection.";
            }
            finally
            {
                IsBusy = false;
                RefreshValidation();
            }
        }
    }

    /// <summary>
    /// Panel 3: e-mail verification. Offers the resend countdown and — only when the server has no
    /// SMTP transport, so the link can never arrive — the instant activation fallback.
    /// </summary>
    public sealed class VerifyPanelViewModel : ViewModelBase
    {
        private readonly AuthFlowViewModel _flow;
        private readonly AppServices _services;
        private readonly DispatcherTimer _cooldown;
        private string _email = string.Empty;
        private string _verificationLink = string.Empty;
        private bool _emailSent;
        private int _resendSeconds;
        private int _activatedState;
        private string _verificationCode = string.Empty;

        public VerifyPanelViewModel(AuthFlowViewModel flow)
        {
            _flow = flow;
            _services = AppServices.Current;

            ResendCommand = new AsyncRelayCommand(_ => ResendAsync(), _ => !IsBusy && _resendSeconds == 0);
            VerifyCodeCommand = new AsyncRelayCommand(_ => VerifyCodeAsync(),
                _ => !IsBusy && _verificationCode.Length == 6 && !string.IsNullOrWhiteSpace(_email));
            BackToSignInCommand = new RelayCommand(_ => _flow.ShowSignIn(Email));
            ShowPlansCommand = new AsyncRelayCommand(_ => _flow.ShowPlansAsync("Your account is active."));

            _cooldown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _cooldown.Tick += (_, _) =>
            {
                if (_resendSeconds > 0)
                {
                    _resendSeconds--;
                    OnPropertyChanged(nameof(ResendLabel));
                    ResendCommand.RaiseCanExecuteChanged();
                }
                else
                {
                    _cooldown.Stop();
                }
            };
        }

        public AsyncRelayCommand ResendCommand { get; }

        public AsyncRelayCommand VerifyCodeCommand { get; }

        /// <summary>The 6 digits from the activation e-mail, typed instead of clicking the link.</summary>
        public string VerificationCode
        {
            get => _verificationCode;
            set
            {
                string digits = new string((value ?? string.Empty).Where(char.IsAsciiDigit).Take(6).ToArray());
                if (SetProperty(ref _verificationCode, digits))
                {
                    OnPropertyChanged(nameof(CanVerifyCode));
                    VerifyCodeCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public bool CanVerifyCode => _verificationCode.Length == 6;

        public AsyncRelayCommand ShowPlansCommand { get; }

        public RelayCommand BackToSignInCommand { get; }

        public string Email
        {
            get => _email;
            private set => SetProperty(ref _email, value);
        }

        public string VerificationLink
        {
            get => _verificationLink;
            private set => SetProperty(ref _verificationLink, value, () => OnPropertyChanged(nameof(HasVerificationLink)));
        }

        public bool HasVerificationLink => !string.IsNullOrWhiteSpace(_verificationLink);

        /// <summary>True when the server reported that it could not deliver the message.</summary>
        public bool EmailDeliveryFailed => !_emailSent;

        public bool IsActivated => _activatedState == 1;

        public string ActivatedMessage => IsActivated
            ? StatusMessage ?? "Your account is active."
            : string.Empty;

        public bool IsWaiting => _activatedState == 0;

        public string ResendLabel => _resendSeconds > 0
            ? Localizer.Instance.Format("verify.wait", _resendSeconds)
            : Localizer.Instance["verify.resend"];

        internal void Prepare(string email, string? verificationLink, bool emailSent, string? userName)
        {
            SignInUserName = userName ?? SignInUserName;
            Email = email ?? string.Empty;
            VerificationLink = verificationLink ?? string.Empty;
            VerificationCode = string.Empty;
            _emailSent = emailSent;
            _activatedState = 0;
            ErrorMessage = null;

            // There is no activation bypass: the 6-digit code from the message is the only path.
            StatusMessage = emailSent
                ? $"Verification code sent to {Email}. Enter the 6-digit code from the message below; it is valid for 15 minutes."
                : "The server could not send the verification e-mail. Check the address, resend the code, or ask the administrator to configure e-mail delivery.";

            OnPropertyChanged(nameof(EmailDeliveryFailed));
            OnPropertyChanged(nameof(IsActivated));
            OnPropertyChanged(nameof(IsWaiting));
            OnPropertyChanged(nameof(ActivatedMessage));

            if (emailSent)
            {
                StartCooldown();
            }
        }

        private async Task ResendAsync()
        {
            if (_resendSeconds > 0 || IsBusy)
            {
                return;
            }

            IsBusy = true;
            ErrorMessage = null;
            try
            {
                ResendVerificationResponse response = await _services.Api.ResendVerificationAsync(Email);
                StatusMessage = response.Message;
                VerificationLink = response.VerificationLink ?? VerificationLink;
                StartCooldown();
            }
            catch (ApiException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (HttpRequestException)
            {
                ErrorMessage = "The cloud service is unreachable.";
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>Activates the account from the 6-digit code; the trial and plans follow.</summary>
        private async Task VerifyCodeAsync()
        {
            IsBusy = true;
            ErrorMessage = null;
            try
            {
                VerifyEmailResponse response = await _services.Api.VerifyEmailCodeAsync(
                    new VerifyEmailCodeRequest(SignInUserName, Email, VerificationCode));

                if (!response.Verified)
                {
                    ErrorMessage = response.Message;
                    return;
                }

                _activatedState = 1;
                StatusMessage = response.Message;
                OnPropertyChanged(nameof(IsActivated));
                OnPropertyChanged(nameof(IsWaiting));
                OnPropertyChanged(nameof(ActivatedMessage));
            }
            catch (ApiException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (HttpRequestException)
            {
                ErrorMessage = "The cloud service is unreachable.";
            }
            finally
            {
                IsBusy = false;
                VerifyCodeCommand.RaiseCanExecuteChanged();
            }
        }

        private void StartCooldown()
        {
            _resendSeconds = 60;
            OnPropertyChanged(nameof(ResendLabel));
            ResendCommand.RaiseCanExecuteChanged();
            _cooldown.Start();
        }

        /// <summary>The server needs the user name as well; the flow supplies it from registration.</summary>
        internal string SignInUserName { get; set; } = string.Empty;
    }

    /// <summary>Panel 4: the subscription tiers, selectable, with the activation instructions.</summary>
    public sealed class PlansPanelViewModel : ViewModelBase
    {
        private readonly AuthFlowViewModel _flow;
        private readonly AppServices _services;
        private SubscriptionTier? _selected;
        private string _instructions = string.Empty;
        private bool _isLoaded;

        public PlansPanelViewModel(AuthFlowViewModel flow)
        {
            _flow = flow;
            _services = AppServices.Current;
            Cards = new ObservableCollection<SubscriptionTier>();
            SelectCommand = new RelayCommand(Select);
            ContinueCommand = new RelayCommand(_ => _flow.ShowSignIn());
        }

        public ObservableCollection<SubscriptionTier> Cards { get; }

        public RelayCommand SelectCommand { get; }

        public RelayCommand ContinueCommand { get; }

        public SubscriptionTier? Selected
        {
            get => _selected;
            private set => SetProperty(ref _selected, value, () => OnPropertyChanged(nameof(HasSelection)));
        }

        public bool HasSelection => _selected is not null;

        public bool IsLoaded
        {
            get => _isLoaded;
            private set => SetProperty(ref _isLoaded, value);
        }

        public string Instructions
        {
            get => _instructions;
            private set => SetProperty(ref _instructions, value, () => OnPropertyChanged(nameof(HasInstructions)));
        }

        public bool HasInstructions => !string.IsNullOrWhiteSpace(_instructions);

        public async Task LoadAsync(string? note)
        {
            ErrorMessage = null;
            IsBusy = true;
            try
            {
                IReadOnlyList<LicensePlanDto> plans = await _services.Api.GetPlansAsync();

                Cards.Clear();
                foreach (LicensePlanDto plan in plans
                    .Where(p => p.IsActive)
                    .OrderBy(p => p.SortOrder))
                {
                    if (string.Equals(plan.Code, Core.Rules.LicenseRules.TrialPlanCode, StringComparison.OrdinalIgnoreCase))
                    {
                        // The trial is automatic; it is not something the technician buys.
                        continue;
                    }

                    Cards.Add(new SubscriptionTier(
                        plan.Code,
                        plan.Name,
                        plan.DurationDays is null ? "Lifetime" : $"{plan.DurationDays} days",
                        string.IsNullOrWhiteSpace(plan.Description) ? plan.Name : plan.Description,
                        plan.Price.HasValue ? $"{plan.Price.Value:0.##} {plan.Currency ?? "USD"}" : "Price on request",
                        plan.IsLifetime ? "Lifetime" : plan.DurationDays is null ? "Custom term" : $"{plan.DurationDays} days",
                        $"{plan.MaxActivations} concurrent PC(s)",
                        false));
                }

                if (!string.IsNullOrWhiteSpace(note))
                {
                    StatusMessage = note;
                }

                IsLoaded = true;
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException)
            {
                ErrorMessage = "The plans could not be loaded. Check the connection and try again.";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void Select(object? parameter)
        {
            if (parameter is not SubscriptionTier tier)
            {
                return;
            }

            Selected = tier;
            Instructions =
                $"{tier.Name} · {tier.PriceText} · {tier.DurationText}. " +
                "Contact the administrator with your username and this tier; you will receive a BVIP-XXXX-XXXX-XXXX " +
                "activation key. Sign in and open License & Activation to paste it. Your 3-day trial starts automatically " +
                "when the account is activated, so nothing is blocked while you wait.";
        }
    }
}
