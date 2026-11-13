extern alias ApiApp;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using LeadPilot.Application.Abstractions;
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

/// <summary>Skips when LEADPILOT_TEST_RABBITMQ (a RabbitMQ host, e.g. "localhost") is not set. A skipped test is NOT a passed test.</summary>
public sealed class RequiresRabbitMqFactAttribute : FactAttribute
{
    public const string EnvVar = "LEADPILOT_TEST_RABBITMQ";
    public RequiresRabbitMqFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RequiresMySqlFactAttribute.EnvVar))) Skip = $"Set {RequiresMySqlFactAttribute.EnvVar} to run integration tests.";
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVar))) Skip = $"RabbitMQ not available: set {EnvVar} (e.g. 'localhost') to run the broker tests. SKIPPED, not passed.";
    }
}

/// <summary>Creates one throw-away database per test run; apps are started against it and migrate on startup.</summary>
public sealed class AppFixture : IAsyncLifetime
{
    public const string AdminEmail = "admin@example.com";
    public const string AdminPassword = "Adm1n!Passw0rd#";

    private readonly string _dbName = "leadpilot_test_" + Guid.NewGuid().ToString("N")[..12];
    private string? _adminConn;
    public EmailOutbox Outbox { get; } = new();
    public FakeScript Script { get; } = new();
    public RecordingScheduler Scheduler { get; } = new();
    public RecordingMessagingScheduler Messaging { get; } = new();
    public OffsetClock Clock { get; } = new();
    public FakeWahaServer Waha { get; private set; } = default!;
    /// <summary>Per-run cache for expensive shared test data (a tenant is limited to 5 WhatsApp accounts, so tests share one).</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<object>>> Cache { get; } = new();
    public string RabbitPrefix { get; } = "lptest." + Guid.NewGuid().ToString("N")[..10];
    public bool RabbitMqAvailable => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RequiresRabbitMqFactAttribute.EnvVar));
    public TestWorld World { get; private set; } = default!;
    public WebApplicationFactory<Program>? Web { get; private set; }
    public WebApplicationFactory<ApiApp::Program>? Api { get; private set; }

    public async Task InitializeAsync()
    {
        _adminConn = Environment.GetEnvironmentVariable(RequiresMySqlFactAttribute.EnvVar);
        if (string.IsNullOrEmpty(_adminConn)) return;

        using (var c = new MySqlConnection(_adminConn))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE `{_dbName}`";
            cmd.ExecuteNonQuery();
        }
        var cs = new MySqlConnectionStringBuilder(_adminConn) { Database = _dbName }.ConnectionString;
        Waha = await FakeWahaServer.StartAsync();

        // Environment variables are read by the minimal-hosting builder before the app is built.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", cs);
        Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "true");
        Environment.SetEnvironmentVariable("Seed__AdminEmail", AdminEmail);
        Environment.SetEnvironmentVariable("Seed__AdminPassword", AdminPassword);
        Environment.SetEnvironmentVariable("Registration__AllowSelfRegistration", "true");
        Environment.SetEnvironmentVariable("RateLimiting__ApiPermitLimit", "100000"); // all test clients share one IP
        Environment.SetEnvironmentVariable("RateLimiting__AuthPermitLimit", "1000"); // the shared test client IP would otherwise trip the 20/min limit

        Environment.SetEnvironmentVariable("App__PublicWebUrl", "http://localhost");
        // messaging: the fake WAHA lives on loopback, sends are not slowed down by typing simulation, retries are quick, queues are isolated per run
        Environment.SetEnvironmentVariable("Messaging__Waha__AllowPrivateHosts", "true");
        Environment.SetEnvironmentVariable("Messaging__Waha__ReadRetries", "1");
        Environment.SetEnvironmentVariable("Messaging__Waha__RetryBaseDelayMs", "5");
        Environment.SetEnvironmentVariable("Messaging__Waha__TimeoutSeconds", "2");
        Environment.SetEnvironmentVariable("Messaging__Waha__BreakerFailureThreshold", "1000");
        Environment.SetEnvironmentVariable("Messaging__PublicWebhookBaseUrl", "https://leadpilot.test");
        Environment.SetEnvironmentVariable("Messaging__TypingMillisecondsPerChar", "0");
        Environment.SetEnvironmentVariable("Messaging__PerTenantDailyLimit", "0");
        if (RabbitMqAvailable)
        {
            Environment.SetEnvironmentVariable("RabbitMq__Host", Environment.GetEnvironmentVariable(RequiresRabbitMqFactAttribute.EnvVar));
            Environment.SetEnvironmentVariable("RabbitMq__Prefix", RabbitPrefix);
            Environment.SetEnvironmentVariable("RabbitMq__RetryDelaysSeconds__0", "1");
            Environment.SetEnvironmentVariable("RabbitMq__RetryDelaysSeconds__1", "1");
            Environment.SetEnvironmentVariable("RabbitMq__RetryDelaysSeconds__2", "1");
        }
        Environment.SetEnvironmentVariable("Crm__StorageRoot", Path.Combine(Path.GetTempPath(), "leadpilot-test-files-" + _dbName));
        Environment.SetEnvironmentVariable("Crm__MaxExportRows", "100");
        // Re-validate the security stamp (and rebuild the principal) on EVERY request, so tests prove the active-tenant claim survives that refresh.
        Environment.SetEnvironmentVariable("Security__StampValidationMinutes", "0");

        // Test double: capture outgoing e-mail so invitation links can be followed. Tests only.
        void UseOutbox(IWebHostBuilder b) => b.UseEnvironment("Development").ConfigureTestServices(s =>
        {
            s.RemoveAll<IEmailSender>();
            s.AddSingleton<IEmailSender>(Outbox);
            DiscoveryKit.RegisterTestServices(s, Script, Scheduler);   // TEST-ONLY fake connector, recording scheduler, no-network website enricher
            MessagingKit.RegisterTestServices(s, Clock, Messaging);    // TEST-ONLY adjustable clock and recording job scheduler (jobs are run by the tests, deterministically)
        });
        Web = new WebApplicationFactory<Program>().WithWebHostBuilder(UseOutbox);
        Web.CreateClient().Dispose(); // boots the app: migrates + seeds the catalog, platform tenant and platform owner
        Api = new WebApplicationFactory<ApiApp::Program>().WithWebHostBuilder(UseOutbox);
        World = await TestWorld.BuildAsync(Web.Services);
    }

    public async Task DisposeAsync()
    {
        Web?.Dispose();
        Api?.Dispose();
        if (Waha is not null) await Waha.DisposeAsync();
        if (string.IsNullOrEmpty(_adminConn)) return;
        if (RabbitMqAvailable) await MessagingKit.DeleteQueuesAsync(RabbitPrefix);
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
