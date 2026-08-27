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

        return new PreviewResult(plan, null);
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
