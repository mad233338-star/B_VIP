namespace B_VIP.Core.Operations
{
    public sealed record OperationTypeDefinition(
        string Key,
        string DisplayName,
        string DisplayNameAr,
        string Module,
        bool RequiresDevice,
        bool RequiresExternalTool,
        bool DataLossRisk,
        string SuccessCondition,
        int DefaultTimeoutSeconds);

    /// <summary>
    /// Registry of operations the engine accepts. Every entry declares whether it needs a connected
    /// device, an external tool and whether it can destroy user data (which forces an explicit confirmation).
    /// </summary>
    public static class OperationTypes
    {
        public const string AdbDevices = "adb.devices";
        public const string AdbReadInfo = "adb.read-info";
        public const string AdbProperties = "adb.read-properties";
        public const string AdbConnectionCheck = "adb.connection-check";
        public const string AdbRestartServer = "adb.restart-server";
        public const string AdbCollectLogcat = "adb.collect-logcat";
        public const string AdbReboot = "adb.reboot";
        public const string FastbootDevices = "fastboot.devices";
        public const string FastbootReadInfo = "fastboot.read-info";
        public const string FastbootResetFrp = "fastboot.reset-frp";
        public const string FastbootGetVar = "fastboot.getvar";
        public const string FastbootReboot = "fastboot.reboot";
        public const string FastbootFlash = "fastboot.flash";
        public const string FastbootWipe = "fastboot.wipe";
        public const string FastbootEraseBatch = "fastboot.erase-batch";
        public const string AndroidDiagnosticRun = "diagnostic.android-run";
        public const string SamsungDiagnostic = "vendor.samsung-diagnostic";
        public const string SamsungReadInfoMtp = "samsung.read-info-mtp";
        public const string SamsungRebootDownload = "samsung.reboot-download";
        public const string SamsungRebootSystem = "samsung.reboot-system";
        public const string SamsungOpenBrowser = "samsung.open-browser";
        public const string SamsungFrpTestMode = "samsung.frp-testmode";
        public const string XiaomiReadInfo = "vendor.xiaomi-read-info";
        public const string XiaomiResetFrp = "vendor.xiaomi-reset-frp";
        public const string XiaomiBypassMiAccount = "vendor.xiaomi-bypass-mi-account";
        public const string InfinixReadInfo = "vendor.infinix-read-info";
        public const string InfinixResetFrp = "vendor.infinix-reset-frp";
        public const string InfinixFactoryFormat = "vendor.infinix-factory-format";
        public const string TecnoReadInfo = "vendor.teco-read-info";
        public const string TecnoResetFrp = "vendor.teco-reset-frp";
        public const string TecnoWipe = "vendor.teco-wipe-userdata";
        public const string MediaTekDiagnostic = "vendor.mediatek-diagnostic";
        public const string MtkReadInfo = "mtk.read-info";
        public const string MtkHandshake = "mtk.handshake";
        public const string MtkResetFrp = "mtk.reset-frp";
        public const string MtkFactoryReset = "mtk.factory-reset";
        public const string MtkScatter = "mtk.scatter";
        public const string MtkReboot = "mtk.reboot";
        public const string QualcommDiagnostic = "vendor.qualcomm-diagnostic";
        public const string QualcommReadInfo = "qualcomm.read-info";
        public const string QualcommSahara = "qualcomm.sahara";
        public const string QualcommGpt = "qualcomm.gpt";
        public const string QualcommResetFrp = "qualcomm.reset-frp";
        public const string QualcommFactoryReset = "qualcomm.factory-reset";
        public const string QualcommReboot = "qualcomm.reboot";
        public const string SpreadtrumReadInfo = "vendor.spreadtrum-read-info";
        public const string SpreadtrumResetFrp = "vendor.spreadtrum-reset-frp";
        public const string SpdReadInfo = "spd.read-info";
        public const string SpdHandshake = "spd.handshake";
        public const string SpdFdl = "spd.fdl-load";
        public const string SpdResetFrp = "spd.reset-frp";
        public const string SpdFactoryReset = "spd.factory-reset";
        public const string SpdReboot = "spd.reboot";
        public const string GoogleRecoveryGuidance = "vendor.google-recovery-guidance";
        public const string GoogleLockStatus = "vendor.google-lock-status";
        public const string GoogleOpenDeviceUrl = "vendor.device-open-url";
        public const string ToolProbe = "tool.probe";
        public const string VendorReboot = "vendor.reboot";

        private static readonly OperationTypeDefinition[] Registry =
        [
            new(AdbDevices, "List ADB devices", "قائمة أجهزة ADB", ModuleKeys.Adb, false, true, false, "exit-code-zero", 30),
            new(AdbReadInfo, "Read detailed device information", "قراءة معلومات الجهاز المفصلة", ModuleKeys.Adb, true, true, false, "exit-code-zero", 120),
            new(AdbProperties, "Read device properties", "قراءة خصائص الجهاز", ModuleKeys.Adb, true, true, false, "exit-code-zero", 60),
            new(AdbConnectionCheck, "Check ADB connection", "فحص اتصال ADB", ModuleKeys.Adb, true, true, false, "exit-code-zero", 30),
            new(AdbRestartServer, "Restart ADB server", "إعادة تشغيل خادم ADB", ModuleKeys.Adb, false, true, false, "exit-code-zero", 45),
            new(AdbCollectLogcat, "Collect diagnostic log", "جمع السجل التشخيصي", ModuleKeys.Adb, true, true, false, "exit-code-zero", 90),
            new(AdbReboot, "Reboot device", "إعادة تشغيل الجهاز", ModuleKeys.Adb, true, true, false, "exit-code-zero", 60),
            new(FastbootDevices, "List Fastboot devices", "قائمة أجهزة Fastboot", ModuleKeys.Fastboot, false, true, false, "exit-code-zero", 30),
            new(FastbootReadInfo, "Read all Fastboot variables", "قراءة جميع متغيرات Fastboot", ModuleKeys.Fastboot, true, true, false, "exit-code-zero", 90),
            new(FastbootResetFrp, "Reset FRP (frp, config, persistent)", "إعادة ضبط FRP", ModuleKeys.Fastboot, true, true, true, "exit-code-zero", 300),
            new(FastbootGetVar, "Read fastboot variables", "قراءة متغيرات Fastboot", ModuleKeys.Fastboot, true, true, false, "exit-code-zero", 30),
            new(FastbootReboot, "Reboot from Fastboot", "إعادة التشغيل من Fastboot", ModuleKeys.Fastboot, true, true, false, "exit-code-zero", 45),
            new(FastbootFlash, "Flash an image", "كتابة صورة", ModuleKeys.Fastboot, true, true, true, "exit-code-zero", 600),
            new(FastbootWipe, "Erase a partition", "مسح قسم", ModuleKeys.Fastboot, true, true, true, "exit-code-zero", 300),
            new(FastbootEraseBatch, "Erase a set of partitions", "مسح مجموعة أقسام", ModuleKeys.Fastboot, true, true, true, "exit-code-zero", 600),
            new(AndroidDiagnosticRun, "Run Android diagnostics", "تشغيل تشخيص Android", ModuleKeys.AndroidDiagnostics, true, true, false, "verified", 180),
            new(SamsungDiagnostic, "Samsung device information", "Samsung device information", ModuleKeys.Samsung, true, true, false, "verified", 120),
            new(SamsungReadInfoMtp, "Read Samsung information over MTP", "Read Samsung information over MTP", ModuleKeys.Samsung, false, false, false, "verified", 120),
            new(SamsungRebootDownload, "Reboot Samsung to Download mode", "Reboot Samsung to Download mode", ModuleKeys.Samsung, true, true, false, "exit-code-zero", 90),
            new(SamsungRebootSystem, "Reboot Samsung to system", "Reboot Samsung to system", ModuleKeys.Samsung, true, true, false, "exit-code-zero", 90),
            new(SamsungOpenBrowser, "Open a web page on the Samsung screen", "Open a web page on the Samsung screen", ModuleKeys.Samsung, true, true, false, "exit-code-zero", 60),
            new(SamsungFrpTestMode, "Samsung FRP reset (test mode / ADB)", "Samsung FRP reset (test mode / ADB)", ModuleKeys.Samsung, true, true, true, "exit-code-zero", 300),
            new(XiaomiReadInfo, "Xiaomi read device information", "Xiaomi read device information", ModuleKeys.Xiaomi, true, true, false, "verified", 120),
            new(XiaomiResetFrp, "Xiaomi reset FRP and config", "Xiaomi reset FRP and config", ModuleKeys.Xiaomi, true, true, true, "exit-code-zero", 180),
            new(XiaomiBypassMiAccount, "Xiaomi disable the Mi Account agent over ADB", "Xiaomi disable the Mi Account agent over ADB", ModuleKeys.Xiaomi, true, true, false, "verified", 120),
            new(InfinixReadInfo, "Infinix read device information", "Infinix read device information", ModuleKeys.Infinix, true, true, false, "verified", 120),
            new(InfinixResetFrp, "Infinix reset FRP and persist", "Infinix reset FRP and persist", ModuleKeys.Infinix, true, true, true, "exit-code-zero", 180),
            new(InfinixFactoryFormat, "Infinix factory format", "Infinix factory format", ModuleKeys.Infinix, true, true, true, "exit-code-zero", 420),
            new(TecnoReadInfo, "Tecno read device information", "Tecno read device information", ModuleKeys.Tecno, true, true, false, "verified", 120),
            new(TecnoResetFrp, "Tecno reset FRP", "Tecno reset FRP", ModuleKeys.Tecno, true, true, true, "exit-code-zero", 180),
            new(TecnoWipe, "Tecno wipe userdata and cache", "Tecno wipe userdata and cache", ModuleKeys.Tecno, true, true, true, "exit-code-zero", 420),
            new(MediaTekDiagnostic, "MediaTek interface diagnostics", "MediaTek interface diagnostics", ModuleKeys.MediaTek, true, true, false, "verified", 120),
            new(MtkReadInfo, "Read MediaTek device information", "Read MediaTek device information", ModuleKeys.MediaTek, false, true, false, "verified", 120),
            new(MtkHandshake, "BROM / preloader handshake", "BROM / preloader handshake", ModuleKeys.MediaTek, false, true, false, "verified", 90),
            new(MtkResetFrp, "Reset FRP through fastboot", "Reset FRP through fastboot", ModuleKeys.MediaTek, true, true, true, "exit-code-zero", 600),
            new(MtkFactoryReset, "Factory format (userdata, cache)", "Factory format (userdata, cache)", ModuleKeys.MediaTek, true, true, true, "exit-code-zero", 1200),
            new(MtkScatter, "Parse scatter table and flash", "Parse scatter table and flash", ModuleKeys.MediaTek, false, true, true, "verified", 900),
            new(MtkReboot, "Reboot MediaTek device", "Reboot MediaTek device", ModuleKeys.MediaTek, true, true, false, "exit-code-zero", 90),
            new(QualcommDiagnostic, "Qualcomm interface diagnostics", "Qualcomm interface diagnostics", ModuleKeys.Qualcomm, true, true, false, "verified", 120),
            new(QualcommReadInfo, "Read Qualcomm EDL interfaces and platform", "Read Qualcomm EDL interfaces and platform", ModuleKeys.Qualcomm, false, true, false, "verified", 120),
            new(QualcommSahara, "Sahara handshake and programmer upload", "Sahara handshake and programmer upload", ModuleKeys.Qualcomm, false, true, false, "verified", 240),
            new(QualcommGpt, "Read the GPT partition table", "Read the GPT partition table", ModuleKeys.Qualcomm, false, true, false, "verified", 300),
            new(QualcommResetFrp, "Reset FRP through EDL", "Reset FRP through EDL", ModuleKeys.Qualcomm, false, true, true, "exit-code-zero", 900),
            new(QualcommFactoryReset, "Factory format through EDL (userdata, cache)", "Factory format through EDL (userdata, cache)", ModuleKeys.Qualcomm, false, true, true, "exit-code-zero", 1800),
            new(QualcommReboot, "Power command through EDL", "Power command through EDL", ModuleKeys.Qualcomm, false, true, false, "verified", 180),
            new(SpreadtrumReadInfo, "SPREADTRUM read device information", "SPREADTRUM read device information", ModuleKeys.Spreadtrum, true, true, false, "verified", 120),
            new(SpreadtrumResetFrp, "SPREADTRUM reset FRP", "SPREADTRUM reset FRP", ModuleKeys.Spreadtrum, true, true, true, "exit-code-zero", 180),
            new(SpdReadInfo, "SPD read device information", "SPD read device information", ModuleKeys.Spreadtrum, false, true, false, "verified", 120),
            new(SpdHandshake, "SPD BROM handshake", "SPD BROM handshake", ModuleKeys.Spreadtrum, false, true, false, "verified", 90),
            new(SpdFdl, "SPD FDL1 / FDL2 loader upload", "SPD FDL1 / FDL2 loader upload", ModuleKeys.Spreadtrum, false, true, false, "verified", 600),
            new(SpdResetFrp, "SPD reset FRP (frp, persistent)", "SPD reset FRP", ModuleKeys.Spreadtrum, true, true, true, "exit-code-zero", 300),
            new(SpdFactoryReset, "SPD factory format (userdata, cache)", "SPD factory format", ModuleKeys.Spreadtrum, true, true, true, "exit-code-zero", 900),
            new(SpdReboot, "SPD reboot device", "SPD reboot device", ModuleKeys.Spreadtrum, true, true, false, "exit-code-zero", 120),
            new(GoogleRecoveryGuidance, "Google account recovery guidance", "Google account recovery guidance", ModuleKeys.GoogleRecovery, false, false, false, "verified", 30),
            new(GoogleLockStatus, "Read FRP and lock status", "Read FRP and lock status", ModuleKeys.GoogleRecovery, true, true, false, "verified", 90),
            new(GoogleOpenDeviceUrl, "Open a web page on the device", "Open a web page on the device", ModuleKeys.GoogleRecovery, true, true, false, "exit-code-zero", 60),
            new(ToolProbe, "Probe external tools", "فحص الأدوات الخارجية", ModuleKeys.Settings, false, true, false, "exit-code-zero", 20),
            new(VendorReboot, "Reboot the handset into another mode", "إعادة تشغيل الجهاز في وضع آخر", ModuleKeys.ServiceOperations, true, true, false, "exit-code-zero", 90)
        ];

        public static IReadOnlyList<OperationTypeDefinition> All => Registry;

        public static OperationTypeDefinition? Find(string key)
            => Registry.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));

        public static bool IsKnown(string key) => Find(key) is not null;
    }
}
