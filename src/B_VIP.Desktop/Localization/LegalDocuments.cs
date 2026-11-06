namespace B_VIP.Desktop.Localization
{
    /// <summary>
    /// The legal texts shipped inside the application. They are compiled in, so the operator can
    /// read them without a network round-trip, and the same strings feed the sidebar pages and the
    /// registration dialog.
    /// </summary>
    public static class LegalDocuments
    {
        public const string TermsTitle = "Terms & Conditions";

        public const string PrivacyTitle = "Privacy Policy";

        public const string Terms = """
            B_VIP TOOL - END USER TERMS & CONDITIONS
            Last Updated: November 2026

            1. SCOPE AND ELIGIBILITY
            B_VIP Tool is a professional desktop maintenance toolkit engineered exclusively for certified mobile device service technicians and authorized workshop personnel. By registering an account or executing any operations, you affirm that you possess the technical competence and legal authorization to service connected devices.

            2. AUTHORIZED USE AND COMPLIANCE WITH LAW
            - Technicians agree to utilize this software solely on devices legitimately owned by the technician or submitted by clients with verified ownership authorization.
            - Tampering with International Mobile Equipment Identity (IMEI) numbers, blacklisted devices, or processing stolen/reported lost hardware is strictly prohibited and constitutes an immediate violation of these terms.
            - The user assumes full civil, commercial, and criminal liability for all operations executed via their registered account credentials.

            3. DEVICE INTEGRITY AND DATA LOSS DISCLAIMER
            Hardware maintenance procedures, including factory formatting, FRP resets, partition wiping, and firmware flashing, inherently involve the risk of irreversible data deletion or device bricking. The technician must ensure critical user data is backed up whenever possible. B_VIP Software and its developers disclaim any liability for hardware malfunction, data loss, or service interruption arising from operational use.

            4. LICENSING, TRIAL PERIOD, AND SUBSCRIPTIONS
            - New registrations receive an automatic 72-hour trial granting full access to maintenance operations.
            - Upon expiration of the trial period, account sign-in remains active, while device operations are locked until administrative authorization or license renewal.
            - Multi-station license sharing, decompilation, binary cracking, or reverse-engineering of the executable and its communication protocols will lead to permanent hardware/account termination.
            """;

        public const string Privacy = """
            B_VIP TOOL - GLOBAL PRIVACY POLICY
            Last Updated: November 2026

            1. COLLECTED TECHNICAL TELEMETRY
            To authenticate licenses and prevent fraudulent distribution, B_VIP Tool collects only minimal operational telemetry:
            - Registered email address and technician username.
            - Unique hardware identifiers (HWID / CPU ID) to bind the authorized license to a single workstation.
            - Standard execution timestamps and operation types (e.g., ADB / Fastboot commands executed) for diagnostic audit logs in the administrative dashboard.

            2. STRICT ZERO-CLIENT-DATA POLICY
            - B_VIP Tool NEVER reads, inspects, extracts, uploads, or stores any personal client data residing on connected mobile devices (including contacts, SMS, photos, accounts, or personal files).
            - All servicing commands and communication protocols (MTP, ADB, Fastboot) operate strictly as a local bridge between the workstation and the connected handset.

            3. NETWORK SECURITY & ENCRYPTION
            All license validations, session handshakes, and diagnostic logs transmitted to our cloud servers (https://b-vip.onrender.com) are encrypted using industry-standard TLS/SSL encryption protocols.

            4. POLICY AMENDMENTS
            We reserve the right to revise this policy as technical and legal frameworks evolve. Technicians will receive notifications of material changes via the in-app Notifications center.
            """;

        /// <summary>The text for a document key: <c>terms</c> or <c>privacy</c>.</summary>
        public static string For(string key) => string.Equals(key, "privacy", StringComparison.OrdinalIgnoreCase)
            ? Privacy
            : Terms;

        /// <summary>The window/page title for a document key.</summary>
        public static string TitleFor(string key) => string.Equals(key, "privacy", StringComparison.OrdinalIgnoreCase)
            ? PrivacyTitle
            : TermsTitle;
    }
}
