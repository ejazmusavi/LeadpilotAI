using FluentValidation;
using LeadPilot.Web.Models;

namespace LeadPilot.Web.Validators;

public class RegisterViewModelValidator : AbstractValidator<RegisterViewModel>
{
    public RegisterViewModelValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200).WithName("Name");
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
        RuleFor(x => x.ConfirmPassword).Equal(x => x.Password).WithMessage("The passwords do not match.");
    }
}

public class ResetPasswordViewModelValidator : AbstractValidator<ResetPasswordViewModel>
{
    public ResetPasswordViewModelValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Token).NotEmpty();
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
        RuleFor(x => x.ConfirmPassword).Equal(x => x.Password).WithMessage("The passwords do not match.");
    }
}
