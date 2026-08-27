using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TicketCloner.Api.Atlassian;
using TicketCloner.Api.Configuration;

namespace TicketCloner.Api.Jira;

/// <param name="Url">A link to the copy, so whoever is deciding can open it and
/// see for themselves whether it really is one.</param>
/// <param name="SummaryMatches">Whether the candidate still carries the same
/// summary. False means its External Issue ID points at this source issue but
/// somebody has since reworded the copy - reported rather than treated as a
/// duplicate, because the rule is that both have to agree.</param>
public sealed record ExistingCopy(string Key, string Url, string Summary, bool SummaryMatches);

/// <summary>
/// Has this source issue already been copied to the target?
///
/// Read-only, and deliberately not part of <see cref="TargetIssueWriter"/> even
/// though it began there: preview needs this answer too, and preview must not
/// be able to reach anything that writes.
///
/// Two things must agree, both exactly: the target's External Issue ID has to
/// hold the source issue's URL, and the two summaries have to be identical.
/// Jira has no separate "title" field - the summary is it.
/// </summary>
public sealed class ExistingCopyFinder(
    AtlassianClientFactory clients,
    IOptions<AtlassianOptions> options)
{
    private TenantOptions Target => options.Value.Target;

    private AtlassianClient Client => clients.For(Tenant.Target);

    /// <summary>
    /// JQL only narrows; the decision is made by comparing exactly in code,
    /// because `~` is a tokenised text match rather than equality. Verified
    /// against the live target: an issue key inside the stored URL matches as a
    /// whole token, and a prefix of it matches nothing.
    /// </summary>
    public async Task<ExistingCopy?> FindAsync(
        string fieldName,
        string fieldId,
        string sourceKey,
        string sourceUrl,
        string summary,
        CancellationToken cancellationToken)
    {
        var client = Client;

        var jql = $"project = {Escape(Target.ProjectKey)} " +
                  $"AND \"{Escape(fieldName)}\" ~ \"{Escape(sourceKey)}\"";

        var response = await client.PostAsync<JsonNode>("rest/api/3/search/jql", new Dictionary<string, object?>
        {
            ["jql"] = jql,
            // Every candidate, not just the first: an issue whose summary has
            // drifted must not hide a later one that still matches properly.
            ["maxResults"] = 50,
            ["fields"] = new[] { "summary", fieldId },
        }, cancellationToken);

        ExistingCopy? summaryDrifted = null;

        foreach (var candidate in response?["issues"] as JsonArray ?? [])
        {
            var key = candidate?["key"]?.GetValue<string>();
            if (key is null)
            {
                continue;
            }

            var fields = candidate?["fields"];

            if (!IdentifiesSource(Text(fields?[fieldId]), sourceKey, sourceUrl))
            {
                continue;
            }

            var candidateSummary = Text(fields?["summary"]) ?? "";
            var url = new Uri(client.SiteUri, $"browse/{key}").ToString();

            if (string.Equals(candidateSummary.Trim(), summary.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return new ExistingCopy(key, url, candidateSummary, true);
            }

            summaryDrifted ??= new ExistingCopy(key, url, candidateSummary, false);
        }

        return summaryDrifted;
    }

    /// <summary>
    /// The same question for an epic, with one extra rule: if no copy carries
    /// this source issue's URL, look for an TGT epic with the identical
    /// summary.
    ///
    /// Epics get created on the target by hand far more often than ordinary
    /// tickets do, and a hand-made one has no External Issue ID to match on.
    /// Without the fallback every such epic would be cloned a second time and
    /// the board would end up with two of them.
    /// </summary>
    public async Task<ExistingCopy?> FindEpicAsync(
        string fieldName,
        string fieldId,
        string sourceKey,
        string sourceUrl,
        string summary,
        CancellationToken cancellationToken)
    {
        var byReference = await FindAsync(fieldName, fieldId, sourceKey, sourceUrl, summary, cancellationToken);

        if (byReference is { SummaryMatches: true })
        {
            return byReference;
        }

        var client = Client;

        var jql = $"project = {Escape(Target.ProjectKey)} AND issuetype = Epic " +
                  $"AND summary ~ \"\\\"{Escape(summary)}\\\"\"";

        var response = await client.PostAsync<JsonNode>("rest/api/3/search/jql", new Dictionary<string, object?>
        {
            ["jql"] = jql,
            ["maxResults"] = 50,
            ["fields"] = new[] { "summary" },
        }, cancellationToken);

        foreach (var candidate in response?["issues"] as JsonArray ?? [])
        {
            var key = candidate?["key"]?.GetValue<string>();
            var candidateSummary = Text(candidate?["fields"]?["summary"]);

            // `~` is a tokenised match, so the title still has to be compared
            // properly before an unrelated epic is treated as this one.
            if (key is not null &&
                candidateSummary is not null &&
                string.Equals(candidateSummary.Trim(), summary.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return new ExistingCopy(
                    key, new Uri(client.SiteUri, $"browse/{key}").ToString(), candidateSummary, true);
            }
        }

        return byReference;
    }

    /// <summary>
    /// The field normally holds the source issue's full URL. A bare key counts
    /// too, so a copy made before the field was filled in this way is still
    /// recognised rather than duplicated.
    /// </summary>
    private static bool IdentifiesSource(string? value, string sourceKey, string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();

        return string.Equals(trimmed, sourceUrl, StringComparison.OrdinalIgnoreCase)
               || string.Equals(trimmed, sourceKey, StringComparison.OrdinalIgnoreCase)
               || trimmed.EndsWith($"/{sourceKey}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A custom field can come back as any JSON shape; only a string is useful here.</summary>
    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
