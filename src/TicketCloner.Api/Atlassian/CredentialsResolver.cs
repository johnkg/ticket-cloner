using Microsoft.Extensions.Options;
using TicketCloner.Api.Configuration;

namespace TicketCloner.Api.Atlassian;

/// <summary>
/// Resolves one tenant's credential: request headers first, configuration
/// second.
///
/// Headers win so a caller acts as themselves. On an instance that holds a
/// credential of its own that matters - the created issue's history should name
/// the person who pressed the button, not the service account.
/// </summary>
public sealed class CredentialsResolver(
    IHttpContextAccessor httpContextAccessor,
    IOptions<StoredCredentialsOptions> stored)
{
    public AtlassianCredentials For(Tenant tenant)
    {
        var fromHeaders = FromHeaders(tenant);
        return fromHeaders.IsPresent ? fromHeaders : FromConfiguration(tenant);
    }

    private AtlassianCredentials FromHeaders(Tenant tenant)
    {
        var headers = httpContextAccessor.HttpContext?.Request.Headers;
        if (headers is null)
        {
            return AtlassianCredentials.None;
        }

        var email = headers[tenant.EmailHeader()].ToString();
        var token = headers[tenant.TokenHeader()].ToString();

        return new AtlassianCredentials(email, token);
    }

    private AtlassianCredentials FromConfiguration(Tenant tenant)
    {
        var options = stored.Value;

        return tenant switch
        {
            Tenant.Source => new AtlassianCredentials(options.SourceEmail, options.SourceApiToken),
            Tenant.Target => new AtlassianCredentials(options.TargetEmail, options.TargetApiToken),
            _ => AtlassianCredentials.None,
        };
    }
}
