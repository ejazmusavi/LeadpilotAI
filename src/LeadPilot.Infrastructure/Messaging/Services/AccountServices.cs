using LeadPilot.Application.Billing;
using LeadPilot.Domain.Billing;
using LeadPilot.Application.Campaigns;
using System.Security.Cryptography;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Messaging.Waha;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenancyResultFlow = LeadPilot.Infrastructure.Services.Tenancy.ResultFlow;

namespace LeadPilot.Infrastructure.Messaging.Services;

/// <summary>
/// Encrypts gateway credentials with the platform secret protector AND binds every ciphertext to its tenant and account: the plaintext is "tenant:account:value", so a
/// row copied or swapped between tenants/accounts fails to open instead of silently using another tenant's key.
/// </summary>
public sealed class CredentialVault(ISecretProtector protector)
{
    public string Seal(Guid tenantId, Guid accountId, string value) => protector.Protect($"{tenantId:N}:{accountId:N}:{value}");

    public string Open(Guid tenantId, Guid accountId, string sealedValue)
    {
        var plain = protector.Unprotect(sealedValue);
        var prefix = $"{tenantId:N}:{accountId:N}:";
        if (!plain.StartsWith(prefix, StringComparison.Ordinal)) throw new CrossTenantViolationException("A stored credential does not belong to this tenant/account.");
        return plain[prefix.Length..];
    }
}

/// <summary>Loads an account of the ACTIVE tenant together with its decrypted connection. The only place credentials are decrypted for gateway calls.</summary>
public sealed class GatewayConnections(CrmContext crm, CredentialVault vault)
{
    public async Task<(ChannelAccount Account, GatewayConnection Connection)?> LoadAsync(Guid accountId, bool tracked, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var q = crm.Db.ChannelAccounts.Where(a => a.Id == accountId && a.TenantId == tenantId && !a.IsDeleted);
        var account = await (tracked ? q : q.AsNoTracking()).FirstOrDefaultAsync(ct);
        if (account is null) return null;
        var cred = await crm.Db.ChannelCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.ChannelAccountId == accountId && c.TenantId == tenantId, ct);
        if (cred is null) return null;
        try { return (account, new GatewayConnection(vault.Open(tenantId, accountId, cred.ProtectedBaseUrl), vault.Open(tenantId, accountId, cred.ProtectedApiKey), account.GatewayAccountId)); }
        catch (Exception ex) when (ex is CrossTenantViolationException or System.Security.Cryptography.CryptographicException)
        {
            // Credentials that do not belong to this tenant/account (or cannot be decrypted) are never used: the account behaves as if it had none.
            crm.Logger.LogError(ex, "Credentials of account {AccountId} cannot be opened for tenant {TenantId}; they were not used", accountId, tenantId);
            return null;
        }
    }
}

public static class SendRateFactsLoader
{
    /// <summary>Counts what the account has used. Messages that are in flight (Sending, Unknown) count as used: they may well have been sent.</summary>
    public static async Task<SendRateFacts> LoadAsync(Persistence.AppDbContext db, ChannelAccount account, DateTime nowUtc, CancellationToken ct)
    {
        var tenantId = account.TenantId; var dayAgo = nowUtc.AddHours(-24); var hourAgo = nowUtc.AddHours(-1);
        var used = await db.Messages.AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.ChannelAccountId == account.Id && m.Direction == MessageDirection.Outbound &&
                        (m.Status == MessageStatus.Sent || m.Status == MessageStatus.Delivered || m.Status == MessageStatus.Read || m.Status == MessageStatus.Sending || m.Status == MessageStatus.Unknown) &&
                        (m.SentAtUtc ?? m.SendStartedAtUtc) >= dayAgo)
            .Select(m => m.SentAtUtc ?? m.SendStartedAtUtc!.Value).ToListAsync(ct);
        var lastHour = used.Where(t => t >= hourAgo).ToList();
        return new SendRateFacts(account.HourlyCap, account.DailyCap, account.MinDelaySeconds, account.WarmupStartedAtUtc, account.LastSendAtUtc, lastHour.Count, used.Count,
            lastHour.Count == 0 ? null : lastHour.Min(), used.Count == 0 ? null : used.Min());
    }
}

