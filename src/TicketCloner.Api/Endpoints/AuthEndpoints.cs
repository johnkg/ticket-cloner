using Microsoft.Extensions.Options;
using TicketCloner.Api.Atlassian;
using TicketCloner.Api.Configuration;
using TicketCloner.Api.Contracts;
using TicketCloner.Api.OAuth;

namespace TicketCloner.Api.Endpoints;

/// <summary>
/// The OAuth 3LO sign-in.
///
/// Mapped inside the /api group on purpose. Registered anywhere after
/// app.Map("/api/{**path}") the callback would 404, and outside /api altogether
/// it would hit MapFallbackToFile and answer Atlassian's redirect with the SPA
/// shell - a sign-in that appears to work and silently stores nothing.
///
/// There is ONE sign-in for both tenants. The app holds an account-level grant,
/// so a single token can reach both sites, and asking a person which tenant they
/// are signing in to was asking them to answer a question the tool can work out
/// for itself. Whatever they consent to is matched against the configured hosts
/// and filed under whichever tenant it turns out to be; if that leaves a tenant
/// unreached, a second round asks for the rest.
/// </summary>
public static class AuthEndpoints
{
    /// <summary>
    /// One consent adds one site, so two rounds can be needed. More than that
    /// means the same site is being chosen repeatedly and another round would
    /// only bounce the person around again.
    /// </summary>
    private const int MaxRounds = 2;

    public static void MapAuth(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");

        // A redirect, not JSON: the browser has to leave the origin, and fetch()
        // cannot follow a cross-origin redirect to auth.atlassian.com. The UI
        // navigates here rather than calling it.
        auth.MapGet("/start", (
            int? round,
            IOptions<OAuthOptions> options,
            OAuthSession session,
            AtlassianOAuthClient oauth) =>
        {
            if (!options.Value.IsConfigured)
            {
                return Results.Problem(
                    title: "OAuth is not set up here",
                    detail: "OAuth:ClientId, OAuth:ClientSecret and OAuth:CallbackUrl must all be " +
                            "set before anyone can sign in. Until then the tool uses API tokens.",
                    statusCode: StatusCodes.Status501NotImplemented);
            }

            session.EnsureUser();

            return Results.Redirect(
                oauth.AuthorizeUri(session.StartState(round ?? 1)).ToString());
        });

        auth.MapGet("/callback", async (
            string? code,
            string? state,
            string? error,
            HttpContext http,
            OAuthSession session,
            AtlassianOAuthClient oauth,
            ITokenStore store,
            IOptions<AtlassianOptions> atlassian,
            IOptions<OAuthOptions> options,
            ILoggerFactory loggers,
            CancellationToken cancellationToken) =>
        {
            var logger = loggers.CreateLogger("TicketCloner.Api.Endpoints.AuthEndpoints");
            var oauthSettings = options.Value;

            // Taken before anything else can return: one shot, whatever happens.
            var pending = session.TakeState();

            if (error is not null)
            {
                logger.LogInformation("Sign-in was refused: {Error}", error);
                return Back(oauthSettings, "denied");
            }

            if (pending is not { } expected ||
                state is null ||
                !string.Equals(state, expected.State, StringComparison.Ordinal))
            {
                // Either a replay, or a callback nobody here started.
                logger.LogWarning("A callback arrived whose state did not match a pending sign-in");
                return Back(oauthSettings, "state");
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                return Back(oauthSettings, "nocode");
            }

            var user = session.EnsureUser();
            var tenants = atlassian.Value;

            try
            {
                var grant = await oauth.ExchangeCodeAsync(code, cancellationToken);
                var granted = await oauth.ResolveSitesAsync(grant.AccessToken, cancellationToken);

                var ours = MatchTenants(granted, tenants);

                if (ours.Count == 0)
                {
                    // A perfectly valid grant for a site that is neither of ours.
                    // Storing it would authorise this tool against a stranger's
                    // Jira, so it is refused before anything is written down.
                    logger.LogWarning(
                        "Consent granted {Granted}, which is neither {Source} nor {Target}",
                        Describe(granted), tenants.Source.SiteUri.Host, tenants.Target.SiteUri.Host);

                    return Back(oauthSettings, "wrong-site");
                }

                // Merged onto whatever is already held: the new grant is the one
                // that works, but a site consented to earlier must not be lost.
                var existing = store.Get(new TokenKey(user));

                var tokens = existing is null
                    ? OAuthTokens.From(grant, ours)
                    : existing.With(grant, ours);

                store.Put(new TokenKey(user), tokens);

                logger.LogInformation("Signed in; the grant now {Tokens}", tokens);

                var missing = Missing(tokens);

                if (missing is null)
                {
                    return Results.Redirect(oauthSettings.ReturnUrl("auth=ok"));
                }

                // One consent, one site. Ask again for the one still missing
                // rather than making somebody notice and press the button twice.
                if (expected.Round < MaxRounds)
                {
                    logger.LogInformation(
                        "The grant does not reach {Tenant} yet; asking again", missing);

                    // PathBase, not a bare "/api/...": mounted as an application
                    // inside another IIS site this app answers under a prefix,
                    // and an absolute path would leave it. IIS integration fills
                    // PathBase in; it is empty when the app is its own site.
                    return Results.Redirect(
                        $"{http.Request.PathBase}/api/auth/start?round={expected.Round + 1}");
                }

                logger.LogWarning(
                    "Gave up asking: the grant still does not reach {Tenant}", missing);

                return Results.Redirect(
                    oauthSettings.ReturnUrl(
                        $"auth=partial&missing={missing.ToString().ToLowerInvariant()}"));
            }
            catch (OAuthTokenException failed)
            {
                // The message can carry Atlassian's own text; it belongs in the
                // log, not in a URL that lands in browser history.
                logger.LogWarning(failed, "Sign-in failed");
                return Back(oauthSettings, failed.Reason);
            }
        });

        auth.MapGet("/status", async (
            IOptions<OAuthOptions> options,
            IOptions<AtlassianOptions> atlassian,
            OAuthSession session,
            TokenProvider tokens,
            CancellationToken cancellationToken) =>
        {
            // Through the provider, so a session whose token expired while the
            // tab sat open is refreshed rather than reported as dead.
            var stored = session.User is { } user
                ? await tokens.ValidTokensAsync(new TokenKey(user), cancellationToken)
                : null;

            AuthTenantStatus Status(Tenant tenant) => new(
                tenant.ToString().ToLowerInvariant(),
                stored?.Reaches(tenant) is true,
                stored?.CloudIdFor(tenant),
                stored?.Reaches(tenant) is true ? stored.ExpiresAt : null);

            return Results.Ok(new AuthStatusResponse(
                options.Value.IsConfigured,
                Status(Tenant.Source),
                Status(Tenant.Target)));
        });

        // POST: signing somebody out on a GET means a prefetch or a stray link
        // can do it.
        //
        // All or nothing, because there is only one grant to drop. Signing out
        // of one tenant alone would mean holding a token and pretending not to.
        auth.MapPost("/signout", (OAuthSession session, ITokenStore store) =>
        {
            if (session.User is { } user)
            {
                store.Remove(new TokenKey(user));
            }

            return Results.NoContent();
        });
    }

