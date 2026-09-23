using System.Net;
using System.Text;
using TicketCloner.Api.Atlassian;

namespace TicketCloner.Api.Tests.Infrastructure;

/// <summary>What actually went out on the wire.</summary>
public sealed record CapturedRequest(
    HttpMethod Method,
    Uri Uri,
    string? Authorization,
    string? Body)
{
    public string Host => Uri.Host;

    public string Path => Uri.AbsolutePath;

    /// <summary>
    /// Which tenant this call was for. Under OAuth every call goes to the same
    /// host, api.atlassian.com, and the cloud id in the path is the only thing
    /// that says which site - so this is what a test asserts on, never Host.
    /// </summary>
    public Tenant? Tenant => StubAtlassian.TenantOf(Uri);

    public bool CarriedBearer(string accessToken) =>
        Authorization == $"Bearer {accessToken}";
}

/// <summary>
/// Stands in for both Atlassian sites. Only the outbound handler is swapped, so
/// the app under test is the real one - middleware order, the credentials
/// filters and options validation all run exactly as in production.
/// </summary>
public sealed class StubAtlassian(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    : HttpMessageHandler
{
    private readonly List<CapturedRequest> _requests = [];

    public IReadOnlyList<CapturedRequest> Requests => _requests;

    public CapturedRequest LastRequest => _requests[^1];

    public CapturedRequest RequestTo(string pathFragment) =>
        _requests.Last(request => request.Uri.ToString().Contains(pathFragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The tenant a URL is for. A signed-in call carries the cloud id at
    /// api.atlassian.com/ex/jira/{cloud id}/; anything still addressed to a
    /// site's own host is recognised too, so a fake can answer either shape.
    /// </summary>
    public static Tenant? TenantOf(Uri uri)
    {
        var path = uri.AbsolutePath;

        if (path.StartsWith($"/ex/jira/{TestApp.SourceCloudId}/", StringComparison.Ordinal))
        {
            return Tenant.Source;
        }

        if (path.StartsWith($"/ex/jira/{TestApp.TargetCloudId}/", StringComparison.Ordinal))
        {
            return Tenant.Target;
        }

        if (uri.Host.Contains("source-site", StringComparison.OrdinalIgnoreCase))
        {
            return Tenant.Source;
        }

        if (uri.Host.Contains("target-site", StringComparison.OrdinalIgnoreCase))
        {
            return Tenant.Target;
        }

        return null;
    }

    public static bool IsSource(HttpRequestMessage request) =>
        TenantOf(request.RequestUri!) == Tenant.Source;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Snapshot now: the client disposes the request once the call returns.
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        _requests.Add(new CapturedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            body));

        return respond?.Invoke(request) ?? Myself(request);
    }

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    /// <summary>A plausible GET /rest/api/3/myself, keyed off the tenant.</summary>
    private static HttpResponseMessage Myself(HttpRequestMessage request)
    {
        var source = IsSource(request);

        // source-site hides email addresses; target-site does not. Mirroring that
        // here keeps the tests honest about what each side can actually tell us.
        var email = source ? "null" : "\"someone@example.com\"";

        return Json(
            $$"""
            {
              "accountId": "acc-{{(source ? "source-site" : "target-site")}}",
              "displayName": "Test User",
              "emailAddress": {{email}},
              "accountType": "atlassian"
            }
            """);
    }
}
