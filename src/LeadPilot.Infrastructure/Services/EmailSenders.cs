using System.Net;
using System.Net.Mail;
using LeadPilot.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Services;

public sealed class SmtpOptions
{
    public const string Section = "Email:Smtp";
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "no-reply@leadpilot.local";
}

/// <summary>Real SMTP delivery. Fails loudly when SMTP is not configured (no silent success).</summary>
public sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Host))
            throw new InvalidOperationException("SMTP is not configured. Set Email:Smtp:Host (and credentials) to send email.");

        using var client = new SmtpClient(o.Host, o.Port) { EnableSsl = o.EnableSsl };
        if (!string.IsNullOrEmpty(o.User)) client.Credentials = new NetworkCredential(o.User, o.Password);
        using var mail = new MailMessage(o.From, message.To, message.Subject, message.HtmlBody) { IsBodyHtml = true };
        await client.SendMailAsync(mail, cancellationToken);
    }
}

/// <summary>DEVELOPMENT ONLY: writes the email to the log instead of sending it. Never registered outside Development.</summary>
public sealed class DevelopmentLogEmailSender(ILogger<DevelopmentLogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("[DEV EMAIL - not sent] To={To} Subject={Subject} Body={Body}", message.To, message.Subject, message.HtmlBody);
        return Task.CompletedTask;
    }
}