/// <summary>Auto-pause: repeated send failures, a disconnected session, and WhatsApp's own restrictions all end up here, each with a notification the owner sees on the account page.</summary>
public sealed class AccountHealthService(CrmContext crm, ICampaignEvents campaigns, IOptions<MessagingOptions> options, ILogger<AccountHealthService> logger) : IAccountHealthService
{
    public async Task RecordSendFailureAsync(Guid accountId, string reason, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var account = await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == accountId && a.TenantId == tenantId, ct);
        if (account is null) return;
        var failures = account.RegisterFailure(crm.Now);
        var paused = false;
        if (failures >= Math.Max(1, options.Value.AutoPauseFailureThreshold) && !account.IsPaused) { Pause(account, $"Paused automatically after {failures} failed sends in a row. Last problem: {reason}", NotificationKind.AccountPaused); paused = true; }
        await db.SaveChangesAsync(ct);
        if (paused) await campaigns.OnAccountUnavailableAsync(accountId, account.PauseReason ?? "paused automatically", ct);
    }

    public async Task RecordSendSuccessAsync(Guid accountId, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var account = await crm.Db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == accountId && a.TenantId == tenantId, ct);
        if (account is null) return;
        account.RegisterSuccess(crm.Now);
        await crm.Db.SaveChangesAsync(ct);
    }

    public async Task ApplySessionStatusAsync(Guid accountId, AccountStatusPayload s, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        var account = await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == accountId && a.TenantId == tenantId, ct);
        if (account is null) throw new PoisonMessageException("account_not_found_for_tenant");
        if (!Enum.TryParse<GatewaySessionStatus>(s.Status, out var gs)) gs = GatewaySessionStatus.Unknown;
        var status = gs switch
        {
            GatewaySessionStatus.Stopped => ConnectionStatus.Stopped, GatewaySessionStatus.Starting => ConnectionStatus.Starting, GatewaySessionStatus.ScanQrCode => ConnectionStatus.ScanQrCode,
            GatewaySessionStatus.Working => ConnectionStatus.Working, GatewaySessionStatus.Failed => ConnectionStatus.Failed, _ => ConnectionStatus.Unknown,
        };
        var previous = account.ConnectionStatus;
        var changed = account.ApplyConnection(status, s.PhoneE164, s.PushName, null, now);

        var wentDown = changed && status is ConnectionStatus.Failed or ConnectionStatus.Stopped && previous == ConnectionStatus.Working;
        if (wentDown)
        {
            if (!account.IsPaused) Pause(account, $"The WhatsApp session is {status.ToString().ToLowerInvariant()} (it was working before). Reconnect it, then resume sending.", NotificationKind.SessionDisconnected);
            else AddNotification(account, NotificationKind.SessionDisconnected, $"The WhatsApp session is {status.ToString().ToLowerInvariant()}.");
        }

        if (s.NewContactsRestricted)
        {
            var until = s.RestrictedUntilUtc ?? now.AddHours(24);
            if (account.NewContactBlockedUntilUtc != until)
            {
                account.BlockNewContacts(until, now);
                var kind = s.RestrictionKind == "message_capping" ? NotificationKind.MessageCapping : NotificationKind.ReachoutTimelock;
                AddNotification(account, kind, kind == NotificationKind.MessageCapping
                    ? $"WhatsApp reports this number reached its quota for messaging new contacts. New contacts are blocked until {until:u}; existing conversations still work. Do not restart or re-link the session."
                    : $"WhatsApp has restricted this number from messaging new contacts until {until:u} (reachout timelock). Existing conversations still work. Do not restart or re-link the session.");
                crm.Record("Channel.Restricted", "ChannelAccount", account.Id, $"WhatsApp restriction on '{account.DisplayName}': {s.RestrictionKind} until {until:u}.");
            }
        }
        else if (account.NewContactBlockedUntilUtc is not null && status == ConnectionStatus.Working) account.BlockNewContacts(null, now);

        await db.SaveChangesAsync(ct);
        if (wentDown) await campaigns.OnAccountUnavailableAsync(accountId, $"the WhatsApp session is {status.ToString().ToLowerInvariant()}", ct);
        logger.LogInformation("Account {AccountId} status {Status}", accountId, status);
    }

    private void Pause(ChannelAccount account, string reason, NotificationKind kind)
    {
        account.Pause(reason, automatic: true, crm.Now);
        AddNotification(account, kind, reason);
        crm.Record("Channel.AutoPaused", "ChannelAccount", account.Id, $"Account '{account.DisplayName}' paused automatically: {reason}");
    }

    private void AddNotification(ChannelAccount account, NotificationKind kind, string message) =>
        crm.Db.ChannelAccountNotifications.Add(ChannelAccountNotification.Create(account.TenantId, account.Id, kind, message, crm.Now));
}

