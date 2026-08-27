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

        // The trap this guards: the project named TGT is on the target-site
        // tenant, and the project named Source Project is on the
        // source-site tenant. Anything that resolves a tenant by project name
        // gets it exactly backwards.
        Assert.Equal("source.example.invalid", config.Source.Host);
        Assert.Equal("SRC", config.Source.ProjectKey);

        Assert.Equal("target.example.invalid", config.Target.Host);
        Assert.Equal("TGT", config.Target.ProjectKey);
    }

    [Fact]
    public async Task Config_says_nothing_about_credentials_at_all()
    {
        // It used to report whether a token was configured and for which email.
        // The tool stores no token now, and whether a person can reach a tenant
        // is what /api/auth/status answers - so this endpoint describes the
        // wiring and nothing else. A credential echoed here would be a leak on
        // an endpoint that is deliberately unguarded.
        using var app = new TestApp(signedIn: true);
        using var client = app.CreateClient();

        var body = await client.GetStringAsync("/api/config");

        Assert.DoesNotContain(TestApp.AccessToken, body);
        Assert.DoesNotContain(TestApp.RefreshToken, body);
        Assert.DoesNotContain(TestApp.SourceCloudId, body);
        Assert.DoesNotContain(TestApp.TargetCloudId, body);

        Assert.DoesNotContain("configured", body, StringComparison.OrdinalIgnoreCase);
    }
}
