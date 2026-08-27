using Microsoft.Extensions.Options;
using TicketCloner.Api.Atlassian;
using TicketCloner.Api.Configuration;
using TicketCloner.Api.Contracts;
using TicketCloner.Api.Jira;
using TicketCloner.Api.Mapping;
using TicketCloner.Api.Apply;

namespace TicketCloner.Api.Endpoints;

public static class ApiEndpoints
{
    public static void MapApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        // Inside the group, so /api/auth/callback is a real route rather than
        // something the /api catch-all answers with a 404.
        api.MapAuth();

        // Deliberately NOT credentials-guarded: the UI reads this before it has
        // any credentials, to decide what to prompt for.
        api.MapGet("/config", (
            IOptions<AtlassianOptions> atlassian,
            IOptions<MappingOptions> mapping) =>
        {
            var options = atlassian.Value;

            return new ConfigResponse(
                Describe(options.Source),
                Describe(options.Target),
                mapping.Value.FallbackIssueType);
        });

        // One per tenant rather than a single combined check, so a half-set-up
        // instance can still prove which half works.
        api.MapGet("/source/me", (AtlassianClientFactory clients, CancellationToken cancellationToken) =>
                WhoAmI(clients, Tenant.Source, cancellationToken))
            .RequireTenant(Tenant.Source);

        api.MapGet("/target/me", (AtlassianClientFactory clients, CancellationToken cancellationToken) =>
                WhoAmI(clients, Tenant.Target, cancellationToken))
            .RequireTenant(Tenant.Target);

