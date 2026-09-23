using Microsoft.Extensions.Options;
using TicketCloner.Api.Configuration;
using TicketCloner.Api.Contracts;
using TicketCloner.Api.Jira;

namespace TicketCloner.Api.Mapping;

public sealed record PreviewResult(MappingPlan? Plan, string? Error);

/// <summary>
/// Reads both sides and hands them to the mapper. Kept separate from
/// <see cref="FieldMapper"/> so the rules stay testable without any HTTP.
/// </summary>
public sealed class MappingService(
    SourceIssueReader source,
    TargetMetadataReader target,
    FieldMapper mapper,
    ExistingCopyFinder existingCopies,
    IOptions<AtlassianOptions> options)
{
    public async Task<PreviewResult> PreviewAsync(
        string sourceKey,
        string? issueTypeOverride,
        CancellationToken cancellationToken)
    {
        var issue = await source.GetAsync(sourceKey, cancellationToken);
        if (issue is null)
        {
            return new PreviewResult(null, $"The source tenant returned no issue for '{sourceKey}'.");
        }

        var issueTypes = await target.GetIssueTypesAsync(cancellationToken);

        // An explicit override still has to exist on the target - the caller
        // picking a type is not evidence that it is creatable.
        var resolution = issueTypeOverride is { Length: > 0 }
            ? ResolveOverride(issueTypeOverride, issueTypes)
            : mapper.ResolveIssueType(issue.IssueType, issueTypes);

        if (resolution.IssueType is null)
        {
            return new PreviewResult(null, resolution.Reason);
        }

        var createScreen = await target.GetFieldsAsync(resolution.IssueType.Id, cancellationToken);

        var plan = mapper.Build(
            issue,
            options.Value.Target.ProjectKey,
            options.Value.Target.ProjectDisplay,
            resolution.IssueType,
            resolution.Reason,
            createScreen);

        // Answered here rather than only at apply time, so the decision about
        // what to copy can be made while looking at the plan instead of being
        // discovered afterwards in the results.
        var existing = await FindExistingCopyAsync(issue, createScreen, cancellationToken);
        var epic = await FindEpicAsync(issue, createScreen, cancellationToken);

        return new PreviewResult(plan with { ExistingCopy = existing, Epic = epic }, null);
    }

    /// <summary>
    /// Only an Epic parent counts. A sub-task's parent is a different
    /// relationship, and re-creating that across tenants is not what this is
    /// for - the parent would have to exist first, and it does not.
    /// </summary>
    private async Task<EpicPlan?> FindEpicAsync(
        SourceIssue issue,
        IReadOnlyList<TargetField> createScreen,
        CancellationToken cancellationToken)
    {
        if (issue.Parent is not { IsEpic: true } parent)
        {
            return null;
        }

        var fieldName = options.Value.Target.SourceUrlFieldName;

        var field = createScreen.FirstOrDefault(candidate =>
            candidate.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));

        var existing = field is null
            ? null
            : await existingCopies.FindEpicAsync(
                field.Name, field.FieldId, parent.Key, parent.Url, parent.Summary, cancellationToken);

        return new EpicPlan(
            parent.Key,
            parent.Url,
            parent.Summary,
            existing is { SummaryMatches: true } ? existing : null);
    }

    private async Task<ExistingCopy?> FindExistingCopyAsync(
        SourceIssue issue,
        IReadOnlyList<TargetField> createScreen,
        CancellationToken cancellationToken)
    {
        var fieldName = options.Value.Target.SourceUrlFieldName;

        var field = createScreen.FirstOrDefault(candidate =>
            candidate.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));

        // Nothing to match on. Apply says so in its own results; a preview that
        // simply shows no copy would be claiming more than it knows.
        if (field is null)
        {
            return null;
        }

        return await existingCopies.FindAsync(
            field.Name, field.FieldId, issue.Key, issue.Url, issue.Summary, cancellationToken);
    }

    private static IssueTypeResolution ResolveOverride(string name, IReadOnlyList<TargetIssueType> available)
    {
        var match = available.FirstOrDefault(type =>
            type.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            type.Id == name);

        return match is not null
            ? new IssueTypeResolution(match, $"Issue type '{match.Name}' was chosen explicitly.")
            : new IssueTypeResolution(null,
                $"The target has no creatable issue type named '{name}'. {FieldMapper.Creatable(available)}");
    }
}
