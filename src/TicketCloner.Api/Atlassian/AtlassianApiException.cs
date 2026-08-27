using System.Net;

namespace TicketCloner.Api.Atlassian;

/// <summary>
/// An upstream Atlassian call failed. Carries which tenant it was, because
/// "403 Forbidden" means very different things depending on which site said it.
/// </summary>
public sealed class AtlassianApiException(
    Tenant tenant,
    HttpMethod method,
    string path,
    HttpStatusCode statusCode,
    string? responseBody)
    : Exception($"{tenant} tenant returned {(int)statusCode} for {method} {path}.")
{
    public Tenant Tenant { get; } = tenant;
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Path { get; } = path;

    /// <summary>Truncated - Jira error bodies can be very large.</summary>
    public string? ResponseBody { get; } = Truncate(responseBody);

    /// <summary>
    /// Jira Cloud rate-limits under bulk load and expects the caller to back
    /// off. ReleaseTool never needed this; copying batches does.
    /// </summary>
    public bool IsRateLimit => StatusCode == HttpStatusCode.TooManyRequests;

    private static string? Truncate(string? body) =>
        body is { Length: > 2000 } ? body[..2000] + "..." : body;
}
