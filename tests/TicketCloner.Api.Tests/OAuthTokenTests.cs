using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketCloner.Api.Atlassian;
using TicketCloner.Api.OAuth;
using TicketCloner.Api.Tests.Infrastructure;

namespace TicketCloner.Api.Tests;

/// <summary>
/// The refresh path, which is the part of OAuth that fails quietly. An access
/// token lasts an hour, so a mistake here works perfectly through any short
/// session and signs everybody out later for no visible reason.
/// </summary>
public class OAuthTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly TokenKey ExampleUser = new("example-user");

    /// <summary>An account-level grant reaching both sites.</summary>
    private static readonly GrantedSite[] BothSites =
    [
        new(Tenant.Source, "cloud-source", "source.example.invalid"),
        new(Tenant.Target, "cloud-target", "target.example.invalid"),
    ];

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Utc;
    }

    private sealed class Harness : IDisposable
    {
        public FixedClock Clock { get; } = new(Now);

        public InMemoryTokenStore Store { get; } = new();

        public TokenRefreshLocks Locks { get; } = new();

        public StubAtlassian Stub { get; }

        public AtlassianOAuthClient OAuth { get; }

        public TokenProvider Provider { get; }

        public Harness(
            Func<HttpRequestMessage, HttpResponseMessage>? respond = null,
            OAuthOptions? options = null)
        {
            Stub = new StubAtlassian(respond ?? Rotating());

            OAuth = new AtlassianOAuthClient(
                new HttpClient(Stub) { BaseAddress = new Uri("https://auth.atlassian.com/") },
                Options.Create(options ?? Configured()),
                Clock,
                NullLogger<AtlassianOAuthClient>.Instance);

            Provider = new TokenProvider(
                Store, Locks, OAuth, Clock, NullLogger<TokenProvider>.Instance);
        }

        /// <summary>Tokens already in the store, expiring however far out.</summary>
        public void SignedIn(
            TokenKey key,
            TimeSpan expiresIn,
            string refreshToken = "refresh-0",
            IReadOnlyList<GrantedSite>? sites = null) =>
            Store.Put(key, new OAuthTokens(
                "access-0", refreshToken, Clock.Utc + expiresIn, "read:jira-work",
                sites ?? BothSites));

        public void Dispose()
        {
            Locks.Dispose();
            Stub.Dispose();
        }
    }

    private static OAuthOptions Configured() => new()
    {
        ClientId = "client-id",
        ClientSecret = "client-secret",
        CallbackUrl = "http://localhost:5002/api/auth/callback",
    };

    /// <summary>
    /// Atlassian rotates: every response carries a NEW refresh token and kills
    /// the one just used. The numbering is what lets a test see which one the
    /// store ended up with.
    /// </summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> Rotating(int delayMs = 0)
    {
        var issued = 0;

        return _ =>
        {
            if (delayMs > 0)
            {
                Thread.Sleep(delayMs);
            }

            var n = Interlocked.Increment(ref issued);

            return StubAtlassian.Json($$"""
                {
                  "access_token": "access-{{n}}",
                  "refresh_token": "refresh-{{n}}",
                  "expires_in": 3600,
                  "scope": "read:jira-work write:jira-work offline_access"
                }
                """);
        };
    }

    private static HttpResponseMessage Refused(string error) =>
        new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent($$"""{"error":"{{error}}"}"""),
        };

    // ------------------------------------------------------------- exchange

    [Fact]
    public async Task Exchanging_a_code_asks_for_the_right_grant_and_dates_the_expiry_absolutely()
    {
        using var sut = new Harness();

        var tokens = await sut.OAuth.ExchangeCodeAsync("the-code", CancellationToken.None);

        var body = sut.Stub.LastRequest.Body;
        Assert.NotNull(body);
        Assert.Contains("\"grant_type\":\"authorization_code\"", body);
        Assert.Contains("\"code\":\"the-code\"", body);
        Assert.Contains("http://localhost:5002/api/auth/callback", body);

        // expires_in is a duration on the wire and useless once stored - the
        // store outlives the response by an hour.
        Assert.Equal(Now.AddSeconds(3600), tokens.ExpiresAt);
        Assert.Equal("access-1", tokens.AccessToken);
        Assert.Equal("refresh-1", tokens.RefreshToken);
    }

    [Fact]
    public async Task An_unconfigured_app_says_so_rather_than_sending_an_empty_client_id()
    {
        // Atlassian answers an empty client_id with a generic invalid_client,
        // which reads as "the secret is wrong" and sends somebody hunting.
        using var sut = new Harness(options: new OAuthOptions());

        var failed = await Assert.ThrowsAsync<OAuthTokenException>(() =>
            sut.OAuth.ExchangeCodeAsync("the-code", CancellationToken.None));

        Assert.Contains("OAuth:ClientId", failed.Message);
        Assert.Empty(sut.Stub.Requests);
    }

    // -------------------------------------------------------------- refresh

    [Fact]
    public async Task A_refresh_stores_the_new_refresh_token()
    {
        // The one that was sent up is dead the moment this succeeds. Keeping the
        // old one signs the user out at the NEXT refresh, an hour later, which
        // is far enough from the cause to look like something else.
        using var sut = new Harness();
        sut.SignedIn(ExampleUser, TimeSpan.FromMinutes(1), refreshToken: "refresh-0");

        var tokens = await sut.Provider.ValidTokensAsync(ExampleUser, CancellationToken.None);

        Assert.NotNull(tokens);
        Assert.Equal("refresh-1", tokens.RefreshToken);
        Assert.Equal("refresh-1", sut.Store.Get(ExampleUser)!.RefreshToken);
        Assert.Equal("access-1", sut.Store.Get(ExampleUser)!.AccessToken);
    }

    [Fact]
    public async Task A_refresh_keeps_the_cloud_id_the_sign_in_resolved()
    {
        // It is not in the refresh response. Rebuilding the record instead of
        // carrying it forward would leave a valid token aimed at no site.
        using var sut = new Harness();
        sut.SignedIn(ExampleUser, TimeSpan.FromMinutes(1));

        var tokens = await sut.Provider.ValidTokensAsync(ExampleUser, CancellationToken.None);

        Assert.Equal("cloud-target", tokens!.CloudIdFor(Tenant.Target));
        Assert.Equal("cloud-source", sut.Store.Get(ExampleUser)!.CloudIdFor(Tenant.Source));
    }

    [Fact]
    public async Task A_token_with_time_left_is_not_refreshed()
    {
        using var sut = new Harness();
        sut.SignedIn(ExampleUser, TimeSpan.FromMinutes(30));

        var tokens = await sut.Provider.ValidTokensAsync(ExampleUser, CancellationToken.None);

        Assert.Equal("access-0", tokens!.AccessToken);
        Assert.Empty(sut.Stub.Requests);
    }

    [Fact]
    public async Task A_token_inside_the_skew_window_is_refreshed_before_it_expires()
    {
        // Two minutes left is still valid, and would still expire midway through
        // a copy run. The skew buys the run enough room to finish.
        using var sut = new Harness();
        sut.SignedIn(ExampleUser, TimeSpan.FromMinutes(2));

        var tokens = await sut.Provider.ValidTokensAsync(ExampleUser, CancellationToken.None);

        Assert.Equal("access-1", tokens!.AccessToken);
        Assert.Single(sut.Stub.Requests);
    }

    [Fact]
    public async Task Two_callers_arriving_together_refresh_once_between_them()
    {
        // Both refreshes would succeed, and the second would invalidate the
        // first - leaving the store holding whichever token lost the race.
        using var sut = new Harness(Rotating(delayMs: 50));
        sut.SignedIn(ExampleUser, TimeSpan.FromMinutes(1));

        var both = await Task.WhenAll(
            sut.Provider.ValidTokensAsync(ExampleUser, CancellationToken.None),
            sut.Provider.ValidTokensAsync(ExampleUser, CancellationToken.None));

        Assert.Single(sut.Stub.Requests);
        Assert.Equal("access-1", both[0]!.AccessToken);
        Assert.Equal("access-1", both[1]!.AccessToken);
        Assert.Equal("refresh-1", sut.Store.Get(ExampleUser)!.RefreshToken);
    }

    [Fact]
    public async Task A_refresh_response_with_no_new_refresh_token_keeps_the_old_one()
    {
        // The spec allows it, Atlassian does not do it, and dropping the only
        // refresh token we hold because a field was absent would sign the user
        // out for nothing.
        using var sut = new Harness(_ => StubAtlassian.Json("""
            { "access_token": "access-9", "expires_in": 3600, "scope": "read:jira-work" }
            """));

        sut.SignedIn(ExampleUser, TimeSpan.FromMinutes(1), refreshToken: "refresh-kept");

        var tokens = await sut.Provider.ValidTokensAsync(ExampleUser, CancellationToken.None);

        Assert.Equal("access-9", tokens!.AccessToken);
        Assert.Equal("refresh-kept", sut.Store.Get(ExampleUser)!.RefreshToken);
    }

    [Fact]
    public async Task A_dead_refresh_token_signs_the_user_out_instead_of_throwing()
    {
        // invalid_grant is unrecoverable here: consent has to happen again. A
        // throw would surface as a 500 on whatever endpoint happened to ask.
        using var sut = new Harness(_ => Refused("invalid_grant"));
        sut.SignedIn(ExampleUser, TimeSpan.FromMinutes(1));

        var tokens = await sut.Provider.ValidTokensAsync(ExampleUser, CancellationToken.None);

        Assert.Null(tokens);
        Assert.Null(sut.Store.Get(ExampleUser));
    }

    // ---------------------------------------------------------------- state

    [Fact]
    public async Task A_grant_that_reaches_one_site_does_not_reach_the_other()
    {
        // The account-level grant is one row and one token, so "signed in" is
        // not the question - "does this grant reach THIS site" is. A grant
        // covering only target-site must not authorise a call to source-site.
        using var sut = new Harness();

        sut.SignedIn(
            ExampleUser,
            TimeSpan.FromMinutes(30),
            sites: [new GrantedSite(Tenant.Target, "cloud-target", "target.example.invalid")]);

        var tokens = await sut.Provider.ValidTokensAsync(ExampleUser, CancellationToken.None);

        Assert.NotNull(tokens);
        Assert.True(tokens.Reaches(Tenant.Target));
        Assert.False(tokens.Reaches(Tenant.Source));
        Assert.Null(tokens.CloudIdFor(Tenant.Source));

        // Nothing was refreshed to find that out.
        Assert.Empty(sut.Stub.Requests);
    }

    [Fact]
    public void A_later_consent_adds_a_site_without_dropping_the_first()
    {
        // The second consent returns fresh tokens for the whole grant. If
        // accessible-resources ever named only the site just added, replacing
        // outright would sign the user out of the other one.
        var first = new OAuthTokens(
            "access-0", "refresh-0", Now, "read:jira-work",
            [new GrantedSite(Tenant.Target, "cloud-target", "target.example.invalid")]);

        var second = first.With(
            new TokenGrant("access-1", "refresh-1", Now, "read:jira-work"),
            [new GrantedSite(Tenant.Source, "cloud-source", "source.example.invalid")]);

        Assert.Equal("access-1", second.AccessToken);
        Assert.True(second.Reaches(Tenant.Source));
        Assert.True(second.Reaches(Tenant.Target));
    }

    [Fact]
    public async Task Being_signed_out_reads_as_no_credentials_rather_than_a_broken_one()
    {
        using var sut = new Harness();

        var credential = await sut.Provider.CredentialForAsync(ExampleUser, CancellationToken.None);

        Assert.False(credential.IsPresent);
        Assert.Null(credential.ToAuthorizationHeader());
    }

    [Fact]
    public async Task A_signed_in_user_authorises_as_bearer()
    {
        using var sut = new Harness();
        sut.SignedIn(ExampleUser, TimeSpan.FromMinutes(30));

        var credential = await sut.Provider.CredentialForAsync(ExampleUser, CancellationToken.None);

        Assert.Equal("Bearer access-0", credential.ToAuthorizationHeader()?.ToString());
    }

    [Fact]
    public void Stored_tokens_do_not_render_their_secrets()
    {
        var rendered =
            new OAuthTokens("access-secret", "refresh-secret", Now, "read:jira-work", BothSites)
                .ToString();

        Assert.DoesNotContain("access-secret", rendered);
        Assert.DoesNotContain("refresh-secret", rendered);
    }
}
