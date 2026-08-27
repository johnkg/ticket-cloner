using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace TicketCloner.Api.Configuration;

/// <summary>
/// Source and Target, never TGT and target-site. The project named TGT lives on
/// the target-site tenant and the project named Source Project lives
/// on the source-site tenant, so a section named after the project resolves to
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
    /// <summary>
    /// The site a person visits - https://target.example.invalid. Every URL
    /// the tool shows, links to, or writes into an issue is built from this.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string BaseUrl { get; init; } = "";

    [Required(AllowEmptyStrings = false)]
    public string ProjectKey { get; init; } = "";

    /// <summary>
    /// The project's display name. The key alone does not tell a reader which
    /// tenant they are about to write to, and the names here are inverted - so
    /// the preview says "Target Project (TGT)" rather than "TGT",
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
    /// Target only: a text field holding the source issue's URL, so whoever
    /// reads the copy can open the original. It is also the duplicate check,
    /// together with the summary - remote links cannot be queried, and this
    /// field can.
    /// </summary>
    public string SourceUrlFieldName { get; init; } = "External Issue ID";

    /// <summary>
    /// The site, for links and anything a person reads. Never where the REST
    /// calls go: under OAuth those go to api.atlassian.com/ex/jira/{cloudId}/,
    /// a host that renders as nothing in a browser, and the cloud id is only
    /// known once somebody has signed in - see TenantAccessResolver.
    /// </summary>
    public Uri SiteUri => new(BaseUrl, UriKind.Absolute);
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
