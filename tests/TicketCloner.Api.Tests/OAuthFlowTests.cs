using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using TicketCloner.Api.Contracts;
using TicketCloner.Api.Tests.Infrastructure;

namespace TicketCloner.Api.Tests;

/// <summary>
/// The sign-in round trip, hosted for real. What is faked is Atlassian, not any
/// part of this app - so the route order, the cookies and the credentials filter
/// are all the ones that ship.
///
/// The app holds an ACCOUNT-LEVEL grant: one token can reach both sites, and one
/// button signs in to both. Atlassian still consents to one site at a time, so
/// the interesting cases are what happens between the two consents.
/// </summary>
public class OAuthFlowTests
{
    private const string Callback = "http://localhost:5002/api/auth/callback";

    private const string SourceHost = "https://source.example.invalid";
    private const string TargetHost = "https://target.example.invalid";

    private static Dictionary<string, string?> WithOAuth() => new()
    {
        ["OAuth:ClientId"] = "client-id",
        ["OAuth:ClientSecret"] = "client-secret",
        ["OAuth:CallbackUrl"] = Callback,

        // Pinned, not inherited: appsettings.Development.json points this at
        // Vite, and these tests are about the outcome rather than the port.
        ["OAuth:PostSignInUrl"] = "/",
    };

    /// <summary>
    /// Atlassian: the token endpoint and accessible-resources. The sites listed
    /// are what the person consented to.
    /// </summary>
    private static StubAtlassian Atlassian(params string[] grantedSites)
    {
        var sites = grantedSites.Length == 0 ? [TargetHost] : grantedSites;

        var resources = string.Join(",", sites.Select((site, index) =>
            $$"""{ "id": "cloud-{{index}}", "url": "{{site}}", "name": "granted" }"""));

        return new StubAtlassian(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/oauth/token", StringComparison.OrdinalIgnoreCase))
            {
                return StubAtlassian.Json("""
                    {
                      "access_token": "access-1",
                      "refresh_token": "refresh-1",
                      "expires_in": 3600,
                      "scope": "read:jira-work offline_access"
                    }
                    """);
            }

            if (path.EndsWith("/accessible-resources", StringComparison.OrdinalIgnoreCase))
            {
                return StubAtlassian.Json($"[{resources}]");
            }

            return StubAtlassian.Json("{}");
        });
    }

    private static HttpClient Unfollowing(TestApp app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>One value out of a query string.</summary>
    private static string Query(Uri uri, string key)
    {
        var pair = uri.Query.TrimStart('?').Split('&')
            .First(part => part.StartsWith(key + "=", StringComparison.Ordinal));

        return Uri.UnescapeDataString(pair[(key.Length + 1)..]);
    }

    /// <summary>
    /// One consent round: start, then hand the callback the state that went out.
    /// Reading it off the redirect rather than out of the cookie is what a
    /// browser effectively does, and the test client keeps its own cookie jar.
    /// </summary>
    private static async Task<HttpResponseMessage> Consent(
        HttpClient client, string start = "/api/auth/start", string? state = null)
    {
        var started = await client.GetAsync(start);
        var minted = Query(started.Headers.Location!, "state");

        return await client.GetAsync($"/api/auth/callback?code=the-code&state={state ?? minted}");
    }

    // ------------------------------------------------------------------ start

    [Fact]
    public async Task Start_redirects_to_atlassian_with_the_scopes_and_a_state()
    {
        using var app = new TestApp(Atlassian(), WithOAuth());
        using var client = Unfollowing(app);

        var response = await client.GetAsync("/api/auth/start");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        var to = response.Headers.Location!.ToString();
        Assert.StartsWith("https://auth.atlassian.com/authorize?", to);
        Assert.Contains("audience=api.atlassian.com", to);
        Assert.Contains("client_id=client-id", to);
        Assert.Contains("response_type=code", to);

        // Without offline_access no refresh token is ever issued and the session
        // dies an hour later with nothing to renew it.
        var scopes = Uri.UnescapeDataString(to);
        Assert.Contains("offline_access", scopes);

        // The classic scopes do not reach the agile API: listing sprints under
        // read:jira-work alone came back "scope does not match" on 18/09/2026.
        Assert.Contains("read:sprint:jira-software", scopes);
        Assert.Contains("read:board-scope:jira-software", scopes);

        // The callback must match what is registered on the app, exactly.
        Assert.Contains(Uri.EscapeDataString(Callback), to);

        Assert.NotEmpty(Query(response.Headers.Location!, "state"));
    }

    [Fact]
    public async Task Start_says_so_plainly_when_no_app_is_registered()
    {
        // The default state of this tool: API tokens, no 3LO app.
        using var app = new TestApp(Atlassian());
        using var client = Unfollowing(app);

        var response = await client.GetAsync("/api/auth/start");

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    // --------------------------------------------------------------- callback

    [Fact]
    public async Task One_consent_covering_both_sites_finishes_the_sign_in()
    {
        // What an account-level grant is for: the sites are already on the grant,
        // so there is nothing left to ask.
        var stub = Atlassian(SourceHost, TargetHost);
        using var app = new TestApp(stub, WithOAuth());
        using var client = Unfollowing(app);

        var response = await Consent(client);

        Assert.Equal("/?auth=ok", response.Headers.Location!.ToString());

        Assert.Contains(stub.Requests, r => r.Path.EndsWith("/oauth/token", StringComparison.Ordinal));
        Assert.Contains(stub.Requests, r => r.Path.EndsWith("/accessible-resources", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_grant_reaching_only_one_site_asks_again_for_the_other()
    {
        // Atlassian consents to one site at a time. Rather than making somebody
        // notice a half-done sign-in and press the button again, the callback
        // starts the next round itself.
        using var app = new TestApp(Atlassian(TargetHost), WithOAuth());
        using var client = Unfollowing(app);

        var response = await Consent(client);

        Assert.Equal("/api/auth/start?round=2", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Choosing_the_same_site_twice_stops_asking_and_names_what_is_missing()
    {
        // The guard against bouncing somebody around for ever. The stub grants
        // the same site both times, which is what picking wrong twice looks like.
        using var app = new TestApp(Atlassian(TargetHost), WithOAuth());
        using var client = Unfollowing(app);

        var first = await Consent(client);
        Assert.Equal("/api/auth/start?round=2", first.Headers.Location!.ToString());

        var second = await Consent(client, first.Headers.Location!.ToString());

        Assert.Equal("/?auth=partial&missing=source", second.Headers.Location!.ToString());
    }

    [Fact]
    public async Task A_site_that_is_neither_of_ours_is_refused_rather_than_stored()
    {
        // A perfectly valid grant for somebody else's Jira. Storing it would
        // authorise this tool against a site it must never touch.
        using var app = new TestApp(Atlassian("https://unrelated.example.invalid"), WithOAuth());
        using var client = Unfollowing(app);

        var response = await Consent(client);

        Assert.Equal("/?auth=error&reason=wrong-site", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task A_callback_whose_state_does_not_match_is_refused()
    {
        // A cross-site request forgery on the sign-in, or a replayed callback.
        var stub = Atlassian(SourceHost, TargetHost);
        using var app = new TestApp(stub, WithOAuth());
        using var client = Unfollowing(app);

        var response = await Consent(client, state: "not-the-state-we-minted");

        Assert.Equal("/?auth=error&reason=state", response.Headers.Location!.ToString());

        // Nothing was exchanged: the check happens before the code is spent.
        Assert.DoesNotContain(stub.Requests, r => r.Path.EndsWith("/oauth/token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_consent_comes_back_as_denied_rather_than_an_error_page()
    {
        using var app = new TestApp(Atlassian(), WithOAuth());
        using var client = Unfollowing(app);

        await client.GetAsync("/api/auth/start");

        var response = await client.GetAsync("/api/auth/callback?error=access_denied");

        Assert.Equal("/?auth=error&reason=denied", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task The_callback_returns_to_wherever_the_spa_is_served_from()
    {
        // In development that is not this app: Vite serves the SPA on another
        // port and there is no wwwroot here, so returning to "/" answers a
        // completed sign-in with a 404.
        var settings = WithOAuth();
        settings["OAuth:PostSignInUrl"] = "http://localhost:5174/";

        using var app = new TestApp(Atlassian(SourceHost, TargetHost), settings);
        using var client = Unfollowing(app);

        var response = await Consent(client);

        Assert.Equal("http://localhost:5174/?auth=ok", response.Headers.Location!.ToString());
    }

    // ----------------------------------------------------------------- status

    [Fact]
    public async Task Status_reports_nothing_available_when_no_app_is_registered()
    {
        using var app = new TestApp(Atlassian());
        using var client = app.CreateClient();

        var status = await client.GetFromJsonAsync<AuthStatusResponse>("/api/auth/status");

        Assert.NotNull(status);
        Assert.False(status.Available);
        Assert.False(status.Source.SignedIn);
        Assert.False(status.Target.SignedIn);
    }

    [Fact]
    public async Task Status_reports_both_sites_once_the_grant_reaches_both()
    {
        using var app = new TestApp(Atlassian(SourceHost, TargetHost), WithOAuth());
        using var client = Unfollowing(app);

        await Consent(client);

        // Same client, so the same session cookie - this is the signed-in
        // browser asking about itself.
        var status = await client.GetFromJsonAsync<AuthStatusResponse>("/api/auth/status");

        Assert.NotNull(status);
        Assert.True(status.Available);
        Assert.True(status.Source.SignedIn);
        Assert.True(status.Target.SignedIn);

        // Different sites, so different cloud ids - one token, two hosts.
        Assert.NotEqual(status.Source.CloudId, status.Target.CloudId);
    }

    [Fact]
    public async Task A_half_finished_grant_reports_only_the_site_it_reaches()
    {
        using var app = new TestApp(Atlassian(TargetHost), WithOAuth());
        using var client = Unfollowing(app);

        await Consent(client);

        var status = await client.GetFromJsonAsync<AuthStatusResponse>("/api/auth/status");

        Assert.NotNull(status);
        Assert.True(status.Target.SignedIn);
        Assert.False(status.Source.SignedIn);
    }

    [Fact]
    public async Task Signing_out_drops_the_whole_grant()
    {
        // One grant, so it goes all at once. Signing out of one tenant alone
        // would mean holding a token and pretending not to.
        using var app = new TestApp(Atlassian(SourceHost, TargetHost), WithOAuth());
        using var client = Unfollowing(app);

        await Consent(client);
        await client.PostAsync("/api/auth/signout", null);

        var status = await client.GetFromJsonAsync<AuthStatusResponse>("/api/auth/status");

        Assert.NotNull(status);
        Assert.False(status.Source.SignedIn);
        Assert.False(status.Target.SignedIn);
    }

    // ----------------------------------------------- the guarded endpoints

    [Fact]
    public async Task An_unsigned_in_caller_is_told_to_sign_in_when_oauth_is_available()
    {
        // "Not signed in" and "nobody configured a token" are different problems
        // with different fixes, and one message for both sends people to edit a
        // config file when all they needed was to press sign in.
        using var app = new TestApp(Atlassian(), WithOAuth());
        using var client = app.CreateClient();

        var response = await client.GetAsync("/api/target/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("/api/auth/start", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Without_an_app_the_401_says_nobody_can_sign_in()
    {
        // Pointing at /api/auth/start would send people to a 501. The fix is
        // an administrator's, and the message says so.
        using var app = new TestApp(Atlassian());
        using var client = app.CreateClient();

        var response = await client.GetAsync("/api/target/me");

        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("OAuth:ClientId", body);
        Assert.DoesNotContain("/api/auth/start", body);
        Assert.DoesNotContain("X-Target-Token", body);
    }

    [Fact]
    public async Task The_callback_route_is_reachable_rather_than_swallowed_by_the_api_catch_all()
    {
        // Registered after app.Map("/api/{**path}") it would 404; outside /api
        // it would hit the SPA fallback and answer Atlassian with index.html.
        using var app = new TestApp(Atlassian(), WithOAuth());
        using var client = Unfollowing(app);

        var response = await client.GetAsync("/api/auth/callback");

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
    }
}
