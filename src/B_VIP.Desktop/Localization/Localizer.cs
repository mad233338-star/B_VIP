using System.ComponentModel;

namespace B_VIP.Desktop.Localization
{
    /// <summary>
    /// English string table. Views bind to <see cref="Instance"/> with the indexer, so every label,
    /// tooltip and dialog in the application resolves from one place.
    /// </summary>
    public sealed class Localizer : INotifyPropertyChanged
    {
        public const string English = "en";

        private static readonly Dictionary<string, string> EnglishStrings = new()
        {
            ["app.name"] = "B_VIP Tool",
            ["app.author"] = "Bashir Al-Farhan",
            ["app.tagline"] = "Mobile service & GSM toolkit",

            ["nav.dashboard"] = "Dashboard",
            ["nav.devices"] = "Device Manager",
            ["nav.diagnostics"] = "Android Diagnostics",
            ["nav.adb"] = "ADB",
            ["nav.fastboot"] = "Fastboot",
            ["nav.samsung"] = "Samsung",
            ["nav.odin"] = "Samsung Odin",
            ["nav.xiaomi"] = "Xiaomi",
            ["nav.infinix"] = "Infinix",
            ["nav.teco"] = "Tecno",
            ["nav.mediatek"] = "MediaTek",
            ["nav.qualcomm"] = "Qualcomm",
            ["nav.spreadtrum"] = "SPREADTRUM",
            ["nav.drivers"] = "Drivers & Prerequisites",
            ["nav.google"] = "Google Account Recovery",
            ["nav.operations"] = "Service Operations",
            ["nav.reports"] = "Diagnostic Reports",
            ["nav.license"] = "License & Activation",
            ["nav.support"] = "Support Center",
            ["nav.notifications"] = "Notifications",
            ["nav.terms"] = "Terms & Conditions",
            ["nav.privacy"] = "Privacy Policy",
            ["nav.settings"] = "Settings",
            ["nav.about"] = "About",
            ["nav.admin"] = "Admin Dashboard",

            ["login.title"] = "Sign in to B_VIP Tool",
            ["login.username"] = "Username",
            ["login.email"] = "Email Address",
            ["login.password"] = "Password",
            ["login.remember"] = "Keep me signed in",
            ["login.submit"] = "Sign in",
            ["login.forgot"] = "Forgot password?",
            ["login.language"] = "English",
            ["login.server"] = "Server",
            ["login.connecting"] = "Signing in…",
            ["login.required"] = "Enter your username and password.",
            ["login.register"] = "Create technician account",
            ["login.hwid"] = "This workstation",

            ["register.title"] = "Technician registration",
            ["register.subtitle"] = "Create your account, verify your e-mail and choose a subscription tier.",
            ["register.username"] = "Username",
            ["register.email"] = "E-mail address",
            ["register.password"] = "Password",
            ["register.confirm"] = "Confirm password",
            ["register.terms"] = "I accept the terms of service and the acceptable-use policy.",
            ["register.submit"] = "Create account",
            ["register.back"] = "Back to sign in",
            ["register.mismatch"] = "The passwords do not match.",
            ["register.created"] = "Account created. Check your inbox to activate it.",

            ["verify.title"] = "Verify your e-mail",
            ["verify.sentTo"] = "An activation link was sent to {0}.",
            ["verify.email"] = "Email Address",
            ["verify.code"] = "6-digit verification code",
            ["verify.submitCode"] = "Verify code",
            ["verify.codeHint"] = "Enter the 6-digit code from the activation e-mail. The link in the same message works too.",
            ["verify.resend"] = "Resend verification e-mail",
            ["verify.wait"] = "Resend available in {0}s",
            ["verify.hwid"] = "Hardware ID bound to this account",

            ["tiers.title"] = "Subscription tiers",
            ["tiers.standard"] = "Standard Technician",
            ["tiers.standard.billing"] = "Monthly",
            ["tiers.pro"] = "Workshop Pro",
            ["tiers.pro.billing"] = "Quarterly / semi-annual",
            ["tiers.enterprise"] = "VIP Enterprise",
            ["tiers.enterprise.billing"] = "Annual",
            ["tiers.devices"] = "Concurrent PCs",
            ["tiers.duration"] = "Duration",
            ["tiers.select"] = "Select tier",
            ["tiers.active"] = "Your current tier",
            ["tiers.renew"] = "Renew subscription",
            ["tiers.payment"] = "Payment and activation",
            ["tiers.paymentNote"] = "Contact the administrator with your username to receive an activation key, then activate it below.",
            ["tiers.days"] = "days",

            ["common.refresh"] = "Refresh",
            ["common.search"] = "Search",
            ["common.run"] = "Run",
            ["common.cancel"] = "Cancel",
            ["common.close"] = "Close",
            ["common.save"] = "Save",
            ["common.confirm"] = "Confirm",
            ["common.logout"] = "Sign out",
            ["nav.account"] = "Account",
            ["common.status"] = "Status",
            ["common.details"] = "Details",
            ["common.empty"] = "No records to show.",
            ["common.busy"] = "Working…",
            ["common.ready"] = "Ready",
            ["common.error"] = "Something went wrong: {0}",
            ["common.language"] = "Language",

            ["dashboard.welcome"] = "Welcome back, {0}",
            ["dashboard.devices"] = "Devices connected",
            ["dashboard.operations"] = "Operations today",
            ["dashboard.license"] = "License status",
            ["dashboard.tools"] = "External tools",
            ["dashboard.quick"] = "Quick actions",

            ["devices.scan"] = "Scan for devices",
            ["devices.title"] = "Connected devices",
            ["devices.serial"] = "Serial",
            ["devices.model"] = "Model",
            ["devices.state"] = "State",
            ["devices.interface"] = "Interface",
            ["devices.none"] = "No device detected. Connect a handset and enable USB debugging.",
            ["devices.tools"] = "Platform tools status",

            ["diagnostics.run"] = "Run diagnostics",
            ["diagnostics.title"] = "Device diagnostics",
            ["diagnostics.check"] = "Check",
            ["diagnostics.result"] = "Result",
            ["diagnostics.note"] = "Every check below reads the real device; skipped checks are reported honestly.",

            ["adb.title"] = "ADB",
            ["adb.devices"] = "adb devices",
            ["adb.restart"] = "Restart adb server",
            ["adb.logcat"] = "Collect logcat",
            ["adb.shell"] = "Shell command",
            ["adb.run"] = "Run command",

            ["fastboot.title"] = "Fastboot",
            ["fastboot.devices"] = "List fastboot devices",
            ["fastboot.getvar"] = "Read variable",
            ["fastboot.flash"] = "Flash image",
            ["fastboot.wipe"] = "Erase partition",
            ["fastboot.warning"] = "Flashing and erasing can permanently damage a handset. Double-check the target.",

            ["vendor.title"] = "Vendor diagnostics",
            ["vendor.run"] = "Run vendor diagnostic",
            ["vendor.official"] = "Open official page",
            ["vendor.notice"] = "This module reports what the device really exposes; proprietary flashing is not simulated.",

            ["operations.title"] = "Service operations",
            ["operations.type"] = "Operation",
            ["operations.device"] = "Device",
            ["operations.result"] = "Result",
            ["operations.queued"] = "Queued",
            ["operations.create"] = "Start operation",
            ["operations.local"] = "Run locally and record the result",

            ["reports.title"] = "Diagnostic reports",
            ["reports.open"] = "Open report",
            ["reports.export"] = "Export JSON",

            ["license.title"] = "License & activation",
            ["license.activate"] = "Activate",
            ["license.deactivate"] = "Deactivate",
            ["license.verify"] = "Verify",
            ["license.key"] = "License key",
            ["license.plan"] = "Plan",
            ["license.expires"] = "Expires",
            ["license.activations"] = "Activations",

            ["support.title"] = "Support center",
            ["support.new"] = "New ticket",
            ["support.reply"] = "Send reply",
            ["support.priority"] = "Priority",
            ["support.subject"] = "Subject",
            ["support.message"] = "Message",

            ["notifications.title"] = "Notifications",
            ["notifications.markAll"] = "Mark all as read",
            ["notifications.unreadOnly"] = "Unread only",

            ["settings.title"] = "Settings",
            ["settings.language"] = "Interface language",
            ["settings.theme"] = "Theme",
            ["settings.themeValue"] = "Cyber Blue (dark)",
            ["settings.serverStatus"] = "Server status",
            ["settings.status.online"] = "Connected",
            ["settings.status.offline"] = "Offline",
            ["settings.preferences"] = "User preferences",
            ["settings.preferencesHint"] = "Saved automatically on this workstation.",
            ["settings.languageValue"] = "English",

            ["terms.title"] = "Terms & Conditions",
            ["privacy.title"] = "Privacy Policy",
            ["legal.close"] = "Close",

            ["about.title"] = "About B_VIP Tool",
            ["about.version"] = "Version",
            ["about.author"] = "Developer",
            ["about.license"] = "Licensed to",
            ["about.build"] = "Build",

            ["admin.title"] = "Admin dashboard",
            ["admin.overview"] = "Overview",
            ["admin.users"] = "Users",
            ["admin.roles"] = "Roles & permissions",
            ["admin.licenses"] = "Licenses",
            ["admin.settings"] = "Server settings",
            ["admin.integrations"] = "Integrations",
            ["admin.audit"] = "Audit log",
            ["admin.backups"] = "Backups",
            ["admin.releases"] = "Releases",
            ["admin.maintenance"] = "Maintenance mode",

            ["group.main"] = "Main",
            ["group.devices"] = "Devices",
            ["group.vendors"] = "Vendors",
            ["group.operations"] = "Operations",
            ["group.system"] = "System",
            ["group.admin"] = "Administration"
        };

        private static readonly Localizer _instance = new();

        private string _language = English;

        private Localizer()
        {
        }

        public static Localizer Instance => _instance;

        public event PropertyChangedEventHandler? PropertyChanged;

        public event Action? LanguageChanged;

        public string CurrentLanguage
        {
            get => _language;
            private set
            {
                if (_language == value)
                {
                    return;
                }

                _language = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRightToLeft)));
                LanguageChanged?.Invoke();
            }
        }

        public bool IsRightToLeft => false;

        public string this[string key]
        {
            get
            {
                Dictionary<string, string> table = EnglishStrings;
                if (table.TryGetValue(key, out string? value))
                {
                    return value;
                }

                return EnglishStrings.TryGetValue(key, out string? fallback) ? fallback : key;
            }
        }

        public string Format(string key, params object?[] args)
            => string.Format(this[key], args);

        public void SetLanguage(string language)
            // This build ships English only: the culture is locked to "en" so every label,
            // tooltip and dialog stays in English regardless of settings or saved preferences.
            => CurrentLanguage = English;

        public IReadOnlyDictionary<string, string> Snapshot(string language)
            => EnglishStrings;
    }
}
