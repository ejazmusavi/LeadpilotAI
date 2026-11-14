using System.ComponentModel.DataAnnotations;

namespace LeadPilot.Web.Models;

public class LoginViewModel
{
    [Required, EmailAddress, Display(Name = "Email")] public string Email { get; set; } = string.Empty;
    [Required, DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
    [Display(Name = "Keep me signed in on this device")] public bool RememberMe { get; set; }
    public string? ReturnUrl { get; set; }
}

public class RegisterViewModel
{
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class ForgotPasswordViewModel
{
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
}

public class ResetPasswordViewModel
{
    public string Email { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class ErrorViewModel
{
    public string? CorrelationId { get; set; }
    public int? StatusCode { get; set; }
}
