using System.Text.Json.Nodes;
using TicketCloner.Api.Atlassian;
using TicketCloner.Api.Tests.Infrastructure;

namespace TicketCloner.Api.Tests;

/// <summary>
/// The tool reads source-company and never writes to it. That is enforced in the
/// client rather than left to convention, so a future endpoint cannot quietly
/// acquire the ability however it is wired up.
/// </summary>
public class ReadOnlySourceTests
{
    private static (AtlassianClient Client, StubAtlassian Stub) ClientFor(Tenant tenant)
    {
        var stub = new StubAtlassian(_ => StubAtlassian.Json("{}"));
        var host = tenant == Tenant.Source
            ? "https://source-domain.atlassian.net/"
            : "https://target-domain.atlassian.net/";

        var http = new HttpClient(stub) { BaseAddress = new Uri(host) };
        return (new AtlassianClient(http, tenant, new AtlassianCredentials("a@b.com", "token")), stub);
    }

    [Theory]
    [InlineData("rest/api/3/issue")]
    [InlineData("rest/api/3/issue/SOURCE_PROJECT-1234/comment")]
    [InlineData("rest/api/3/issue/SOURCE_PROJECT-1234/attachments")]
    public async Task Writing_to_the_source_is_refused(string path)
    {
        var (client, stub) = ClientFor(Tenant.Source);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.PostAsync<JsonNode>(path, new { }, CancellationToken.None));

        Assert.Contains("only ever reads from the source", refused.Message);

        // Refused before anything left the process, not after.
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Jql_search_is_allowed_on_the_source_because_it_is_a_read()
    {
        // The one POST that does not modify anything.
        var (client, stub) = ClientFor(Tenant.Source);

        await client.PostAsync<JsonNode>("rest/api/3/search/jql", new { jql = "project = SOURCE_PROJECT" }, CancellationToken.None);

        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Reads_are_allowed_on_the_source()
    {
        var (client, stub) = ClientFor(Tenant.Source);

        await client.GetAsync<JsonNode>("rest/api/3/issue/SOURCE_PROJECT-1234", CancellationToken.None);

        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Writing_to_the_target_is_allowed()
    {
        var (client, stub) = ClientFor(Tenant.Target);

        await client.PostAsync<JsonNode>("rest/api/3/issue", new { }, CancellationToken.None);

        Assert.Single(stub.Requests);
    }
}
