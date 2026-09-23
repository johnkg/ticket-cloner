using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TicketCloner.Api.Atlassian;
using TicketCloner.Api.Configuration;
using TicketCloner.Api.Contracts;

namespace TicketCloner.Api.Jira;

/// <summary>
/// Reads issues from the source tenant. Read-only by construction - the client
/// it uses refuses any write to the source.
/// </summary>
public sealed class SourceIssueReader(
    AtlassianClientFactory clients,
    IOptions<AtlassianOptions> options,
    ILogger<SourceIssueReader> logger)
{
    /// <summary>
    /// Surfaced as first-class properties on <see cref="SourceIssue"/>, or
    /// handled by their own pipeline step. Repeating them in the field list
    /// would bloat the preview with values nothing maps.
    /// </summary>
    private static readonly HashSet<string> HandledElsewhere = new(StringComparer.OrdinalIgnoreCase)
    {
        "summary", "description", "issuetype", "status", "priority",
        "reporter", "assignee", "labels", "created", "updated",
        "comment", "attachment", "project", "parent",
    };

    private TenantOptions Source => options.Value.Source;

    private AtlassianClient Client => clients.For(Tenant.Source);

    public async Task<IssueListResponse> ListAsync(
        string? search,
        string? pageToken,
        int maxResults,
        int? sprintId,
        CancellationToken cancellationToken)
    {
        var jql = BuildJql(search, sprintId);

        var body = new Dictionary<string, object?>
        {
            ["jql"] = jql,
            ["maxResults"] = Math.Clamp(maxResults, 1, 100),
            ["fields"] = new[]
            {
                "summary", "status", "priority", "issuetype", "reporter", "assignee", "updated",
            },
        };

        // Jira's bounded search pages by opaque token, not by offset, and does
        // not report a total. Only send the token once we have one.
        if (!string.IsNullOrWhiteSpace(pageToken))
        {
            body["nextPageToken"] = pageToken;
        }

        var client = Client;
        var response = await client.PostAsync<JsonNode>("rest/api/3/search/jql", body, cancellationToken);

        var issues = (response?["issues"] as JsonArray ?? [])
            .Select(issue => ToSummary(issue, client.SiteUri))
            .Where(issue => issue is not null)
            .Select(issue => issue!)
            .ToList();

        var nextPageToken = response?["nextPageToken"]?.GetValue<string>();
        var isLast = response?["isLast"]?.GetValue<bool>() ?? nextPageToken is null;

        logger.LogDebug("Read {Count} issues from {Host}", issues.Count, client.SiteUri.Host);

        return new IssueListResponse(issues, nextPageToken, isLast, jql);
    }

    public async Task<SourceIssue?> GetAsync(string key, CancellationToken cancellationToken)
    {
        // expand=names returns fieldId -> display name, without which every
        // custom field is an opaque customfield_NNNNN and nothing can be mapped.
        // expand=schema returns its type, which the mapper needs to reject a
        // name match whose type differs.
        var issue = await Client.GetJsonAsync(
            $"rest/api/3/issue/{Uri.EscapeDataString(key)}?expand=names,schema", cancellationToken);

        if (issue is null)
        {
            return null;
        }

        var fields = issue["fields"] as JsonObject ?? [];
        var names = issue["names"] as JsonObject ?? [];
        var schema = issue["schema"] as JsonObject ?? [];

        var comments = await ReadCommentsAsync(key, cancellationToken);

        return new SourceIssue(
            Key: issue["key"]?.GetValue<string>() ?? key,
            Url: new Uri(Client.SiteUri, $"browse/{key}").ToString(),
            IssueType: fields["issuetype"]?["name"]?.GetValue<string>() ?? "",
            Summary: fields["summary"]?.GetValue<string>() ?? "",
            Status: fields["status"]?["name"]?.GetValue<string>(),
            Priority: fields["priority"]?["name"]?.GetValue<string>(),
            Reporter: ToUser(fields["reporter"]),
            Assignee: ToUser(fields["assignee"]),
            Created: ToTimestamp(fields["created"]),
            Updated: ToTimestamp(fields["updated"]),
            Labels: (fields["labels"] as JsonArray ?? [])
                .Select(label => label?.GetValue<string>() ?? "")
                .Where(label => label.Length > 0)
                .ToList(),
            Parent: ToParent(fields["parent"]),
            Description: fields["description"]?.DeepClone(),
            Fields: ExtractFields(fields, names, schema),
            Comments: comments,
            Attachments: ExtractAttachments(fields));
    }

    /// <summary>
    /// Jira nests the parent's own summary and issue type under the reference,
    /// so an Epic parent is recognisable without fetching it separately.
    /// </summary>
    private SourceParent? ToParent(JsonNode? parent)
    {
        var key = parent?["key"]?.GetValue<string>();

        if (key is null)
        {
            return null;
        }

        var nested = parent?["fields"];

        return new SourceParent(
            Key: key,
            Url: new Uri(Client.SiteUri, $"browse/{key}").ToString(),
            Summary: nested?["summary"]?.GetValue<string>() ?? "",
            IssueType: nested?["issuetype"]?["name"]?.GetValue<string>() ?? "");
    }

    /// <summary>
    /// Attachment content, fetched at apply time rather than at preview - there
    /// is no reason to pull megabytes to draw a table.
    /// </summary>
    public Task<byte[]> GetAttachmentAsync(string attachmentId, CancellationToken cancellationToken) =>
        Client.GetBytesAsync(
            $"rest/api/3/attachment/content/{Uri.EscapeDataString(attachmentId)}", cancellationToken);

    /// <summary>
    /// The JQL that decides what is even copyable. Excluding the Xray types
    /// here rather than at the mapping stage means they are never read, and a
    /// type that is never read cannot be mis-mapped.
    /// </summary>
    public string BuildJql(string? search, int? sprintId = null)
    {
        var jql = new StringBuilder($"project = {Escape(Source.ProjectKey)}");

        // A source sprint id, from the source's own board, used only to search
        // the source. An integer, so nothing to escape - and it narrows on top
        // of whatever the search box says rather than instead of it.
        if (sprintId is > 0)
        {
            jql.Append($" AND sprint = {sprintId}");
        }

        if (Source.ExcludedIssueTypes.Length > 0)
        {
            var excluded = string.Join(", ", Source.ExcludedIssueTypes.Select(type => $"\"{Escape(type)}\""));
            jql.Append($" AND issuetype NOT IN ({excluded})");
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var keys = IssueKeysIn(search);

            jql.Append(keys.Count switch
            {
                // A pasted list, which is how a batch gets picked out in one go.
                > 1 => $" AND key IN ({string.Join(", ", keys.Select(Escape))})",

                // A bare key is far and away the common case, so match it
                // exactly rather than making the user remember which field to
                // search.
                1 => $" AND key = {Escape(keys[0])}",

                _ => $" AND summary ~ \"{Escape(search.Trim())}\"",
            });
        }

        jql.Append(" ORDER BY updated DESC");
        return jql.ToString();
    }

    /// <summary>
    /// Every issue key in the search box - but only if that is ALL it holds.
    /// Pasting a list is how several tickets get selected at once, so commas,
    /// semicolons, tabs and newlines all separate.
    ///
    /// One non-key token makes the whole thing a summary search instead. A
    /// half-understood list would quietly copy the subset it recognised, and
    /// silently doing less than asked is the worst of the available outcomes.
    /// </summary>
    private List<string> IssueKeysIn(string search)
    {
        var tokens = search.Split(
            [',', ';', ' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0 || !tokens.All(LooksLikeAnIssueKey))
        {
            return [];
        }

        // Deduplicated: the same key twice in a paste must not become two
        // copies of the same ticket.
        return tokens
            .Select(token => token.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private bool LooksLikeAnIssueKey(string search)
    {
        var trimmed = search.Trim();
        var separator = trimmed.IndexOf('-');

        return separator > 0
            && trimmed[..separator].Equals(Source.ProjectKey, StringComparison.OrdinalIgnoreCase)
            && trimmed[(separator + 1)..].All(char.IsDigit);
    }

    private async Task<List<SourceComment>> ReadCommentsAsync(string key, CancellationToken cancellationToken)
    {
        var comments = new List<SourceComment>();
        var startAt = 0;

        // This endpoint still pages by offset and does report a total, unlike
        // the JQL search above.
        while (true)
        {
            var page = await Client.GetJsonAsync(
                $"rest/api/3/issue/{Uri.EscapeDataString(key)}/comment?startAt={startAt}&maxResults=100&orderBy=created",
                cancellationToken);

            var batch = page?["comments"] as JsonArray ?? [];

            comments.AddRange(batch.Select(comment => new SourceComment(
                Id: comment?["id"]?.GetValue<string>() ?? "",
                Author: ToUser(comment?["author"]),
                Created: ToTimestamp(comment?["created"]),
                Body: comment?["body"]?.DeepClone())));

            var total = page?["total"]?.GetValue<int>() ?? comments.Count;
            startAt += batch.Count;

            if (batch.Count == 0 || startAt >= total)
            {
                break;
            }
        }

        return comments;
    }

    private static List<SourceFieldValue> ExtractFields(JsonObject fields, JsonObject names, JsonObject schema)
    {
        var extracted = new List<SourceFieldValue>();

        foreach (var (fieldId, value) in fields)
        {
            if (HandledElsewhere.Contains(fieldId) || IsEmpty(value))
            {
                continue;
            }

            var fieldSchema = schema[fieldId];

            extracted.Add(new SourceFieldValue(
                FieldId: fieldId,
                Name: names[fieldId]?.GetValue<string>() ?? fieldId,
                IsCustom: fieldId.StartsWith("customfield_", StringComparison.OrdinalIgnoreCase),
                SchemaType: fieldSchema?["type"]?.GetValue<string>(),
                SchemaCustom: fieldSchema?["custom"]?.GetValue<string>(),
                Value: value?.DeepClone()));
        }

        return extracted.OrderBy(field => field.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<SourceAttachment> ExtractAttachments(JsonObject fields) =>
        (fields["attachment"] as JsonArray ?? [])
        .Select(attachment => new SourceAttachment(
            Id: attachment?["id"]?.GetValue<string>() ?? "",
            FileName: attachment?["filename"]?.GetValue<string>() ?? "",
            MimeType: attachment?["mimeType"]?.GetValue<string>(),
            Size: attachment?["size"]?.GetValue<long>() ?? 0,
            Author: ToUser(attachment?["author"]),
            Created: ToTimestamp(attachment?["created"])))
        .ToList();

    private static IssueSummary? ToSummary(JsonNode? issue, Uri baseAddress)
    {
        var key = issue?["key"]?.GetValue<string>();
        if (key is null)
        {
            return null;
        }

        var fields = issue!["fields"];

        return new IssueSummary(
            Key: key,
            Url: new Uri(baseAddress, $"browse/{key}").ToString(),
            IssueType: fields?["issuetype"]?["name"]?.GetValue<string>() ?? "",
            Summary: fields?["summary"]?.GetValue<string>() ?? "",
            Status: fields?["status"]?["name"]?.GetValue<string>(),
            Priority: fields?["priority"]?["name"]?.GetValue<string>(),
            Reporter: fields?["reporter"]?["displayName"]?.GetValue<string>(),
            Assignee: fields?["assignee"]?["displayName"]?.GetValue<string>(),
            Updated: ToTimestamp(fields?["updated"]));
    }

    private static SourceUser? ToUser(JsonNode? user)
    {
        var accountId = user?["accountId"]?.GetValue<string>();
        if (accountId is null)
        {
            return null;
        }

        return new SourceUser(
            AccountId: accountId,
            DisplayName: user!["displayName"]?.GetValue<string>() ?? accountId,
            // Null on source-site: the tenant hides email addresses, which is why
            // users cannot be matched by email in the direction that matters.
            EmailAddress: user["emailAddress"]?.GetValue<string>());
    }

    private static DateTimeOffset? ToTimestamp(JsonNode? node)
    {
        var raw = node?.GetValue<string>();
        return DateTimeOffset.TryParse(raw, out var timestamp) ? timestamp : null;
    }

    private static bool IsEmpty(JsonNode? value) => value switch
    {
        null => true,
        JsonArray array => array.Count == 0,
        JsonValue jsonValue => jsonValue.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text),
        _ => false,
    };

    /// <summary>
    /// JQL string escaping. Backslash first, or the quote escapes get escaped
    /// a second time.
    /// </summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
