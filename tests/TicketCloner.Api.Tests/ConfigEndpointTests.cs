using System.Net;
using System.Net.Http.Json;
using TicketCloner.Api.Contracts;
using TicketCloner.Api.Tests.Infrastructure;

namespace TicketCloner.Api.Tests;

public class ConfigEndpointTests
{
    [Fact]
    public async Task Config_is_readable_without_credentials()
    {
        // The UI reads this before it has any credentials, to decide what to
        // prompt for. Guarding it would deadlock the first-run experience.
        using var app = new TestApp();
        using var client = app.CreateClient();

        var response = await client.GetAsync("/api/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Config_reports_each_tenant_by_host_not_by_project_name()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();

        var config = await client.GetFromJsonAsync<ConfigResponse>("/api/config");

        Assert.NotNull(config);

        // The trap this guards: the project named TARGET_PROJECT is on the YOUR_COMPANY
        // tenant, and the project named YOUR_SOURCE_PROJECT is on the
        // SOURCE_COMPANY tenant. Anything that resolves a tenant by project name
        // gets it exactly backwards.
        Assert.Equal("source-domain.atlassian.net", config.Source.Host);
        Assert.Equal("SOURCE_PROJECT", config.Source.ProjectKey);

        Assert.Equal("target-domain.atlassian.net", config.Target.Host);
        Assert.Equal("TARGET_PROJECT", config.Target.ProjectKey);
    }

    [Fact]
    public async Task Config_reports_configured_false_when_no_credentials_are_stored()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();

        var config = await client.GetFromJsonAsync<ConfigResponse>("/api/config");

        Assert.NotNull(config);
        Assert.False(config.Source.Configured);
        Assert.False(config.Target.Configured);
    }

    [Fact]
    public async Task Config_never_returns_a_token()
    {
        using var app = new TestApp(configuration: new Dictionary<string, string?>
        {
            ["Credentials:SourceEmail"] = "source@example.com",
            ["Credentials:SourceApiToken"] = "source-token-should-never-be-returned",
            ["Credentials:TargetEmail"] = "target@example.com",
            ["Credentials:TargetApiToken"] = "target-token-should-never-be-returned",
        });
        using var client = app.CreateClient();

        var body = await client.GetStringAsync("/api/config");

        Assert.DoesNotContain("source-token-should-never-be-returned", body);
        Assert.DoesNotContain("target-token-should-never-be-returned", body);

        // The emails are fine to show - they are how a user recognises which
        // account an instance is holding.
        Assert.Contains("source@example.com", body);
        Assert.Contains("target@example.com", body);
    }
}
