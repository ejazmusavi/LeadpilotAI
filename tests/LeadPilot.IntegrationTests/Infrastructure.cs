extern alias ApiApp;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MySqlConnector;

namespace LeadPilot.IntegrationTests;

/// <summary>Skips when LEADPILOT_TEST_CONNECTION (admin connection to a MySQL 8 server) is not set.</summary>
public sealed class RequiresMySqlFactAttribute : FactAttribute
{
    public const string EnvVar = "LEADPILOT_TEST_CONNECTION";
    public RequiresMySqlFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVar)))
            Skip = $"Set {EnvVar} (e.g. 'Server=localhost;User=root;Password=...;') to run MySQL integration tests.";
    }
}

/// <summary>Creates one throw-away database per test run; apps are started against it and migrate on startup.</summary>
public sealed class AppFixture : IAsyncLifetime
{
    public const string AdminEmail = "admin@example.com";
    public const string AdminPassword = "Adm1n!Passw0rd#";

    private readonly string _dbName = "leadpilot_test_" + Guid.NewGuid().ToString("N")[..12];
    private string? _adminConn;
    public WebApplicationFactory<Program>? Web { get; private set; }
    public WebApplicationFactory<ApiApp::Program>? Api { get; private set; }

    public Task InitializeAsync()
    {
        _adminConn = Environment.GetEnvironmentVariable(RequiresMySqlFactAttribute.EnvVar);
        if (string.IsNullOrEmpty(_adminConn)) return Task.CompletedTask;

        using (var c = new MySqlConnection(_adminConn))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE `{_dbName}`";
            cmd.ExecuteNonQuery();
        }
        var cs = new MySqlConnectionStringBuilder(_adminConn) { Database = _dbName }.ConnectionString;

        // Environment variables are read by the minimal-hosting builder before the app is built.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", cs);
        Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "true");
        Environment.SetEnvironmentVariable("Seed__AdminEmail", AdminEmail);
        Environment.SetEnvironmentVariable("Seed__AdminPassword", AdminPassword);
        Environment.SetEnvironmentVariable("Registration__AllowSelfRegistration", "true");
        Environment.SetEnvironmentVariable("RateLimiting__AuthPermitLimit", "1000"); // the shared test client IP would otherwise trip the 20/min limit

        Web = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        Web.CreateClient().Dispose(); // boots the app: migrates + seeds
        Api = new WebApplicationFactory<ApiApp::Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        Web?.Dispose();
        Api?.Dispose();
        if (string.IsNullOrEmpty(_adminConn)) return;
        MySqlConnection.ClearAllPools();
        await using var c = new MySqlConnection(_adminConn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS `{_dbName}`";
        await cmd.ExecuteNonQueryAsync();
    }

    public HttpClient NewWebClient() => Web!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    public static async Task<string> GetAntiForgeryTokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(m.Success, "antiforgery token not found on " + url);
        return m.Groups[1].Value;
    }
}

[CollectionDefinition("app")]
public class AppCollection : ICollectionFixture<AppFixture>;
