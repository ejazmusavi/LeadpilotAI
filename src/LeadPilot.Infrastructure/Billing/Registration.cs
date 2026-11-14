using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Billing;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Billing;
using LeadPilot.Domain.Messaging;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Identity;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Services.Tenancy;
using LeadPilot.Infrastructure.Tenancy;
using LeadPilot.Shared.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Billing;

/// <summary>Default CAPTCHA port implementation: none is installed (no provider was chosen). When the platform setting <c>registration.captcha_required</c> is on, registration is REFUSED rather than skipping the check.</summary>
public sealed class NotConfiguredCaptchaVerifier : ICaptchaVerifier
{
    public bool IsConfigured => false;
    public Task<bool> VerifyAsync(string? token, string? ipAddress, CancellationToken ct = default) => Task.FromResult(false);
}

/// <summary>
/// Default disposable-domain policy: a built-in seed list of well-known throw-away mailbox providers plus the platform setting <c>registration.blocked_domains</c> (comma, space or line separated; a leading
/// "*." or "@" is ignored; subdomains of a listed domain are blocked too). The seed list is NOT exhaustive: swap the port for a maintained feed in production.
/// </summary>
public sealed class DefaultDisposableDomainPolicy(IPlatformSettings settings) : IDisposableDomainPolicy
{
    private static readonly HashSet<string> Seed = new(StringComparer.OrdinalIgnoreCase)
    {
        "mailinator.com", "guerrillamail.com", "guerrillamail.net", "guerrillamail.org", "10minutemail.com", "10minutemail.net", "tempmail.com", "temp-mail.org", "throwawaymail.com", "yopmail.com", "yopmail.net",
        "trashmail.com", "trashmail.net", "getnada.com", "sharklasers.com", "dispostable.com", "maildrop.cc", "fakeinbox.com", "mintemail.com", "mohmal.com", "emailondeck.com", "spamgourmet.com", "tempinbox.com",
        "burnermail.io", "moakt.com", "mailnesia.com", "mytemp.email", "tempail.com", "discard.email", "spambox.us", "mailcatch.com", "inboxkitten.com",
    };

