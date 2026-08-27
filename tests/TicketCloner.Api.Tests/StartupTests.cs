using System.Net;
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
