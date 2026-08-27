namespace TicketCloner.Api.Contracts;

/// <summary>
/// What the UI needs before it has any credentials: which sites this instance
/// talks to, and whether it already holds a credential for each.
/// </summary>
/// <param name="FallbackIssueType">What a source type with no same-named
/// counterpart becomes. The UI names it rather than hardcoding a sentence that
/// drifts the moment the configuration changes.</param>
public sealed record ConfigResponse(
    TenantConfig Source,
    TenantConfig Target,
    string FallbackIssueType);

/// <param name="Host">Shown instead of a friendly name. The project names are
/// inverted across the two tenants, so only the host is unambiguous.</param>
/// <param name="Configured">Whether this instance holds a stored credential.
/// Never the token itself.</param>
public sealed record TenantConfig(
    string Host,
    string ProjectKey,
    IReadOnlyList<string> AvailableProjects,
    int BoardId,
    bool Configured,
    string Email);

/// <summary>Whoever the supplied credential authenticates as.</summary>
public sealed record IdentityResponse(
    string Host,
    string AccountId,
    string DisplayName,
    string? EmailAddress,
    bool EmailVisible);

/// <summary>Shape of GET /rest/api/3/myself.</summary>
public sealed record AtlassianUser(
    string AccountId,
    string DisplayName,
    string? EmailAddress,
    string? AccountType);
