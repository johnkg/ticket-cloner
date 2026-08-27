namespace TicketCloner.Api.Atlassian;

/// <summary>
/// Which of the two Atlassian sites. Never "TARGET_PROJECT" or "YOUR_COMPANY" - the project
/// names are inverted across the tenants and a reader resolves them wrongly.
/// </summary>
public enum Tenant
{
    Source,
    Target,
}

public static class TenantExtensions
{
    /// <summary>
    /// Named HttpClient key. Two clients, each with its own BaseAddress, rather
    /// than one client with a swappable base address - the latter is the shape
    /// that eventually writes to the wrong site.
    /// </summary>
    public static string ClientName(this Tenant tenant) => tenant switch
    {
        Tenant.Source => "source",
        Tenant.Target => "target",
        _ => throw new ArgumentOutOfRangeException(nameof(tenant), tenant, null),
    };

    public static string EmailHeader(this Tenant tenant) => $"X-{tenant}-Email";

    public static string TokenHeader(this Tenant tenant) => $"X-{tenant}-Token";
}
