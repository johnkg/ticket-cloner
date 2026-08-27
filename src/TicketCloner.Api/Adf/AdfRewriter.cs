using System.Text.Json.Nodes;

namespace TicketCloner.Api.Adf;

/// <param name="AccountIds">Source accountId -> target accountId, for people who
/// resolved. Anyone absent has their mention flattened to plain text.</param>
/// <param name="AttachmentUrlsByFileName">Attachment FILE NAME -> an absolute
/// URL to that file's content on the TARGET. Media nodes with no entry are
/// removed, because one still pointing at the source renders as a broken image.
///
/// Keyed by file name, and that is not a stylistic choice: a media node's
/// attrs.id is a Media Services UUID (a1b2c3d4-...) while an attachment's REST
/// id is a plain number. They are different identifier spaces and never
/// match. The file name - carried on the node as attrs.alt - is the only value
/// the two records share.</param>
/// <param name="FileNamesByMediaId">Media Services UUID to file name, scraped
/// from the source's RENDERED description - see
/// SourceIssue.AttachmentNamesByMediaId. It is what lets a node that carries
/// only a UUID reach an attachment at all, and without it an inline file is
/// unresolvable on principle.</param>
public sealed record AdfRewriteContext(
    Uri SourceBaseUrl,
    IReadOnlyDictionary<string, string> AccountIds,
    IReadOnlyDictionary<string, string> AttachmentUrlsByFileName,
    IReadOnlyDictionary<string, string>? FileNamesByMediaId = null)
{
    public static AdfRewriteContext For(Uri sourceBaseUrl) =>
        new(sourceBaseUrl, new Dictionary<string, string>(), new Dictionary<string, string>());

    /// <summary>The target URL for a node carrying only a Media Services UUID.</summary>
    public string? UrlForMediaId(string? mediaId) =>
        mediaId is not null &&
        FileNamesByMediaId?.TryGetValue(mediaId, out var fileName) is true &&
        AttachmentUrlsByFileName.TryGetValue(fileName, out var url)
            ? url
            : null;

    public string? FileNameForMediaId(string? mediaId) =>
        mediaId is not null && FileNamesByMediaId?.TryGetValue(mediaId, out var fileName) is true
            ? fileName
            : null;
}

/// <param name="Removals">What had to be changed or dropped, so the outcome can
/// say so rather than leaving someone to notice later.</param>
public sealed record AdfRewriteResult(JsonNode? Document, IReadOnlyList<string> Removals);

/// <summary>
/// Rewrites an ADF tree written for one tenant so it renders on the other.
///
/// This is a rewriter, not a flattener: the document has to survive as a
/// document. Three node types carry tenant-local references and all three
/// render broken if copied verbatim.
/// </summary>
public sealed class AdfRewriter
{
    public AdfRewriteResult Rewrite(JsonNode? node, AdfRewriteContext context)
    {
        var removals = new List<string>();

        var document = node is null ? null : RewriteNode(node.DeepClone(), context, removals);

        return new AdfRewriteResult(document, removals);
    }

    private JsonNode? RewriteNode(JsonNode node, AdfRewriteContext context, List<string> removals)
    {
        switch (node)
        {
            case JsonArray array:
            {
                var rewritten = new JsonArray();
                foreach (var item in array.ToList())
                {
                    var result = item is null ? null : RewriteNode(item.DeepClone(), context, removals);
                    if (result is not null)
                    {
                        rewritten.Add(result);
                    }
                }

                return rewritten;
            }

            case JsonObject obj:
                return obj["type"]?.GetValue<string>() switch
                {
                    "mention" => RewriteMention(obj, context, removals),
                    "inlineCard" or "blockCard" or "embedCard" => RewriteCard(obj, context),
                    "media" => RewriteMedia(obj, context, removals),
                    "mediaInline" => RewriteMediaInline(obj, context, removals),
                    "mediaSingle" or "mediaGroup" => RewriteMediaContainer(obj, context, removals),
                    _ => RewriteChildren(obj, context, removals),
                };

            default:
                return node;
        }
    }

    private JsonObject RewriteChildren(JsonObject obj, AdfRewriteContext context, List<string> removals)
    {
        if (obj["content"] is { } content)
        {
            obj["content"] = RewriteNode(content.DeepClone(), context, removals);
        }

        return obj;
    }

    /// <summary>
    /// A mention carries an accountId that means nothing on the other tenant
    /// unless that person holds one Atlassian account across both. Unresolved,
    /// it renders as a dead link - so it becomes the plain text of the name.
    /// </summary>
    private static JsonNode RewriteMention(JsonObject mention, AdfRewriteContext context, List<string> removals)
    {
        var accountId = mention["attrs"]?["id"]?.GetValue<string>();
        var displayName = mention["attrs"]?["text"]?.GetValue<string>() ?? "@unknown";

        if (accountId is not null && context.AccountIds.TryGetValue(accountId, out var targetAccountId))
        {
            mention["attrs"]!["id"] = targetAccountId;
            return mention;
        }

        removals.Add($"mention of {displayName} flattened to text");

        return new JsonObject
        {
            ["type"] = "text",
            ["text"] = displayName.StartsWith('@') ? displayName : $"@{displayName}",
        };
    }

