using System.Net;
using System.Net.Http.Json;
using TicketCloner.Api.Atlassian;
using TicketCloner.Api.Contracts;
using TicketCloner.Api.Tests.Infrastructure;

namespace TicketCloner.Api.Tests;

/// <summary>
/// The tests that matter most in this project. Two tenants, one signed-in
/// grant, and the worst realistic bug is a request for one site going to the
/// other's cloud id - or worse, a write landing on the source.
///
/// Under OAuth every call goes to the same host, api.atlassian.com, with the
/// same Bearer token. The cloud id in the path is the whole of the isolation,
/// which is why these assert on it and never on the host.
/// </summary>
public class TenantIsolationTests
{
    [Fact]
    public async Task Source_calls_go_to_the_source_cloud_id_with_the_session_token()
    {
        var stub = new StubAtlassian();
        using var app = new TestApp(stub, signedIn: true);
        using var client = app.CreateClient();

        await client.GetFromJsonAsync<IdentityResponse>("/api/source/me");

        var sent = stub.LastRequest;
        Assert.Equal("api.atlassian.com", sent.Host);
        Assert.StartsWith($"/ex/jira/{TestApp.SourceCloudId}/", sent.Path);
        Assert.Equal(Tenant.Source, sent.Tenant);
        Assert.True(sent.CarriedBearer(TestApp.AccessToken));
    }

    [Fact]
    public async Task Target_calls_go_to_the_target_cloud_id_with_the_session_token()
    {
        var stub = new StubAtlassian();
        using var app = new TestApp(stub, signedIn: true);
        using var client = app.CreateClient();

        await client.GetFromJsonAsync<IdentityResponse>("/api/target/me");

        var sent = stub.LastRequest;
        Assert.Equal("api.atlassian.com", sent.Host);
        Assert.StartsWith($"/ex/jira/{TestApp.TargetCloudId}/", sent.Path);
        Assert.Equal(Tenant.Target, sent.Tenant);
        Assert.True(sent.CarriedBearer(TestApp.AccessToken));
    }

    [Fact]
    public async Task Both_tenants_can_be_called_in_one_session_without_crossing_over()
    {
        // The failure this catches is a shared client whose base address is
        // mutated per call: the first request looks right and the second
        // inherits the first one's cloud id.
        var stub = new StubAtlassian();
        using var app = new TestApp(stub, signedIn: true);
        using var client = app.CreateClient();

        await client.GetFromJsonAsync<IdentityResponse>("/api/source/me");
        await client.GetFromJsonAsync<IdentityResponse>("/api/target/me");

        Assert.Equal(2, stub.Requests.Count);
        Assert.Equal(Tenant.Source, stub.Requests[0].Tenant);
        Assert.Equal(Tenant.Target, stub.Requests[1].Tenant);
    }

    [Fact]
    public async Task A_grant_that_reaches_only_the_target_cannot_call_the_source()
    {
        // The dangerous version of this is not a 401: it is a call that goes
        // out anyway, to the target's cloud id, for an endpoint that asked for
        // the source - which would read the wrong site and report it as the
        // right one.
        var stub = new StubAtlassian();
        using var app = new TestApp(stub, signedIn: true, grants: [Tenant.Target]);
        using var client = app.CreateClient();

        var response = await client.GetAsync("/api/source/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task A_grant_that_reaches_only_the_source_cannot_call_the_target()
    {
        var stub = new StubAtlassian();
        using var app = new TestApp(stub, signedIn: true, grants: [Tenant.Source]);
        using var client = app.CreateClient();

        var response = await client.GetAsync("/api/target/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Email_visibility_is_reported_per_tenant()
    {
        // source-site returned an email for 0 of 25 sampled users; target-site for
        // 23 of 25. That asymmetry is why users cannot be matched by email in
        // the direction that matters, so the API states it rather than assuming.
        var stub = new StubAtlassian();
        using var app = new TestApp(stub, signedIn: true);
        using var client = app.CreateClient();

        var source = await client.GetFromJsonAsync<IdentityResponse>("/api/source/me");
        var target = await client.GetFromJsonAsync<IdentityResponse>("/api/target/me");

        Assert.NotNull(source);
        Assert.NotNull(target);
        Assert.False(source.EmailVisible);
        Assert.True(target.EmailVisible);
    }
}
