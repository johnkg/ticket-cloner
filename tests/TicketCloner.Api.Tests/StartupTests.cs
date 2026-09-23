using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using TicketCloner.Api.Tests.Infrastructure;

namespace TicketCloner.Api.Tests;

public class StartupTests
{
    [Fact]
    public async Task Health_reports_ok()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static TestApp Deployed() => new(
        configuration: new Dictionary<string, string?> { ["Https:Port"] = "443" },
        environment: Environments.Production);

    private static HttpClient Unfollowing(TestApp app, string baseAddress = "http://localhost") =>
        app.CreateClient(new WebApplicationFactoryClientOptions
        {
            // The redirect itself is the thing being asserted, so following it
            // would hide exactly what these tests exist to see.
            AllowAutoRedirect = false,
            BaseAddress = new Uri(baseAddress),
        });

    [Fact]
    public async Task Outside_development_plain_http_is_redirected_to_https()
    {
        // Credentials travel in request headers. An unredirected hop puts an
        // Atlassian API token on the wire in clear, which is the whole point.
        using var app = Deployed();
        using var client = Unfollowing(app);

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal("https://localhost/health", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Outside_development_an_https_response_carries_HSTS()
    {
        using var app = Deployed();

        // Not localhost: HstsOptions excludes localhost, 127.0.0.1 and [::1] by
        // default, so asserting the header against localhost proves nothing.
        using var client = Unfollowing(app, "https://ticketcloner.example");

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "max-age=", response.Headers.GetValues("Strict-Transport-Security").Single());
    }

    [Fact]
    public async Task Development_is_not_redirected_so_the_http_profile_still_runs()
    {
        // http://localhost:5002 has no certificate behind it. Redirecting there
        // would break every local run and every test in this suite.
        using var app = new TestApp();
        using var client = Unfollowing(app);

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_api_path_404s_instead_of_falling_back_to_the_spa()
    {
        // The SPA fallback is greedy. If it is registered ahead of the /api
        // catch-all, a mistyped endpoint answers 200 with index.html and the
        // caller sees HTML where it expected JSON.
        using var app = new TestApp();
        using var client = app.CreateClient();

        var response = await client.GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
