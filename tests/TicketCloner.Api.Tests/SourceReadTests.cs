using System.Net;
using System.Net.Http.Json;
using TicketCloner.Api.Contracts;
using TicketCloner.Api.Tests.Infrastructure;

namespace TicketCloner.Api.Tests;

public class SourceReadTests
{
    private static Dictionary<string, string?> SourceConfigured() => new()
    {
        ["Credentials:SourceEmail"] = "source-account@example.com",
        ["Credentials:SourceApiToken"] = "source-only-token",
    };

    private static (TestApp App, StubAtlassian Stub) Sut()
    {
        var stub = new StubAtlassian(SourceTenantFake.Handler);
        return (new TestApp(stub, SourceConfigured()), stub);
    }

    // ------------------------------------------------------------ the JQL

    [Theory]
    [InlineData("Test")]
    [InlineData("Test Set")]
    [InlineData("Test Plan")]
    [InlineData("Test Execution")]
    [InlineData("Sub Test Execution")]
    [InlineData("Precondition")]
    public async Task Xray_types_are_excluded_in_the_query_not_at_mapping(string excluded)
    {
        // Excluded at the source query on purpose: a type that is never read
        // cannot be mis-mapped later.
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<IssueListResponse>("/api/source/issues");

        Assert.NotNull(response);
        Assert.Contains("issuetype NOT IN", response.Jql);
        Assert.Contains($"\"{excluded}\"", response.Jql);

        // And it actually went upstream, not just into the response. The body
        // is JSON, so the JQL's own quotes arrive escaped.
        Assert.Contains(excluded, stub.RequestTo("search/jql").Body);
    }

    [Fact]
    public async Task Listing_is_scoped_to_the_source_project()
    {
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<IssueListResponse>("/api/source/issues");

        Assert.NotNull(response);
        Assert.StartsWith("project = SOURCE_PROJECT", response.Jql);
    }

    [Fact]
    public async Task Searching_for_something_shaped_like_a_key_matches_the_key_exactly()
    {
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<IssueListResponse>("/api/source/issues?search=SOURCE_PROJECT-1234");

        Assert.NotNull(response);
        Assert.Contains("key = SOURCE_PROJECT-1234", response.Jql);
        Assert.DoesNotContain("summary ~", response.Jql);
    }

    [Fact]
    public async Task Searching_for_free_text_matches_the_summary()
    {
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<IssueListResponse>("/api/source/issues?search=abn");

        Assert.NotNull(response);
        Assert.Contains("summary ~ \"abn\"", response.Jql);
    }

    [Fact]
    public async Task A_quote_in_the_search_term_cannot_break_out_of_the_jql()
    {
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<IssueListResponse>(
            "/api/source/issues?search=%22%20OR%20project%20%3D%20AFG");

        Assert.NotNull(response);
        Assert.Contains("\\\"", response.Jql);
        Assert.DoesNotContain("summary ~ \"\" OR", response.Jql);
    }

    // ------------------------------------------------------------ listing

    [Fact]
    public async Task Listing_maps_summaries_and_absolute_urls()
    {
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<IssueListResponse>("/api/source/issues");

        Assert.NotNull(response);
        Assert.Equal(2, response.Issues.Count);

        var first = response.Issues[0];
        Assert.Equal("SOURCE_PROJECT-1234", first.Key);
        Assert.Equal("Bug", first.IssueType);
        Assert.Equal("Open", first.Status);
        Assert.Equal("Ray Tester", first.Reporter);
        Assert.Null(first.Assignee);

        // Absolute, and pointing at the SOURCE site - a bare key is meaningless
        // once it is sitting in the target's UI.
        Assert.Equal("https://source-domain.atlassian.net/browse/SOURCE_PROJECT-1234", first.Url);
    }

    [Fact]
    public async Task Listing_pages_by_token_rather_than_offset()
    {
        // Jira's bounded JQL search reports no total and pages by opaque token,
        // so there is nothing to count towards and startAt does not apply.
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<IssueListResponse>("/api/source/issues");

        Assert.NotNull(response);
        Assert.Equal("page-2-token", response.NextPageToken);
        Assert.False(response.IsLast);
    }

    [Fact]
    public async Task A_page_token_is_only_sent_once_we_have_one()
    {
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        await client.GetAsync("/api/source/issues");
        Assert.DoesNotContain("nextPageToken", stub.RequestTo("search/jql").Body);

        await client.GetAsync("/api/source/issues?pageToken=page-2-token");
        Assert.Contains("page-2-token", stub.RequestTo("search/jql").Body);
    }

    // ------------------------------------------------------------ one issue

