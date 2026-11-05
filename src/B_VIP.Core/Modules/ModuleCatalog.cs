namespace B_VIP.Core.Modules
{
    public sealed record ModuleDefinition(
        string Key,
        string TitleEn,
        string TitleAr,
        string Group,
        string Icon,
        bool RequiresDevice,
        bool RequiresExternalTool);

    /// <summary>The navigation modules. Availability is computed at runtime from real checks.</summary>
    public static class ModuleCatalog
    {
        public static readonly IReadOnlyList<ModuleDefinition> All =
        [
            new(ModuleKeys.Dashboard, "Dashboard", "لوحة التحكم", "main", "dashboard", false, false),
            new(ModuleKeys.DeviceManager, "Device Manager", "مدير الأجهزة", "devices", "phone", true, false),
            new(ModuleKeys.AndroidDiagnostics, "Android Diagnostics", "تشخيص Android", "devices", "stethoscope", true, false),
            new(ModuleKeys.Adb, "ADB", "ADB", "devices", "terminal", true, true),
            new(ModuleKeys.Fastboot, "Fastboot", "Fastboot", "devices", "zap", true, true),
            new(ModuleKeys.Samsung, "Samsung", "Samsung", "vendors", "badge", true, true),
            new(ModuleKeys.Xiaomi, "Xiaomi", "Xiaomi", "vendors", "badge", true, true),
            new(ModuleKeys.Infinix, "Infinix", "Infinix", "vendors", "badge", true, true),
            new(ModuleKeys.Tecno, "Tecno", "Tecno", "vendors", "badge", true, true),
            new(ModuleKeys.MediaTek, "MediaTek", "MediaTek", "vendors", "cpu", true, true),
            new(ModuleKeys.Qualcomm, "Qualcomm", "Qualcomm", "vendors", "microchip", true, true),
            new(ModuleKeys.Spreadtrum, "SPREADTRUM", "SPREADTRUM", "vendors", "cpu", true, true),
            new(ModuleKeys.GoogleRecovery, "Google Account Recovery", "Google Account Recovery", "vendors", "lifebuoy", false, false),
            new(ModuleKeys.ServiceOperations, "Service Operations", "عمليات الخدمة", "operations", "tools", false, false),
            new(ModuleKeys.DiagnosticReports, "Diagnostic Reports", "تقارير التشخيص", "operations", "file-text", false, false),
            new(ModuleKeys.LicenseActivation, "License & Activation", "الترخيص والتفعيل", "system", "key", false, false),
            new(ModuleKeys.SupportCenter, "Support Center", "مركز الدعم", "system", "headset", false, false),
            new(ModuleKeys.Drivers, "Drivers & Prerequisites", "Drivers & Prerequisites", "system", "download", false, false),
            new(ModuleKeys.Notifications, "Notifications", "الإشعارات", "system", "bell", false, false),
            new(ModuleKeys.Settings, "Settings", "الإعدادات", "system", "settings", false, false),
            new(ModuleKeys.About, "About B_VIP Tool", "حول B_VIP Tool", "system", "info", false, false),
            new(ModuleKeys.AdminDashboard, "Admin Dashboard", "لوحة الإدارة", "admin", "shield", false, false)
        ];

        public static ModuleDefinition? Find(string key)
            => All.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Well known external support links used by recovery modules.</summary>
    public static class OfficialLinks
    {
        public const string GoogleAccountRecovery = "https://accounts.google.com/signin/recovery";
        public const string AndroidSupport = "https://support.google.com/android/";
        public const string GoogleAccountsSupport = "https://support.google.com/accounts/";
    }
}
