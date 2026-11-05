namespace B_VIP.Core
{
    public enum AccountStatus
    {
        Active = 0,
        Suspended = 1,
        Deleted = 2,

        /// <summary>Self-registered technician waiting for the e-mail activation link.</summary>
        PendingVerification = 3
    }

    public enum LicenseStatus
    {
        Pending = 0,
        Active = 1,
        Expired = 2,
        Suspended = 3,
        Revoked = 4,
        Invalid = 5
    }

    public enum OperationStatus
    {
        Queued = 0,
        Validating = 1,
        Running = 2,
        Completed = 3,
        Failed = 4,
        Cancelled = 5,
        TimedOut = 6,
        RequiresAttention = 7
    }

    public enum DeviceConnectionState
    {
        NoDevice = 0,
        Detecting = 1,
        Connected = 2,
        Unauthorized = 3,
        DriverProblem = 4,
        Unsupported = 5,
        Disconnected = 6,
        Busy = 7,
        OperationFailed = 8,
        Fastboot = 9,
        Recovery = 10,
        EdgeDownloadMode = 11
    }

    public enum DeviceInterfaceType
    {
        Unknown = 0,
        Usb = 1,
        Adb = 2,
        Fastboot = 3,
        Recovery = 4,
        Mtp = 5,
        Edl = 6,
        Diag = 7
    }

    public enum OperationOutcome
    {
        Unknown = 0,
        Succeeded = 1,
        Failed = 2,
        Cancelled = 3,
        TimedOut = 4,
        RequiresAttention = 5
    }

    public enum TicketStatus
    {
        Open = 0,
        InProgress = 1,
        WaitingForUser = 2,
        WaitingForSupport = 3,
        Resolved = 4,
        Closed = 5,
        Reopened = 6
    }

    public enum TicketPriority
    {
        Low = 0,
        Normal = 1,
        High = 2,
        Urgent = 3
    }

    public enum NotificationType
    {
        License = 0,
        LicenseExpiry = 1,
        Support = 2,
        SoftwareUpdate = 3,
        Maintenance = 4,
        Administrative = 5,
        Security = 6
    }

    public enum IntegrationKind
    {
        ExternalTool = 0,
        LicensedService = 1,
        UpdateServer = 2,
        EmailSender = 3,
        BackupStorage = 4,
        SmsGateway = 5
    }

    public enum HealthStatus
    {
        Healthy = 0,
        Degraded = 1,
        Unhealthy = 2,
        Unknown = 3,
        NotConfigured = 4
    }

    /// <summary>Availability state shown for every navigation module.</summary>
    public enum ModuleAvailability
    {
        Available = 0,
        Connected = 1,
        NotConfigured = 2,
        RequiresDriver = 3,
        RequiresAuthorization = 4,
        RequiresExternalTool = 5,
        RequiresHardwareTesting = 6,
        Unavailable = 7
    }

    public enum AuditOutcome
    {
        Success = 0,
        Failure = 1,
        Denied = 2
    }

    public enum ReportFormat
    {
        Json = 0,
        Text = 1,
        Html = 2
    }

    public enum Language
    {
        English = 0,
        Arabic = 1
    }

    public enum VerificationResult
    {
        Valid = 0,
        Expired = 1,
        Suspended = 2,
        Revoked = 3,
        NotFound = 4,
        ActivationLimitReached = 5,
        ServerUnreachable = 6,
        InvalidRequest = 7,
        Pending = 8
    }

    public static class RoleNames
    {
        public const string User = "User";
        public const string SupportStaff = "Support Staff";
        public const string Supervisor = "Supervisor";
        public const string FinanceManager = "Finance Manager";
        public const string GeneralManager = "General Manager";
        public const string SuperAdmin = "Super Admin";

        public static readonly string[] All =
        [
            User, SupportStaff, Supervisor, FinanceManager, GeneralManager, SuperAdmin
        ];
    }

    /// <summary>Permission keys enforced on the server for every protected endpoint.</summary>
    public static class Permissions
    {
        public const string UsersRead = "users.read";
        public const string UsersWrite = "users.write";
        public const string UsersSuspend = "users.suspend";
        public const string UsersRolesWrite = "users.roles.write";

        public const string RolesRead = "roles.read";
        public const string RolesWrite = "roles.write";

        public const string LicensesRead = "licenses.read";
        public const string LicensesWrite = "licenses.write";
        public const string LicensesManage = "licenses.manage";

        public const string OperationsRead = "operations.read";
        public const string OperationsRun = "operations.run";
        public const string OperationsCancel = "operations.cancel";

        public const string ReportsRead = "reports.read";
        public const string ReportsWrite = "reports.write";

        public const string TicketsRead = "tickets.read";
        public const string TicketsWrite = "tickets.write";
        public const string TicketsAssign = "tickets.assign";
        public const string TicketsAll = "tickets.all";

        public const string AdminOverview = "admin.overview";
        public const string AdminSettings = "admin.settings";
        public const string AdminIntegrations = "admin.integrations";
        public const string AdminAudit = "admin.audit";
        public const string AdminBackups = "admin.backups";
        public const string AdminHealth = "admin.health";
        public const string AdminReleases = "admin.releases";

        public static readonly string[] All =
        [
            UsersRead, UsersWrite, UsersSuspend, UsersRolesWrite,
            RolesRead, RolesWrite,
            LicensesRead, LicensesWrite, LicensesManage,
            OperationsRead, OperationsRun, OperationsCancel,
            ReportsRead, ReportsWrite,
            TicketsRead, TicketsWrite, TicketsAssign, TicketsAll,
            AdminOverview, AdminSettings, AdminIntegrations, AdminAudit,
            AdminBackups, AdminHealth, AdminReleases
        ];

        public const string GroupUsers = "Users";
        public const string GroupRoles = "Roles & Permissions";
        public const string GroupLicenses = "Licenses";
        public const string GroupOperations = "Operations";
        public const string GroupTickets = "Support";
        public const string GroupAdmin = "Administration";

        public static string GroupOf(string permission) => permission switch
        {
            UsersRead or UsersWrite or UsersSuspend or UsersRolesWrite => GroupUsers,
            RolesRead or RolesWrite => GroupRoles,
            LicensesRead or LicensesWrite or LicensesManage => GroupLicenses,
            OperationsRead or OperationsRun or OperationsCancel => GroupOperations,
            ReportsRead or ReportsWrite => GroupOperations,
            TicketsRead or TicketsWrite or TicketsAssign or TicketsAll => GroupTickets,
            _ => GroupAdmin
        };

        /// <summary>Default permission assignment for the built-in roles.</summary>
        public static IReadOnlyList<string> DefaultForRole(string role) => role switch
        {
            RoleNames.SuperAdmin => All,
            RoleNames.GeneralManager => new[]
            {
                UsersRead, RolesRead, LicensesRead, LicensesWrite, LicensesManage,
                OperationsRead, ReportsRead, TicketsRead, TicketsAll,
                AdminOverview, AdminHealth, AdminAudit, AdminReleases
            },
            RoleNames.FinanceManager => new[]
            {
                UsersRead, LicensesRead, LicensesWrite, LicensesManage,
                OperationsRead, ReportsRead, AdminOverview
            },
            RoleNames.Supervisor => new[]
            {
                UsersRead, LicensesRead, OperationsRead, OperationsRun, OperationsCancel,
                ReportsRead, ReportsWrite, TicketsRead, TicketsWrite, TicketsAssign,
                TicketsAll, AdminOverview
            },
            RoleNames.SupportStaff => new[]
            {
                LicensesRead, OperationsRead, OperationsRun, OperationsCancel,
                ReportsRead, ReportsWrite, TicketsRead, TicketsWrite, TicketsAssign
            },
            _ => new[]
            {
                LicensesRead, OperationsRead, OperationsRun, ReportsRead,
                ReportsWrite, TicketsRead, TicketsWrite
            }
        };
    }

    /// <summary>Stable keys for the 18 main navigation modules.</summary>
    public static class ModuleKeys
    {
        public const string Dashboard = "dashboard";
        public const string DeviceManager = "device-manager";
        public const string AndroidDiagnostics = "android-diagnostics";
        // ADB and Fastboot tool keys are now referenced via ModuleKeys.Adb and ModuleKeys.Fastboot
        public const string Samsung = "samsung";
        public const string Xiaomi = "xiaomi";
        public const string Infinix = "infinix";
        public const string Tecno = "teco";
        public const string MediaTek = "mediatek";
        public const string Qualcomm = "qualcomm";
        public const string Spreadtrum = "spreadtrum";
        public const string Drivers = "drivers";
        public const string GoogleRecovery = "google-recovery";
        public const string ServiceOperations = "service-operations";
        public const string DiagnosticReports = "diagnostic-reports";
        public const string LicenseActivation = "license-activation";
        public const string SupportCenter = "support-center";
        public const string Notifications = "notifications";
        public const string Settings = "settings";
        public const string About = "about";
        public const string AdminDashboard = "admin-dashboard";

        public const string Adb = "adb";
        public const string Fastboot = "fastboot";
        public const string PlatformTools = "platform-tools";
    }
}
