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
public sealed record AdfRewriteContext(
    Uri SourceBaseUrl,
    IReadOnlyDictionary<string, string> AccountIds,
    IReadOnlyDictionary<string, string> AttachmentUrlsByFileName)
{
    public static AdfRewriteContext For(Uri sourceBaseUrl) =>
        new(sourceBaseUrl, new Dictionary<string, string>(), new Dictionary<string, string>());
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
    /// A smartlink to SOURCE_PROJECT-1234 means nothing on the target, so a relative URL is
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
        var fileName = attrs?["alt"]?.GetValue<string>();
        var sourceId = attrs?["id"]?.GetValue<string>();

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

    private JsonNode? RewriteMediaContainer(JsonObject container, AdfRewriteContext context, List<string> removals)
    {
        var rewritten = RewriteChildren(container, context, removals);

        // An empty mediaSingle is invalid ADF and Jira rejects the whole
        // document, so the container goes with its last child.
        return rewritten["content"] is JsonArray { Count: 0 } ? null : rewritten;
    }
}
