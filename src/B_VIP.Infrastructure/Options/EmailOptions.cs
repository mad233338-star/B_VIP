namespace B_VIP.Infrastructure.Options
{
    /// <summary>
    /// Transactional e-mail configuration (<c>Email</c> section). Any SMTP provider works: the
    /// host, port and credentials come from configuration or environment variables, so no
    /// provider-specific SDK is required.
    /// </summary>
    public sealed class EmailOptions
    {
        public const string SectionName = "Email";

        public string? Host { get; set; }

        public int Port { get; set; } = 587;

        public string? UserName { get; set; }

        public string? Password { get; set; }

        /// <summary>Sender address. Delivery is disabled when this or <see cref="Host"/> is empty.</summary>
        public string? From { get; set; }

        public string? FromName { get; set; } = "B_VIP Tool";

        public bool EnableSsl { get; set; } = true;

        /// <summary>Public base address used inside activation links.</summary>
        public string PublicBaseUrl { get; set; } = "https://b-vip.onrender.com";

        /// <summary>
        /// When true, the registration answer also contains the activation link. Keep this off in
        /// production so the token is only delivered by e-mail.
        /// </summary>
        public bool ExposeVerificationLink { get; set; }

        /// <summary>Lifetime of an activation link in hours.</summary>
        public int VerificationTokenHours { get; set; } = 48;
    }
}
