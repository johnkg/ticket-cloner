using System.Net;
using TicketCloner.Api.Tests.Infrastructure;

namespace TicketCloner.Api.Tests;

/// <summary>
/// Signing in is the only way to reach a tenant. These hold the two doors that
/// used to exist shut: a token in configuration, and a token on request
/// headers. Both once worked, both are gone, and both are the kind of thing
/// somebody reasonably assumes is still there.
/// </summary>
public class CredentialsTests
{
    [Theory]
    [InlineData("/api/source/me")]
    [InlineData("/api/target/me")]
    public async Task Guarded_endpoints_401_when_not_signed_in(string path)
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
    public async Task A_signed_in_session_satisfies_the_credential_requirement()
    {
        var stub = new StubAtlassian();
        using var app = new TestApp(stub, signedIn: true);
        using var client = app.CreateClient();

        var response = await client.GetAsync("/api/source/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task The_session_token_is_what_goes_on_the_wire()
    {
        // Whoever pressed the button, not a shared account: the created issue's
        // history should name a person.
        var stub = new StubAtlassian();
        using var app = new TestApp(stub, signedIn: true);
        using var client = app.CreateClient();

        await client.GetAsync("/api/source/me");

        Assert.True(stub.LastRequest.CarriedBearer(TestApp.AccessToken));
    }

    [Fact]
    public async Task A_session_cookie_that_points_at_nothing_is_not_signed_in()
    {
        // A restart empties the in-memory store but the browser still has its
        // cookie. That browser is signed out, not half signed in.
        var stub = new StubAtlassian();
        using var app = new TestApp(stub);
        using var client = app.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/source/me");
        request.Headers.Add("Cookie", "tc.session=nobody-by-this-name");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Credential_headers_no_longer_authenticate_anything()
    {
        // X-{tenant}-Email / X-{tenant}-Token let a caller act without a browser
        // until 17/09/2026. A script written against that contract must get a
        // clear 401 and reach nothing, not a request that goes out unsigned.
        var stub = new StubAtlassian();
        using var app = new TestApp(stub);
        using var client = app.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/source/me");
        request.Headers.Add("X-Source-Email", "someone@example.com");
        request.Headers.Add("X-Source-Token", "header-token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task A_credentials_section_left_in_configuration_authenticates_nothing()
    {
        // The section is gone from appsettings.json, but an older deployment or
        // a stale appsettings.Local.json can still carry one. It has to be inert
        // rather than quietly signing every request in as whoever it names.
        var stub = new StubAtlassian();
        using var app = new TestApp(stub, new Dictionary<string, string?>
        {
            ["Credentials:TargetEmail"] = "stored@example.com",
            ["Credentials:TargetApiToken"] = "stored-token",
        });
        using var client = app.CreateClient();

        var response = await client.GetAsync("/api/target/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(stub.Requests);
    }
}
