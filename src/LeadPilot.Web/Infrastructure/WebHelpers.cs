using System.Security.Claims;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Infrastructure.Identity;
using LeadPilot.Shared.Results;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace LeadPilot.Web.Infrastructure;

public abstract class AppController : Controller
{
    /// <summary>Maps a failed use-case result to the right HTTP semantics: 404 for unknown/other-tenant resources, 403 for permission, otherwise a message back on the form.</summary>
    protected IActionResult? Reject(Result result) => result.IsSuccess ? null : result.Error.Code switch
    {
        "not_found" => NotFound(),
        "forbidden" => Forbid(),
        _ => null,
    };

    protected void AddError(Result result) => ModelState.AddModelError(string.Empty, result.Error.Message);

    protected void Success(string message) => TempData["Success"] = message;
    protected void Failure(string message) => TempData["Error"] = message;

    protected void Crumbs(params (string Title, string? Url)[] crumbs) => ViewData["Breadcrumbs"] = crumbs.AsEnumerable();

    protected static string? Pick(string? value, int max = 200) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];

    /// <summary>Replaces the cookie principal with one whose active-tenant claim is <paramref name="tenantId"/>.</summary>
    protected async Task SignInIntoTenantAsync(SignInManager<ApplicationUser> signIn, ApplicationUser user, Guid tenantId, bool persistent = false)
    {
        var principal = await signIn.CreateUserPrincipalAsync(user);
        ((ClaimsIdentity)principal.Identity!).ReplaceTenantClaim(tenantId);
        await HttpContext.SignInAsync(IdentityConstants.ApplicationScheme, principal, new AuthenticationProperties { IsPersistent = persistent });
    }
}

public static class PagerExtensions
{
    public static IEnumerable<int> PageWindow(int page, int totalPages)
    {
        var start = Math.Max(1, page - 2);
        var end = Math.Min(totalPages, page + 2);
        for (var i = start; i <= end; i++) yield return i;
    }
}

public sealed record PagerModel(int Page, int TotalPages, long Total, string Action, IDictionary<string, string?> Query);
