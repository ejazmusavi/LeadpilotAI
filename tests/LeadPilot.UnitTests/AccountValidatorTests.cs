using LeadPilot.Web.Models;
using LeadPilot.Web.Validators;

namespace LeadPilot.UnitTests;

public class AccountValidatorTests
{
    private readonly RegisterViewModelValidator _register = new();
    private readonly ResetPasswordViewModelValidator _reset = new();

    private static RegisterViewModel ValidRegister() => new()
    {
        DisplayName = "Sara Khan", Email = "sara@example.com", Password = "Str0ng!Passw0rd", ConfirmPassword = "Str0ng!Passw0rd"
    };

    [Fact] public void Valid_registration_passes() => Assert.True(_register.Validate(ValidRegister()).IsValid);

    [Fact]
    public void Mismatched_passwords_fail()
    {
        var m = ValidRegister(); m.ConfirmPassword = "different";
        Assert.Contains(_register.Validate(m).Errors, e => e.PropertyName == nameof(RegisterViewModel.ConfirmPassword));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public void Invalid_email_fails(string email)
    {
        var m = ValidRegister(); m.Email = email;
        Assert.False(_register.Validate(m).IsValid);
    }

    [Fact]
    public void Reset_requires_token()
    {
        var m = new ResetPasswordViewModel { Email = "a@b.co", Token = "", Password = "x", ConfirmPassword = "x" };
        Assert.Contains(_reset.Validate(m).Errors, e => e.PropertyName == nameof(ResetPasswordViewModel.Token));
    }
}
