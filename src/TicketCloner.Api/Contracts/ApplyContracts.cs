using System.Text.Json.Serialization;

namespace TicketCloner.Api.Contracts;

/// <summary>
/// Apply is handed the plans the preview produced, so the two cannot disagree
/// about what was about to happen.
///
/// Field values come from the plan. Comments and attachments do not - they are
/// re-read from the source at apply time, because they were never part of the
/// plan and nothing should be able to inject them by editing it.
/// </summary>
public sealed record ApplyRequest(
    IReadOnlyList<MappingPlan> Plans,
    bool IncludeComments = true,
    bool IncludeAttachments = true,
    bool SkipDuplicates = true);

[JsonConverter(typeof(JsonStringEnumConverter<ApplyStatus>))]
public enum ApplyStatus
{
    Created,

    /// <summary>Already copied, or the plan had blockers. Nothing was written.</summary>
    Skipped,

    /// <summary>The create itself failed. Nothing exists on the target.</summary>
    Failed,

    /// <summary>
    /// The issue was created but something after it did not. The copy exists
    /// and needs finishing by hand - which is why this is not just "Created".
    /// </summary>
    CreatedWithProblems,
}

public sealed record StepOutcome(string Step, bool Succeeded, string Detail);

public sealed record TicketOutcome(
    string SourceKey,
    string? TargetKey,
    string? TargetUrl,
    ApplyStatus Status,
    string Summary,
    IReadOnlyList<StepOutcome> Steps);

/// <summary>
/// Failures are per ticket and never abort the run. Tickets are independent -
/// unlike a cherry-pick sequence, where one conflict strands everything after
/// it - so one refusal must not sink the rest.
/// </summary>
public sealed record ApplyResponse(IReadOnlyList<TicketOutcome> Tickets)
{
    public int Created => Tickets.Count(ticket => ticket.Status is ApplyStatus.Created or ApplyStatus.CreatedWithProblems);

    public int Skipped => Tickets.Count(ticket => ticket.Status == ApplyStatus.Skipped);

    public int Failed => Tickets.Count(ticket => ticket.Status == ApplyStatus.Failed);
}
