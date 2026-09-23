namespace TicketCloner.Api.Contracts;

/// <param name="CloudId">Which site the grant reaches. Reported because a person
/// with access to several can consent to the wrong one, and seeing the id is how
/// that gets noticed.</param>
public sealed record AuthTenantStatus(
    string Tenant,
    bool SignedIn,
    string? CloudId,
    DateTimeOffset? ExpiresAt);

/// <param name="Available">False when no 3LO app is registered on this instance,
/// which is a normal state - the tool runs on API tokens without one.</param>
public sealed record AuthStatusResponse(
    bool Available,
    AuthTenantStatus Source,
    AuthTenantStatus Target);
