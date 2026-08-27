using System.Net;
using System.Net.Http.Json;
using TicketCloner.Api.Contracts;
using TicketCloner.Api.Tests.Infrastructure;

namespace TicketCloner.Api.Tests;

public class CredentialsTests
{
    [Theory]
    [InlineData("/api/source/me")]
    [InlineData("/api/target/me")]
    public async Task Guarded_endpoints_401_without_credentials(string path)
    {
        using var app = new TestApp(new StubAtlassian());
        using var client = app.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Nothing_is_sent_upstream_when_the_credential_check_fails()
    {
        // The filter has to run BEFORE the handler, or a request with no
        // credential still reaches Atlassian and comes back as a confusing 401
        // from them rather than a clear one from us.
        var stub = new StubAtlassian();
        using var app = new TestApp(stub);
        using var client = app.CreateClient();

        await client.GetAsync("/api/source/me");

        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Headers_satisfy_the_credential_requirement()
    {
        var stub = new StubAtlassian();
        using var app = new TestApp(stub);
        using var client = app.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/source/me");
        request.Headers.Add("X-Source-Email", "you@example.com");
        request.Headers.Add("X-Source-Token", "header-token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Headers_take_precedence_over_stored_configuration()
    {
        // So a caller acts as themselves. On an instance holding a service
        // account, the created issue's history should name whoever pressed the
        // button.
        var stub = new StubAtlassian();
        using var app = new TestApp(stub, new Dictionary<string, string?>
        {
            ["Credentials:SourceEmail"] = "you@example.com",
            ["Credentials:SourceApiToken"] = "stored-token",
        });
        using var client = app.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/source/me");
        request.Headers.Add("X-Source-Email", "you@example.com");
        request.Headers.Add("X-Source-Token", "caller-token");

        await client.SendAsync(request);

        Assert.True(stub.LastRequest.CarriedCredentialsFor("you@example.com", "caller-token"));
        Assert.False(stub.LastRequest.CarriedCredentialsFor("you@example.com", "stored-token"));
    }

    [Fact]
    public async Task Stored_credentials_are_used_when_the_caller_sends_none()
    {
        var stub = new StubAtlassian();
        using var app = new TestApp(stub, new Dictionary<string, string?>
        {
            ["Credentials:TargetEmail"] = "you@example.com",
            ["Credentials:TargetApiToken"] = "stored-token",
        });
        using var client = app.CreateClient();

        var identity = await client.GetFromJsonAsync<IdentityResponse>("/api/target/me");

        Assert.NotNull(identity);
        Assert.True(stub.LastRequest.CarriedCredentialsFor("you@example.com", "stored-token"));
    }
}
