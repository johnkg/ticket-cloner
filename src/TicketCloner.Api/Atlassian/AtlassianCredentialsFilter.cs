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
        var resolver = context.HttpContext.RequestServices.GetRequiredService<CredentialsResolver>();

        if (!resolver.For(tenant).IsPresent)
        {
            return Results.Problem(
                title: "No Atlassian credentials",
                detail: $"This endpoint needs {tenant.ToString().ToLowerInvariant()}-tenant credentials. " +
                        $"Send {tenant.EmailHeader()} and {tenant.TokenHeader()}, " +
                        $"or configure Credentials:{tenant}Email and Credentials:{tenant}ApiToken.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

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
