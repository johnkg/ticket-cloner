using System.Net.Http.Headers;

namespace TicketCloner.Api.Atlassian;

/// <summary>
/// How one tenant's requests are authorised.
///
/// An interface rather than a string because the client asks for a finished
/// header and never learns what produced it. There used to be two schemes
/// behind it - an API token sent as Basic, and OAuth sent as Bearer - and the
/// seam is what let the second land without touching a call site. The Basic
/// one is gone: the tool stores no API token and no longer takes one on request
/// headers either, so the only way to reach a tenant is to have signed in.
/// </summary>
public interface IAtlassianCredential
{
    /// <summary>False when there is nothing to authorise with.</summary>
    bool IsPresent { get; }

    /// <summary>The Authorization header, or null when nothing is present.</summary>
    AuthenticationHeaderValue? ToAuthorizationHeader();
}

/// <summary>
/// An OAuth 3LO access token, issued by <see cref="TenantAccessResolver"/> for
/// a signed-in browser whose grant reaches the tenant in question.
/// </summary>
public sealed record BearerCredential(string AccessToken) : IAtlassianCredential
{
    public bool IsPresent => !string.IsNullOrWhiteSpace(AccessToken);

    public AuthenticationHeaderValue? ToAuthorizationHeader() =>
        IsPresent ? new AuthenticationHeaderValue("Bearer", AccessToken) : null;

    /// <summary>Safe to log. Never render the token itself.</summary>
    public override string ToString() =>
        IsPresent ? "OAuth access token (present)" : "(no credentials)";
}

/// <summary>
/// Nothing to authorise with: the browser has not signed in, or its grant does
/// not reach this tenant. Sends NO header, never an empty one - an empty
/// Authorization reads as a real attempt and comes back as a 401 from
/// Atlassian, which is a far worse error than the one the endpoint filter
/// raises before a request ever gets that far.
/// </summary>
public sealed class NoCredential : IAtlassianCredential
{
    public static readonly NoCredential Instance = new();

    private NoCredential()
    {
    }

    public bool IsPresent => false;

    public AuthenticationHeaderValue? ToAuthorizationHeader() => null;

    public override string ToString() => "(no credentials)";
}
