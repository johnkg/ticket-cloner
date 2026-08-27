namespace TicketCloner.Api.Contracts;

public sealed record TargetIssueType(string Id, string Name, bool Subtask);

/// <summary>
/// One field on the target's CREATE screen. Anything absent from this list is a
/// hard 400 if sent, which is why unmapped fields are dropped and reported
/// rather than sent hopefully.
/// </summary>
/// <param name="HasDefaultValue">Only meaningful together with
/// <paramref name="Required"/>: required with a default is fine, required
/// without one and with no source value stops a create dead.</param>
public sealed record TargetField(
    string FieldId,
    string Name,
    bool Required,
    bool HasDefaultValue,
    string? SchemaType,
    string? SchemaCustom,
    bool IsArray,
    IReadOnlyList<AllowedValue> AllowedValues);

/// <param name="Name">Jira calls this <c>name</c> on system fields and
/// <c>value</c> on custom select options. Both land here.</param>
public sealed record AllowedValue(string Id, string Name);

public sealed record TargetIssueTypeFields(
    TargetIssueType IssueType,
    IReadOnlyList<TargetField> Fields);

/// <summary>
/// The target board's current sprint. Ids here belong to the TARGET tenant and
/// are read from it - nothing about this comes from the source.
/// </summary>
public sealed record ActiveSprint(
    int Id,
    string Name,
    string? Goal,
    DateTimeOffset? StartDate,
    DateTimeOffset? EndDate);

/// <param name="Sprint">Null when there is no single active sprint.</param>
/// <param name="Reason">Why not, in a sentence the UI can show. Null when
/// <paramref name="Sprint"/> is set.</param>
public sealed record ActiveSprintResult(ActiveSprint? Sprint, string? Reason);
