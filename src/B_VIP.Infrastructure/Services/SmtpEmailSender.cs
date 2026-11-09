using System.Net;
using System.Net.Mail;
using B_VIP.Application.Interfaces;
using B_VIP.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B_VIP.Infrastructure.Services
{
    /// <summary>
    /// Sends transactional e-mail over SMTP. When no host or sender address is configured the
    /// message is written to the server log instead, so a local installation can still complete
    /// the verification flow without an e-mail account.
    /// </summary>
    public sealed class SmtpEmailSender : IEmailSender
    {
        private readonly EmailOptions _options;
        private readonly ILogger<SmtpEmailSender> _logger;

        public SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        /// <summary>How long one SMTP conversation may take before it is abandoned.</summary>
        public const int DeliveryTimeoutMilliseconds = 8000;

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_options.Host) && !string.IsNullOrWhiteSpace(_options.From);

        /// <summary>
        /// The relay port, read from <c>Email__Port</c> first so a hosting platform can point the
        /// same build at 587 or 465 without editing configuration files.
        /// </summary>
        private int ResolvePort()
        {
            string? configured = Environment.GetEnvironmentVariable("Email__Port");
            if (int.TryParse(configured, out int port) && port > 0 && port <= 65535)
            {
                return port;
            }

            return _options.Port is > 0 and <= 65535 ? _options.Port : 587;
        }

        /// <summary>STARTTLS on/off from <c>Email__EnableSsl</c>, falling back to configuration.</summary>
        private bool ResolveEnableSsl()
        {
            string? configured = Environment.GetEnvironmentVariable("Email__EnableSsl");
            return bool.TryParse(configured, out bool enableSsl) ? enableSsl : _options.EnableSsl;
        }

        public async Task<bool> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(message);

            if (!IsConfigured)
            {
                _logger.LogWarning(
                    "E-mail delivery is not configured. Message for {To} ({Subject}) was written to the log instead: {Body}",
                    message.To, message.Subject, message.TextBody);
                return false;
            }

            try
            {
                using MailMessage mail = new()
                {
                    From = new MailAddress(_options.From!, string.IsNullOrWhiteSpace(_options.FromName) ? "B_VIP Tool" : _options.FromName),
                    Subject = message.Subject,
                    Body = message.TextBody,
                    IsBodyHtml = false
                };

                mail.To.Add(message.To);

                if (!string.IsNullOrWhiteSpace(message.HtmlBody))
                {
                    mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.TextBody, null, "text/plain"));
                    mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.HtmlBody, null, "text/html"));
                }

                using SmtpClient client = new(_options.Host, ResolvePort())
                {
                    EnableSsl = ResolveEnableSsl(),
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    // A relay that does not answer must fail quickly instead of holding a socket
                    // open until the platform timeout.
                    Timeout = DeliveryTimeoutMilliseconds
                };

                if (!string.IsNullOrWhiteSpace(_options.UserName))
                {
                    client.Credentials = new NetworkCredential(_options.UserName, _options.Password);
                }

                await client.SendMailAsync(mail, ct);
                _logger.LogInformation("Transactional e-mail '{Subject}' sent to {To}.", message.Subject, message.To);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                // Reachability problems are reported once, without the stack trace noise, because
                // the client turns this into the 'could not send' notice and the local fallback.
                _logger.LogWarning("SMTP relay {Host}:{Port} is unreachable ({Reason}). The message for {To} was not sent.",
                    _options.Host, _options.Port, ex.SocketErrorCode, message.To);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Transactional e-mail '{Subject}' to {To} could not be sent.", message.Subject, message.To);
                return false;
            }
        }
    }
}
