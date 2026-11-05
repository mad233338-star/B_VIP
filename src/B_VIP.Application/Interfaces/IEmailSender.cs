namespace B_VIP.Application.Interfaces
{
    /// <summary>One transactional e-mail. HTML is optional; the text body is always present.</summary>
    public sealed record EmailMessage(string To, string Subject, string TextBody, string? HtmlBody = null);

    /// <summary>
    /// Transactional e-mail delivery. The SMTP transport is configured by the administrator
    /// (<c>Email</c> section); when it is not configured the message is written to the server log
    /// so local installations can still complete the verification flow.
    /// </summary>
    public interface IEmailSender
    {
        /// <summary>True when an SMTP host is configured and messages leave the server.</summary>
        bool IsConfigured { get; }

        /// <summary>Sends one message. Returns false when delivery was not possible.</summary>
        Task<bool> SendAsync(EmailMessage message, CancellationToken ct = default);
    }
}
