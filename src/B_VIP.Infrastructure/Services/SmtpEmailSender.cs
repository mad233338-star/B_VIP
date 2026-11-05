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

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_options.Host) && !string.IsNullOrWhiteSpace(_options.From);

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

                using SmtpClient client = new(_options.Host, _options.Port)
                {
                    EnableSsl = _options.EnableSsl,
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    // A relay that does not answer must fail the request quickly instead of holding
                    // the registration response open until the platform timeout.
                    Timeout = 15000
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
