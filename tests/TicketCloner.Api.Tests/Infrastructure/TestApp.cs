using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TicketCloner.Api.Tests.Infrastructure;

/// <summary>
/// Hosts the real application. Only outbound HTTP gets swapped, so middleware
/// order, the credentials filters and options validation are all exercised
/// exactly as they run in production.
/// </summary>
public sealed class TestApp(
    StubAtlassian? outbound = null,
    IDictionary<string, string?>? configuration = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            // appsettings.json travels into the test output, so a credential
            // left configured there would make every "no credentials -> 401"
            // test pass through as authenticated and prove nothing.
            var settings = new Dictionary<string, string?>
            {
                ["Credentials:SourceEmail"] = "",
                ["Credentials:SourceApiToken"] = "",
                ["Credentials:TargetEmail"] = "",
                ["Credentials:TargetApiToken"] = "",
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