        MapSourceReads(api);
        MapTargetMetadata(api);
        MapPreview(api);
    }

    private static void MapTargetMetadata(IEndpointRouteBuilder api)
    {
        // Read separately from the plan rather than folded into it: the sprint
        // is one answer for the whole run, and repeating it on every ticket
        // would invite the two to disagree.
        api.MapGet("/target/active-sprint", async (
                SprintReader sprints,
                CancellationToken cancellationToken) =>
                Results.Ok(await sprints.GetActiveAsync(cancellationToken)))
            .RequireTenant(Tenant.Target);

        // Every sprint on the target board, active first then newest to oldest,
        // so a run can be filed into a chosen sprint rather than only the
        // current one. Ids from here go into ApplyRequest.SprintId and nowhere else.
        api.MapGet("/target/sprints", async (
                SprintReader sprints,
                IOptions<AtlassianOptions> options,
                CancellationToken cancellationToken,
                string? search = null) =>
                await ListSprints(sprints, Tenant.Target, options.Value.Target.BoardId, search, cancellationToken))
            .RequireTenant(Tenant.Target);

        api.MapGet("/target/issuetypes", async (
                TargetMetadataReader metadata,
                CancellationToken cancellationToken) =>
                Results.Ok(await metadata.GetIssueTypesAsync(cancellationToken)))
            .RequireTenant(Tenant.Target);

        // The create screen. Anything absent from it is a hard 400 if sent,
        // which is why the mapper drops rather than hopes.
        api.MapGet("/target/issuetypes/{issueTypeId}/fields", async (
                string issueTypeId,
                TargetMetadataReader metadata,
                CancellationToken cancellationToken) =>
                Results.Ok(await metadata.GetFieldsAsync(issueTypeId, cancellationToken)))
            .RequireTenant(Tenant.Target);
    }

    private static void MapPreview(IEndpointRouteBuilder api)
    {
        // Reads both tenants and writes to neither. The plan it returns is what
        // apply will be handed back, so the two cannot disagree.
        api.MapGet("/preview/{key}", async (
                string key,
                MappingService mapping,
                CancellationToken cancellationToken,
                string? issueType = null) =>
            {
                var (plan, error) = await mapping.PreviewAsync(key, issueType, cancellationToken);

                return plan is null
                    ? Results.Problem(
                        title: "Cannot plan this copy",
                        detail: error,
                        statusCode: StatusCodes.Status422UnprocessableEntity)
                    : Results.Ok(plan);
            })
            .RequireTenant(Tenant.Source)
            .RequireTenant(Tenant.Target);

        // The only endpoint that writes anything. It takes the plans the
        // preview produced, so the two cannot disagree about what was about to
        // happen.
        api.MapPost("/apply", async (
                ApplyRequest request,
                ApplyService apply,
                CancellationToken cancellationToken) =>
            {
                if (request.Plans.Count == 0)
                {
                    return Results.Problem(
                        title: "Nothing to apply",
                        detail: "The request contained no plans.",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                return Results.Ok(await apply.ApplyAsync(request, cancellationToken));
            })
            .RequireTenant(Tenant.Source)
            .RequireTenant(Tenant.Target);
    }

    private static async Task<IResult> ListSprints(
        SprintReader sprints, Tenant tenant, int board, string? search, CancellationToken cancellationToken)
    {
        try
        {
            var found = await sprints.ListAsync(tenant, search, cancellationToken);
            return Results.Ok(new SprintListResponse(found, board));
        }
        catch (InvalidOperationException refused)
        {
            return Results.Problem(
                title: "Sprints unavailable",
                detail: refused.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static void MapSourceReads(IEndpointRouteBuilder api)
    {
        // Candidate issues to copy. The Xray types are already excluded by the
        // JQL the reader builds, so nothing here can offer an uncopyable type.
        api.MapGet("/source/issues", async (
                SourceIssueReader reader,
                CancellationToken cancellationToken,
                string? search = null,
                string? pageToken = null,
                int maxResults = 50,
                int? sprint = null) =>
            {
                var issues = await reader.ListAsync(search, pageToken, maxResults, sprint, cancellationToken);
                return Results.Ok(issues);
            })
            .RequireTenant(Tenant.Source);

        // The source board's sprints, so a batch can be picked by sprint. A
        // read of the source and nothing more: the ids only ever go back into
        // the search above.
        api.MapGet("/source/sprints", async (
                SprintReader sprints,
                IOptions<AtlassianOptions> options,
                CancellationToken cancellationToken,
                string? search = null) =>
            {
                return await ListSprints(sprints, Tenant.Source, options.Value.Source.BoardId, search, cancellationToken);
            })
            .RequireTenant(Tenant.Source);

        api.MapGet("/source/issues/{key}", async (
                string key,
                SourceIssueReader reader,
                CancellationToken cancellationToken) =>
            {
                var issue = await reader.GetAsync(key, cancellationToken);

                return issue is null
                    ? Results.Problem(
                        title: "Issue not found",
                        detail: $"The source tenant returned no issue for '{key}'.",
                        statusCode: StatusCodes.Status404NotFound)
                    : Results.Ok(issue);
            })
            .RequireTenant(Tenant.Source);
    }

    private static TenantConfig Describe(TenantOptions options) => new(
        Host: options.SiteUri.Host,
        ProjectKey: options.ProjectKey,
        AvailableProjects: options.ProjectChoices,
        BoardId: options.BoardId);

    private static async Task<IResult> WhoAmI(
        AtlassianClientFactory clients,
        Tenant tenant,
        CancellationToken cancellationToken)
    {
        var client = clients.For(tenant);
        var user = await client.GetAsync<AtlassianUser>("rest/api/3/myself", cancellationToken);

        if (user is null)
        {
            return Results.Problem(
                title: "Unreadable response",
                detail: $"The {tenant} tenant returned no body for /myself.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        // Whether the tenant exposes email addresses at all decides how users
        // can be matched across the two sites. source-site hides them, which is
        // why the mapping table exists - so report it rather than assume it.
        return Results.Ok(new IdentityResponse(
            Host: client.SiteUri.Host,
            AccountId: user.AccountId,
            DisplayName: user.DisplayName,
            EmailAddress: user.EmailAddress,
            EmailVisible: !string.IsNullOrWhiteSpace(user.EmailAddress)));
    }
}
