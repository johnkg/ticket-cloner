using System.Net.Http.Json;
using TicketCloner.Api.Contracts;
using TicketCloner.Api.Tests.Infrastructure;

namespace TicketCloner.Api.Tests;

/// <summary>
/// The tests that matter most in this project. Two tenants, two credentials,
/// and the worst realistic bug is a request going to one site carrying the
/// other's token - or worse, a write landing on the source.
/// </summary>
public class TenantIsolationTests
{
    private static Dictionary<string, string?> BothTenantsConfigured() => new()
    {
        ["Credentials:SourceEmail"] = "source-account@example.com",
        ["Credentials:SourceApiToken"] = "source-only-token",
        ["Credentials:TargetEmail"] = "target-account@example.com",
        ["Credentials:TargetApiToken"] = "target-only-token",
    };

    [Fact]
    public async Task Source_calls_go_to_the_source_host_with_the_source_credential()
    {
        var stub = new StubAtlassian();
        using var app = new TestApp(stub, BothTenantsConfigured());
        using var client = app.CreateClient();

        await client.GetFromJsonAsync<IdentityResponse>("/api/source/me");

        var sent = stub.LastRequest;
        Assert.Equal("source-domain.atlassian.net", sent.Host);
        Assert.True(sent.CarriedCredentialsFor("source-account@example.com", "source-only-token"));
    }

    [Fact]
    public async Task Target_calls_go_to_the_target_host_with_the_target_credential()
    {
        var stub = new StubAtlassian();
        using var app = new TestApp(stub, BothTenantsConfigured());
        using var client = app.CreateClient();

        await client.GetFromJsonAsync<IdentityResponse>("/api/target/me");

        var sent = stub.LastRequest;
        Assert.Equal("target-domain.atlassian.net", sent.Host);
        Assert.True(sent.CarriedCredentialsFor("target-account@example.com", "target-only-token"));
    }

    [Fact]
    public async Task A_source_call_never_carries_the_target_token()
    {
        var stub = new StubAtlassian();
        using var app = new TestApp(stub, BothTenantsConfigured());
        using var client = app.CreateClient();

        await client.GetFromJsonAsync<IdentityResponse>("/api/source/me");

        Assert.False(
            stub.LastRequest.CarriedCredentialsFor("target-account@example.com", "target-only-token"),
            "the target's credential reached the source tenant");
    }

    [Fact]
    public async Task A_target_call_never_carries_the_source_token()
    {
        var stub = new StubAtlassian();
        using var app = new TestApp(stub, BothTenantsConfigured());
        using var client = app.CreateClient();

        await client.GetFromJsonAsync<IdentityResponse>("/api/target/me");

        Assert.False(
            stub.LastRequest.CarriedCredentialsFor("source-account@example.com", "source-only-token"),
            "the source's credential reached the target tenant");
    }

    [Fact]
    public async Task Both_tenants_can_be_called_in_one_session_without_crossing_over()
    {
        // The failure this catches is a shared client whose BaseAddress or
        // default Authorization header is mutated per call: the first request
        // looks right and the second inherits the first one's identity.
        var stub = new StubAtlassian();
        using var app = new TestApp(stub, BothTenantsConfigured());
        using var client = app.CreateClient();

        await client.GetFromJsonAsync<IdentityResponse>("/api/source/me");
        await client.GetFromJsonAsync<IdentityResponse>("/api/target/me");

        Assert.Equal(2, stub.Requests.Count);

        var toSource = stub.Requests[0];
        var toTarget = stub.Requests[1];

        Assert.Equal("source-domain.atlassian.net", toSource.Host);
        Assert.True(toSource.CarriedCredentialsFor("source-account@example.com", "source-only-token"));

        Assert.Equal("target-domain.atlassian.net", toTarget.Host);
        Assert.True(toTarget.CarriedCredentialsFor("target-account@example.com", "target-only-token"));
    }

    [Fact]
    public async Task Email_visibility_is_reported_per_tenant()
    {
        // source-company returned an email for 0 of 25 sampled users; your-company for
        // 23 of 25. That asymmetry is why users cannot be matched by email in
        // the direction that matters, so the API states it rather than assuming.
        var stub = new StubAtlassian();
        using var app = new TestApp(stub, BothTenantsConfigured());
        using var client = app.CreateClient();

        var source = await client.GetFromJsonAsync<IdentityResponse>("/api/source/me");
        var target = await client.GetFromJsonAsync<IdentityResponse>("/api/target/me");

        Assert.NotNull(source);
        Assert.NotNull(target);
        Assert.False(source.EmailVisible);
        Assert.True(target.EmailVisible);
    }
}
