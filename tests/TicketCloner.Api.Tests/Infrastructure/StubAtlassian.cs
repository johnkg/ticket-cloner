using System.Net;
using System.Text;

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

    public bool CarriedCredentialsFor(string email, string apiToken)
    {
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{apiToken}"));
        return Authorization == $"Basic {expected}";
    }
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

    /// <summary>A plausible GET /rest/api/3/myself, keyed off the host.</summary>
    private static HttpResponseMessage Myself(HttpRequestMessage request)
    {
        var host = request.RequestUri!.Host;

        // source-company hides email addresses; your-company does not. Mirroring that
        // here keeps the tests honest about what each side can actually tell us.
        var email = host.Contains("source-company", StringComparison.OrdinalIgnoreCase)
            ? "null"
            : "\"you@example.com\"";

        return Json(
            $$"""
            {
              "accountId": "acc-{{host.Split('.')[0]}}",
              "displayName": "Test User",
              "emailAddress": {{email}},
              "accountType": "atlassian"
            }
            """);
    }
}
