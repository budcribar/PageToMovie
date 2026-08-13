using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>SMTP sender when <see cref="MailOptions.SmtpHost"/> is configured.</summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly MailOptions _mail;
    private readonly ILogger<SmtpEmailSender> _log;

    public SmtpEmailSender(IOptions<PageToMovieOptions> opts, ILogger<SmtpEmailSender> log)
    {
        _mail = opts.Value.Mail ?? new MailOptions();
        _log = log;
    }

    public async Task SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        string? textBody = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_mail.SmtpHost))
            throw new InvalidOperationException("SMTP host is not configured.");

        using var msg = new MailMessage
        {
            From = new MailAddress(
                string.IsNullOrWhiteSpace(_mail.FromAddress) ? "noreply@localhost" : _mail.FromAddress.Trim(),
                string.IsNullOrWhiteSpace(_mail.FromName) ? "PageToMovie" : _mail.FromName.Trim()),
            Subject = subject ?? "",
            Body = htmlBody ?? textBody ?? "",
            IsBodyHtml = !string.IsNullOrWhiteSpace(htmlBody),
        };
        msg.To.Add(toEmail.Trim());
        if (!string.IsNullOrWhiteSpace(textBody) && msg.IsBodyHtml)
            msg.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(textBody, null, "text/plain"));

        using var client = new SmtpClient(_mail.SmtpHost.Trim(), _mail.SmtpPort <= 0 ? 587 : _mail.SmtpPort)
        {
            EnableSsl = _mail.SmtpUseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        if (!string.IsNullOrWhiteSpace(_mail.SmtpUser))
            client.Credentials = new NetworkCredential(_mail.SmtpUser, _mail.SmtpPassword ?? "");

        try
        {
            await client.SendMailAsync(msg, ct).ConfigureAwait(false);
            _log.LogInformation("SMTP email sent To={To} Subject={Subject}", toEmail, subject);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"SMTP send failed To={toEmail}", ex);
        }
    }
}
