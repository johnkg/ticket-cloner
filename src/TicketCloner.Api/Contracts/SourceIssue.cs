using System.Text.Json.Nodes;

namespace TicketCloner.Api.Contracts;

/// <summary>
/// One issue, read from the source tenant and flattened into a shape that says
/// nothing about which tenant produced it.
///
/// That independence is the point: the mapping engine consumes this and never
/// learns whether it came from source-company or anywhere else, which is what lets
/// the whole pipeline be developed and tested your-company-to-your-company before
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
    JsonNode? Description,
    IReadOnlyList<SourceFieldValue> Fields,
    IReadOnlyList<SourceComment> Comments,
    IReadOnlyList<SourceAttachment> Attachments);

/// <summary>
/// A populated field, carrying its NAME as well as its id.
///
/// The name is what matters: customfield_EXAMPLE_ID on the source is a different
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

/// <param name="AccountId">Shared across tenants for anyone holding one
/// Atlassian account on both sites, which is what makes user mapping tractable
/// at all - source-company hides email addresses.</param>
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
