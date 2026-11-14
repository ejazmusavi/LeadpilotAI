using FluentValidation;
using LeadPilot.Shared.Results;

namespace LeadPilot.Application.Contracts;

public static class ValidationHelper
{
    public static async Task<Result?> CheckAsync<T>(this IValidator<T> validator, T instance, CancellationToken ct)
    {
        var r = await validator.ValidateAsync(instance, ct);
        return r.IsValid ? null : Result.Failure(Error.Validation(string.Join(" ", r.Errors.Select(e => e.ErrorMessage).Distinct())));
    }

    public static bool IsEmail(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && System.Net.Mail.MailAddress.TryCreate(value.Trim(), out var a) && a.Address == value.Trim();
}

public sealed class CreateTenantRequestValidator : AbstractValidator<CreateTenantRequest>
{
    public CreateTenantRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.AdminEmail).Must(ValidationHelper.IsEmail).When(x => !string.IsNullOrWhiteSpace(x.AdminEmail)).WithMessage("Administrator e-mail is not valid.");
    }
}

public sealed class CreateInvitationRequestValidator : AbstractValidator<CreateInvitationRequest>
{
    public CreateInvitationRequestValidator()
    {
        RuleFor(x => x.Email).Must(ValidationHelper.IsEmail).WithMessage("A valid e-mail address is required.");
        RuleFor(x => x.RoleId).NotEmpty().WithMessage("A role is required.");
    }
}

public sealed class SaveRoleRequestValidator : AbstractValidator<SaveRoleRequest>
{
    public SaveRoleRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.Permissions).NotNull().Must(p => p is null || p.Count <= 100).WithMessage("Too many permissions.");
    }
}

public sealed class SetSettingRequestValidator : AbstractValidator<SetSettingRequest>
{
    public SetSettingRequestValidator() => RuleFor(x => x.Value).NotNull().MaximumLength(4000);
}
