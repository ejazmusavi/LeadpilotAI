using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Identity;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Shared.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Services.Tenancy;

/// <summary>The signed-in user's own profile. Takes no user id: it can only ever touch the caller.</summary>
public sealed class ProfileService(AppDbContext db, ICurrentUser currentUser, UserManager<ApplicationUser> users, IClock clock) : IProfileService
{
    public async Task<Result<ProfileDto>> GetAsync(CancellationToken ct = default)
    {
        if (currentUser.UserId is not { } id) return Result.Failure<ProfileDto>(Error.Forbidden());
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return Result.Failure<ProfileDto>(Error.NotFound("User"));
        var phone = await db.UserProfiles.AsNoTracking().Where(p => p.UserId == id).Select(p => p.Phone).FirstOrDefaultAsync(ct);
        return Result.Success(new ProfileDto(user.Email ?? "", user.DisplayName, phone));
    }

    public async Task<Result<ProfileDto>> UpdateAsync(UpdateProfileRequest request, CancellationToken ct = default)
    {
        if (currentUser.UserId is not { } id) return Result.Failure<ProfileDto>(Error.Forbidden());
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return Result.Failure<ProfileDto>(Error.NotFound("User"));
        try
        {
            var now = clock.UtcNow;
            var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == id, ct);
            if (profile is null) db.UserProfiles.Add(profile = UserProfile.Create(id, request.DisplayName, request.Phone, now));
            else profile.Update(request.DisplayName, request.Phone, now);
            user.DisplayName = profile.DisplayName;
            await db.SaveChangesAsync(ct);
            await users.UpdateAsync(user);
            return Result.Success(new ProfileDto(user.Email ?? "", profile.DisplayName, profile.Phone));
        }
        catch (DomainException ex) { return Result.Failure<ProfileDto>(Error.Validation(ex.Message)); }
    }
}
