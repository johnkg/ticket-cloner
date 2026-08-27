using System.Net;
using Microsoft.Extensions.Options;
using TicketCloner.Api.Atlassian;
using TicketCloner.Api.Configuration;
using TicketCloner.Api.Contracts;

namespace TicketCloner.Api.Mapping;

/// <param name="IsFallback">True when this is the running account standing in
/// for somebody else. Expect it often - it is a normal path, not an error.</param>
public sealed record ResolvedUser(string? AccountId, string Reason, bool IsFallback);

/// <summary>
/// Works out who a source user is on the target tenant.
///
/// Scoped, so the "does this account exist" answers and the running account are
/// looked up once per request rather than once per ticket.
/// </summary>
public sealed class UserResolver(
    AtlassianClientFactory clients,
    IOptions<MappingOptions> options)
{
    private readonly Dictionary<string, bool> _existsOnTarget = new(StringComparer.OrdinalIgnoreCase);
    private string? _runningAccountId;

    public async Task<ResolvedUser> ResolveAsync(SourceUser? user, CancellationToken cancellationToken)
    {
        if (user is null)
        {
            return new ResolvedUser(null, "Not set on the source.", false);
        }

        // 1. A configured mapping, for people who exist on one site only.
        if (options.Value.Users.TryGetValue(user.AccountId, out var mapped))
        {
            if (await ExistsOnTargetAsync(mapped, cancellationToken))
            {
                return new ResolvedUser(mapped, $"{user.DisplayName} mapped by configuration.", false);
            }

            return await FallbackAsync(
                $"{user.DisplayName} is mapped to an account that does not exist on the target.",
                cancellationToken);
        }

        // 2. The accountId as-is. Shared across tenants for anyone holding one
        //    Atlassian account on both sites - verified, because a stale id
        //    writes a dead link rather than failing loudly.
        if (await ExistsOnTargetAsync(user.AccountId, cancellationToken))
        {
            return new ResolvedUser(user.AccountId, $"{user.DisplayName} exists on both tenants.", false);
        }

        // 3. The running account, with the original name preserved elsewhere.
        return await FallbackAsync(
            $"{user.DisplayName} has no account on the target.",
            cancellationToken);
    }

    private async Task<ResolvedUser> FallbackAsync(string why, CancellationToken cancellationToken)
    {
        var running = await RunningAccountIdAsync(cancellationToken);

        return new ResolvedUser(running, $"{why} Fell back to the running account.", true);
    }

    private async Task<bool> ExistsOnTargetAsync(string accountId, CancellationToken cancellationToken)
    {
        if (_existsOnTarget.TryGetValue(accountId, out var known))
        {
            return known;
        }

        try
        {
            var user = await clients.For(Tenant.Target).GetAsync<AtlassianUser>(
                $"rest/api/3/user?accountId={Uri.EscapeDataString(accountId)}", cancellationToken);

            return _existsOnTarget[accountId] = user is not null;
        }
        catch (AtlassianApiException failed) when (
            failed.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            // Jira answers 404 for an unknown accountId and 400 for one that is
            // not even well-formed. Both mean "not a user here".
            return _existsOnTarget[accountId] = false;
        }
    }

    private async Task<string?> RunningAccountIdAsync(CancellationToken cancellationToken)
    {
        if (_runningAccountId is not null)
        {
            return _runningAccountId;
        }

        var me = await clients.For(Tenant.Target).GetAsync<AtlassianUser>(
            "rest/api/3/myself", cancellationToken);

        return _runningAccountId = me?.AccountId;
    }
}
