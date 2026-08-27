using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TicketCloner.Api.Atlassian;
using TicketCloner.Api.Configuration;
using TicketCloner.Api.Adf;
using TicketCloner.Api.Contracts;

namespace TicketCloner.Api.Jira;

/// <summary>A file already on the copy. Size tells a replaced file from the same one.</summary>
public sealed record TargetAttachment(string Id, string FileName, long Size, DateTimeOffset Created);

/// <summary>
/// What would change if an existing copy were refreshed from its original.
///
/// This is deliberately NOT sync. A copy is still a one-shot snapshot that
/// lives its own life (see CLAUDE.md, "Goal"); this is a one-way, on-demand,
/// per-ticket refresh that somebody asks for while looking at the difference.
/// Nothing runs on a schedule, nothing watches for changes, and nothing flows
/// back to the source - the moment any of those is wanted, the answer is a
/// sync product and not this tool.
///
/// The planner only ever reads. It produces rows; deciding and writing happen
/// in ApplyService, and only for tickets named in
/// <see cref="ApplyRequest.UpdateExisting"/>.
/// </summary>
public sealed class CopyRefreshPlanner(
    AtlassianClientFactory clients,
    AdfRewriter adf,
    IOptions<AtlassianOptions> options,
    ILogger<CopyRefreshPlanner> logger)
{
    /// <summary>
    /// Fields the TARGET owns once the copy exists, whatever the original says.
    ///
    /// A copy somebody has picked up has its own status, its own assignee, and
    /// its own place on the board. Refreshing the wording of a ticket must not
    /// also drag it backwards through the workflow or take it off the person
    /// working it - that would be a worse outcome than not refreshing at all.
    ///
    /// Keyed by field id because these are all system fields, which carry the
    /// same ids on every Jira site. The sprint field is not here: it is custom,
    /// its id differs per tenant, and it is excluded by schema instead.
    /// </summary>
    private static readonly HashSet<string> TargetOwns = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "resolution", "assignee", "reporter",
        "project", "issuetype", "parent",
    };

    /// <summary>
    /// Fields holding ADF. They cannot be written across tenants as they stand
    /// - a mention carries a source accountId, a smartlink a source URL, and a
    /// media node a source attachment id - so they go through AdfRewriter and
    /// are compared as rewritten, never raw.
    /// </summary>
    private static readonly HashSet<string> Adf = new(StringComparer.OrdinalIgnoreCase)
    {
        "description", "environment",
    };

    private TenantOptions Target => options.Value.Target;

    /// <summary>
    /// Null when there is nothing to refresh against - no copy, or a copy whose
    /// summary has drifted, which is already reported as "not treated as a
    /// duplicate" and must not be quietly overwritten either.
    /// </summary>
    public async Task<UpdatePlan?> PlanAsync(
        MappingPlan plan,
        SourceIssue issue,
        ExistingCopy? copy,
        IReadOnlyList<TargetField> createScreen,
        CancellationToken cancellationToken)
    {
        if (copy is not { SummaryMatches: true })
        {
            return null;
        }

        // Rows the mapper already resolved. Anything it dropped or could not
        // map is not suddenly safer on an update than it was on a create.
        var candidates = plan.Rows
            .Where(row => row.Status == MappingStatus.Mapped &&
                          row.TargetFieldId is { Length: > 0 } &&
                          row.MappedValue is not null)
            .ToList();

        if (candidates.Count == 0)
        {
            return new UpdatePlan(copy.Key, copy.Url, []);
        }

        var client = clients.For(Tenant.Target);
        var key = Uri.EscapeDataString(copy.Key);

        var editable = await EditableFieldsAsync(client, key, cancellationToken);
        var current = await CurrentValuesAsync(client, key, candidates, cancellationToken);

        // What the copy already holds, so a media node can point at an
        // attachment that is on the target rather than one that is not.
        var held = await AttachmentsAsync(client, key, cancellationToken);

        var sprintFieldId = createScreen
            .FirstOrDefault(field => field.SchemaCustom == SprintReader.SprintSchema)?.FieldId;

        var sourceUrlFieldId = createScreen
            .FirstOrDefault(field => field.Name.Equals(Target.SourceUrlFieldName, StringComparison.OrdinalIgnoreCase))
            ?.FieldId;

        var rows = candidates
            .Select(row => Classify(row, issue, current, editable, held, sprintFieldId, sourceUrlFieldId))
            .ToList();

        return new UpdatePlan(copy.Key, copy.Url, rows);
    }

    private UpdateRow Classify(
        MappingRow row,
        SourceIssue issue,
        IReadOnlyDictionary<string, JsonNode?> current,
        IReadOnlySet<string>? editable,
        IReadOnlyList<TargetAttachment> held,
        string? sprintFieldId,
        string? sourceUrlFieldId)
    {
        var fieldId = row.TargetFieldId!;
        var value = row.MappedValue;
        var existing = current.GetValueOrDefault(fieldId);

        if (TargetOwns.Contains(fieldId))
        {
            return new UpdateRow(row.Name, fieldId, existing, value, UpdateAction.TargetOwns,
                "The copy owns this once it exists - refreshing it would undo work done on the target.");
        }

        if (fieldId == sprintFieldId)
        {
            return new UpdateRow(row.Name, fieldId, existing, value, UpdateAction.TargetOwns,
                "Which sprint the copy sits in is the target board's business, not the original's.");
        }

        if (fieldId == sourceUrlFieldId)
        {
            return new UpdateRow(row.Name, fieldId, existing, value, UpdateAction.TargetOwns,
                "This is what identifies the copy. It already points at the original.");
        }

        if (Adf.Contains(fieldId))
        {
            return ClassifyAdf(row, issue, existing, value, held);
        }

        // Null means editmeta could not be read; every row then falls through
        // on its own merits rather than the whole refresh being refused.
        if (editable is not null && !editable.Contains(fieldId))
        {
            return new UpdateRow(row.Name, fieldId, existing, value, UpdateAction.NotEditable,
                "On the target's create screen but not its edit screen, so Jira would refuse it.");
        }

        return Same(existing, value)
            ? new UpdateRow(row.Name, fieldId, existing, value, UpdateAction.Unchanged,
                "The copy already matches the original.")
            : new UpdateRow(row.Name, fieldId, existing, value, UpdateAction.Update,
                "The original has changed since the copy was made.");
    }

    /// <summary>
    /// Rich text, compared as it would be WRITTEN rather than as it stands.
    ///
    /// The source's raw ADF never matches the copy's and never should: the copy
    /// holds rewritten mentions and media pointing at the target's own
    /// attachments. So the source is rewritten first, against what the copy
    /// already has, and only then compared.
    ///
    /// A media node whose file is not on the target yet is the interesting
    /// case - somebody added an image to the original after it was copied.
    /// That is a real change and it is reported as one; the upload happens at
    /// apply time, because planning must not write anything.
    /// </summary>
    private UpdateRow ClassifyAdf(
        MappingRow row,
        SourceIssue issue,
        JsonNode? existing,
        JsonNode? value,
        IReadOnlyList<TargetAttachment> held)
    {
        var wanted = MediaFileNames(value, issue.AttachmentNamesByMediaId);

        // Only files the DESCRIPTION refers to. An attachment added to the
        // original but never embedded is a separate question, and turning a
        // description refresh into an attachment sync would be a much bigger
        // promise than the one being made here.
        var missing = wanted
            .Where(name => Current(held, name) is null)
            .ToList();

        var replaced = wanted
            .Where(name => Current(held, name) is { } onTarget &&
                           issue.Attachments.FirstOrDefault(attachment =>
                               attachment.FileName.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } onSource &&
                           onSource.Size != onTarget.Size)
            .ToList();

        // Rewritten against what the copy already holds, which is exactly what
        // apply would write if nothing had to be uploaded.
        var rewritten = adf.Rewrite(value, Context(issue, held));

        var pending = missing.Count + replaced.Count;

        if (pending == 0 && Same(existing, rewritten.Document))
        {
            return new UpdateRow(row.Name, row.TargetFieldId!, existing, rewritten.Document,
                UpdateAction.Unchanged, "The copy already matches the original.");
        }

        var reason = pending == 0
            ? "The original's wording has changed since the copy was made."
            : $"{Describe(missing, "new")}{(missing.Count > 0 && replaced.Count > 0 ? " and " : "")}" +
              $"{Describe(replaced, "changed")} would be uploaded to the copy first, then pointed at.";

        return new UpdateRow(row.Name, row.TargetFieldId!, existing, rewritten.Document,
            UpdateAction.Update, reason);
    }

    private static string Describe(IReadOnlyList<string> files, string kind) =>
        files.Count == 0
            ? ""
            : $"{files.Count} {kind} image{(files.Count == 1 ? "" : "s")} ({string.Join(", ", files)})";

    /// <summary>
    /// The rewrite context for this copy: mentions flattened, links absolutised
    /// against the source, media pointed at the target's own attachments.
    ///
    /// Mentions are deliberately NOT re-resolved here. A refresh does not touch
    /// reporter or assignee - the copy owns those - and re-resolving a mention
    /// would make the rewritten text differ from what the create path wrote,
    /// which would report every description as changed for ever.
    /// </summary>
    private AdfRewriteContext Context(SourceIssue issue, IReadOnlyList<TargetAttachment> held) =>
        new(new Uri(issue.Url),
            new Dictionary<string, string>(),
            held.GroupBy(attachment => attachment.FileName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => ContentUrl(group.OrderByDescending(a => a.Created).First().Id),
                    StringComparer.OrdinalIgnoreCase),
            issue.AttachmentNamesByMediaId);

    /// <summary>
    /// Composed from the SITE, never from anything Jira echoes back - see
    /// TargetIssueWriter.UploadAttachmentAsync for what happens otherwise.
    /// </summary>
    public string ContentUrl(string attachmentId) =>
        new Uri(Target.SiteUri, $"rest/api/3/attachment/content/{attachmentId}").ToString();

    /// <summary>
    /// The newest attachment of that name on the copy, or null. Newest because
    /// a file replaced on the original is uploaded again rather than swapped,
    /// so the same name can appear twice and the later one is the current one.
    /// </summary>
    public static TargetAttachment? Current(IReadOnlyList<TargetAttachment> held, string fileName) =>
        held.Where(attachment => attachment.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(attachment => attachment.Created)
            .FirstOrDefault();

    /// <summary>
    /// Every file an ADF document embeds, by name.
    /// </summary>
    /// <param name="namesByMediaId">Media Services UUID to file name, from the
    /// source's rendered description. Needed for a node carrying no alt - an
    /// inline file never has one - and those files have to be uploaded to the
    /// copy like any other, or the link written for them points at nothing.</param>
    public static IReadOnlyList<string> MediaFileNames(
        JsonNode? document, IReadOnlyDictionary<string, string>? namesByMediaId = null)
    {
        var names = new List<string>();
        Walk(document, names);

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        void Walk(JsonNode? node, List<string> into)
        {
            switch (node)
            {
                case JsonArray array:
                    foreach (var child in array) Walk(child, into);
                    break;

                case JsonObject obj:
                    if (obj["type"]?.GetValue<string>() is { } type &&
                        type.StartsWith("media", StringComparison.OrdinalIgnoreCase))
                    {
                        var byAlt = obj["attrs"]?["alt"]?.GetValue<string>();

                        var byId = obj["attrs"]?["id"]?.GetValue<string>() is { } id &&
                                   namesByMediaId?.TryGetValue(id, out var fromMap) is true
                            ? fromMap
                            : null;

                        if ((byAlt ?? byId) is { Length: > 0 } name)
                        {
                            into.Add(name);
                        }
                    }

                    foreach (var pair in obj) Walk(pair.Value, into);
                    break;
            }
        }
    }

    /// <summary>What the copy holds, so a refresh knows what it need not upload.</summary>
    public async Task<IReadOnlyList<TargetAttachment>> AttachmentsAsync(
        AtlassianClient client, string escapedKey, CancellationToken cancellationToken)
    {
        var response = await client.GetJsonAsync(
            $"rest/api/3/issue/{escapedKey}?fields=attachment", cancellationToken);

        return (response?["fields"]?["attachment"] as JsonArray ?? [])
            .Select(attachment => new TargetAttachment(
                Id: attachment?["id"]?.GetValue<string>() ?? "",
                FileName: attachment?["filename"]?.GetValue<string>() ?? "",
                Size: attachment?["size"]?.GetValue<long>() ?? 0,
                Created: DateTimeOffset.TryParse(attachment?["created"]?.GetValue<string>(), out var when)
                    ? when
                    : DateTimeOffset.MinValue))
            .Where(attachment => attachment.Id.Length > 0)
            .ToList();
    }

    /// <summary>The client for the target, for callers that need the same one.</summary>
    public AtlassianClient TargetClient => clients.For(Tenant.Target);

    /// <summary>
    /// Which fields the EDIT screen offers, which is not the same question as
    /// the create screen the mapper worked from. A field can be creatable and
    /// not editable, and sending one Jira will not take fails the whole PUT -
    /// so every other field on the refresh would be lost with it.
    ///
    /// GOTCHA: editmeta's "fields" is an OBJECT keyed by field id, not the
    /// array createmeta returns, and it does not page.
    ///
    /// Null on failure rather than empty: an empty set reads as "nothing is
    /// editable" and would silently refuse every row.
    /// </summary>
    private async Task<IReadOnlySet<string>?> EditableFieldsAsync(
        AtlassianClient client, string escapedKey, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetJsonAsync(
                $"rest/api/3/issue/{escapedKey}/editmeta", cancellationToken);

            if (response?["fields"] is not JsonObject fields)
            {
                logger.LogWarning("editmeta for {Key} carried no 'fields' object", escapedKey);
                return null;
            }

            return fields.Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (AtlassianApiException failed)
        {
            logger.LogWarning(
                "Could not read editmeta for {Key}: {Status}", escapedKey, (int)failed.StatusCode);

            return null;
        }
    }

    /// <summary>What the copy holds now, for the fields a refresh might touch.</summary>
    private static async Task<IReadOnlyDictionary<string, JsonNode?>> CurrentValuesAsync(
        AtlassianClient client,
        string escapedKey,
        IReadOnlyList<MappingRow> candidates,
        CancellationToken cancellationToken)
    {
        var wanted = candidates.Select(row => row.TargetFieldId!).Distinct().ToList();

        var response = await client.GetJsonAsync(
            $"rest/api/3/issue/{escapedKey}?fields={Uri.EscapeDataString(string.Join(",", wanted))}",
            cancellationToken);

        var fields = response?["fields"] as JsonObject ?? [];

        return wanted.ToDictionary(
            id => id,
            id => fields[id]?.DeepClone(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the copy already carries what the original would write.
    ///
    /// Compared as canonical JSON. Jira reads a field back in a richer shape
    /// than it is written with - a select comes back with id, value, self and
    /// more, where only the id goes up - so this compares what is SENT against
    /// the same subset of what is held, and treats a difference elsewhere in
    /// the read shape as no difference at all.
    /// </summary>
    private static bool Same(JsonNode? current, JsonNode? wanted)
    {
        if (current is null || wanted is null)
        {
            return current is null && wanted is null;
        }

        // The written shape decides what is compared: { "id": "3" } against a
        // read object carrying id, value and self matches on id alone.
        if (wanted is JsonObject wantedObject && current is JsonObject currentObject)
        {
            return wantedObject.All(pair =>
                currentObject.TryGetPropertyValue(pair.Key, out var held) &&
                Canonical(held) == Canonical(pair.Value));
        }

        return Canonical(current) == Canonical(wanted);
    }

    private static string Canonical(JsonNode? node) =>
        node is null ? "null" : node.ToJsonString(CanonicalOptions);

    private static readonly JsonSerializerOptions CanonicalOptions = new() { WriteIndented = false };
}