    [Fact]
    public async Task Fields_are_surfaced_by_name_not_by_id()
    {
        // customfield_EXAMPLE_ID on the source is a different field on the target,
        // so the name is the only thing worth mapping on.
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var issue = await client.GetFromJsonAsync<SourceIssue>("/api/source/issues/SOURCE_PROJECT-1234");

        Assert.NotNull(issue);

        var storyPoints = issue.Fields.Single(field => field.FieldId == "customfield_EXAMPLE_ID");
        Assert.Equal("Story point estimate", storyPoints.Name);
        Assert.True(storyPoints.IsCustom);

        // The issue call specifically - not the comments call, which shares the
        // /issue/SOURCE_PROJECT-1234 prefix.
        var issueRequest = stub.Requests.Single(request => request.Path.EndsWith("/issue/SOURCE_PROJECT-1234"));
        Assert.Contains("expand=names", issueRequest.Uri.Query);
    }

    [Fact]
    public async Task Unpopulated_fields_are_left_out()
    {
        // Null and empty-string fields are noise in the preview - nothing maps
        // a value that was never set.
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var issue = await client.GetFromJsonAsync<SourceIssue>("/api/source/issues/SOURCE_PROJECT-1234");

        Assert.NotNull(issue);
        Assert.DoesNotContain(issue.Fields, field => field.FieldId == "customfield_99999");
        Assert.DoesNotContain(issue.Fields, field => field.FieldId == "customfield_13621");
    }

    [Fact]
    public async Task Fields_already_surfaced_as_properties_are_not_repeated()
    {
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var issue = await client.GetFromJsonAsync<SourceIssue>("/api/source/issues/SOURCE_PROJECT-1234");

        Assert.NotNull(issue);
        Assert.DoesNotContain(issue.Fields, field => field.FieldId is "summary" or "description" or "attachment");

        Assert.Equal("Broker portal rejects valid ABN", issue.Summary);
        Assert.Equal(["broker", "abn"], issue.Labels);
    }

    [Fact]
    public async Task Description_adf_is_carried_through_untouched()
    {
        // It is rewritten later, before create. Reading must not flatten it or
        // the rewriter has nothing to work with.
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var issue = await client.GetFromJsonAsync<SourceIssue>("/api/source/issues/SOURCE_PROJECT-1234");

        Assert.NotNull(issue);
        Assert.NotNull(issue.Description);
        Assert.Equal("doc", issue.Description!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Comments_are_read_across_every_page()
    {
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var issue = await client.GetFromJsonAsync<SourceIssue>("/api/source/issues/SOURCE_PROJECT-1234");

        Assert.NotNull(issue);
        Assert.Equal(2, issue.Comments.Count);
        Assert.Equal(["9001", "9002"], issue.Comments.Select(comment => comment.Id));
    }

    [Fact]
    public async Task Attachment_metadata_is_read_but_content_is_not()
    {
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var issue = await client.GetFromJsonAsync<SourceIssue>("/api/source/issues/SOURCE_PROJECT-1234");

        Assert.NotNull(issue);
        var attachment = Assert.Single(issue.Attachments);
        Assert.Equal("screenshot.png", attachment.FileName);
        Assert.Equal(84213, attachment.Size);

        // Downloading happens at apply time, not while previewing.
        Assert.DoesNotContain(stub.Requests, request =>
            request.Path.Contains("attachment/content", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Reporter_email_is_null_because_the_source_hides_it()
    {
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var issue = await client.GetFromJsonAsync<SourceIssue>("/api/source/issues/SOURCE_PROJECT-1234");

        Assert.NotNull(issue);
        Assert.NotNull(issue.Reporter);
        Assert.Null(issue.Reporter!.EmailAddress);

        // The accountId still comes through, and that is what user mapping
        // leans on - it is shared across tenants for anyone on both sites.
        Assert.Equal("acc-1", issue.Reporter.AccountId);
    }

    // ------------------------------------------------------------ guards

    [Fact]
    public async Task Reading_the_source_requires_source_credentials()
    {
        var stub = new StubAtlassian(SourceTenantFake.Handler);
        using var app = new TestApp(stub);
        using var client = app.CreateClient();

        var response = await client.GetAsync("/api/source/issues");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Source_reads_never_carry_the_target_credential()
    {
        var stub = new StubAtlassian(SourceTenantFake.Handler);
        using var app = new TestApp(stub, new Dictionary<string, string?>
        {
            ["Credentials:SourceEmail"] = "source-account@example.com",
            ["Credentials:SourceApiToken"] = "source-only-token",
            ["Credentials:TargetEmail"] = "target-account@example.com",
            ["Credentials:TargetApiToken"] = "target-only-token",
        });
        using var client = app.CreateClient();

        await client.GetAsync("/api/source/issues/SOURCE_PROJECT-1234");

        Assert.All(stub.Requests, request =>
        {
            Assert.Equal("source-domain.atlassian.net", request.Host);
            Assert.True(request.CarriedCredentialsFor("source-account@example.com", "source-only-token"));
        });
    }
}
