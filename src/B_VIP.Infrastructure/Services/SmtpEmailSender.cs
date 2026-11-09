using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using B_VIP.Application.Interfaces;
using B_VIP.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B_VIP.Infrastructure.Services
{
    /// <summary>
    /// Sends transactional e-mail. When <c>Email__WebhookUrl</c> is set the message is posted to
    /// that endpoint as JSON over HTTPS (one POST on port 443), which keeps delivery working on
    /// networks that filter outbound SMTP ports. Otherwise the message goes out over SMTP, and when
    /// neither transport is configured it is written to the server log so a local installation can
    /// still complete the verification flow without an e-mail account.
    /// </summary>
    public sealed class SmtpEmailSender : IEmailSender
    {
        /// <summary>Environment variable holding the mail-relay webhook endpoint.</summary>
        public const string WebhookVariable = "Email__WebhookUrl";

        private static readonly HttpClient WebhookClient = new() { Timeout = TimeSpan.FromSeconds(8) };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

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
            ResolveWebhookUrl() is not null ||
            (!string.IsNullOrWhiteSpace(_options.Host) && !string.IsNullOrWhiteSpace(_options.From));

        /// <summary>The webhook endpoint from <c>Email__WebhookUrl</c>, or null when it is unset.</summary>
        private static string? ResolveWebhookUrl()
        {
            string? configured = Environment.GetEnvironmentVariable(WebhookVariable);
            return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
        }

        /// <summary>
        /// Posts the message to the mail webhook as <c>{ to, subject, body }</c>. The HTML body is
        /// preferred so the recipient sees the designed card instead of the plain-text fallback.
        /// </summary>
        private async Task<bool> SendViaWebhookAsync(string url, EmailMessage message, CancellationToken ct)
        {
            string body = string.IsNullOrWhiteSpace(message.HtmlBody) ? message.TextBody : message.HtmlBody!;
            // The relay receives the HTML verbatim: escaping every tag would still parse, but the
            // literal markup is what an operator sees when inspecting the webhook payload.
            string json = JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    ["to"] = message.To,
                    ["subject"] = message.Subject,
                    ["body"] = body
                },
                JsonOptions);

            try
            {
                using StringContent content = new(json, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await WebhookClient.PostAsync(url, content, ct).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Transactional e-mail '{Subject}' posted to the mail webhook for {To}.", message.Subject, message.To);
                    return true;
                }

                _logger.LogWarning("The mail webhook answered HTTP {Status} for {To}; '{Subject}' was not delivered.",
                    (int)response.StatusCode, message.To, message.Subject);
                return false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The mail webhook could not be reached for {To}; '{Subject}' was not delivered.",
                    message.To, message.Subject);
                return false;
            }
        }

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

            string? webhookUrl = ResolveWebhookUrl();
            if (webhookUrl is not null)
            {
                return await SendViaWebhookAsync(webhookUrl, message, ct).ConfigureAwait(false);
            }

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
