using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TicketCloner.Api.Atlassian;
using TicketCloner.Api.Configuration;
using TicketCloner.Api.Contracts;

namespace TicketCloner.Api.Jira;

/// <summary>
/// Sprints, from either board's own agile API.
///
/// Two questions with nothing in common but the endpoint. The TARGET's active
/// sprint is where a copy may be filed. The SOURCE's sprints are only a way to
/// pick a batch - a source sprint id is used to search the source and for
/// nothing else, which is the rule `Sprint` in the mapper's NeverMappedByName
/// list enforces and this class must never bend.
///
/// Verified against board 61 on 08/09/2026 by scripts/probe-sprints.ps1:
/// the agile API pages under "values" (unlike createmeta) with startAt,
/// maxResults, isLast and total. `?state=active` came back with one row and
/// isLast true, so the active call needs no paging loop; the unfiltered list
/// (98 sprints, 50 to a page) does.
/// </summary>
public sealed class SprintReader(
    AtlassianClientFactory clients,
    IOptions<AtlassianOptions> options,
    ILogger<SprintReader> logger)
{
    /// <summary>
    /// The greenhopper sprint field. Matched on this rather than on a field id,
    /// because ids differ per tenant, and rather than on the NAME "Sprint",
    /// because the source has two fields with that name and neither is this.
    /// </summary>
    public const string SprintSchema = "com.pyxis.greenhopper.jira:gh-sprint";

    private TenantOptions Target => options.Value.Target;

    private TenantOptions Source => options.Value.Source;

    /// <summary>
    /// Every sprint on a tenant's board whose name contains the search: the
    /// active one(s) first, then newest to oldest. Empty search lists them all.
    ///
    /// The agile API has no name parameter, so the whole list is paged through
    /// and filtered here. Names repeat on a board - two sprints can both be
    /// "Sprint 1" a year apart - which is why every row carries its id and
    /// dates, and why the id is the only thing a caller may use.
    /// </summary>
    public async Task<IReadOnlyList<BoardSprint>> ListAsync(
        Tenant tenant, string? search, CancellationToken cancellationToken)
    {
        var board = (tenant == Tenant.Source ? Source : Target).BoardId;
        var side = tenant.ToString().ToLowerInvariant();

        if (board <= 0)
        {
            throw new InvalidOperationException(
                $"No board is configured for the {side}, so its sprints cannot be listed.");
        }

        var client = clients.For(tenant);
        var sprints = new List<BoardSprint>();
        var startAt = 0;

        while (true)
        {
            var response = await client.GetJsonAsync(
                $"rest/agile/1.0/board/{board}/sprint?startAt={startAt}&maxResults=50",
                cancellationToken);

            if (response?["values"] is not JsonArray values)
            {
                throw new InvalidOperationException(
                    $"The sprint list for board {board} came back without a 'values' array.");
            }

            sprints.AddRange(values.Select(ReadRow).OfType<BoardSprint>());

            // isLast is authoritative; an empty page is the belt to its braces,
            // so a fake or a misbehaving server cannot loop this for ever.
            if (response["isLast"]?.GetValue<bool>() is not false || values.Count == 0)
            {
                break;
            }

            startAt += values.Count;
        }

        var wanted = search?.Trim() ?? "";

        return sprints
            .Where(sprint => wanted.Length == 0 ||
                             sprint.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(sprint => sprint.State == "active")
            .ThenByDescending(sprint => sprint.StartDate ?? DateTimeOffset.MinValue)
            .ThenByDescending(sprint => sprint.Id)
            .ToList();
    }

    /// <summary>
    /// One TARGET sprint by id, for a run that chose it from the list rather
    /// than asking for whatever is current. Same result shape as
    /// <see cref="GetActiveAsync"/> so the apply step does not care which way
    /// the sprint was decided. Null with a reason when the target has no such
    /// sprint - an id typed or remembered wrong must not turn into a 400 on
    /// every ticket of the run.
    /// </summary>
    public async Task<ActiveSprintResult> GetTargetAsync(int sprintId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await clients.For(Tenant.Target).GetJsonAsync(
                $"rest/agile/1.0/sprint/{sprintId}", cancellationToken);

            return Read(response) is { } sprint
                ? new ActiveSprintResult(sprint, null)
                : new ActiveSprintResult(null, $"Sprint {sprintId} came back in a shape this tool does not recognise.");
        }
        catch (AtlassianApiException failed) when (failed.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new ActiveSprintResult(null, $"The target has no sprint with id {sprintId}.");
        }
    }

    /// <summary>
    /// Null when the board has no active sprint, or more than one.
    ///
    /// More than one is not an error on Jira's side - a board can run parallel
    /// sprints - but it IS ambiguous here, and quietly picking the first would
    /// put copies somewhere nobody chose. Both cases carry a reason for the
    /// caller to show.
    /// </summary>
    public async Task<ActiveSprintResult> GetActiveAsync(CancellationToken cancellationToken)
    {
        if (Target.BoardId <= 0)
        {
            return new ActiveSprintResult(null,
                "No board is configured for the target, so its current sprint cannot be read.");
        }

        var client = clients.For(Tenant.Target);

        var response = await client.GetJsonAsync(
            $"rest/agile/1.0/board/{Target.BoardId}/sprint?state=active", cancellationToken);

        // "values" here, "issueTypes" and "fields" on createmeta. The agile API
        // is a different API with different conventions, so this was checked
        // rather than assumed - reading the wrong key returns an empty array
        // with no error, which reads as "no active sprint" and is a lie.
        if (response?["values"] is not JsonArray values)
        {
            logger.LogWarning(
                "The sprint response for board {Board} had no 'values' array", Target.BoardId);

            return new ActiveSprintResult(null,
                "The board's sprint list came back in a shape this tool does not recognise.");
        }

        var sprints = values
            .Select(Read)
            .Where(sprint => sprint is not null)
            .Select(sprint => sprint!)
            .ToList();

        return sprints.Count switch
        {
            1 => new ActiveSprintResult(sprints[0], null),

            0 => new ActiveSprintResult(null,
                $"Board {Target.BoardId} has no active sprint."),

            _ => new ActiveSprintResult(null,
                $"Board {Target.BoardId} has {sprints.Count} active sprints " +
                $"({string.Join(", ", sprints.Select(sprint => sprint.Name))}), so which one " +
                "a copy belongs in is not something this tool can decide."),
        };
    }

    private static ActiveSprint? Read(JsonNode? node)
    {
        if (node?["id"]?.GetValue<int>() is not { } id)
        {
            return null;
        }

        return new ActiveSprint(
            Id: id,
            Name: node["name"]?.GetValue<string>() ?? $"Sprint {id}",
            Goal: Text(node["goal"]),
            StartDate: Time(node["startDate"]),
            EndDate: Time(node["endDate"]));
    }

    private static BoardSprint? ReadRow(JsonNode? node)
    {
        if (node?["id"]?.GetValue<int>() is not { } id)
        {
            return null;
        }

        return new BoardSprint(
            Id: id,
            Name: node["name"]?.GetValue<string>() ?? $"Sprint {id}",
            State: node["state"]?.GetValue<string>() ?? "",
            StartDate: Time(node["startDate"]),
            EndDate: Time(node["endDate"]));
    }

    private static string? Text(JsonNode? node) =>
        node?.GetValue<string>() is { Length: > 0 } value ? value : null;

    private static DateTimeOffset? Time(JsonNode? node) =>
        DateTimeOffset.TryParse(Text(node), out var parsed) ? parsed : null;
}
