using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace TicketCloner.Api.Configuration;

/// <summary>
/// Source and Target, never TARGET_PROJECT and YOUR_COMPANY. The project named TARGET_PROJECT lives on
/// the YOUR_COMPANY tenant and the project named YOUR_SOURCE_PROJECT lives
/// on the SOURCE_COMPANY tenant, so a section named after the project resolves to
/// the wrong site. See CLAUDE.md.
/// </summary>
public sealed class AtlassianOptions
{
    public const string SectionName = "Atlassian";

    /// <summary>Read from. The tool must never write here.</summary>
    public TenantOptions Source { get; init; } = new();

    /// <summary>Written to.</summary>
    public TenantOptions Target { get; init; } = new();
}

public sealed class TenantOptions
{
    [Required(AllowEmptyStrings = false)]
    public string BaseUrl { get; init; } = "";

    [Required(AllowEmptyStrings = false)]
    public string ProjectKey { get; init; } = "";

    /// <summary>
    /// The project's display name. The key alone does not tell a reader which
    /// tenant they are about to write to, and the names here are inverted - so
    /// the preview says "YOUR_TARGET_PROJECT (TARGET_PROJECT)" rather than "TARGET_PROJECT",
    /// which reads as the other site. Falls back to the key when unset.
    /// </summary>
    public string ProjectName { get; init; } = "";

    /// <summary>How the preview names this project.</summary>
    public string ProjectDisplay =>
        string.IsNullOrWhiteSpace(ProjectName) ? ProjectKey : $"{ProjectName} ({ProjectKey})";

    public int BoardId { get; init; }

    /// <summary>
    /// What the UI offers in its project dropdown. Defaults to just
    /// <see cref="ProjectKey"/>.
    ///
    /// NOTE: choosing a different one does not yet do anything - ProjectKey is
    /// what the reader, createmeta and the writer all use. Adding a second
    /// entry here needs that plumbed through first.
    /// </summary>
    public string[] AvailableProjects { get; init; } = [];

    public IReadOnlyList<string> ProjectChoices =>
        AvailableProjects.Length > 0 ? AvailableProjects : [ProjectKey];

    /// <summary>
    /// Source only: issue types excluded at the QUERY, not at the mapping
    /// stage. A type that is never read cannot be mis-mapped, and the six Xray
    /// test-management types have no business in a development project.
    /// </summary>
    public string[] ExcludedIssueTypes { get; init; } = [];

    /// <summary>
    /// Target only: a JQL-searchable text field holding the source issue key.
    /// This is the duplicate check - remote links cannot be queried.
    /// </summary>
    public string ProvenanceFieldName { get; init; } = "TARGET_PROJECT Source Key";

    /// <summary>
    /// Target only: a text field holding the source issue's URL, so whoever
    /// reads the copy can open the original.
    ///
    /// Separate from <see cref="ProvenanceFieldName"/> on purpose: that one
    /// holds the bare key because the duplicate check searches it, and a URL
    /// makes for a needlessly loose match.
    /// </summary>
    public string SourceUrlFieldName { get; init; } = "External Issue ID";

    public Uri BaseUri => new(BaseUrl, UriKind.Absolute);
}

/// <summary>
/// DataAnnotations do not recurse into nested objects, so the tenants are
/// validated by hand. Both must be complete before the app serves anything -
/// a half-configured tenant fails at the first request instead of at startup,
/// which is much harder to diagnose.
/// </summary>
public sealed class AtlassianOptionsValidator : IValidateOptions<AtlassianOptions>
{
    public ValidateOptionsResult Validate(string? name, AtlassianOptions options)
    {
        var failures = new List<string>();

        Check(nameof(AtlassianOptions.Source), options.Source, failures);
        Check(nameof(AtlassianOptions.Target), options.Target, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void Check(string tenant, TenantOptions options, List<string> failures)
    {
        var section = $"{AtlassianOptions.SectionName}:{tenant}";

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            failures.Add($"{section}:BaseUrl is required.");
        }
        else if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{section}:BaseUrl must be an absolute http(s) URL, but was '{options.BaseUrl}'.");
        }

        if (string.IsNullOrWhiteSpace(options.ProjectKey))
        {
            failures.Add($"{section}:ProjectKey is required.");
        }
    }
}
