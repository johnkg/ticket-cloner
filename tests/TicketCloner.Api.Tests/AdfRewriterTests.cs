using System.Text.Json.Nodes;
using TicketCloner.Api.Adf;

namespace TicketCloner.Api.Tests;

/// <summary>
/// A tree written for one tenant renders broken on the other. Three node types
/// carry tenant-local references, and each has its own failure mode.
/// </summary>
public class AdfRewriterTests
{
    private static readonly Uri SourceSite = new("https://source.example.invalid/");

    private static JsonNode Doc(params JsonNode[] content) => new JsonObject
    {
        ["type"] = "doc",
        ["version"] = 1,
        ["content"] = new JsonArray(content),
    };

    private static JsonNode Paragraph(params JsonNode[] content) => new JsonObject
    {
        ["type"] = "paragraph",
        ["content"] = new JsonArray(content),
    };

    private static JsonNode Mention(string accountId, string text) => new JsonObject
    {
        ["type"] = "mention",
        ["attrs"] = new JsonObject { ["id"] = accountId, ["text"] = text },
    };

    private static AdfRewriteContext Context(
        Dictionary<string, string>? accounts = null,
        Dictionary<string, string>? attachments = null) =>
        new(SourceSite, accounts ?? [], attachments ?? []);

    /// <summary>
    /// The real node out of SRC-7210, pasted from the live source on
    /// 25/09/2026. Note what it does NOT carry: no attrs.alt, so no file name,
    /// so nothing that could ever be matched to an attachment on the target.
    /// The id is a Media Services UUID, which no REST endpoint maps back.
    /// </summary>
    private static JsonNode MediaInline() => new JsonObject
    {
        ["type"] = "mediaInline",
        ["attrs"] = new JsonObject
        {
            ["id"] = "3f2a9c51-7d84-4e6b-9a10-5c8e2b7d4f63",
            ["collection"] = "",
            ["type"] = "file",
            ["localId"] = "a75198a94abb",
        },
    };

    // ------------------------------------------------------------ inline files

    [Fact]
    public void An_inline_file_never_survives_as_a_file_reference()
    {
        // Left as it stands, Jira refuses the WHOLE request with
        // ATTACHMENT_VALIDATION_ERROR - it cost a refresh of TGT-1942 on
        // 25/09/2026, and would cost a create just as easily.
        var result = new AdfRewriter().Rewrite(Doc(Paragraph(MediaInline())), Context());

        var node = result.Document!["content"]![0]!["content"]![0]!;

        Assert.NotEqual("mediaInline", node["type"]!.GetValue<string>());
        Assert.DoesNotContain("1ca7ce94", result.Document.ToJsonString());
    }

    [Fact]
    public void An_inline_file_becomes_a_link_to_the_original()
    {
        // The same bargain a mention and a card make: the reference cannot come
        // across, so the reader is told where to look instead of it vanishing.
        var result = new AdfRewriter().Rewrite(Doc(Paragraph(MediaInline())), Context());

        var node = result.Document!["content"]![0]!["content"]![0]!;

        Assert.Equal("text", node["type"]!.GetValue<string>());
        Assert.Equal("link", node["marks"]![0]!["type"]!.GetValue<string>());
        Assert.Equal(
            SourceSite.ToString(), node["marks"]![0]!["attrs"]!["href"]!.GetValue<string>());
    }

    [Fact]
    public void An_inline_file_is_reported_rather_than_dropped_quietly()
    {
        var result = new AdfRewriter().Rewrite(Doc(Paragraph(MediaInline())), Context());

        Assert.Contains(result.Removals, removal => removal.Contains("inline file"));
    }

    // ---------------------------------------------------------------- mentions

    [Fact]
    public void A_resolved_mention_keeps_its_node_with_the_target_account_id()
    {
        var document = Doc(Paragraph(Mention("acc-source", "@Ray Tester")));

        var result = new AdfRewriter().Rewrite(document,
            Context(accounts: new() { ["acc-source"] = "acc-target" }));

        var node = result.Document!["content"]![0]!["content"]![0]!;
        Assert.Equal("mention", node["type"]!.GetValue<string>());
        Assert.Equal("acc-target", node["attrs"]!["id"]!.GetValue<string>());
        Assert.Empty(result.Removals);
    }

    [Fact]
    public void An_unresolved_mention_becomes_plain_text_rather_than_a_dead_link()
    {
        var document = Doc(Paragraph(Mention("acc-source", "@Ray Tester")));

        var result = new AdfRewriter().Rewrite(document, Context());

        var node = result.Document!["content"]![0]!["content"]![0]!;
        Assert.Equal("text", node["type"]!.GetValue<string>());
        Assert.Equal("@Ray Tester", node["text"]!.GetValue<string>());
        Assert.Contains(result.Removals, removal => removal.Contains("flattened to text"));
    }

    // ---------------------------------------------------------------- cards

