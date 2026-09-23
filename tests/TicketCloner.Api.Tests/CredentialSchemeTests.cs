using TicketCloner.Api.Atlassian;
using TicketCloner.Api.Tests.Infrastructure;

namespace TicketCloner.Api.Tests;

/// <summary>
/// Authorisation is the credential's decision, not the client's. The client
/// asks for a finished header and carries whatever it is given - which is how
/// OAuth replaced API tokens without a call site changing, and why the seam
/// stays now that Bearer is the only scheme left.
/// </summary>
public class CredentialSchemeTests
{
    private static readonly Uri Site = new("https://target.example.invalid/");

    private static (AtlassianClient Client, StubAtlassian Stub) ClientWith(
        IAtlassianCredential credential)
    {
        var stub = new StubAtlassian(_ => StubAtlassian.Json("{}"));
        var http = new HttpClient(stub) { BaseAddress = Site };

        return (new AtlassianClient(http, Tenant.Target, credential, Site, Site), stub);
    }

    [Fact]
    public async Task An_oauth_access_token_is_sent_as_bearer()
    {
        var (client, stub) = ClientWith(new BearerCredential("access-token-value"));

        await client.GetJsonAsync("rest/api/3/myself", CancellationToken.None);

        Assert.Equal("Bearer access-token-value", stub.LastRequest.Authorization);
    }

    [Fact]
    public async Task Nothing_present_sends_no_header_rather_than_an_empty_one()
    {
        // An empty Basic header reads as a real attempt and comes back as a 401
        // from Atlassian, which is a much worse error than the one the endpoint
        // filter raises before a request ever gets this far.
        var (client, stub) = ClientWith(NoCredential.Instance);

        await client.GetJsonAsync("rest/api/3/myself", CancellationToken.None);

        Assert.Null(stub.LastRequest.Authorization);
    }

    [Fact]
    public void A_credential_never_renders_its_secret()
    {
        Assert.DoesNotContain(
            "access-token-value", new BearerCredential("access-token-value").ToString());
    }

    [Fact]
    public void An_empty_bearer_token_is_not_present()
    {
        Assert.False(new BearerCredential("").IsPresent);
        Assert.Null(new BearerCredential("").ToAuthorizationHeader());
    }
}
