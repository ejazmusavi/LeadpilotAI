namespace LeadPilot.Application.Abstractions;

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed record EmailMessage(string To, string Subject, string HtmlBody);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
