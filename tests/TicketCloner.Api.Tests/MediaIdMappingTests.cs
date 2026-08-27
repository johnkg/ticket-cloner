using TicketCloner.Api.Adf;
using TicketCloner.Api.Jira;
using System.Text.Json.Nodes;

namespace TicketCloner.Api.Tests;

/// <summary>
/// Connecting a media node's UUID to an attachment.
///
/// These are two different identifier spaces - a node carries
/// 1ca7ce94-fb48-..., an attachment carries 517001 - and nothing in the REST
/// model joins them: the attachment endpoint returns no UUID and the ADF
/// carries no file name. The RENDERED description does, on the anchor Jira
/// writes for each embedded file, and that is the only bridge there is.
/// </summary>
public class MediaIdMappingTests
{
    /// <summary>
    /// The real anchor out of SRC-7210, pasted from the live source on
    /// 25/09/2026 - not a guess at Jira's markup.
    /// </summary>
    private const string RenderedXsd =
        """
        <h2><a name="XSD"></a>XSD</h2>
        <p><span class="nobr"><a href="/rest/api/3/attachment/content/517001"
        title="example-schema.xsd attached to SRC-7210"
        data-attachment-type="file" data-attachment-name="example-schema.xsd"
        data-media-services-type="file" data-media-services-id="3f2a9c51-7d84-4e6b-9a10-5c8e2b7d4f63"
        rel="noreferrer">example-schema.xsd</a></span> </p>
        """;

    [Fact]
    public void The_rendered_description_maps_a_media_uuid_to_its_file_name()
    {
        var map = SourceIssueReader.MediaIdsToFileNames(RenderedXsd);

        Assert.Equal(
            "example-schema.xsd",
            map["3f2a9c51-7d84-4e6b-9a10-5c8e2b7d4f63"]);
    }

    [Fact]
    public void Markup_that_carries_neither_attribute_simply_yields_nothing()
    {
        // Scraping rendered HTML is not something to do lightly, so a change in
        // Jira's markup has to cost the mapping and not throw.
        Assert.Empty(SourceIssueReader.MediaIdsToFileNames("<p>no anchors here</p>"));
        Assert.Empty(SourceIssueReader.MediaIdsToFileNames("<a href=\"/x\">plain link</a>"));
        Assert.Empty(SourceIssueReader.MediaIdsToFileNames(null));
    }

    [Fact]
    public void An_inline_file_becomes_a_link_to_the_copys_own_attachment()
    {
        // The whole point: the FILE can be carried even though the NODE cannot,
        // so the copy gets a working download rather than a pointer elsewhere.
        var document = new JsonObject
        {
            ["type"] = "doc",
            ["version"] = 1,
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "paragraph",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "mediaInline",
                            ["attrs"] = new JsonObject
                            {
                                ["id"] = "3f2a9c51-7d84-4e6b-9a10-5c8e2b7d4f63",
                                ["type"] = "file",
                            },
                        },
                    },
                },
            },
        };

        var result = new AdfRewriter().Rewrite(document, new AdfRewriteContext(
            new Uri("https://source.example.invalid/browse/SRC-7210"),
            new Dictionary<string, string>(),
            new Dictionary<string, string>
            {
                ["example-schema.xsd"] =
                    "https://target.example.invalid/rest/api/3/attachment/content/900123",
            },
            SourceIssueReader.MediaIdsToFileNames(RenderedXsd)));

        var node = result.Document!["content"]![0]!["content"]![0]!;

        Assert.Equal("text", node["type"]!.GetValue<string>());
        Assert.Equal("example-schema.xsd", node["text"]!.GetValue<string>());
        Assert.Equal(
            "https://target.example.invalid/rest/api/3/attachment/content/900123",
            node["marks"]![0]!["attrs"]!["href"]!.GetValue<string>());

        // And never the source's own UUID, which is what Jira refuses.
        Assert.DoesNotContain("1ca7ce94", result.Document.ToJsonString());
    }

    [Fact]
    public void An_inline_file_missing_from_the_target_still_names_itself()
    {
        // Resolvable to a name but not to a file: the reader is told which file
        // and where to find it, rather than being left with a bare placeholder.
        var document = new JsonObject
        {
            ["type"] = "mediaInline",
            ["attrs"] = new JsonObject { ["id"] = "3f2a9c51-7d84-4e6b-9a10-5c8e2b7d4f63" },
        };

        var result = new AdfRewriter().Rewrite(document, new AdfRewriteContext(
            new Uri("https://source.example.invalid/browse/SRC-7210"),
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            SourceIssueReader.MediaIdsToFileNames(RenderedXsd)));

        Assert.Contains("example-schema.xsd", result.Document!["text"]!.GetValue<string>());
        Assert.Equal(
            "https://source.example.invalid/browse/SRC-7210",
            result.Document["marks"]![0]!["attrs"]!["href"]!.GetValue<string>());
    }

    [Fact]
    public void An_inline_files_own_attachment_is_collected_for_uploading()
    {
        // The link is only worth writing if the file is actually put on the
        // copy, and an inline node carries no alt to find it by.
        var document = new JsonObject
        {
            ["type"] = "mediaInline",
            ["attrs"] = new JsonObject { ["id"] = "3f2a9c51-7d84-4e6b-9a10-5c8e2b7d4f63" },
        };

        var names = CopyRefreshPlanner.MediaFileNames(
            document, SourceIssueReader.MediaIdsToFileNames(RenderedXsd));

        Assert.Equal(["example-schema.xsd"], names);
    }
}
