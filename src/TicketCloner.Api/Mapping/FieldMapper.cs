using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TicketCloner.Api.Configuration;
using TicketCloner.Api.Contracts;

namespace TicketCloner.Api.Mapping;

public sealed record IssueTypeResolution(TargetIssueType? IssueType, string Reason);

/// <summary>
/// Turns a source issue into a plan for creating its counterpart.
///
/// Pure: it takes a source-issue DTO and the target's create screen, and knows
/// nothing about which tenants produced either. That is what lets it be tested
/// exhaustively without touching a live site.
/// </summary>
public sealed class FieldMapper(IOptions<MappingOptions> options)
{
    /// <summary>
    /// Cannot be set when an issue is created, whatever the create screen says.
    /// Sending any of them is either ignored or a 400.
    /// </summary>
    private static readonly HashSet<string> NeverSettableOnCreate = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "resolution", "created", "updated", "creator", "votes", "watches",
        "attachment", "comment", "issuelinks", "worklog", "subtasks", "progress",
        "aggregateprogress", "timespent", "workratio", "lastViewed", "thumbnail",
    };

    /// <summary>
    /// Fields that must never be mapped however well their types agree, keyed
    /// by NAME because the field id differs per tenant - which is exactly why
    /// <see cref="NeverSettableOnCreate"/>, keyed by id, cannot express this.
    ///
    /// Each of these carries a value that only means something on the tenant
    /// that issued it, while looking perfectly mappable on the way through.
    /// They are all the same shape of mistake: a reference - to a sprint, an
    /// epic, a parent - that is just an id or a key, with nothing in the value
    /// itself to say which site it came from.
    /// </summary>
    private static readonly Dictionary<string, string> NeverMappedByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sprint"] =
            "A sprint id belongs to the tenant that issued it. The source's Sprint is a greenhopper " +
            "field just like the target's, so the types agree and the value looks mappable - but it " +
            "names a sprint on the source board, and the target rejects it with 'Specify a valid " +
            "value for Sprint'. Set the sprint by hand after the copy.",

        ["Epic Link"] =
            "An epic link holds an issue key, and that key belongs to the source tenant. Both sides " +
            "carry the same greenhopper field type, so every check agrees and the value goes " +
            "straight through - onto an epic that either does not exist here or, worse, is a " +
            "different epic that happens to hold the same key. Link the copy to its epic by hand.",

        ["Parent"] =
            "A parent is an issue in the tenant that owns it, so a parent from the source names " +
            "nothing here. The two sides do not agree on shape either - the source reports an array " +
            "of issue links where the target takes a single one. Re-parent the copy by hand if it " +
            "needs one.",
    };

    private MappingOptions Options => options.Value;

    /// <summary>
    /// Configured rule first, then an exact name match, then the configured
    /// fallback. No fuzzy matching: guessing that "Production Issue" means
    /// "Bug" is exactly the kind of decision a person should have made.
    /// </summary>
    public IssueTypeResolution ResolveIssueType(string sourceType, IReadOnlyList<TargetIssueType> available)
    {
        if (Options.IssueTypes.TryGetValue(sourceType, out var configured))
        {
            var mapped = Find(available, configured);
            return mapped is not null
                ? new IssueTypeResolution(mapped, $"'{sourceType}' is mapped to '{configured}' by configuration.")
                : new IssueTypeResolution(null,
                    $"'{sourceType}' is mapped to '{configured}' by configuration, but the target cannot " +
                    $"create an issue type of that name. {Creatable(available)}");
        }

        var sameName = Find(available, sourceType);
        if (sameName is not null)
        {
            return new IssueTypeResolution(sameName, $"'{sourceType}' exists on the target under the same name.");
        }

        if (!string.IsNullOrWhiteSpace(Options.FallbackIssueType))
        {
            var fallback = Find(available, Options.FallbackIssueType);
            if (fallback is not null)
            {
                return new IssueTypeResolution(fallback,
                    $"'{sourceType}' has no counterpart; fell back to '{fallback.Name}'.");
            }
        }

        return new IssueTypeResolution(null,
            $"'{sourceType}' has no counterpart on the target and no mapping rule covers it. " +
            Creatable(available));
    }

    /// <summary>
    /// Createmeta lists what the running account can actually CREATE, which is
    /// narrower than the project's issue types - a type is missing here if
    /// permissions or the issue type screen scheme exclude it from creation.
    /// Saying which types are on offer turns a dead end into a fixable one.
    /// </summary>
    public static string Creatable(IReadOnlyList<TargetIssueType> available) =>
        available.Count == 0
            ? "The target offers no creatable issue types at all, which usually means the account lacks Create Issues on the project."
            : $"Creatable there: {string.Join(", ", available.Select(type => type.Name))}.";

    public MappingPlan Build(
        SourceIssue issue,
        string targetProjectKey,
        string targetProjectDisplay,
        TargetIssueType issueType,
        string issueTypeReason,
        IReadOnlyList<TargetField> createScreen)
    {
        var byName = createScreen
            .GroupBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var byId = createScreen.ToDictionary(field => field.FieldId, StringComparer.OrdinalIgnoreCase);

        var rows = new List<MappingRow>();
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Structural, and decided before this method was called: the writer
        // sends both on every create. Without these two rows the create
        // screen's own 'project' and 'issuetype' come back required-and-
        // unsupplied, and every ticket blocks on two fields never in doubt.
        rows.Add(MapProject(targetProjectKey, targetProjectDisplay, covered));
        rows.Add(MapIssueType(issue.IssueType, issueType, issueTypeReason, covered));

        rows.Add(MapSystemField("Summary", "summary", JsonValue.Create(issue.Summary), byId, covered));
        rows.Add(MapSystemField("Description", "description", issue.Description, byId, covered));
        rows.Add(MapPriority(issue, byId, covered));
        rows.Add(MapSystemField("Labels", "labels",
            new JsonArray(issue.Labels.Select(label => (JsonNode?)JsonValue.Create(label)).ToArray()),
            byId, covered));
        rows.Add(MapUser("Reporter", "reporter", issue.Reporter, byId, covered));
        rows.Add(MapUser("Assignee", "assignee", issue.Assignee, byId, covered));

        rows.AddRange(issue.Fields.Select(field => MapCustomField(field, byName, covered)));

        rows.AddRange(RequiredFieldsNotYetCovered(createScreen, covered));

        var ordered = rows
            .Where(row => row is not null)
            .OrderBy(row => row.Status switch
            {
                MappingStatus.MissingRequired => 0,
                MappingStatus.Unmappable => 1,
                MappingStatus.Dropped => 2,
                _ => 3,
            })
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var blockers = ordered
            .Where(row => row.Status == MappingStatus.MissingRequired)
            .Select(row => $"'{row.Name}' is required on the target, has no source value and no default. " +
                           $"Configure Mapping:Constants:{row.Name}.")
            .ToList();

        return new MappingPlan(
            SourceKey: issue.Key,
            SourceUrl: issue.Url,
            TargetProjectKey: targetProjectKey,
            TargetProjectName: targetProjectDisplay,
            TargetIssueTypeId: issueType.Id,
            TargetIssueTypeName: issueType.Name,
            IssueTypeReason: issueTypeReason,
            Rows: ordered,
            Blockers: blockers);
    }

    // ------------------------------------------------------------ structural

    /// <summary>
    /// The destination project, which is not read from the source at all -
    /// every copy goes to the one configured project. Named rather than keyed
    /// because the tenant names are inverted and the bare key reads as the
    /// wrong site.
    /// </summary>
    private static MappingRow MapProject(string projectKey, string display, HashSet<string> covered)
    {
        covered.Add("project");

        return new MappingRow("Project", "project", JsonValue.Create(display), "project",
            new JsonObject { ["key"] = projectKey }, MappingStatus.Mapped,
            $"Every copy is created in {display}. Set by Atlassian:Target:ProjectKey, never read from the source.");
    }

    /// <summary>
    /// The type this copy is created as, already settled by
    /// <see cref="ResolveIssueType"/>. Its reason travels with it so the row
    /// says which rule chose the type rather than only what it chose.
    /// </summary>
    private static MappingRow MapIssueType(
        string sourceType,
        TargetIssueType issueType,
        string reason,
        HashSet<string> covered)
    {
        covered.Add("issuetype");

        return new MappingRow("Issue Type", "issuetype", JsonValue.Create(sourceType), "issuetype",
            new JsonObject { ["id"] = issueType.Id }, MappingStatus.Mapped, reason);
    }

    // ---------------------------------------------------------------- fields

    private static MappingRow MapSystemField(
        string label,
        string fieldId,
        JsonNode? value,
        Dictionary<string, TargetField> byId,
        HashSet<string> covered)
    {
        if (IsEmpty(value))
        {
            return new MappingRow(label, fieldId, value, null, null, MappingStatus.Dropped,
                "Not set on the source.");
        }

        if (!byId.TryGetValue(fieldId, out var target))
        {
            return new MappingRow(label, fieldId, value, null, null, MappingStatus.Dropped,
                "Not on the target's create screen. Sending it would be a 400.");
        }

        covered.Add(target.FieldId);
        return new MappingRow(label, fieldId, value, target.FieldId, value, MappingStatus.Mapped,
            "Carried over as-is.");
    }

    private MappingRow MapPriority(
        SourceIssue issue,
        Dictionary<string, TargetField> byId,
        HashSet<string> covered)
    {
        var sourceValue = issue.Priority is null ? null : JsonValue.Create(issue.Priority);

        if (issue.Priority is null)
        {
            return new MappingRow("Priority", "priority", null, null, null, MappingStatus.Dropped,
                "Not set on the source.");
        }

        if (!byId.TryGetValue("priority", out var target))
        {
            return new MappingRow("Priority", "priority", sourceValue, null, null, MappingStatus.Dropped,
                "Not on the target's create screen.");
        }

        // Configured rename first - the source's 'None' has no counterpart and
        // is mapped to a real priority rather than left to fail.
        var wanted = Options.Priorities.TryGetValue(issue.Priority, out var renamed)
            ? renamed
            : issue.Priority;

        var match = target.AllowedValues
            .FirstOrDefault(allowed => allowed.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));

        covered.Add(target.FieldId);

        if (match is null)
        {
            return new MappingRow("Priority", "priority", sourceValue, target.FieldId, null,
                MappingStatus.Unmappable,
                $"No target priority is named '{wanted}'.",
                target.AllowedValues.Select(allowed => allowed.Name).ToList());
        }

        var reason = wanted == issue.Priority
            ? $"Matched target priority '{match.Name}' by name."
            : $"'{issue.Priority}' is mapped to '{match.Name}' by configuration.";

        return new MappingRow("Priority", "priority", sourceValue, target.FieldId,
            new JsonObject { ["id"] = match.Id }, MappingStatus.Mapped, reason);
    }

    private static MappingRow MapUser(
        string label,
        string fieldId,
        SourceUser? user,
        Dictionary<string, TargetField> byId,
        HashSet<string> covered)
    {
        if (user is null)
        {
            return new MappingRow(label, fieldId, null, null, null, MappingStatus.Dropped,
                "Not set on the source.");
        }

        var sourceValue = JsonValue.Create(user.DisplayName);

        if (!byId.TryGetValue(fieldId, out var target))
        {
            return new MappingRow(label, fieldId, sourceValue, null, null, MappingStatus.Dropped,
                "Not on the target's create screen.");
        }

        covered.Add(target.FieldId);

        // accountId is shared across tenants for anyone holding one Atlassian
        // account on both sites. Whether this particular person exists on the
        // target is checked against the target's user directory at apply time -
        // the mapper has no directory to consult.
        return new MappingRow(label, fieldId, sourceValue, target.FieldId,
            new JsonObject { ["accountId"] = user.AccountId }, MappingStatus.Mapped,
            "accountId carried across. Falls back to the running account if that person does not exist on the target.");
    }

    private MappingRow MapCustomField(
        SourceFieldValue field,
        Dictionary<string, TargetField> byName,
        HashSet<string> covered)
    {
        if (NeverSettableOnCreate.Contains(field.FieldId))
        {
            return new MappingRow(field.Name, field.FieldId, field.Value, null, null, MappingStatus.Dropped,
                "Not settable when an issue is created.");
        }

        // Checked before the name lookup, so a field on this list is dropped
        // whether or not the target happens to offer a counterpart.
        if (NeverMappedByName.TryGetValue(field.Name, out var why))
        {
            return new MappingRow(field.Name, field.FieldId, field.Value, null, null,
                MappingStatus.Dropped, why);
        }

        // By NAME, never by id: customfield_70010 on the source is a different
        // field on the target.
        if (!byName.TryGetValue(field.Name, out var target))
        {
            return new MappingRow(field.Name, field.FieldId, field.Value, null, null, MappingStatus.Dropped,
                "No field of this name on the target's create screen.");
        }

        if (!TypesAgree(field, target))
        {
            // The dangerous case: it looks mappable and would either 400 or
            // write the wrong shape of value.
            return new MappingRow(field.Name, field.FieldId, field.Value, target.FieldId, null,
                MappingStatus.Unmappable,
                $"Name matches but the type differs: source is '{Describe(field.SchemaCustom, field.SchemaType)}', " +
                $"target is '{Describe(target.SchemaCustom, target.SchemaType)}'.");
        }

        covered.Add(target.FieldId);

        if (target.AllowedValues.Count == 0)
        {
            return new MappingRow(field.Name, field.FieldId, field.Value, target.FieldId, field.Value,
                MappingStatus.Mapped, "Carried over as-is.");
        }

        return MapAgainstAllowedValues(field, target);
    }

    /// <summary>
    /// Single-selects, multi-selects and anything else carrying per-tenant
    /// option ids. Matching is by name; the id is regenerated from the target's
    /// own list.
    /// </summary>
    private static MappingRow MapAgainstAllowedValues(SourceFieldValue field, TargetField target)
    {
        var wanted = NamesIn(field.Value);

        if (wanted.Count == 0)
        {
            return new MappingRow(field.Name, field.FieldId, field.Value, target.FieldId, null,
                MappingStatus.Unmappable,
                "The source value has no name to match against the target's options.",
                target.AllowedValues.Select(allowed => allowed.Name).ToList());
        }

        var matched = new List<AllowedValue>();
        var missing = new List<string>();

        foreach (var name in wanted)
        {
            var match = target.AllowedValues
                .FirstOrDefault(allowed => allowed.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                missing.Add(name);
            }
            else
            {
                matched.Add(match);
            }
        }

        if (missing.Count > 0)
        {
            return new MappingRow(field.Name, field.FieldId, field.Value, target.FieldId, null,
                MappingStatus.Unmappable,
                $"No target option named {string.Join(", ", missing.Select(name => $"'{name}'"))}.",
                target.AllowedValues.Select(allowed => allowed.Name).ToList());
        }

        JsonNode mapped = target.IsArray
            ? new JsonArray(matched.Select(value => (JsonNode?)new JsonObject { ["id"] = value.Id }).ToArray())
            : new JsonObject { ["id"] = matched[0].Id };

        return new MappingRow(field.Name, field.FieldId, field.Value, target.FieldId, mapped,
            MappingStatus.Mapped,
            $"Matched target option{(matched.Count == 1 ? "" : "s")} " +
            $"{string.Join(", ", matched.Select(value => $"'{value.Name}'"))} by name.");
    }

    /// <summary>
    /// Required fields nothing has supplied. A configured constant satisfies
    /// them; otherwise this is a blocker, because a wrong constant is worse
    /// than a refusal - it writes plausible nonsense into every copy.
    /// </summary>
    private IEnumerable<MappingRow> RequiredFieldsNotYetCovered(
        IReadOnlyList<TargetField> createScreen,
        HashSet<string> covered)
    {
        foreach (var field in createScreen.Where(field => field.Required))
        {
            if (covered.Contains(field.FieldId) || NeverSettableOnCreate.Contains(field.FieldId))
            {
                continue;
            }

            if (Options.Constants.TryGetValue(field.Name, out var constant))
            {
                yield return new MappingRow(field.Name, null, null, field.FieldId,
                    JsonValue.Create(constant), MappingStatus.Mapped,
                    $"Required by the target; supplied from Mapping:Constants as '{constant}'.");
                continue;
            }

            if (field.HasDefaultValue)
            {
                yield return new MappingRow(field.Name, null, null, field.FieldId, null,
                    MappingStatus.Dropped,
                    "Required by the target, but it has a default and the source has no value.");
                continue;
            }

            yield return new MappingRow(field.Name, null, null, field.FieldId, null,
                MappingStatus.MissingRequired,
                "Required by the target, with no source value and no default.");
        }
    }

    // ---------------------------------------------------------------- helpers

    private static TargetIssueType? Find(IReadOnlyList<TargetIssueType> available, string name) =>
        available.FirstOrDefault(type => type.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool TypesAgree(SourceFieldValue source, TargetField target)
    {
        // A custom type key is exact or nothing. Two fields both called
        // "Sprint" where one is a textfield and the other is the greenhopper
        // sprint field must not map onto each other.
        if (source.SchemaCustom is not null || target.SchemaCustom is not null)
        {
            return string.Equals(source.SchemaCustom, target.SchemaCustom, StringComparison.Ordinal);
        }

        return string.Equals(source.SchemaType, target.SchemaType, StringComparison.OrdinalIgnoreCase);
    }

    private static string Describe(string? custom, string? type) => custom ?? type ?? "unknown";

    /// <summary>Pulls the human-readable name(s) out of whatever shape a field value takes.</summary>
    private static List<string> NamesIn(JsonNode? value) => value switch
    {
        null => [],
        JsonArray array => array.SelectMany(item => NamesIn(item)).ToList(),
        JsonObject obj => Name(obj) is { } name ? [name] : [],
        JsonValue leaf => leaf.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? [text]
            : [],
        _ => [],
    };

    private static string? Name(JsonObject obj) =>
        obj["value"]?.GetValue<string>() ?? obj["name"]?.GetValue<string>();

    private static bool IsEmpty(JsonNode? value) => value switch
    {
        null => true,
        JsonArray array => array.Count == 0,
        JsonValue leaf => leaf.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text),
        _ => false,
    };
}