public sealed class ChannelAccountService(
    IEntitlementService entitlements, CrmContext crm, CredentialVault vault, GatewayConnections connections, IWhatsAppGateway gateway, IOptions<WahaOptions> waha,
    ICampaignEvents campaigns, Microsoft.Extensions.Configuration.IConfiguration config, ILogger<ChannelAccountService> logger) : IChannelAccountService
{
    public const int MaxAccountsPerTenant = 5;
    public const string PublicWebhookBaseSetting = "Messaging:PublicWebhookBaseUrl";
    public static readonly IReadOnlyList<string> WebhookEvents = ["message", "message.ack", "session.status"];

    public async Task<IReadOnlyList<ChannelAccountDto>> ListAsync(CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var accounts = await crm.Db.ChannelAccounts.AsNoTracking().Where(a => a.TenantId == tenantId && !a.IsDeleted).OrderBy(a => a.DisplayName).ToListAsync(ct);
        var list = new List<ChannelAccountDto>();
        foreach (var a in accounts) list.Add(await ToDtoAsync(a, ct));
        return list;
    }

    public async Task<Result<ChannelAccountDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var a = await crm.Db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, ct);
        return a is null ? Result.Failure<ChannelAccountDto>(Error.NotFound("WhatsApp account")) : Result.Success(await ToDtoAsync(a, ct));
    }

    public async Task<Result<ChannelAccountSetupDto>> GetSetupAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var a = await crm.Db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (a is null) return Result.Failure<ChannelAccountSetupDto>(Error.NotFound("WhatsApp account"));
        return Result.Success(new ChannelAccountSetupDto(await ToDtoAsync(a, ct), WebhookPath(a), WebhookEvents, waha.Value.EnableAckTracking));
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(TestConnectionRequest r, CancellationToken ct = default)
    {
        crm.TenantId.ToString();   // fail closed outside a tenant scope
        if (!WahaEndpoint.TryNormalize(r.BaseUrl, out var baseUrl, out var error)) return new ConnectionTestResult(false, error, false, null);
        if (string.IsNullOrWhiteSpace(r.ApiKey)) return new ConnectionTestResult(false, "Enter the WAHA API key.", false, null);
        var name = string.IsNullOrWhiteSpace(r.SessionName) ? "default" : r.SessionName.Trim();
        try
        {
            var sessions = await gateway.GetAccountsAsync(new GatewayConnection(baseUrl, r.ApiKey.Trim(), name), ct);
            var mine = sessions.FirstOrDefault(s => s.AccountId == name);
            return new ConnectionTestResult(true, mine is null ? $"Connected to WAHA. Session '{name}' does not exist yet; it is created when you connect the account." : $"Connected to WAHA. Session '{name}' is {mine.Status}.", mine is not null, mine?.Status);
        }
        catch (GatewayException ex) { return new ConnectionTestResult(false, Describe(ex), false, null); }
    }

    public Task<Result<ChannelAccountDto>> CreateAsync(CreateChannelAccountRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
            if (!WahaEndpoint.TryNormalize(r.BaseUrl, out var baseUrl, out var urlError)) return Result.Failure<ChannelAccountDto>(Error.Validation(urlError));
            if (string.IsNullOrWhiteSpace(r.ApiKey)) return Result.Failure<ChannelAccountDto>(Error.Validation("Enter the WAHA API key."));
            if (r.ApiKey.Trim().Length > 500) return Result.Failure<ChannelAccountDto>(Error.Validation("The API key is too long."));
            var session = r.SessionName?.Trim();
            if (!ChannelAccount.IsValidSessionName(session)) return Result.Failure<ChannelAccountDto>(Error.Validation("The WAHA session name must be 3–63 characters: letters, digits, '-' and '_' (starting with a letter or digit)."));
            if (await db.ChannelAccounts.CountAsync(a => a.TenantId == tenantId && !a.IsDeleted, ct) >= MaxAccountsPerTenant)
                return Result.Failure<ChannelAccountDto>(Error.Conflict($"A workspace can have at most {MaxAccountsPerTenant} WhatsApp accounts."));

            var test = await TestConnectionAsync(new TestConnectionRequest(baseUrl, r.ApiKey, session), ct);
            if (!test.Ok) return Result.Failure<ChannelAccountDto>(Error.Validation("The connection test failed: " + test.Message));

            var channel = await db.Channels.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Type == ChannelType.WhatsApp && c.Provider == ChannelProvider.Waha, ct);
            if (channel is null) { channel = Channel.Create(tenantId, ChannelType.WhatsApp, ChannelProvider.Waha, "WhatsApp (WAHA)", now); db.Channels.Add(channel); }

            var account = ChannelAccount.Create(tenantId, channel.Id, r.DisplayName, session!, crm.Actor, now);
            account.SetLimits(r.HourlyCap ?? ChannelAccount.DefaultHourlyCap, r.DailyCap ?? ChannelAccount.DefaultDailyCap, r.MinDelaySeconds ?? ChannelAccount.DefaultMinDelaySeconds, now);
            var secret = string.IsNullOrWhiteSpace(r.WebhookSecret) ? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)) : r.WebhookSecret.Trim();
            if (secret.Length is < 16 or > 200) return Result.Failure<ChannelAccountDto>(Error.Validation("The webhook secret must be 16–200 characters (leave it empty to generate one)."));
            var added = await entitlements.RunWithinLimitAsync(UsageDimension.MessagingAccounts, 1, async c2 =>
            {
                db.ChannelAccounts.Add(account);
                db.ChannelCredentials.Add(ChannelCredential.Create(tenantId, account.Id, vault.Seal(tenantId, account.Id, baseUrl), vault.Seal(tenantId, account.Id, r.ApiKey.Trim()), vault.Seal(tenantId, account.Id, secret), Hint(r.ApiKey), now));
                crm.Record("Channel.Created", "ChannelAccount", account.Id, $"WhatsApp account '{account.DisplayName}' added (session '{session}').");
                try { await db.SaveChangesAsync(c2); }
                catch (DbUpdateException ex) when (TenancyResultFlow.IsDuplicate(ex))
                {
                    return Result.Failure<bool>(Error.Conflict("That WAHA session name is not available. Choose a different session name."));
                }
                return Result.Success(true);
            }, ct);
            if (added.IsFailure) { db.ChangeTracker.Clear(); return Result.Failure<ChannelAccountDto>(added.Error); }
            return Result.Success(await ToDtoAsync(account, ct));
        }, logger);

    public Task<Result<ChannelAccountDto>> UpdateAsync(Guid id, UpdateChannelAccountRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var a = await crm.Db.ChannelAccounts.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, ct);
            if (a is null) return Result.Failure<ChannelAccountDto>(Error.NotFound("WhatsApp account"));
            a.Rename(r.DisplayName, crm.Now);
            a.SetLimits(r.HourlyCap, r.DailyCap, r.MinDelaySeconds, crm.Now);
            crm.Record("Channel.Updated", "ChannelAccount", a.Id, $"'{a.DisplayName}': limits {r.HourlyCap}/hour, {r.DailyCap}/day, {r.MinDelaySeconds}s between messages.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await ToDtoAsync(a, ct));
        }, logger);

    public Task<Result<ChannelAccountDto>> RotateCredentialsAsync(Guid id, RotateCredentialsRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId; var now = crm.Now;
            if (!WahaEndpoint.TryNormalize(r.BaseUrl, out var baseUrl, out var err)) return Result.Failure<ChannelAccountDto>(Error.Validation(err));
            if (string.IsNullOrWhiteSpace(r.ApiKey)) return Result.Failure<ChannelAccountDto>(Error.Validation("Enter the WAHA API key."));
            var a = await crm.Db.ChannelAccounts.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, ct);
            var cred = await crm.Db.ChannelCredentials.FirstOrDefaultAsync(c => c.ChannelAccountId == id && c.TenantId == tenantId, ct);
            if (a is null || cred is null) return Result.Failure<ChannelAccountDto>(Error.NotFound("WhatsApp account"));
            var test = await TestConnectionAsync(new TestConnectionRequest(baseUrl, r.ApiKey, a.GatewayAccountId), ct);
            if (!test.Ok) return Result.Failure<ChannelAccountDto>(Error.Validation("The connection test failed: " + test.Message));
            var secret = string.IsNullOrWhiteSpace(r.WebhookSecret) ? vault.Open(tenantId, id, cred.ProtectedWebhookSecret) : r.WebhookSecret.Trim();
            if (secret.Length is < 16 or > 200) return Result.Failure<ChannelAccountDto>(Error.Validation("The webhook secret must be 16–200 characters."));
            cred.Replace(vault.Seal(tenantId, id, baseUrl), vault.Seal(tenantId, id, r.ApiKey.Trim()), vault.Seal(tenantId, id, secret), Hint(r.ApiKey), now);
            crm.Record("Channel.CredentialsRotated", "ChannelAccount", id, $"Gateway credentials of '{a.DisplayName}' were replaced. Connect the account again to register the new webhook secret.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await ToDtoAsync(a, ct));
        }, logger);

    public Task<Result<ChannelAccountDto>> ConnectAsync(Guid id, CancellationToken ct = default) =>
        GatewayCallAsync(id, async (acc, conn) =>
        {
            var baseUrl = config[PublicWebhookBaseSetting];
            if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var b) || (b.Scheme != Uri.UriSchemeHttps && b.Scheme != Uri.UriSchemeHttp))
                return Result.Failure<ChannelAccountDto>(Error.Validation($"The public address of the API is not configured ({PublicWebhookBaseSetting}), so WAHA would not know where to send events."));
            var cred = await crm.Db.ChannelCredentials.AsNoTracking().FirstAsync(c => c.ChannelAccountId == id && c.TenantId == crm.TenantId, ct);
            var secret = vault.Open(crm.TenantId, id, cred.ProtectedWebhookSecret);
            var session = await gateway.ConnectAccountAsync(conn, new GatewayWebhookRegistration(baseUrl.TrimEnd('/') + WebhookPath(acc), secret, WebhookEvents, waha.Value.EnableAckTracking), ct);
            Apply(acc, session);
            crm.Record("Channel.Connected", "ChannelAccount", id, $"Session '{acc.GatewayAccountId}' of '{acc.DisplayName}' started; webhook registered.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await ToDtoAsync(acc, ct));
        }, ct);

    public Task<Result<ChannelAccountDto>> RefreshStatusAsync(Guid id, CancellationToken ct = default) =>
        GatewayCallAsync(id, async (acc, conn) =>
        {
            var session = await gateway.GetAccountStatusAsync(conn, ct);
            if (session is null) acc.ApplyConnection(ConnectionStatus.Stopped, null, null, null, crm.Now);
            else Apply(acc, session);
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await ToDtoAsync(acc, ct));
        }, ct);

    public async Task<Result<QrCodeDto>> GetQrAsync(Guid id, CancellationToken ct = default)
    {
        var loaded = await connections.LoadAsync(id, tracked: false, ct);
        if (loaded is null) return Result.Failure<QrCodeDto>(Error.NotFound("WhatsApp account"));
        try
        {
            var session = await gateway.GetAccountStatusAsync(loaded.Value.Connection, ct);
            if (session is null) return Result.Failure<QrCodeDto>(Error.Conflict("The session does not exist in WAHA yet. Connect the account first."));
            if (session.Status != GatewaySessionStatus.ScanQrCode) return Result.Success(new QrCodeDto(null, null, session.Status));
            var qr = await gateway.GetQrCodeAsync(loaded.Value.Connection, ct);
            return Result.Success(new QrCodeDto(qr.RawValue, qr.PngImage is null ? null : Convert.ToBase64String(qr.PngImage), session.Status));
        }
        catch (GatewayException ex) { return Result.Failure<QrCodeDto>(Error.Conflict(Describe(ex))); }
    }

    public Task<Result<ChannelAccountDto>> DisconnectAsync(Guid id, bool logout, CancellationToken ct = default) =>
        GatewayCallAsync(id, async (acc, conn) =>
        {
            await gateway.DisconnectAccountAsync(conn, logout, ct);
            acc.ApplyConnection(ConnectionStatus.Stopped, null, null, null, crm.Now);
            if (!acc.IsPaused) acc.Pause(logout ? "Disconnected (WhatsApp unlinked)" : "Disconnected (session stopped)", automatic: false, crm.Now);
            crm.Record("Channel.Disconnected", "ChannelAccount", id, $"'{acc.DisplayName}' {(logout ? "logged out of WhatsApp" : "stopped")}.");
            await crm.Db.SaveChangesAsync(ct);
            await campaigns.OnAccountUnavailableAsync(id, "the session was disconnected", ct);
            return Result.Success(await ToDtoAsync(acc, ct));
        }, ct);

    public async Task<Result<ChannelAccountDto>> PauseAsync(Guid id, CancellationToken ct = default)
    {
        var r = await MutateAsync(id, a => { a.Pause("Paused by " + (crm.Actor ?? "a user"), automatic: false, crm.Now); crm.Record("Channel.Paused", "ChannelAccount", id, $"'{a.DisplayName}' paused."); }, ct);
        if (r.IsSuccess) await campaigns.OnAccountUnavailableAsync(id, "the account was paused", ct);
        return r;
    }

    public Task<Result<ChannelAccountDto>> ResumeAsync(Guid id, CancellationToken ct = default) =>
        MutateAsync(id, a =>
        {
            a.Resume(crm.Now);
            foreach (var n in crm.Db.ChannelAccountNotifications.Where(n => n.TenantId == a.TenantId && n.ChannelAccountId == a.Id && n.AcknowledgedAtUtc == null)) n.Acknowledge(crm.Now);
            crm.Db.ChannelAccountNotifications.Add(ChannelAccountNotification.Create(a.TenantId, a.Id, NotificationKind.AccountResumed, "Sending was resumed by " + (crm.Actor ?? "a user") + ".", crm.Now));
            crm.Record("Channel.Resumed", "ChannelAccount", id, $"'{a.DisplayName}' resumed.");
        }, ct);

    public async Task<Result> RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var a = await crm.Db.ChannelAccounts.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (a is null) return Result.Failure(Error.NotFound("WhatsApp account"));
        a.Remove(crm.Now);
        crm.Record("Channel.Removed", "ChannelAccount", id, $"'{a.DisplayName}' removed. Its webhook address no longer accepts events; the WAHA session itself was not touched.");
        await crm.Db.SaveChangesAsync(ct);
        await campaigns.OnAccountUnavailableAsync(id, "the account was removed", ct);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<AccountNotificationDto>>> NotificationsAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        if (!await crm.Db.ChannelAccounts.AnyAsync(a => a.Id == id && a.TenantId == tenantId && !a.IsDeleted, ct)) return Result.Failure<IReadOnlyList<AccountNotificationDto>>(Error.NotFound("WhatsApp account"));
        var rows = await crm.Db.ChannelAccountNotifications.AsNoTracking().Where(n => n.TenantId == tenantId && n.ChannelAccountId == id).OrderByDescending(n => n.CreatedAtUtc).Take(50).ToListAsync(ct);
        return Result.Success<IReadOnlyList<AccountNotificationDto>>(rows.Select(n => new AccountNotificationDto(n.Id, n.Kind, n.Message, n.CreatedAtUtc, n.AcknowledgedAtUtc)).ToList());
    }

    public async Task<Result> AcknowledgeNotificationsAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        if (!await crm.Db.ChannelAccounts.AnyAsync(a => a.Id == id && a.TenantId == tenantId && !a.IsDeleted, ct)) return Result.Failure(Error.NotFound("WhatsApp account"));
        foreach (var n in await crm.Db.ChannelAccountNotifications.Where(n => n.TenantId == tenantId && n.ChannelAccountId == id && n.AcknowledgedAtUtc == null).ToListAsync(ct)) n.Acknowledge(crm.Now);
        await crm.Db.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ------------------------------------------------------------------

    private async Task<Result<ChannelAccountDto>> MutateAsync(Guid id, Action<ChannelAccount> change, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var a = await crm.Db.ChannelAccounts.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (a is null) return Result.Failure<ChannelAccountDto>(Error.NotFound("WhatsApp account"));
        change(a);
        await crm.Db.SaveChangesAsync(ct);
        return Result.Success(await ToDtoAsync(a, ct));
    }

    private async Task<Result<ChannelAccountDto>> GatewayCallAsync(Guid id, Func<ChannelAccount, GatewayConnection, Task<Result<ChannelAccountDto>>> work, CancellationToken ct)
    {
        var loaded = await connections.LoadAsync(id, tracked: true, ct);
        if (loaded is null) return Result.Failure<ChannelAccountDto>(Error.NotFound("WhatsApp account"));
        try { return await work(loaded.Value.Account, loaded.Value.Connection); }
        catch (GatewayException ex) { return Result.Failure<ChannelAccountDto>(Error.Conflict(Describe(ex))); }
    }

    private void Apply(ChannelAccount acc, GatewaySession s)
    {
        var status = s.Status switch
        {
            GatewaySessionStatus.Stopped => ConnectionStatus.Stopped, GatewaySessionStatus.Starting => ConnectionStatus.Starting, GatewaySessionStatus.ScanQrCode => ConnectionStatus.ScanQrCode,
            GatewaySessionStatus.Working => ConnectionStatus.Working, GatewaySessionStatus.Failed => ConnectionStatus.Failed, _ => ConnectionStatus.Unknown,
        };
        acc.ApplyConnection(status, s.PhoneE164, s.PushName, s.Engine, crm.Now);
    }

    public static string WebhookPath(ChannelAccount a) => $"/api/v1/webhooks/waha/{a.WebhookKey}";

    private static string Hint(string apiKey) { var k = apiKey.Trim(); return k.Length <= 4 ? "••••" : "••••" + k[^4..]; }

    internal static string Describe(GatewayException ex) => ex.Kind switch
    {
        GatewayFailureKind.Unauthorized => "WAHA rejected the API key. Check the key (WAHA_API_KEY) and try again.",
        GatewayFailureKind.Unreachable => "WAHA could not be reached at that address. Check the URL, that WAHA is running and that it is reachable from this server.",
        GatewayFailureKind.UnsafeEndpoint => ex.Message,
        GatewayFailureKind.NotFound => "WAHA does not know that session. " + ex.Message,
        GatewayFailureKind.RateLimited => "WAHA is rate limiting requests. Try again in a moment.",
        _ => "WAHA did not accept the request. " + ex.Message,
    };

    private async Task<ChannelAccountDto> ToDtoAsync(ChannelAccount a, CancellationToken ct)
    {
        var now = crm.Now;
        var facts = await SendRateFactsLoader.LoadAsync(crm.Db, a, now, ct);
        var cred = await crm.Db.ChannelCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.ChannelAccountId == a.Id && c.TenantId == a.TenantId, ct);
        var host = string.Empty;
        if (cred is not null) { try { host = new Uri(vault.Open(a.TenantId, a.Id, cred.ProtectedBaseUrl)).Authority; } catch (Exception ex) when (ex is UriFormatException or CrossTenantViolationException) { host = "(unreadable)"; } }
        var unack = await crm.Db.ChannelAccountNotifications.CountAsync(n => n.TenantId == a.TenantId && n.ChannelAccountId == a.Id && n.AcknowledgedAtUtc == null, ct);
        return new ChannelAccountDto(a.Id, a.DisplayName, a.GatewayAccountId, a.ConnectionStatus, a.ConnectionStatusAtUtc, a.PhoneE164, a.PushName, a.Engine, a.IsPaused, a.PausedAutomatically, a.PauseReason,
            a.ConsecutiveFailures, a.HourlyCap, a.DailyCap, a.MinDelaySeconds, SendRatePolicy.DailyLimitToday(a.DailyCap, a.WarmupStartedAtUtc, now), facts.SentLastHour, facts.SentLast24Hours,
            a.NewContactBlockedUntilUtc > now ? a.NewContactBlockedUntilUtc : null, a.CreatedAtUtc, host, cred?.KeyHint ?? string.Empty, unack);
    }
}
