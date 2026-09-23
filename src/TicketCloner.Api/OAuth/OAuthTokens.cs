using TicketCloner.Api.Atlassian;

namespace TicketCloner.Api.OAuth;

/// <summary>
/// Whose grant. One row per user, NOT per user and tenant.
///
/// The app is registered with an ACCOUNT-LEVEL grant, so a single grant covers
/// every site the person has consented to and one access token works against
/// all of them.
///
/// GOTCHA — keying this by tenant as well would be actively dangerous, not
/// merely redundant. Both rows would hold the same refresh token; refresh
/// tokens rotate, so refreshing either one kills the other row's copy and the
/// second tenant is signed out at its next refresh. Which site a token reaches
/// belongs on the row, not in the key.
/// </summary>
public readonly record struct TokenKey(string User);

/// <summary>
/// One Atlassian site the grant reaches, already matched to the tenant it
/// serves. The host is kept for logging and for saying, in a sentence a person
/// can act on, which site was actually granted.
/// </summary>
public sealed record GrantedSite(Tenant Tenant, string CloudId, string Host);

/// <summary>
/// What the authorisation server hands back. Carries no cloud id: the grant is
/// for an account, and which site it reaches is a separate question answered by
/// accessible-resources.
/// </summary>
public sealed record TokenGrant(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string Scope);

/// <summary>
/// One user's tokens for one tenant.
/// </summary>
/// <param name="RefreshToken">Rotates. Atlassian issues a new one on every
/// refresh and invalidates the one just used, so a refresh that is not
/// persisted signs the user out roughly an hour later - long enough for the
/// bug to look like something else entirely.</param>
/// <param name="Sites">Which of our tenants this grant reaches. Resolved from
/// accessible-resources at sign-in and carried forward across every refresh - a
/// refresh returns new tokens for the same account, never a different set of
/// sites.</param>
public sealed record OAuthTokens(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string Scope,
    IReadOnlyList<GrantedSite> Sites)
{
    public static OAuthTokens From(TokenGrant grant, IReadOnlyList<GrantedSite> sites) =>
        new(grant.AccessToken, grant.RefreshToken, grant.ExpiresAt, grant.Scope, sites);

    /// <summary>
    /// The same session, re-tokened. Keeping the sites by construction is what
    /// stops a refresh from quietly losing which sites these reach.
    /// </summary>
    public OAuthTokens With(TokenGrant grant) => From(grant, Sites);

    /// <summary>
    /// A later consent, which adds a site to the same account-level grant and
    /// issues fresh tokens for all of it.
    ///
    /// The union is deliberate rather than a straight replace: accessible-
    /// resources is expected to list the whole grant, but if it ever returned
    /// only the site just added, replacing would silently sign the user out of
    /// the other tenant. Newly resolved entries win on conflict.
    /// </summary>
    public OAuthTokens With(TokenGrant grant, IReadOnlyList<GrantedSite> sites)
    {
        var merged = sites.ToList();

        merged.AddRange(Sites.Where(known => merged.All(site => site.Tenant != known.Tenant)));

        return From(grant, merged);
    }

    public string? CloudIdFor(Tenant tenant) =>
        Sites.FirstOrDefault(site => site.Tenant == tenant)?.CloudId;

    public bool Reaches(Tenant tenant) => CloudIdFor(tenant) is not null;

    /// <summary>
    /// Refresh this far ahead of expiry. An access token that passes the check
    /// and then expires during the call it was fetched for is the failure this
    /// avoids, and a copy run makes a lot of calls.
    /// </summary>
    public static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    public bool IsUsableAt(DateTimeOffset now) => now + Skew < ExpiresAt;

    /// <summary>Safe to log. Never render either token.</summary>
    public override string ToString() =>
        $"reaches {(Sites.Count == 0 ? "nothing" : string.Join(", ", Sites.Select(site => site.Host)))}, " +
        $"expires {ExpiresAt:O}, scope '{Scope}' (tokens present)";
}

/// <summary>Raised when the authorisation server refuses an exchange or refresh.</summary>
/// <param name="reason">A short, non-secret token for the UI. The message may
/// carry detail from Atlassian and belongs in the log, not in a redirect.</param>
public sealed class OAuthTokenException(string message, string reason = "oauth") : Exception(message)
{
    public string Reason { get; } = reason;
}
