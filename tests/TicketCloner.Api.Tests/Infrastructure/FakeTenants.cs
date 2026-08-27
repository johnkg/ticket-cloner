using System.Net;

namespace TicketCloner.Api.Tests.Infrastructure;

/// <summary>
/// Both tenants, including the write routes. Configurable per test, because the
/// interesting apply cases are the ones where something goes wrong partway.
/// </summary>
public sealed class FakeTenants
{
    /// <summary>Non-null makes the duplicate check find an existing copy.</summary>
    public string? ExistingCopyKey { get; set; }

    /// <summary>accountIds that exist on the TARGET. Anything else 404s, which
    /// is what drives the fallback to the running account.</summary>
    public HashSet<string> KnownTargetAccountIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string RunningAccountId { get; set; } = "acc-running";

    public string CreatedKey { get; set; } = "TARGET_PROJECT-9001";

    public bool FailCreate { get; set; }

    public bool FailComments { get; set; }

    public bool FailAttachments { get; set; }

    /// <summary>Makes the second description pass fail, once the attachments
    /// are already up - the copy exists and only its images are wrong.</summary>
    public bool FailDescriptionUpdate { get; set; }

    /// <summary>Every description written to the target, in order. There are
    /// two on any issue whose description embeds an attachment.</summary>
    public List<string> DescriptionsWritten { get; } = [];

    /// <summary>Source keys the source tenant no longer returns.</summary>
    public HashSet<string> MissingSourceKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Func<HttpRequestMessage, HttpResponseMessage> Handler => request =>
    {
        var isSource = request.RequestUri!.Host.Contains("source-company", StringComparison.OrdinalIgnoreCase);
        var path = request.RequestUri.AbsolutePath;

        return isSource ? Source(request, path) : Target(request, path);
    };

    private HttpResponseMessage Source(HttpRequestMessage request, string path)
    {
        if (MissingSourceKeys.Any(key => path.Contains($"/issue/{key}", StringComparison.OrdinalIgnoreCase)))
        {
            return Error(HttpStatusCode.NotFound, "Issue does not exist or you do not have permission to see it.");
        }

        if (path.Contains("/attachment/content/", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("pretend png bytes"u8.ToArray()),
            };
        }

        return SourceTenantFake.Handler(request);
    }

    private HttpResponseMessage Target(HttpRequestMessage request, string path)
    {
        if (path.Contains("/createmeta/", StringComparison.OrdinalIgnoreCase))
        {
            return TargetTenantFake.Handler(request);
        }

        if (path.EndsWith("/rest/api/3/myself", StringComparison.OrdinalIgnoreCase))
        {
            return StubAtlassian.Json(
                $$"""{"accountId":"{{RunningAccountId}}","displayName":"Running Account","emailAddress":"you@example.com"}""");
        }

        if (path.EndsWith("/rest/api/3/user", StringComparison.OrdinalIgnoreCase))
        {
            var accountId = request.RequestUri!.Query
                .TrimStart('?')
                .Split('&')
                .Select(pair => pair.Split('=', 2))
                .Where(pair => pair is ["accountId", _])
                .Select(pair => Uri.UnescapeDataString(pair[1]))
                .FirstOrDefault() ?? "";

            return KnownTargetAccountIds.Contains(accountId)
                ? StubAtlassian.Json($$"""{"accountId":"{{accountId}}","displayName":"Known Person"}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        }

        if (path.EndsWith("/search/jql", StringComparison.OrdinalIgnoreCase))
        {
            return StubAtlassian.Json(ExistingCopyKey is null
                ? """{"issues":[],"isLast":true}"""
                : $$"""{"issues":[{"key":"{{ExistingCopyKey}}"}],"isLast":true}""");
        }

        if (path.EndsWith("/attachments", StringComparison.OrdinalIgnoreCase))
        {
            return FailAttachments
                ? Error(HttpStatusCode.RequestEntityTooLarge, "attachment exceeds the size cap")
                // The id is deliberately nothing like the media node's UUID:
                // rewriting has to go through the file name to connect them.
                // 'content' is the canonical URL Jira returns, and what an
                // external media node ends up pointing at.
                : StubAtlassian.Json(
                    """
                    [{
                      "id": "70001",
                      "filename": "screenshot.png",
                      "content": "https://target-domain.atlassian.net/rest/api/3/attachment/content/70001"
                    }]
                    """);
        }

        if (path.EndsWith("/comment", StringComparison.OrdinalIgnoreCase))
        {
            return FailComments
                ? Error(HttpStatusCode.Forbidden, "no permission to comment")
                : StubAtlassian.Json("""{"id":"80001"}""");
        }

        if (path.EndsWith("/remotelink", StringComparison.OrdinalIgnoreCase))
        {
            return StubAtlassian.Json("""{"id":90001}""");
        }

        // The second description pass, once the attachments exist and the
        // media nodes have real target ids to point at.
        if (request.Method == HttpMethod.Put &&
            path.Contains("/rest/api/3/issue/", StringComparison.OrdinalIgnoreCase))
        {
            if (FailDescriptionUpdate)
            {
                return Error(HttpStatusCode.BadRequest, "cannot update the description");
            }

            Record(request);
            return new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new StringContent("") };
        }

        if (path.EndsWith("/rest/api/3/issue", StringComparison.OrdinalIgnoreCase))
        {
            if (FailCreate)
            {
                return Error(HttpStatusCode.BadRequest, "Field 'customfield_10300' cannot be set");
            }

            Record(request);
            return StubAtlassian.Json($$"""{"id":"10500","key":"{{CreatedKey}}"}""");
        }

        return Error(HttpStatusCode.NotFound, $"unexpected target path: {path}");
    }

    private void Record(HttpRequestMessage request)
    {
        var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
        var description = body is null ? null : System.Text.Json.Nodes.JsonNode.Parse(body)?["fields"]?["description"];

        if (description is not null)
        {
            DescriptionsWritten.Add(description.ToJsonString());
        }
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string detail) =>
        new(status) { Content = new StringContent($$"""{"errorMessages":["{{detail}}"]}""") };
}
