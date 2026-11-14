using System.Text;
using FluentValidation;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Infrastructure.Identity;
using LeadPilot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;

namespace LeadPilot.Web.Controllers;

[AllowAnonymous]
[EnableRateLimiting("auth")]
public class AccountController(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    IEmailSender email,
    IClock clock,
    IConfiguration config,
    IAuditService audit,
    IMembershipResolver membership,
    IValidator<RegisterViewModel> registerValidator,
    IValidator<ResetPasswordViewModel> resetValidator,
    ILogger<AccountController> logger) : Controller
{
    private bool SelfRegistrationEnabled => config.GetValue<bool>("Registration:AllowSelfRegistration");

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Home");
        ViewBag.SelfRegistration = SelfRegistrationEnabled;
        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        ViewBag.SelfRegistration = SelfRegistrationEnabled;
        if (!ModelState.IsValid) return View(model);

        var user = await users.FindByEmailAsync(model.Email);
        if (user is { IsActive: true })
        {
            var result = await signIn.PasswordSignInAsync(user, model.Password, model.RememberMe, lockoutOnFailure: true);
            if (result.Succeeded)
            {
                user.LastLoginAtUtc = clock.UtcNow;
                await users.UpdateAsync(user);
                logger.LogInformation("User {UserId} signed in", user.Id);
                await AuditAuth("Auth.SignedIn", user, $"{user.Email} signed in.");
                return LocalRedirect(Url.IsLocalUrl(model.ReturnUrl) ? model.ReturnUrl! : "/");
            }
            if (result.IsLockedOut)
            {
                logger.LogWarning("User {UserId} locked out", user.Id);
                await AuditAuth("Auth.LockedOut", user, $"{user.Email} was locked out after repeated failed sign-ins.");
                return View("Lockout");
            }
        }
        else
        {
            // Burn comparable time so account existence is not revealed by timing.
            await signIn.PasswordSignInAsync(model.Email, model.Password, false, lockoutOnFailure: false);
        }

        ModelState.AddModelError(string.Empty, "Invalid email or password.");
        return View(model);
    }

    private async Task AuditAuth(string action, ApplicationUser user, string summary)
    {
        // Auth events are recorded against the user's default workspace (no tenant is active yet at sign-in).
        if (await membership.GetDefaultTenantIdAsync(user.Id) is { } tid)
            await audit.RecordAndSaveAsync(new AuditEntry(action, "User", user.Id.ToString(), summary, tid, user.Id, user.Email));
    }

    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        logger.LogInformation("User signed out");
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    public IActionResult Register()
    {
        if (!SelfRegistrationEnabled) return NotFound();
        return View(new RegisterViewModel());
    }

    [HttpPost]
    public async Task<IActionResult> Register(RegisterViewModel model)
    {
        if (!SelfRegistrationEnabled) return NotFound();

        var validation = await registerValidator.ValidateAsync(model);
        if (!validation.IsValid)
        {
            foreach (var e in validation.Errors) ModelState.AddModelError(e.PropertyName, e.ErrorMessage);
            return View(model);
        }

        var user = new ApplicationUser { UserName = model.Email, Email = model.Email, DisplayName = model.DisplayName.Trim(), CreatedAtUtc = clock.UtcNow };
        var result = await users.CreateAsync(user, model.Password);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors) ModelState.AddModelError(string.Empty, e.Description);
            return View(model);
        }

        await signIn.SignInAsync(user, isPersistent: false);
        return RedirectToAction("Index", "Home");
    }

    [HttpGet]
    public IActionResult ForgotPassword() => View(new ForgotPasswordViewModel());

    [HttpPost]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await users.FindByEmailAsync(model.Email);
        if (user is { IsActive: true })
        {
            var token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GeneratePasswordResetTokenAsync(user)));
            var link = Url.Action(nameof(ResetPassword), "Account", new { email = user.Email, token }, Request.Scheme)!;
            try
            {
                await email.SendAsync(new EmailMessage(user.Email!, "Reset your LeadPilot AI password",
                    $"<p>Hello {System.Net.WebUtility.HtmlEncode(user.DisplayName)},</p><p>Use the link below to reset your password. It expires in 2 hours.</p><p><a href=\"{link}\">Reset password</a></p><p>If you did not request this, you can ignore this email.</p>"));
            }
            catch (Exception ex)
            {
                // Do not reveal to the requester whether the account exists; operators see the failure in logs.
                logger.LogError(ex, "Password reset email could not be sent for user {UserId}", user.Id);
            }
        }
        // Same response whether or not the account exists (prevents account enumeration).
        return RedirectToAction(nameof(ForgotPasswordConfirmation));
    }

    [HttpGet] public IActionResult ForgotPasswordConfirmation() => View();

    [HttpGet]
    public IActionResult ResetPassword(string? email, string? token)
    {
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(token)) return BadRequest("A reset link is required.");
        return View(new ResetPasswordViewModel { Email = email, Token = token });
    }

    [HttpPost]
    public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
    {
        var validation = await resetValidator.ValidateAsync(model);
        if (!validation.IsValid)
        {
            foreach (var e in validation.Errors) ModelState.AddModelError(e.PropertyName, e.ErrorMessage);
            return View(model);
        }

        string rawToken;
        try { rawToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(model.Token)); }
        catch (FormatException) { ModelState.AddModelError(string.Empty, "This reset link is invalid or has expired."); return View(model); }

        var user = await users.FindByEmailAsync(model.Email);
        if (user is null || !user.IsActive)
            return RedirectToAction(nameof(ResetPasswordConfirmation)); // no enumeration

        var result = await users.ResetPasswordAsync(user, rawToken, model.Password);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors)
                ModelState.AddModelError(string.Empty, e.Code == "InvalidToken" ? "This reset link is invalid or has expired." : e.Description);
            return View(model);
        }

        await users.UpdateSecurityStampAsync(user); // invalidates existing sessions
        logger.LogInformation("Password reset for user {UserId}", user.Id);
        return RedirectToAction(nameof(ResetPasswordConfirmation));
    }

    [HttpGet] public IActionResult ResetPasswordConfirmation() => View();
    [HttpGet] public IActionResult AccessDenied() { Response.StatusCode = StatusCodes.Status403Forbidden; return View(); }
}
