namespace TicketCloner.Api.Configuration;

/// <summary>
/// The per-tenant vocabulary the mapper cannot infer.
///
/// Every entry here is a decision somebody made, not something discovered at
/// runtime - which is why they live in configuration where they can be read and
/// argued with, rather than in a switch statement.
/// </summary>
public sealed class MappingOptions
{
    public const string SectionName = "Mapping";

    /// <summary>
    /// Source issue type name -> target issue type name. Only needed where the
    /// names differ; an exact name match is used automatically.
    /// </summary>
    public Dictionary<string, string> IssueTypes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Source priority name -> target priority name.</summary>
    public Dictionary<string, string> Priorities { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Source accountId -> target accountId, for people who exist on one site
    /// only. Anyone holding a single Atlassian account across both tenants
    /// needs no entry: the accountId is already the same on each.
    ///
    /// This table exists because source-company hides email addresses, so there is
    /// no way to match those people programmatically.
    /// </summary>
    public Dictionary<string, string> Users { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Target field NAME -> literal value, for fields the target requires that
    /// the source has no equivalent of. Without an entry the plan reports a
    /// blocker rather than guessing, because a wrong constant is worse than a
    /// refusal: it writes plausible nonsense into every copy.
    /// </summary>
    public Dictionary<string, string> Constants { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Used when no rule maps the source type and no name matches. Empty means
    /// fail instead of falling back, which is the safer default.
    /// </summary>
    public string FallbackIssueType { get; init; } = "";
}
