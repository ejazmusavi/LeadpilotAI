using LeadPilot.Application.Billing;
using LeadPilot.Domain.Billing;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Identity;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Tenancy;
using LeadPilot.Shared.Results;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Services.Tenancy;

public sealed class InvitationService(
    AppDbContext db, ITenantContext tenant, ITenantContextAccessor accessor, ICurrentUser currentUser, IAuditService audit, IClock clock,
    IPermissionEvaluator evaluator, LeadPilot.Application.Billing.IEntitlementService entitlements, InvitationIssuer issuer, UserManager<ApplicationUser> users, IValidator<CreateInvitationRequest> validator,
    ILogger<InvitationService> logger) : IInvitationService
{
    public async Task<PagedResult<InvitationDto>> ListAsync(int page, int pageSize, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var q = from i in db.TenantInvitations.AsNoTracking()
                join r in db.TenantRoles.AsNoTracking() on i.RoleId equals r.Id
                where i.TenantId == tenantId
                select new { i, RoleName = r.Name };
        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderByDescending(x => x.i.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var now = clock.UtcNow;
        return new PagedResult<InvitationDto>(rows.Select(x => ToDto(x.i, x.RoleName, now)).ToList(), page, pageSize, total);
    }

    public Task<Result<InvitationDto>> CreateAsync(CreateInvitationRequest request, CancellationToken ct = default) =>
        ResultFlow.GuardAsync(async () =>
        {
            var tenantId = tenant.RequireTenantId();
            if (await validator.CheckAsync(request, ct) is { } invalid) return Result.Failure<InvitationDto>(invalid.Error);
            var role = await db.TenantRoles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.RoleId && r.TenantId == tenantId, ct);
            if (role is null) return Result.Failure<InvitationDto>(Error.Validation("The selected role does not exist."));

            var codes = (await TenantQueries.PermissionCodesByRoleAsync(db, tenantId, [role.Id], ct)).GetValueOrDefault(role.Id) ?? [];
            var blocked = (await evaluator.GetCurrentAsync(ct)).Ungrantable(codes);
            if (blocked.Count > 0) return Result.Failure<InvitationDto>(Error.Forbidden($"You cannot invite someone into a role carrying permissions you do not hold: {string.Join(", ", blocked)}."));

            var email = TenantInvitation.NormalizeEmail(request.Email);
            var existingUser = await users.FindByEmailAsync(email);
            if (existingUser is not null && await db.UserTenants.AnyAsync(m => m.TenantId == tenantId && m.UserId == existingUser.Id && m.Status == MembershipStatus.Active, ct))
                return Result.Failure<InvitationDto>(Error.Conflict("That person is already a member of this workspace."));

            // One live invitation per e-mail: older pending ones are revoked.
            var now = clock.UtcNow;
            foreach (var old in await db.TenantInvitations.Where(i => i.TenantId == tenantId && i.Email == email && i.Status == InvitationStatus.Pending).ToListAsync(ct)) old.Revoke(now);

            // a pending invitation holds a seat: the plan's user limit counts active members plus live invitations
            TenantInvitation? madeInv = null; var raw = string.Empty;
            var seat = await entitlements.RunWithinLimitAsync(LeadPilot.Domain.Billing.UsageDimension.Users, 1, async c2 =>
            {
                var created = issuer.Create(tenantId, email, role.Id, currentUser.UserId); madeInv = created.Invitation; raw = created.RawToken;
                audit.Record(new AuditEntry("Invitation.Created", "TenantInvitation", madeInv.Id.ToString(), $"Invitation sent to {email} for role '{role.Name}'."));
                await db.SaveChangesAsync(c2);
                return Result.Success(true);
            }, ct);
            if (seat.IsFailure) { db.ChangeTracker.Clear(); return Result.Failure<InvitationDto>(seat.Error); }
            var invitation = madeInv!;

            var tenantName = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.Name).FirstAsync(ct);
            try { await issuer.SendAsync(tenantName, role.Name, invitation, raw, currentUser.Email, ct); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Invitation e-mail for {InvitationId} could not be sent", invitation.Id);
                return Result.Failure<InvitationDto>(new Error("email_failed", "The invitation was saved but the e-mail could not be sent. Use Resend once e-mail delivery works."));
            }
            return Result.Success(ToDto(invitation, role.Name, now));
        }, logger);

    public async Task<Result> RevokeAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var inv = await db.TenantInvitations.FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, ct);
        if (inv is null) return Result.Failure(Error.NotFound("Invitation"));
        try { inv.Revoke(clock.UtcNow); }
        catch (DomainException ex) { return Result.Failure(Error.Conflict(ex.Message)); }
        audit.Record(new AuditEntry("Invitation.Revoked", "TenantInvitation", id.ToString(), $"Invitation to {inv.Email} revoked."));
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public Task<Result<InvitationDto>> ResendAsync(Guid id, CancellationToken ct = default) =>
        ResultFlow.GuardAsync(async () =>
        {
            var tenantId = tenant.RequireTenantId();
            var inv = await db.TenantInvitations.FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, ct);
            if (inv is null) return Result.Failure<InvitationDto>(Error.NotFound("Invitation"));
            if (inv.Status != InvitationStatus.Pending) return Result.Failure<InvitationDto>(Error.Conflict("Only pending invitations can be re-sent."));
            var role = await db.TenantRoles.AsNoTracking().FirstAsync(r => r.Id == inv.RoleId && r.TenantId == tenantId, ct);

            var raw = TokenHasher.NewToken();
            var now = clock.UtcNow;
            inv.ReplaceToken(TokenHasher.Hash(raw), now.AddDays(issuer.ExpiryDays), now); // the previous link stops working
            audit.Record(new AuditEntry("Invitation.Resent", "TenantInvitation", id.ToString(), $"Invitation to {inv.Email} re-sent."));
            await db.SaveChangesAsync(ct);

            var tenantName = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.Name).FirstAsync(ct);
            try { await issuer.SendAsync(tenantName, role.Name, inv, raw, currentUser.Email, ct); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Invitation e-mail for {InvitationId} could not be re-sent", id);
                return Result.Failure<InvitationDto>(new Error("email_failed", "The e-mail could not be sent."));
            }
            return Result.Success(ToDto(inv, role.Name, now));
        }, logger);

    public async Task<InvitationPreview?> PreviewAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200) return null;
        var hash = TokenHasher.Hash(token);
        var now = clock.UtcNow;
        // The token is the credential: this lookup is deliberately cross-tenant (allow-listed IgnoreQueryFilters), keyed only by the token hash.
        var row = await (from i in db.TenantInvitations.IgnoreQueryFilters().AsNoTracking()
                         join r in db.TenantRoles.IgnoreQueryFilters().AsNoTracking() on i.RoleId equals r.Id
                         join t in db.Tenants.AsNoTracking() on i.TenantId equals t.Id
                         where i.TokenHash == hash && i.Status == InvitationStatus.Pending && i.ExpiresAtUtc > now && t.Status == TenantStatus.Active
                         select new { i.Email, i.ExpiresAtUtc, RoleName = r.Name, TenantName = t.Name }).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        return new InvitationPreview(row.TenantName, row.Email, row.RoleName, row.ExpiresAtUtc, await users.FindByEmailAsync(row.Email) is not null);
    }

    public async Task<Result<AcceptInvitationResult>> AcceptAsync(AcceptInvitationRequest request, Guid? signedInUserId, CancellationToken ct = default)
    {
        var invalidInvitation = Result.Failure<AcceptInvitationResult>(new Error("invalid_invitation", "This invitation is invalid, expired or has already been used."));
        if (string.IsNullOrWhiteSpace(request.Token) || request.Token.Length > 200) return invalidInvitation;

        using var _ = accessor.BeginPlatformScope(); // the invitee has no tenant context yet; everything below is keyed by the invitation itself
        var now = clock.UtcNow;
        var hash = TokenHasher.Hash(request.Token);
        var inv = await db.TenantInvitations.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (inv is null || !inv.CanBeAccepted(now)) return invalidInvitation;

        var tenantRow = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == inv.TenantId, ct);
        if (tenantRow is null || !tenantRow.IsActive) return Result.Failure<AcceptInvitationResult>(new Error("tenant_suspended", "This workspace is not active."));
        var roleExists = await db.TenantRoles.IgnoreQueryFilters().AnyAsync(r => r.Id == inv.RoleId && r.TenantId == inv.TenantId, ct);
        if (!roleExists) return invalidInvitation;

        var user = await users.FindByEmailAsync(inv.Email);
        var created = false;
        if (user is not null)
        {
            if (signedInUserId != user.Id)
                return Result.Failure<AcceptInvitationResult>(new Error("sign_in_required", $"An account for {inv.Email} already exists. Sign in with it to accept this invitation."));
            if (!user.IsActive) return Result.Failure<AcceptInvitationResult>(Error.Forbidden("This account is disabled."));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.DisplayName)) return Result.Failure<AcceptInvitationResult>(Error.Validation("Your name is required."));
            if (string.IsNullOrEmpty(request.Password)) return Result.Failure<AcceptInvitationResult>(Error.Validation("A password is required."));
            user = new ApplicationUser { UserName = inv.Email, Email = inv.Email, EmailConfirmed = true, DisplayName = request.DisplayName.Trim(), CreatedAtUtc = now };
            var create = await users.CreateAsync(user, request.Password);
            if (!create.Succeeded) return Result.Failure<AcceptInvitationResult>(Error.Validation(string.Join(" ", create.Errors.Select(e => e.Description))));
            created = true;
        }

        // Single use, race-safe: only one concurrent request flips Pending → Accepted.
        var claimed = await db.TenantInvitations.IgnoreQueryFilters()
            .Where(i => i.Id == inv.Id && i.Status == InvitationStatus.Pending && i.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, InvitationStatus.Accepted).SetProperty(i => i.AcceptedAtUtc, now).SetProperty(i => i.AcceptedByUserId, user.Id).SetProperty(i => i.UpdatedAtUtc, now), ct);
        if (claimed == 0)
        {
            if (created) await users.DeleteAsync(user);
            return invalidInvitation;
        }

        var membership = await db.UserTenants.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.TenantId == inv.TenantId && m.UserId == user.Id, ct);
        if (membership is null)
        {
            var hasOther = await db.UserTenants.IgnoreQueryFilters().AnyAsync(m => m.UserId == user.Id, ct);
            db.UserTenants.Add(UserTenant.Create(inv.TenantId, user.Id, isDefault: !hasOther, inv.InvitedByUserId, now));
        }
        else membership.SetStatus(MembershipStatus.Active);

        if (!await db.TenantUserRoles.IgnoreQueryFilters().AnyAsync(r => r.TenantId == inv.TenantId && r.UserId == user.Id && r.RoleId == inv.RoleId, ct))
            db.TenantUserRoles.Add(UserRole.Create(inv.TenantId, user.Id, inv.RoleId, inv.InvitedByUserId, now));
        if (created && !await db.UserProfiles.AnyAsync(p => p.UserId == user.Id, ct))
            db.UserProfiles.Add(UserProfile.Create(user.Id, user.DisplayName, null, now));

        audit.Record(new AuditEntry("Invitation.Accepted", "TenantInvitation", inv.Id.ToString(), $"{inv.Email} accepted an invitation{(created ? " and created an account" : "")}.",
            inv.TenantId, user.Id, inv.Email));
        await db.SaveChangesAsync(ct);
        return Result.Success(new AcceptInvitationResult(user.Id, inv.TenantId, inv.Email));
    }

    private static InvitationDto ToDto(TenantInvitation i, string roleName, DateTime now) =>
        new(i.Id, i.Email, i.RoleId, roleName, i.EffectiveStatus(now), i.ExpiresAtUtc, i.CreatedAtUtc);
}