    [Fact]
    public void A_relative_card_url_is_made_absolute_against_the_source_site()
    {
        // A smartlink to SRC-1234 means nothing on the target. Pointing it back
        // at the source at least makes it go somewhere real.
        var document = Doc(new JsonObject
        {
            ["type"] = "inlineCard",
            ["attrs"] = new JsonObject { ["url"] = "/browse/SRC-1234" },
        });

        var result = new AdfRewriter().Rewrite(document, Context());

        Assert.Equal("https://source.example.invalid/browse/SRC-1234",
            result.Document!["content"]![0]!["attrs"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void An_already_absolute_card_url_is_left_alone()
    {
        var document = Doc(new JsonObject
        {
            ["type"] = "blockCard",
            ["attrs"] = new JsonObject { ["url"] = "https://example.com/thing" },
        });

        var result = new AdfRewriter().Rewrite(document, Context());

        Assert.Equal("https://example.com/thing",
            result.Document!["content"]![0]!["attrs"]!["url"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- media

    [Fact]
    public void A_media_node_becomes_an_external_node_matched_by_file_name()
    {
        // The shape the live tenants return: attrs.id is a Media Services UUID
        // while the attachment's REST id is a number. Different identifier
        // spaces, so matching on id can never work - attrs.alt carries the only
        // value the two records share.
        const string Url = "https://target.example.invalid/rest/api/3/attachment/content/70001";

        var document = Doc(new JsonObject
        {
            ["type"] = "mediaSingle",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "media",
                ["attrs"] = new JsonObject
                {
                    ["id"] = "a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d",
                    ["type"] = "file",
                    ["alt"] = "screenshot.png",
                    ["collection"] = "source-tenant-bucket",
                },
            }),
        });

        var result = new AdfRewriter().Rewrite(document,
            Context(attachments: new() { ["screenshot.png"] = Url }));

        var attrs = result.Document!["content"]![0]!["content"]![0]!["attrs"]!;

        // External, not a repointed file node: a file node needs a Media
        // Services UUID that no REST endpoint will give us, and Jira drops it
        // silently rather than complaining.
        Assert.Equal("external", attrs["type"]!.GetValue<string>());
        Assert.Equal(Url, attrs["url"]!.GetValue<string>());
        Assert.Equal("screenshot.png", attrs["alt"]!.GetValue<string>());

        // The source's UUID and bucket must not survive - both name things that
        // do not exist on the target.
        Assert.Null(attrs["id"]);
        Assert.Null(attrs["collection"]);
        Assert.Empty(result.Removals);
    }

    [Fact]
    public void A_media_node_with_no_file_name_cannot_be_matched_and_is_removed()
    {
        var document = Doc(new JsonObject
        {
            ["type"] = "mediaSingle",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "media",
                ["attrs"] = new JsonObject { ["id"] = "a1b2c3d4", ["type"] = "file" },
            }),
        });

        var result = new AdfRewriter().Rewrite(document,
            Context(attachments: new() { ["screenshot.png"] = "https://target.example.invalid/x" }));

        Assert.Empty((JsonArray)result.Document!["content"]!);
        Assert.Single(result.Removals);
    }

    [Fact]
    public void An_unmapped_media_node_is_removed_along_with_its_empty_container()
    {
        // A media node pointing at a foreign attachment id renders as a broken
        // image, and an empty mediaSingle makes Jira reject the whole document.
        var document = Doc(new JsonObject
        {
            ["type"] = "mediaSingle",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "media",
                ["attrs"] = new JsonObject { ["id"] = "50021" },
            }),
        });

        var result = new AdfRewriter().Rewrite(document, Context());

        Assert.Empty((JsonArray)result.Document!["content"]!);
        Assert.Contains(result.Removals, removal => removal.Contains("50021"));
    }

    // ---------------------------------------------------------------- general

    [Fact]
    public void Ordinary_content_survives_untouched()
    {
        var document = Doc(Paragraph(new JsonObject
        {
            ["type"] = "text",
            ["text"] = "Steps to reproduce",
        }));

        var result = new AdfRewriter().Rewrite(document, Context());

        Assert.Equal("Steps to reproduce",
            result.Document!["content"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Empty(result.Removals);
    }

    [Fact]
    public void Nested_content_is_rewritten_all_the_way_down()
    {
        var document = Doc(new JsonObject
        {
            ["type"] = "bulletList",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "listItem",
                ["content"] = new JsonArray(Paragraph(Mention("acc-source", "@Deep Person"))),
            }),
        });

        var result = new AdfRewriter().Rewrite(document, Context());

        var text = result.Document!["content"]![0]!["content"]![0]!["content"]![0]!["content"]![0]!;
        Assert.Equal("text", text["type"]!.GetValue<string>());
    }

    [Fact]
    public void The_original_document_is_not_mutated()
    {
        var document = Doc(Paragraph(Mention("acc-source", "@Ray Tester")));

        new AdfRewriter().Rewrite(document, Context());

        Assert.Equal("mention",
            document["content"]![0]!["content"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void A_null_description_stays_null()
    {
        var result = new AdfRewriter().Rewrite(null, Context());

        Assert.Null(result.Document);
    }
}
