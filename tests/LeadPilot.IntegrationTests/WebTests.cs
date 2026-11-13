using System.Net;

namespace LeadPilot.IntegrationTests;

[Collection("app")]
public class WebTests(AppFixture app)
{
    [RequiresMySqlFact]
    public async Task Migrations_applied_and_ready_health_is_healthy()
    {
        var client = app.NewWebClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Anonymous_request_to_dashboard_redirects_to_login()
    {
        var res = await app.NewWebClient().GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/Account/Login", res.Headers.Location!.AbsolutePath);
    }

    [RequiresMySqlFact]
    public async Task Login_page_renders_and_sends_correlation_id()
    {
        var res = await app.NewWebClient().GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(res.Headers.Contains("X-Correlation-ID"));
    }

    private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient c, string email, string password)
    {
        var token = await AppFixture.GetAntiForgeryTokenAsync(c, "/Account/Login");
        return await c.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email, ["Password"] = password, ["__RequestVerificationToken"] = token
        }));
    }

    [RequiresMySqlFact]
    public async Task Login_with_valid_credentials_then_dashboard_then_logout()
    {
        var c = app.NewWebClient();
        var login = await PostLoginAsync(c, AppFixture.AdminEmail, AppFixture.AdminPassword);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var dash = await c.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, dash.StatusCode);
        Assert.Contains("Dashboard", await dash.Content.ReadAsStringAsync());

        var token = await AppFixture.GetAntiForgeryTokenAsync(c, "/");
        var logout = await c.PostAsync("/Account/Logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);

        var after = await c.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Login_with_wrong_password_is_rejected_with_generic_message()
    {
        var res = await PostLoginAsync(app.NewWebClient(), AppFixture.AdminEmail, "Wrong!Passw0rd#");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Invalid email or password.", await res.Content.ReadAsStringAsync());
    }

    [RequiresMySqlFact]
    public async Task Login_with_unknown_email_gives_same_message()
    {
        var res = await PostLoginAsync(app.NewWebClient(), "nobody@example.com", "Wrong!Passw0rd#");
        Assert.Contains("Invalid email or password.", await res.Content.ReadAsStringAsync());
    }

    [RequiresMySqlFact]
    public async Task Post_without_antiforgery_token_is_rejected()
    {
        var res = await app.NewWebClient().PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Email"] = AppFixture.AdminEmail, ["Password"] = AppFixture.AdminPassword }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Forgot_password_gives_identical_response_for_known_and_unknown_email()
    {
        var c = app.NewWebClient();
        async Task<string?> Post(string email)
        {
            var token = await AppFixture.GetAntiForgeryTokenAsync(c, "/Account/ForgotPassword");
            var r = await c.PostAsync("/Account/ForgotPassword", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["Email"] = email, ["__RequestVerificationToken"] = token }));
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            return r.Headers.Location?.OriginalString;
        }
        Assert.Equal(await Post(AppFixture.AdminEmail), await Post("ghost@example.com"));
    }

    [RequiresMySqlFact]
    public async Task Registration_creates_user_who_can_sign_in()
    {
        var c = app.NewWebClient();
        var email = $"user{Guid.NewGuid():N}@example.com";
        var token = await AppFixture.GetAntiForgeryTokenAsync(c, "/Account/Register");
        var res = await c.PostAsync("/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["DisplayName"] = "Test User", ["Email"] = email, ["Password"] = "Str0ng!Passw0rd", ["ConfirmPassword"] = "Str0ng!Passw0rd",
            ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Weak_password_registration_is_rejected()
    {
        var c = app.NewWebClient();
        var token = await AppFixture.GetAntiForgeryTokenAsync(c, "/Account/Register");
        var res = await c.PostAsync("/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["DisplayName"] = "Weak", ["Email"] = $"weak{Guid.NewGuid():N}@example.com", ["Password"] = "short", ["ConfirmPassword"] = "short",
            ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode); // form re-rendered with errors
        Assert.Contains("at least 10", await res.Content.ReadAsStringAsync());
    }

    [RequiresMySqlFact]
    public async Task Lockout_after_repeated_failures()
    {
        var c = app.NewWebClient();
        var email = $"lock{Guid.NewGuid():N}@example.com";
        var t = await AppFixture.GetAntiForgeryTokenAsync(c, "/Account/Register");
        await c.PostAsync("/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["DisplayName"] = "L", ["Email"] = email, ["Password"] = "Str0ng!Passw0rd", ["ConfirmPassword"] = "Str0ng!Passw0rd", ["__RequestVerificationToken"] = t }));
        var c2 = app.NewWebClient();
        HttpResponseMessage last = default!;
        for (var i = 0; i < 6; i++) last = await PostLoginAsync(c2, email, "Bad!Passw0rd#" + i);
        Assert.Contains("temporarily locked", await last.Content.ReadAsStringAsync());
    }
}
