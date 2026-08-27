using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace TicketCloner.Api.OAuth;

/// <summary>One site a grant reaches, before it is matched to a tenant.</summary>
public sealed record AccessibleSite(string CloudId, string Host);

/// <summary>
/// The two calls to Atlassian's authorisation server.
///
/// A client of its own, pointed at auth.atlassian.com rather than at either
/// tenant, because it must never carry a tenant credential - the same reasoning
/// that keeps the source and target clients apart.
/// </summary>
public sealed class AtlassianOAuthClient(
    HttpClient http,
    IOptions<OAuthOptions> options,
    TimeProvider clock,
    ILogger<AtlassianOAuthClient> logger)
{
    /// <summary>
    /// Where to send the browser. 3LO offers no way to preselect a site - the
    /// person chooses on Atlassian's own screen - so which site was granted is
    /// only knowable afterwards, which is what ResolveCloudIdAsync is for.
    /// </summary>
    public Uri AuthorizeUri(string state)
    {
        var query = new Dictionary<string, string?>
        {
            ["audience"] = "api.atlassian.com",
            ["client_id"] = Options.ClientId,
            ["scope"] = Options.Scopes,
            ["redirect_uri"] = Options.CallbackUrl,
            ["state"] = state,
            ["response_type"] = "code",

            // Without this Atlassian may skip consent on a repeat sign-in and
            // return no refresh token, which looks like it worked until an hour
            // later.
            ["prompt"] = "consent",
        };

        var parts = query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value ?? "")}");

        return new Uri(
            new Uri(Options.AuthorizationServer.TrimEnd('/') + "/"),
            "authorize?" + string.Join("&", parts));
    }

    /// <summary>
    /// Every site this grant reaches.
    ///
    /// Reporting rather than judging: with an account-level grant one token can
    /// serve several sites, so the caller matches them against the tenants it
    /// knows about instead of this deciding that one particular site was the
    /// only acceptable answer.
    /// </summary>
    public async Task<IReadOnlyList<AccessibleSite>> ResolveSitesAsync(
        string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(new Uri(Options.ApiGateway.TrimEnd('/') + "/"), "oauth/token/accessible-resources"));

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await http.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new OAuthTokenException(
                $"Could not list the sites this grant reaches ({(int)response.StatusCode}).",
                "resources");
        }

        var resources =
            await response.Content.ReadFromJsonAsync<AccessibleResource[]>(cancellationToken) ?? [];

        return resources
            .Where(resource => Uri.TryCreate(resource.Url, UriKind.Absolute, out _))
            .Select(resource => new AccessibleSite(resource.Id, new Uri(resource.Url).Host))
            .ToList();
    }

    /// <summary>Trades the code the callback received for a first set of tokens.</summary>
    public Task<TokenGrant> ExchangeCodeAsync(string code, CancellationToken cancellationToken) =>
        PostAsync(
            new
            {
                grant_type = "authorization_code",
                client_id = Options.ClientId,
                client_secret = Options.ClientSecret,
                code,
                redirect_uri = Options.CallbackUrl,
            },
            "exchange an authorization code",
            previousRefreshToken: null,
            cancellationToken);

    /// <summary>
    /// Trades a refresh token for a new pair. The response carries a NEW refresh
    /// token and the one sent here stops working, so the caller must persist
    /// what comes back.
    /// </summary>
    public Task<TokenGrant> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        PostAsync(
            new
            {
                grant_type = "refresh_token",
                client_id = Options.ClientId,
                client_secret = Options.ClientSecret,
                refresh_token = refreshToken,
            },
            "refresh an access token",
            previousRefreshToken: refreshToken,
            cancellationToken);

    private OAuthOptions Options => options.Value;

    private async Task<TokenGrant> PostAsync(
        object body,
        string attempt,
        string? previousRefreshToken,
        CancellationToken cancellationToken)
    {
        if (!Options.IsConfigured)
        {
            throw new OAuthTokenException(
                $"Cannot {attempt}: OAuth:ClientId, OAuth:ClientSecret and OAuth:CallbackUrl " +
                "must all be set.");
        }

        using var response = await http.PostAsJsonAsync("oauth/token", body, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // The body names the reason - invalid_grant for a refresh token that
            // has already been used or revoked - and is worth having in the log,
            // but it is the authorisation server's text and never a secret of
            // ours.
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);

            logger.LogWarning(
                "Atlassian refused to {Attempt}: {Status} {Detail}",
                attempt, (int)response.StatusCode, detail);

            throw new OAuthTokenException(
                $"Atlassian refused to {attempt} ({(int)response.StatusCode}). {detail}");
        }

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
            ?? throw new OAuthTokenException($"Atlassian returned no body when asked to {attempt}.");

        // Defensive: the spec allows a refresh response to omit a new refresh
        // token, in which case the old one stays valid. Atlassian rotates, so
        // this should not fire - but dropping the only refresh token we have
        // because a field was absent would sign the user out for nothing.
        var refreshToken = token.RefreshToken ?? previousRefreshToken;

        if (token.RefreshToken is null && previousRefreshToken is not null)
        {
            logger.LogInformation(
                "The refresh response carried no new refresh token; keeping the existing one.");
        }

        if (string.IsNullOrWhiteSpace(token.AccessToken) || string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new OAuthTokenException(
                $"Atlassian's response to {attempt} was missing a token. " +
                "offline_access must be among the requested scopes or no refresh token is issued.");
        }

        return new TokenGrant(
            token.AccessToken,
            refreshToken,
            clock.GetUtcNow().AddSeconds(token.ExpiresIn),
            token.Scope ?? "");
    }

    private sealed record AccessibleResource(string Id, string Url, string Name);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("scope")] string? Scope);
}
