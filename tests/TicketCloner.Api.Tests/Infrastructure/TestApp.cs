using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TicketCloner.Api.Atlassian;
using TicketCloner.Api.OAuth;

namespace TicketCloner.Api.Tests.Infrastructure;

/// <summary>
/// Hosts the real application. Only outbound HTTP gets swapped, so middleware
/// order, the credentials filters and options validation are all exercised
/// exactly as they run in production.
/// </summary>
/// <param name="environment">Development unless a test needs otherwise. The
/// HTTPS redirect is the one behaviour that only exists outside it, so proving
/// it needs a host that is not in Development.</param>
/// <param name="signedIn">Makes every client this factory creates a browser
/// that has already signed in. Signing in is the only way to reach a tenant -
/// the tool stores no API token and takes none on request headers - so a test
/// that touches one either drives the whole consent round trip (OAuthFlowTests
/// does) or starts from here: a token seeded straight into the store, and the
/// session cookie that points at it.</param>
/// <param name="grants">Which tenants that grant reaches. Both by default. One
/// account-level grant can cover both sites or only one of them, and the
/// isolation tests need the second case.</param>
public sealed class TestApp(
    StubAtlassian? outbound = null,
    IDictionary<string, string?>? configuration = null,
    string? environment = null,
    bool signedIn = false,
    IReadOnlyList<Tenant>? grants = null)
    : WebApplicationFactory<Program>
{
    /// <summary>The opaque browser handle in the tc.session cookie.</summary>
    public const string User = "test-browser";

    public const string AccessToken = "session-access-token";

    public const string RefreshToken = "session-refresh-token";

    /// <summary>
    /// What accessible-resources would have returned for each site. Every
    /// REST call then goes to api.atlassian.com/ex/jira/{cloud id}/, and the
    /// cloud id is the only thing in the URL that says which tenant it is for.
    /// </summary>
    public const string SourceCloudId = "cloud-source";

    public const string TargetCloudId = "cloud-target";

    public static readonly IReadOnlyList<GrantedSite> BothSites =
    [
        new(Tenant.Source, SourceCloudId, "source.example.invalid"),
        new(Tenant.Target, TargetCloudId, "target.example.invalid"),
    ];

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);

        if (!signedIn)
        {
            return;
        }

        // Straight into the store, not through the callback: what is being
        // tested is everything AFTER sign-in, and the round trip has its own
        // tests. The token is far from expiry so no test here trips a refresh.
        var wanted = grants ?? [Tenant.Source, Tenant.Target];
        var sites = BothSites.Where(site => wanted.Contains(site.Tenant)).ToList();

        Services.GetRequiredService<ITokenStore>().Put(
            new TokenKey(User),
            new OAuthTokens(
                AccessToken,
                RefreshToken,
                DateTimeOffset.UtcNow.AddDays(1),
                "read:jira-work write:jira-work read:jira-user offline_access",
                sites));

        client.DefaultRequestHeaders.Add("Cookie", $"{OAuthSession.UserCookie}={User}");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment ?? Environments.Development);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            // appsettings.json travels into the test output - and so does
            // appsettings.Local.json, which Program.cs loads last and which on a
            // developer's machine holds the real thing.
            //
            // The OAuth keys are cleared because of it: once somebody registers
            // an app locally, every "no app is registered" test starts seeing
            // one and fails on a machine rather than on a change.
            var settings = new Dictionary<string, string?>
            {
                ["OAuth:ClientId"] = "",
                ["OAuth:ClientSecret"] = "",
                ["OAuth:CallbackUrl"] = "",

                // appsettings.Development.json points this at Vite, and the
                // tests assert the outcome rather than the port.
                ["OAuth:PostSignInUrl"] = "/",
            };

            if (configuration is not null)
            {
                foreach (var (key, value) in configuration)
                {
                    settings[key] = value;
                }
            }

            config.AddInMemoryCollection(settings);
        });

        if (outbound is not null)
        {
            builder.ConfigureServices(services =>
                services.ConfigureHttpClientDefaults(http =>
                    http.ConfigurePrimaryHttpMessageHandler(() => outbound)));
        }
    }
}
