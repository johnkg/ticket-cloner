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
/// <param name="CreateEpicsFor">Source epic keys the caller agreed to create.
/// An epic missing from the target is only cloned when its key appears here, so
/// a ticket nobody selected is never written on the strength of a parent
/// relationship alone.</param>
/// <param name="AddToActiveSprint">Put each copy in the TARGET board's current
/// sprint, whichever that is when the run happens. Off by default: a copy
/// landing in the sprint somebody is working now is a decision, not a detail,
/// and the sprint that gets used is named in the UI before the button is
/// pressed.</param>
/// <param name="SprintId">Put each copy in this TARGET sprint instead, chosen
/// from the target board's own list. A target id and nothing else - the source
/// has sprint ids too and they mean nothing here. Wins over
/// <see cref="AddToActiveSprint"/> when both are set.</param>
public sealed record ApplyRequest(
    IReadOnlyList<MappingPlan> Plans,
    bool IncludeComments = true,
    bool IncludeAttachments = true,
    bool SkipDuplicates = true,
    IReadOnlyList<string>? CreateEpicsFor = null,
    bool AddToActiveSprint = false,
    int? SprintId = null);

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
