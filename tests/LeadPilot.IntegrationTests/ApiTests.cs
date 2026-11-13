extern alias ApiApp;
using System.Net;
using System.Net.Http.Json;

namespace LeadPilot.IntegrationTests;

[Collection("app")]
public class ApiTests(AppFixture app)
{
    [RequiresMySqlFact]
    public async Task Swagger_document_is_available()
    {
        var res = await app.Api!.CreateClient().GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("/api/v1/system/info", await res.Content.ReadAsStringAsync());
    }

    [RequiresMySqlFact]
    public async Task System_info_is_anonymous()
    {
        var res = await app.Api!.CreateClient().GetAsync("/api/v1/system/info");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("LeadPilot.Api", body.GetProperty("name").GetString());
    }

    [RequiresMySqlFact]
    public async Task Ready_health_check_reports_database_healthy() =>
        Assert.Equal(HttpStatusCode.OK, (await app.Api!.CreateClient().GetAsync("/health/ready")).StatusCode);

    [RequiresMySqlFact]
    public async Task Unknown_route_without_token_is_unauthorized_not_a_server_error() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Api!.CreateClient().GetAsync("/api/v1/nope")).StatusCode);

    [RequiresMySqlFact]
    public async Task Forged_bearer_token_is_rejected()
    {
        var c = app.Api!.CreateClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", "eyJhbGciOiJIUzI1NiJ9.e30.invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/nope")).StatusCode);
    }
}
