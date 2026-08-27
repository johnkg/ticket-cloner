using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TicketCloner.Api.Jira;

namespace TicketCloner.Api.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<MappingStatus>))]
public enum MappingStatus
{
    /// <summary>Carries over as-is, or with a value mapped by name.</summary>
    Mapped,

    /// <summary>Not on the target's create screen, or not settable on create.</summary>
    Dropped,

    /// <summary>
    /// The field exists on the target but the value cannot be expressed: the
    /// type differs, or no allowed value matches by name.
    /// </summary>
    Unmappable,

    /// <summary>
    /// Required on the target, no source value, and no default. This one stops
    /// the create outright.
    /// </summary>
    MissingRequired,
}

/// <summary>One row of the preview table.</summary>
public sealed record MappingRow(
    string Name,
    string? SourceFieldId,
    JsonNode? SourceValue,
    string? TargetFieldId,
    JsonNode? MappedValue,
    MappingStatus Status,
    string Reason,
    IReadOnlyList<string>? AllowedValues = null);

/// <summary>
/// The epic a copy should end up under, and whether one already exists there.
/// </summary>
/// <param name="ExistingCopy">The TGT epic that already corresponds to this
/// source epic, matched on External Issue ID or an identical summary. Null
/// means there is none and one has to be created for the copy to be
/// parented.</param>
public sealed record EpicPlan(
    string SourceKey,
    string SourceUrl,
    string Summary,
    ExistingCopy? ExistingCopy)
{
    public bool WillCreate => ExistingCopy is null;
}

/// <summary>
/// The whole preview for one issue, and the thing the apply step is handed back
/// so the two cannot disagree about what was about to happen.
/// </summary>
/// <summary>What a refresh would do to one field of an existing copy.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UpdateAction>))]
public enum UpdateAction
{
    /// <summary>The original has changed; this would be written.</summary>
    Update,

    /// <summary>The copy already matches.</summary>
    Unchanged,

    /// <summary>On the create screen but not the edit screen, so Jira would refuse it.</summary>
    NotEditable,

    /// <summary>
    /// The copy owns this now - status, assignee, sprint and the like. Never
    /// written by a refresh, however far the original has moved on.
    /// </summary>
    TargetOwns,
}

/// <param name="CurrentValue">What the copy holds now, in the shape Jira reads
/// it back as - which is richer than the shape a write uses.</param>
public sealed record UpdateRow(
    string Name,
    string TargetFieldId,
    JsonNode? CurrentValue,
    JsonNode? NewValue,
    UpdateAction Action,
    string Reason);

/// <summary>
/// What refreshing this copy from its original would change. Present only when
/// a copy exists AND its summary still matches - a drifted copy is reported,
/// never refreshed, for the same reason it is not treated as a duplicate.
///
/// Comments and attachments are deliberately NOT part of a refresh. Re-posting
/// comments duplicates the ones already copied, and matching them needs a
/// marker nothing writes yet; see todo.md.
/// </summary>
public sealed record UpdatePlan(
    string TargetKey,
    string TargetUrl,
    IReadOnlyList<UpdateRow> Rows)
{
    public int ChangedCount => Rows.Count(row => row.Action == UpdateAction.Update);

    public bool HasChanges => ChangedCount > 0;
}

public sealed record MappingPlan(
    string SourceKey,
    string SourceUrl,
    string TargetProjectKey,
    string TargetProjectName,
    string TargetIssueTypeId,
    string TargetIssueTypeName,
    string IssueTypeReason,
    IReadOnlyList<MappingRow> Rows,
    IReadOnlyList<string> Blockers,

    /// <summary>
    /// A copy already sitting on the target, found while previewing so the UI
    /// can show it and link to it.
    ///
    /// Advisory only. The plan makes a round trip through the browser, so apply
    /// runs the same search again itself rather than believing this.
    /// </summary>
    ExistingCopy? ExistingCopy = null,

    /// <summary>
    /// Set when the source issue sits under an Epic. Advisory like
    /// <see cref="ExistingCopy"/> - apply looks again rather than trusting a
    /// plan that has been through the browser.
    /// </summary>
    EpicPlan? Epic = null,

    /// <summary>
    /// What refreshing the existing copy would change. Null unless there is a
    /// copy to refresh - and it is only acted on for tickets named in
    /// ApplyRequest.UpdateExisting.
    /// </summary>
    UpdatePlan? Update = null)
{
    /// <summary>
    /// Nothing is created while a blocker stands. Unmappable values do not
    /// block - they are dropped, reported, and finished by hand.
    /// </summary>
    public bool CanCreate => Blockers.Count == 0;

    public int MappedCount => Rows.Count(row => row.Status == MappingStatus.Mapped);

    public int NeedsAttentionCount =>
        Rows.Count(row => row.Status is MappingStatus.Unmappable or MappingStatus.MissingRequired);
}