    public async Task<bool> IsBlockedAsync(string domain, CancellationToken ct = default)
    {
        var d = domain.Trim().ToLowerInvariant().TrimStart('@');
        if (d.Length == 0) return true;
        var extra = ((await settings.GetAsync(PlatformSettingKeys.ExtraBlockedDomains, ct)) ?? "")
            .Split([',', ' ', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().TrimStart('@').TrimStart('*').TrimStart('.').ToLowerInvariant()).ToHashSet();
        for (var candidate = d; candidate.Contains('.'); candidate = candidate[(candidate.IndexOf('.') + 1)..])
            if (Seed.Contains(candidate) || extra.Contains(candidate)) return true;
        return false;
    }
}

public static class RegistrationErrors
{
    public const string Closed = "registration_closed", RateLimited = "rate_limited", DomainBlocked = "email_domain_blocked", CaptchaFailed = "captcha_failed", Unavailable = "registration_unavailable";
}

/// <summary>
/// Public sign-up: creates a tenant, its system roles and defaults, the first administrator (e-mail NOT yet verified) and a subscription on the platform's sign-up plan. Anti-abuse, in order: the platform switch
/// (default OFF), a honeypot field, per-IP and per-domain rate limits (counted in the database so they hold across hosts), the disposable-domain port, the CAPTCHA port, and no account enumeration (an address that
/// already exists gets the same answer). Sensitive features (sending, AI, discovery, campaigns, workflows) stay locked until the e-mail is verified (see <see cref="EntitlementService"/>).
/// </summary>
public sealed class RegistrationService(AppDbContext db, UserManager<ApplicationUser> users, ITenantContextAccessor accessor, IPlatformSettings settings, ICaptchaVerifier captcha, IDisposableDomainPolicy domains,
    IEmailSender email, IConfiguration config, IHttpContextAccessor http, IClock clock, ILogger<RegistrationService> logger) : IRegistrationService
{
    public Task<bool> IsOpenAsync(CancellationToken ct = default) => settings.GetBoolAsync(PlatformSettingKeys.RegistrationEnabled, false, ct);

    private static string Hash(string prefix, string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prefix + value.Trim().ToLowerInvariant())));

    private async Task RecordAsync(string ip, string emailAddress, string domain, RegistrationOutcome outcome, string? reason, CancellationToken ct)
    {
        db.RegistrationAttempts.Add(RegistrationAttempt.Create(Hash("ip:", ip), Hash("email:", emailAddress), domain, outcome, reason, clock.UtcNow));
        await db.SaveChangesAsync(ct);
    }

    public async Task<Result<RegisterResult>> RegisterAsync(RegisterRequest r, RegistrationContext context, CancellationToken ct = default)
    {
        var now = clock.UtcNow; var ip = context.IpAddress ?? "unknown";
        if (!await IsOpenAsync(ct)) return Result.Failure<RegisterResult>(new Error(RegistrationErrors.Closed, "Sign-up is currently closed. Contact the platform owner for an invitation."));
        var emailAddress = (r.Email ?? "").Trim();
        var domain = emailAddress.Contains('@') ? emailAddress[(emailAddress.LastIndexOf('@') + 1)..].ToLowerInvariant() : "";

        // honeypot: a field no human sees. Pretend it worked, create nothing, and let the rate limiter see the attempt.
        if (!string.IsNullOrWhiteSpace(r.Website))
        {
            await RecordAsync(ip, emailAddress, domain, RegistrationOutcome.Blocked, "honeypot", ct);
            return Result.Success(new RegisterResult(Guid.Empty, string.Empty, false));
        }

        var ipHash = Hash("ip:", ip); var since = now.AddHours(-1);
        var perIp = await settings.GetIntAsync(PlatformSettingKeys.MaxRegistrationsPerIpPerHour, 3, ct);
        if (await db.RegistrationAttempts.CountAsync(a => a.IpHash == ipHash && a.CreatedAtUtc >= since, ct) >= perIp)
        {
            await RecordAsync(ip, emailAddress, domain, RegistrationOutcome.RateLimited, "ip", ct);
            return Result.Failure<RegisterResult>(new Error(RegistrationErrors.RateLimited, "Too many sign-up attempts from this address. Try again later."));
        }
        var perDomain = await settings.GetIntAsync(PlatformSettingKeys.MaxRegistrationsPerDomainPerDay, 20, ct);
        var daySince = now.AddDays(-1);
        if (domain.Length > 0 && await db.RegistrationAttempts.CountAsync(a => a.EmailDomain == domain && a.Outcome == RegistrationOutcome.Accepted && a.CreatedAtUtc >= daySince, ct) >= perDomain)
        {
            await RecordAsync(ip, emailAddress, domain, RegistrationOutcome.RateLimited, "domain", ct);
            return Result.Failure<RegisterResult>(new Error(RegistrationErrors.RateLimited, "Too many workspaces were created from this e-mail domain today. Contact the platform owner."));
        }

        string? invalid = Validate(r, emailAddress);
        if (invalid is not null) { await RecordAsync(ip, emailAddress, domain, RegistrationOutcome.Invalid, "validation", ct); return Result.Failure<RegisterResult>(Error.Validation(invalid)); }
        if (await domains.IsBlockedAsync(domain, ct))
        {
            await RecordAsync(ip, emailAddress, domain, RegistrationOutcome.Blocked, "disposable-domain", ct);
            return Result.Failure<RegisterResult>(new Error(RegistrationErrors.DomainBlocked, "Please use a permanent (work) e-mail address: temporary mailbox providers are not accepted."));
        }
        if (await settings.GetBoolAsync(PlatformSettingKeys.CaptchaRequired, false, ct))
        {
            if (!captcha.IsConfigured) return Result.Failure<RegisterResult>(new Error(RegistrationErrors.Unavailable, "Sign-up needs a CAPTCHA but none is installed on this platform. Contact the platform owner."));
            if (!await captcha.VerifyAsync(r.CaptchaToken, ip, ct)) { await RecordAsync(ip, emailAddress, domain, RegistrationOutcome.Blocked, "captcha", ct); return Result.Failure<RegisterResult>(new Error(RegistrationErrors.CaptchaFailed, "The CAPTCHA could not be verified. Try again.")); }
        }

        var planId = Guid.TryParse(await settings.GetAsync(PlatformSettingKeys.RegistrationPlanId, ct), out var pid) ? pid : (Guid?)null;
        var plan = planId is null ? null : await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId && p.Status == PlanStatus.Active, ct);
        if (plan is null || plan.Price != 0) return Result.Failure<RegisterResult>(new Error(RegistrationErrors.Unavailable, "Sign-up is not configured yet (no free or trial plan is selected for new workspaces)."));

        // no account enumeration: an existing address gets exactly the same answer and nothing is created
        if (await users.FindByEmailAsync(emailAddress) is not null)
        {
            await RecordAsync(ip, emailAddress, domain, RegistrationOutcome.Duplicate, null, ct);
            return Result.Success(new RegisterResult(Guid.Empty, string.Empty, false));
        }

        ApplicationUser user; Tenant tenant;
        using (accessor.BeginPlatformScope())
        {
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            try
            {
                tenant = Tenant.Create(r.WorkspaceName, await UniqueSlugAsync(r.WorkspaceName, ct), null, r.Timezone, null, now);
                tenant.MarkSelfRegistered();
                db.Tenants.Add(tenant);
                var permissions = await RoleProvisioner.SyncPermissionsAsync(db, ct);
                await RoleProvisioner.SyncSystemRolesAsync(db, tenant, permissions, now, ct);
                Services.Crm.CrmDefaults.AddAll(db, tenant.Id, now);
                db.TenantSubscriptions.Add(TenantSubscription.StartFreeOrTrial(tenant.Id, await db.Plans.Include(p => p.Limits).FirstAsync(p => p.Id == plan.Id, ct), now, emailAddress));
                db.SubscriptionEvents.Add(SubscriptionEvent.Create(tenant.Id, "Started", $"Subscription started on plan '{plan.Name}' through self-registration.", null, plan.Id, emailAddress, now));
                await db.SaveChangesAsync(ct);

                user = new ApplicationUser { UserName = emailAddress, Email = emailAddress, EmailConfirmed = false, DisplayName = r.DisplayName.Trim(), CreatedAtUtc = now };
                var created = await users.CreateAsync(user, r.Password);
                if (!created.Succeeded)
                {
                    await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
                    await RecordAsync(ip, emailAddress, domain, RegistrationOutcome.Invalid, "password", ct);
                    return Result.Failure<RegisterResult>(Error.Validation(string.Join(" ", created.Errors.Select(e => e.Description))));
                }
                var adminRole = db.TenantRoles.Local.First(x => x.TenantId == tenant.Id && x.Name == SystemRoles.TenantAdministrator);
                db.UserTenants.Add(UserTenant.Create(tenant.Id, user.Id, isDefault: true, invitedBy: null, now));
                db.TenantUserRoles.Add(UserRole.Create(tenant.Id, user.Id, adminRole.Id, null, now));
                db.UserProfiles.Add(UserProfile.Create(user.Id, user.DisplayName, null, now));
                db.AuditLogs.Add(AuditLog.Create(tenant.Id, null, user.Id, emailAddress, "Tenant.Registered", "Tenant", tenant.Id.ToString(), $"Workspace '{tenant.Name}' ({tenant.Slug}) created through self-registration on plan '{plan.Name}'; e-mail verification pending.", ip, null, now));
                db.RegistrationAttempts.Add(RegistrationAttempt.Create(ipHash, Hash("email:", emailAddress), domain, RegistrationOutcome.Accepted, null, now));
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (ResultFlow.IsDuplicate(ex))
            {
                await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
                return Result.Success(new RegisterResult(Guid.Empty, string.Empty, false));   // a concurrent identical sign-up won
            }
            catch (LeadPilot.Domain.Common.DomainException ex)
            {
                await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
                return Result.Failure<RegisterResult>(Error.Validation(ex.Message));
            }
        }
        var sent = await SendVerificationAsync(user, tenant.Name, ct);
        return Result.Success(new RegisterResult(tenant.Id, tenant.Slug, sent));
    }

    private static string? Validate(RegisterRequest r, string emailAddress)
    {
        if (string.IsNullOrWhiteSpace(r.WorkspaceName) || r.WorkspaceName.Trim().Length > 200) return "Enter a workspace name (up to 200 characters).";
        if (string.IsNullOrWhiteSpace(r.DisplayName) || r.DisplayName.Trim().Length > 200) return "Enter your name (up to 200 characters).";
        if (emailAddress.Length is < 5 or > 254 || !MailAddress.TryCreate(emailAddress, out var addr) || addr.Address != emailAddress || !addr.Host.Contains('.')) return "Enter a valid e-mail address.";
        if (string.IsNullOrEmpty(r.Password)) return "Choose a password.";
        return null;
    }

    private async Task<string> UniqueSlugAsync(string name, CancellationToken ct)
    {
        var baseSlug = Tenant.SlugFromName(name);
        if (baseSlug.Length < 3) baseSlug = ("ws-" + baseSlug).Trim('-');
        for (var i = 0; i < 8; i++)
        {
            var candidate = i == 0 ? baseSlug : $"{baseSlug[..Math.Min(baseSlug.Length, 56)]}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}";
            if (Tenant.IsValidSlug(candidate) && !await db.Tenants.AnyAsync(t => t.Slug == candidate, ct)) { try { _ = Tenant.Create("x", candidate, null, null, null, DateTime.UtcNow); return candidate; } catch (LeadPilot.Domain.Common.DomainException) { /* reserved word: try a suffixed one */ } }
        }
        return $"ws-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}";
    }

    private async Task<bool> SendVerificationAsync(ApplicationUser user, string workspaceName, CancellationToken ct)
    {
        try
        {
            var token = await users.GenerateEmailConfirmationTokenAsync(user);
            var baseUrl = config["App:PublicWebUrl"];
            if (string.IsNullOrWhiteSpace(baseUrl)) { var req = http.HttpContext?.Request; baseUrl = req is null ? "http://localhost:5002" : $"{req.Scheme}://{req.Host}"; }
            var link = $"{baseUrl.TrimEnd('/')}/Register/Verify?userId={user.Id}&token={Uri.EscapeDataString(token)}";
            var html = $"<p>Welcome to LeadPilot AI! Confirm your e-mail address to unlock messaging, AI and discovery in <strong>{WebUtility.HtmlEncode(workspaceName)}</strong>.</p>" +
                       $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">Confirm my e-mail address</a></p><p>The link works once and expires after a short time; you can request a new one on the sign-in page. If you did not sign up, ignore this e-mail.</p>";
            await email.SendAsync(new EmailMessage(user.Email!, "Confirm your e-mail address for LeadPilot AI", html), ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Verification e-mail for user {UserId} could not be sent", user.Id); return false; }
    }

    public async Task<Result> VerifyEmailAsync(Guid userId, string token, CancellationToken ct = default)
    {
        var invalid = Result.Failure(Error.Validation("This verification link is invalid or has expired. Request a new one."));
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(token)) return invalid;
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return invalid;
        var confirmed = await users.ConfirmEmailAsync(user, token);
        if (!confirmed.Succeeded) return invalid;
        var now = clock.UtcNow;
        using var _ = accessor.BeginPlatformScope();
        var memberOf = await db.UserTenants.IgnoreQueryFilters().AsNoTracking().Where(m => m.UserId == userId).Select(m => m.TenantId).ToListAsync(ct);
        foreach (var t in await db.Tenants.Where(t => memberOf.Contains(t.Id) && t.IsSelfRegistered && t.OwnerEmailVerifiedAtUtc == null).ToListAsync(ct))
        {
            t.VerifyOwnerEmail(now);
            db.AuditLogs.Add(AuditLog.Create(t.Id, null, userId, user.Email, "Tenant.EmailVerified", "Tenant", t.Id.ToString(), "The owner verified the e-mail address; sensitive features are unlocked.", null, null, now));
        }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> ResendVerificationAsync(string emailAddress, RegistrationContext context, CancellationToken ct = default)
    {
        var now = clock.UtcNow; var ip = context.IpAddress ?? "unknown"; var addr = (emailAddress ?? "").Trim();
        var ipHash = Hash("ip:", ip); var emailHash = Hash("email:", addr); var since = now.AddHours(-1);
        var limit = await settings.GetIntAsync(PlatformSettingKeys.MaxRegistrationsPerIpPerHour, 3, ct);
        var recent = await db.RegistrationAttempts.CountAsync(a => (a.IpHash == ipHash || a.EmailHash == emailHash) && a.CreatedAtUtc >= since, ct);
        await RecordAsync(ip, addr, addr.Contains('@') ? addr[(addr.LastIndexOf('@') + 1)..] : "", recent >= limit ? RegistrationOutcome.RateLimited : RegistrationOutcome.Accepted, "resend", ct);
        if (recent >= limit) return Result.Failure(new Error(RegistrationErrors.RateLimited, "Too many requests. Try again later."));
        var user = addr.Length == 0 ? null : await users.FindByEmailAsync(addr);
        if (user is { EmailConfirmed: false, IsActive: true })
        {
            using var _ = accessor.BeginPlatformScope();
            var tenantName = await (from m in db.UserTenants.IgnoreQueryFilters().AsNoTracking() join t in db.Tenants.AsNoTracking() on m.TenantId equals t.Id where m.UserId == user.Id && t.IsSelfRegistered select t.Name).FirstOrDefaultAsync(ct);
            if (tenantName is not null) await SendVerificationAsync(user, tenantName, ct);
        }
        return Result.Success();   // the same answer for unknown, verified and pending addresses
    }
}

/// <summary>The guided checklist of a new workspace. Every step is computed from real rows of the active tenant and links to the existing page where it is done; nothing is stored as "ticked".</summary>
public sealed class OnboardingService(AppDbContext db, ITenantContext tenant) : IOnboardingService
{
    public async Task<OnboardingOverview> GetAsync(CancellationToken ct = default)
    {
        var t = tenant.RequireTenantId();
        var tn = await db.Tenants.AsNoTracking().Where(x => x.Id == t).Select(x => new { x.IsSelfRegistered, x.OwnerEmailVerifiedAtUtc }).FirstAsync(ct);
        var members = await db.UserTenants.AsNoTracking().CountAsync(m => m.TenantId == t && m.Status == MembershipStatus.Active, ct);
        var invites = await db.TenantInvitations.AsNoTracking().CountAsync(i => i.TenantId == t, ct);
        var accounts = await db.ChannelAccounts.AsNoTracking().Where(a => a.TenantId == t && !a.IsDeleted).Select(a => a.ConnectionStatus).ToListAsync(ct);
        var ai = await db.AiTenantSettings.AsNoTracking().AnyAsync(s => s.TenantId == t && s.ProtectedApiKey != null, ct);
        var docs = await db.KnowledgeDocuments.AsNoTracking().CountAsync(d => d.TenantId == t, ct);
        var products = await db.CatalogProducts.AsNoTracking().CountAsync(p => p.TenantId == t, ct);
        var leads = await db.Leads.AsNoTracking().CountAsync(l => l.TenantId == t, ct);
        var campaigns = await db.Campaigns.AsNoTracking().CountAsync(c => c.TenantId == t, ct);

        var steps = new List<OnboardingStep>();
        if (tn.IsSelfRegistered) steps.Add(new("verify_email", "Verify your e-mail address", "Unlocks messaging, AI, discovery, campaigns and workflows.", tn.OwnerEmailVerifiedAtUtc is not null, "Register", "Resend", tn.OwnerEmailVerifiedAtUtc is null ? "Check your inbox or request a new link." : null));
        steps.Add(new("whatsapp", "Connect a WhatsApp account", "Add your gateway account and scan the QR code.", accounts.Any(s => s == ConnectionStatus.Working), "Channels", "Index", accounts.Count == 0 ? "No account added yet." : accounts.Any(s => s == ConnectionStatus.Working) ? null : $"{accounts.Count} added, none connected yet."));
        steps.Add(new("ai", "Configure the AI assistant", "Store your AI provider key and turn the assistant on.", ai, "AiSettings", "Index", null));
        steps.Add(new("knowledge", "Add knowledge", "Give the assistant documents to answer from.", docs > 0, "Knowledge", "Index", docs > 0 ? $"{docs} document(s)." : null));
        steps.Add(new("catalog", "Create your product catalog", "Products and prices the assistant may quote.", products > 0, "Catalog", "Index", products > 0 ? $"{products} product(s)." : null));
        steps.Add(new("team", "Invite your team", "Add colleagues with the right role.", members > 1 || invites > 0, "Invitations", "Index", $"{members} member(s), {invites} invitation(s)."));
        steps.Add(new("leads", "Add your first leads", "Import a file, add a lead or run a discovery search.", leads > 0, "Leads", "Index", leads > 0 ? $"{leads} lead(s)." : null));
        steps.Add(new("campaign", "Create your first campaign", "Reach your audience (messages respect consent and opt-outs).", campaigns > 0, "Campaigns", "Index", campaigns > 0 ? $"{campaigns} campaign(s)." : null));
        var done = steps.Count(s => s.Done);
        return new OnboardingOverview(steps, done, done == steps.Count);
    }
}
