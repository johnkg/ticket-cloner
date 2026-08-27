namespace TicketCloner.Api.OAuth;

/// <summary>
/// The 3LO app's own identity.
///
/// NOT ValidateOnStart-validated, and deliberately so: holding no OAuth
/// configuration is a legitimate state. The tool runs on API tokens today and
/// must keep running that way on any instance where nobody has registered an
/// app - holding no OAuth registration is a legitimate state.
/// </summary>
public sealed class OAuthOptions
{
    public const string SectionName = "OAuth";

    public string ClientId { get; init; } = "";

    /// <summary>
    /// Never put this in appsettings.json: that file is not gitignored and is
    /// copied into the publish output. user-secrets in development, an
    /// environment variable (OAuth__ClientSecret) or a vault in production.
    /// </summary>
    public string ClientSecret { get; init; } = "";

    /// <summary>
    /// Must match the callback registered on the app EXACTLY - scheme, host,
    /// port, path and trailing slash. A mismatch fails as invalid_redirect_uri
    /// with nothing useful to go on.
    /// </summary>
    public string CallbackUrl { get; init; } = "";

    /// <summary>
    /// Atlassian's authorisation server. Configurable only so a test can point
    /// it at a stub; there is no reason to change it in a real deployment.
    /// </summary>
    public string AuthorizationServer { get; init; } = "https://auth.atlassian.com/";

    /// <summary>
    /// Where the REST calls go once signed in. Also configurable only for tests.
    /// </summary>
    public string ApiGateway { get; init; } = "https://api.atlassian.com/";

    /// <summary>
    /// Where the callback sends the browser when it is finished.
    ///
    /// "/" is right in production: the SPA is served from wwwroot by this same
    /// app, so the callback lands back on the page that started it.
    ///
    /// GOTCHA: in development it is NOT. Vite serves the SPA on another port and
    /// this app has no wwwroot at all, so "/" answers a completed sign-in with a
    /// 404 - the callback URL is registered against the API's port, so Atlassian
    /// has no choice but to return here first. appsettings.Development.json
    /// points this at Vite. The session cookie survives the hop: cookies are
    /// scoped by host and ignore the port.
    /// </summary>
    public string PostSignInUrl { get; init; } = "/";

    /// <summary>The callback's landing page, with its outcome attached.</summary>
    public string ReturnUrl(string query) => PostSignInUrl.TrimEnd('/') + "/?" + query;

    /// <summary>
    /// Space-separated, and sent on the authorize URL rather than set in the
    /// developer console.
    ///
    /// GOTCHA: without offline_access no refresh token is ever issued, and the
    /// session dies silently an hour after sign-in with nothing to renew it.
    ///
    /// GOTCHA: the classic scopes do NOT cover the Jira Software agile API.
    /// Listing a board's sprints under read:jira-work alone answered
    /// `401 {"code":401,"message":"Unauthorized; scope does not match"}` on
    /// 18/09/2026. Sprints need the two granular Jira Software scopes, and
    /// they have to be enabled on the console app as well or the authorize
    /// request itself fails with invalid_scope for everybody.
    /// </summary>
    public string Scopes { get; init; } =
        "read:jira-work write:jira-work read:jira-user " +
        "read:sprint:jira-software read:board-scope:jira-software offline_access";

    /// <summary>The REST base for one site, once its cloud id is known.</summary>
    public Uri ApiBaseUriFor(string cloudId) =>
        new(new Uri(ApiGateway.TrimEnd('/') + "/"), $"ex/jira/{cloudId}/");

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(CallbackUrl);
}
