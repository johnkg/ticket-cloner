using Microsoft.Extensions.Options;
using TicketCloner.Api.OAuth;

namespace TicketCloner.Api.Atlassian;

/// <summary>
/// Rejects a request that has no usable credential for the tenant the endpoint
/// needs, before the handler runs.
///
/// Applied per tenant rather than globally: reading the source and writing to
/// the target are separate capabilities, and an endpoint that only reads should
/// not demand a write credential it will never use.
/// </summary>
public sealed class AtlassianCredentialsFilter(Tenant tenant) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        var resolver = services.GetRequiredService<TenantAccessResolver>();

        var access = await resolver.ResolveAsync(tenant, context.HttpContext.RequestAborted);

        if (!access.Credential.IsPresent)
        {
            // "Not signed in" and "nobody can sign in" are different problems
            // with different fixes, and answering both with the same sentence
            // sends people to press a button that is not there.
            var oauth = services.GetRequiredService<IOptions<OAuthOptions>>().Value;

            return oauth.IsConfigured
                ? Results.Problem(
                    title: "Not signed in",
                    // One sign-in covers both tenants - the grant is account-level
                    // - so this names no tenant and takes no parameter.
                    detail: $"Sign in to Atlassian at {context.HttpContext.Request.PathBase}" +
                            "/api/auth/start. That grants both tenants; this request needed " +
                            $"the {tenant.ToString().ToLowerInvariant()} one.",
                    statusCode: StatusCodes.Status401Unauthorized)
                : Results.Problem(
                    title: "No Atlassian app is registered",
                    // Signing in is the only way to reach a tenant: the tool
                    // stores no API token and takes none on request headers.
                    detail: $"This endpoint needs the {tenant.ToString().ToLowerInvariant()} tenant, " +
                            "and nobody can sign in until an administrator registers an OAuth app " +
                            "and sets OAuth:ClientId, OAuth:ClientSecret and OAuth:CallbackUrl.",
                    statusCode: StatusCodes.Status401Unauthorized);
        }

        // Everything downstream reads this rather than resolving again, so the
        // refresh above happens exactly once per request per tenant.
        context.HttpContext.Items[TenantAccess.ItemKey(tenant)] = access;

        return await next(context);
    }
}

public static class CredentialsFilterExtensions
{
    public static TBuilder RequireTenant<TBuilder>(this TBuilder builder, Tenant tenant)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(new AtlassianCredentialsFilter(tenant));
        return builder;
    }
}
