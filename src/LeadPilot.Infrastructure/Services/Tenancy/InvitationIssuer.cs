using System.Net;
using LeadPilot.Application.Abstractions;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Tenancy;
using Microsoft.Extensions.Configuration;

namespace LeadPilot.Infrastructure.Services.Tenancy;

/// <summary>Creates invitation rows (token hashed) and sends the e-mail. Shared by tenant provisioning and the invitation service.</summary>
public sealed class InvitationIssuer(AppDbContext db, IClock clock, IConfiguration config, IEmailSender email, IInvitationLinkBuilder links)
{
    public int ExpiryDays => Math.Clamp(config.GetValue("Invitations:ExpiryDays", 7), 1, 30);

    public (TenantInvitation Invitation, string RawToken) Create(Guid tenantId, string emailAddress, Guid roleId, Guid? invitedBy)
    {
        var raw = TokenHasher.NewToken();
        var now = clock.UtcNow;
        var inv = TenantInvitation.Create(tenantId, emailAddress, roleId, TokenHasher.Hash(raw), now.AddDays(ExpiryDays), invitedBy, now);
        db.TenantInvitations.Add(inv);
        return (inv, raw);
    }

    public Task SendAsync(string tenantName, string roleName, TenantInvitation inv, string rawToken, string? inviterName, CancellationToken ct)
    {
        var link = links.Build(rawToken);
        var who = string.IsNullOrWhiteSpace(inviterName) ? "An administrator" : WebUtility.HtmlEncode(inviterName);
        var html = $"<p>{who} invited you to join <strong>{WebUtility.HtmlEncode(tenantName)}</strong> on LeadPilot AI as <strong>{WebUtility.HtmlEncode(roleName)}</strong>.</p>" +
                   $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">Accept the invitation</a></p>" +
                   $"<p>This link expires on {inv.ExpiresAtUtc:yyyy-MM-dd HH:mm} UTC and can be used once. If you were not expecting it, ignore this e-mail.</p>";
        return email.SendAsync(new EmailMessage(inv.Email, $"You're invited to {tenantName} on LeadPilot AI", html), ct);
    }
}
