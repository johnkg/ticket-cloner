using System.Text.Json.Nodes;

namespace TicketCloner.Api.Contracts;

/// <summary>
/// One issue, read from the source tenant and flattened into a shape that says
/// nothing about which tenant produced it.
///
/// That independence is the point: the mapping engine consumes this and never
/// learns whether it came from source-site or anywhere else, which is what lets
/// the whole pipeline be developed and tested target-site-to-target-site before
/// pointing it across.
/// </summary>
public sealed record SourceIssue(
    string Key,
    string Url,
    string IssueType,
    string Summary,
    string? Status,
    string? Priority,
    SourceUser? Reporter,
    SourceUser? Assignee,
    DateTimeOffset? Created,
    DateTimeOffset? Updated,
    IReadOnlyList<string> Labels,

    /// <summary>
    /// The issue this one sits under, if any. Only an Epic parent is acted on:
    /// a sub-task's parent is a different relationship and re-creating it
    /// across tenants is not what anybody asked for.
    /// </summary>
    SourceParent? Parent,

    JsonNode? Description,
    IReadOnlyList<SourceFieldValue> Fields,
    IReadOnlyList<SourceComment> Comments,
    IReadOnlyList<SourceAttachment> Attachments);

/// <summary>
/// A populated field, carrying its NAME as well as its id.
///
/// The name is what matters: customfield_70010 on the source is a different
/// field on the target, so mapping is by name and the id survives only for
/// traceability in the preview.
/// </summary>
/// <param name="SchemaCustom">The custom field TYPE key. A name match with a
/// differing type is the dangerous case - it looks mappable and will either 400
/// or write the wrong shape of value - so the type travels with the value.</param>
public sealed record SourceFieldValue(
    string FieldId,
    string Name,
    bool IsCustom,
    string? SchemaType,
    string? SchemaCustom,
    JsonNode? Value);

/// <summary>
/// The parent named on a source issue. Jira returns the parent's own issue type
/// alongside the key, so whether it is an Epic is known without a second read.
/// </summary>
public sealed record SourceParent(string Key, string Url, string Summary, string IssueType)
{
    public bool IsEpic => IssueType.Equals("Epic", StringComparison.OrdinalIgnoreCase);
}

/// <param name="AccountId">Shared across tenants for anyone holding one
/// Atlassian account on both sites, which is what makes user mapping tractable
/// at all - source-site hides email addresses.</param>
public sealed record SourceUser(string AccountId, string DisplayName, string? EmailAddress);

public sealed record SourceComment(
    string Id,
    SourceUser? Author,
    DateTimeOffset? Created,
    JsonNode? Body);

/// <summary>Metadata only. Content is fetched at apply time, not at preview.</summary>
public sealed record SourceAttachment(
    string Id,
    string FileName,
    string? MimeType,
    long Size,
    SourceUser? Author,
    DateTimeOffset? Created);

// ---------------------------------------------------------------- listing

public sealed record IssueSummary(
    string Key,
    string Url,
    string IssueType,
    string Summary,
    string? Status,
    string? Priority,
    string? Reporter,
    string? Assignee,
    DateTimeOffset? Updated);

/// <param name="NextPageToken">Opaque. Jira's bounded JQL search pages by token
/// rather than by offset, and there is no total to count towards.</param>
public sealed record IssueListResponse(
    IReadOnlyList<IssueSummary> Issues,
    string? NextPageToken,
    bool IsLast,
    string Jql);

/// <summary>
/// One sprint on a board, from either tenant's own agile API. A sprint id
/// belongs to the tenant that issued it: a SOURCE sprint is only ever used to
/// search the source, and a TARGET sprint is only ever written to the target.
/// The two never meet, which is the rule the mapper enforces.
/// </summary>
/// <param name="State">closed, active or future, as Jira reports it.</param>
public sealed record BoardSprint(
    int Id,
    string Name,
    string State,
    DateTimeOffset? StartDate,
    DateTimeOffset? EndDate);

/// <param name="Board">Which board was listed, so the UI can say so.</param>
public sealed record SprintListResponse(IReadOnlyList<BoardSprint> Sprints, int Board);