    /// <summary>
    /// A smartlink to SRC-1234 means nothing on the target, so a relative URL is
    /// made absolute against the SOURCE site. It stops being a live card and
    /// becomes a link that at least goes somewhere real.
    /// </summary>
    private static JsonNode RewriteCard(JsonObject card, AdfRewriteContext context)
    {
        var url = card["attrs"]?["url"]?.GetValue<string>();

        if (!string.IsNullOrWhiteSpace(url) && !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            card["attrs"]!["url"] = new Uri(context.SourceBaseUrl, url.TrimStart('/')).ToString();
        }

        return card;
    }

    /// <summary>
    /// A media node names a file living in the source tenant. Matched to a
    /// re-uploaded attachment by file name it becomes an EXTERNAL media node
    /// pointing at the copy; otherwise it goes, because one still referring to
    /// the source renders as a broken image.
    ///
    /// External rather than repointed, and this is the whole difficulty: a file
    /// media node needs a Media Services UUID, and no REST endpoint returns one
    /// for an attachment uploaded over the API - GET /attachment/{id} gives
    /// id, filename, size, mimeType, content and thumbnail, and nothing else.
    /// Sending a file node with an attachment id instead is accepted with a 204
    /// and the node is then silently dropped, which is how a copy ends up with
    /// its images simply missing. An external node takes a URL instead, and the
    /// target's own attachment content URL is one its readers are already
    /// authenticated for. Verified against a live target: Jira stores the node
    /// and renders it as an img.
    /// </summary>
    private static JsonNode? RewriteMedia(JsonObject media, AdfRewriteContext context, List<string> removals)
    {
        var attrs = media["attrs"];
        var sourceId = attrs?["id"]?.GetValue<string>();

        // alt first, because it is the node's own claim about the file. The
        // UUID map is the fallback for a node that carries no alt at all,
        // which used to make it unmatchable.
        var fileName = attrs?["alt"]?.GetValue<string>()
                       ?? context.FileNameForMediaId(sourceId);

        if (fileName is not null && context.AttachmentUrlsByFileName.TryGetValue(fileName, out var url))
        {
            return new JsonObject
            {
                ["type"] = "media",
                ["attrs"] = new JsonObject
                {
                    ["type"] = "external",
                    ["url"] = url,
                    ["alt"] = fileName,
                },
            };
        }

        removals.Add($"media node for {fileName ?? sourceId ?? "(unidentified file)"} removed");
        return null;
    }

    /// <summary>
    /// An inline file reference - a paperclip in the middle of a sentence
    /// rather than an image on its own line.
    ///
    /// The node itself cannot be reproduced: a mediaInline needs a TARGET Media
    /// Services UUID, and no REST endpoint issues one for a file uploaded over
    /// the API. But the FILE can be carried, and now so can the reference to
    /// it: the UUID resolves to a file name through the source's rendered HTML
    /// (see SourceIssue.AttachmentNamesByMediaId), and that name resolves to
    /// the copy's own attachment - so this becomes a working link, named after
    /// the file, pointing at the target's version.
    ///
    /// Only when the file cannot be found does it fall back to a link to the
    /// source issue, the way a mention becomes plain text: the reader is still
    /// told a file is there and where to look.
    ///
    /// Left as it stands it is worse than either: Jira rejects the whole
    /// request with ATTACHMENT_VALIDATION_ERROR, which cost a refresh of
    /// TGT-1942 on 25/09/2026 and would cost a create just as easily.
    /// </summary>
    private static JsonNode RewriteMediaInline(
        JsonObject media, AdfRewriteContext context, List<string> removals)
    {
        var id = media["attrs"]?["id"]?.GetValue<string>();
        var fileName = context.FileNameForMediaId(id);
        var url = context.UrlForMediaId(id);

        if (url is not null)
        {
            removals.Add($"inline file {fileName} became a link to the copy's own attachment");
            return Link(fileName!, url);
        }

        removals.Add(
            $"inline file {fileName ?? id ?? "(unidentified)"} is not on the target; " +
            "linked to the original instead");

        return Link(
            fileName is null ? "[attached file - see the original]" : $"[{fileName} - see the original]",
            context.SourceBaseUrl.ToString());
    }

    private static JsonNode Link(string text, string href) => new JsonObject
    {
        ["type"] = "text",
        ["text"] = text,
        ["marks"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "link",
                ["attrs"] = new JsonObject { ["href"] = href },
            },
        },
    };

    private JsonNode? RewriteMediaContainer(JsonObject container, AdfRewriteContext context, List<string> removals)
    {
        var rewritten = RewriteChildren(container, context, removals);

        // An empty mediaSingle is invalid ADF and Jira rejects the whole
        // document, so the container goes with its last child.
        return rewritten["content"] is JsonArray { Count: 0 } ? null : rewritten;
    }
}
