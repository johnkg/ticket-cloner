using Microsoft.Extensions.Options;
using TicketCloner.Api.Configuration;
using TicketCloner.Api.OAuth;

namespace TicketCloner.Api.Atlassian;

/// <summary>
/// Everything needed to reach one tenant on this request: how to authorise, the
/// site for links, and where the REST calls go.
///
/// The three travel together because they are decided together - a Bearer
/// token is only meaningful against the cloud id it was resolved for, so
/// pairing one user's token with another's REST base has to be impossible
/// rather than merely unlikely.
/// </summary>
public sealed record TenantAccess(
    IAtlassianCredential Credential,
    Uri SiteUri,
    Uri ApiBaseUri)
{
    public static string ItemKey(Tenant tenant) => $"TenantAccess:{tenant}";

    /// <summary>
    /// Nothing to authorise with. The REST base is the site itself, which is
    /// where an unauthenticated call would go if one were ever made - it is
    /// not, because the endpoint filter answers 401 first.
    /// </summary>
    public static TenantAccess None(TenantOptions settings) =>
        new(NoCredential.Instance, settings.SiteUri, settings.SiteUri);
}

/// <summary>
/// Resolves that from the signed-in OAuth session. There is no other source
/// any more: the tool stores no API token, and the request headers that once
/// let a caller bring its own went with it.
///
/// Called from <see cref="AtlassianCredentialsFilter"/>, which runs before every
/// handler that touches a tenant. That is deliberate: refreshing a token has to
/// be awaited, and doing it there keeps AtlassianClientFactory synchronous - so
/// none of the six services that build clients had to change shape for OAuth.
/// </summary>
public sealed class TenantAccessResolver(
    IOptions<AtlassianOptions> atlassian,
    IOptions<OAuthOptions> oauth,
    TokenProvider tokens,
    OAuthSession session)
{
    public async Task<TenantAccess> ResolveAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        var settings = tenant == Tenant.Source ? atlassian.Value.Source : atlassian.Value.Target;

        if (session.User is not { } user)
        {
            return TenantAccess.None(settings);
        }

        var signedIn = await tokens.ValidTokensAsync(new TokenKey(user), cancellationToken);

        // One account-level grant can serve both tenants, or only one of them.
        // Reaching a tenant is a property of the grant, not of having signed in
        // at all - so a session with no cloud id for THIS tenant is refused
        // exactly as a signed-out one is, rather than authorising against a
        // site it cannot see.
        if (signedIn?.CloudIdFor(tenant) is not { } cloudId)
        {
            return TenantAccess.None(settings);
        }

        return new TenantAccess(
            new BearerCredential(signedIn.AccessToken),
            settings.SiteUri,
            oauth.Value.ApiBaseUriFor(cloudId));
    }
}