    /// <summary>
    /// Which of the granted sites are ours, and which tenant each one serves.
    /// Anything else is ignored rather than refused - a person can legitimately
    /// have access to sites this tool knows nothing about.
    /// </summary>
    private static List<GrantedSite> MatchTenants(
        IReadOnlyList<AccessibleSite> granted, AtlassianOptions tenants)
    {
        var wanted = new[]
        {
            (Tenant: Tenant.Source, Host: tenants.Source.SiteUri.Host),
            (Tenant: Tenant.Target, Host: tenants.Target.SiteUri.Host),
        };

        return (from site in granted
                from tenant in wanted
                where site.Host.Equals(tenant.Host, StringComparison.OrdinalIgnoreCase)
                select new GrantedSite(tenant.Tenant, site.CloudId, site.Host))
            .DistinctBy(site => site.Tenant)
            .ToList();
    }

    /// <summary>The first tenant the grant cannot reach, or null when it reaches both.</summary>
    private static Tenant? Missing(OAuthTokens tokens) =>
        !tokens.Reaches(Tenant.Source) ? Tenant.Source
        : !tokens.Reaches(Tenant.Target) ? Tenant.Target
        : null;

    private static string Describe(IReadOnlyList<AccessibleSite> granted) =>
        granted.Count == 0 ? "nothing" : string.Join(", ", granted.Select(site => site.Host));

    /// <summary>
    /// Back to the SPA with a short, non-secret reason. The callback is a
    /// browser navigation, so it has to end somewhere a person can look at.
    /// </summary>
    private static IResult Back(OAuthOptions oauth, string reason) =>
        Results.Redirect(oauth.ReturnUrl($"auth=error&reason={Uri.EscapeDataString(reason)}"));
}
