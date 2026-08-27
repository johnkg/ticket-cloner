using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TicketCloner.Api.Atlassian;
using TicketCloner.Api.Configuration;

namespace TicketCloner.Api.Jira;

public sealed record CreatedIssue(string Id, string Key, string Url);

/// <summary>
/// Every write this tool performs. All of it lands on the target tenant; the
/// client refuses to send any of it to the source.
/// </summary>
public sealed class TargetIssueWriter(
    AtlassianClientFactory clients,
    IOptions<AtlassianOptions> options)
{
    private TenantOptions Target => options.Value.Target;

    private AtlassianClient Client => clients.For(Tenant.Target);

    /// <summary>
    /// The duplicate check. A remote link would be the natural place to record
    /// where a copy came from, but remote links are not JQL-searchable - hence
    /// a real custom field holding the source key.
    /// </summary>
    public async Task<string?> FindExistingCopyAsync(
        string provenanceFieldName,
        string sourceKey,
        CancellationToken cancellationToken)
    {
        var jql = $"project = {Escape(Target.ProjectKey)} " +
                  $"AND \"{Escape(provenanceFieldName)}\" ~ \"{Escape(sourceKey)}\"";

        var response = await Client.PostAsync<JsonNode>("rest/api/3/search/jql", new Dictionary<string, object?>
        {
            ["jql"] = jql,
            ["maxResults"] = 1,
            ["fields"] = new[] { "summary" },
        }, cancellationToken);

        return (response?["issues"] as JsonArray)?.FirstOrDefault()?["key"]?.GetValue<string>();
    }

    public async Task<CreatedIssue> CreateAsync(JsonObject fields, CancellationToken cancellationToken)
    {
        var created = await Client.PostAsync<JsonNode>(
            "rest/api/3/issue", new JsonObject { ["fields"] = fields }, cancellationToken);

        var key = created?["key"]?.GetValue<string>()
            ?? throw new InvalidOperationException("The target created an issue but returned no key.");

        return new CreatedIssue(
            Id: created?["id"]?.GetValue<string>() ?? "",
            Key: key,
            Url: new Uri(Client.BaseAddress, $"browse/{key}").ToString());
    }

    /// <summary>
    /// Cross-tenant issue links do not exist - POST /issueLink is same-site
    /// only - so provenance for a human is a remote link instead.
    /// </summary>
    public Task AddRemoteLinkAsync(
        string issueKey,
        string sourceUrl,
        string title,
        CancellationToken cancellationToken) =>
        Client.PostAsync<JsonNode>(
            $"rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/remotelink",
            new JsonObject
            {
                ["globalId"] = $"ticketcloner:{title}",
                ["object"] = new JsonObject
                {
                    ["url"] = sourceUrl,
                    ["title"] = title,
                },
            },
            cancellationToken);

    /// <summary>
    /// A second pass over the description, once the attachments exist here and
    /// their ids are known. The first pass cannot keep the images: a media node
    /// points at an attachment id, and at create time the only ids in hand
    /// belong to the source tenant.
    /// </summary>
    public Task UpdateDescriptionAsync(
        string issueKey,
        JsonNode description,
        CancellationToken cancellationToken) =>
        Client.PutAsync<JsonNode>(
            $"rest/api/3/issue/{Uri.EscapeDataString(issueKey)}",
            new JsonObject
            {
                ["fields"] = new JsonObject { ["description"] = description.DeepClone() },
            },
            cancellationToken);

    public Task AddCommentAsync(string issueKey, JsonNode body, CancellationToken cancellationToken) =>
        Client.PostAsync<JsonNode>(
            $"rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/comment",
            new JsonObject { ["body"] = body.DeepClone() },
            cancellationToken);

    /// <summary>
    /// Returns an absolute URL to the uploaded file's content on this tenant,
    /// which is what a media node in the description is rewritten to point at.
    /// The attachment id alone is no use there - see AdfRewriter.RewriteMedia.
    /// </summary>
    public async Task<string?> UploadAttachmentAsync(
        string issueKey,
        string fileName,
        byte[] content,
        string? mimeType,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(mimeType ?? "application/octet-stream");

        // Jira insists the part be named "file".
        form.Add(file, "file", fileName);

        var uploaded = await Client.PostMultipartAsync<JsonNode>(
            $"rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/attachments", form, cancellationToken);

        var attachment = (uploaded as JsonArray)?.FirstOrDefault();

        // Jira returns the canonical content URL; fall back to composing it
        // from the id if a response ever arrives without one.
        var contentUrl = attachment?["content"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(contentUrl))
        {
            return contentUrl;
        }

        var id = attachment?["id"]?.GetValue<string>();

        return id is null
            ? null
            : new Uri(Client.BaseAddress, $"rest/api/3/attachment/content/{id}").ToString();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
