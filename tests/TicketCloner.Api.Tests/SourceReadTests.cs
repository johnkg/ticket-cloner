using System.Net;
using System.Net.Http.Json;
using TicketCloner.Api.Atlassian;
using TicketCloner.Api.Contracts;
using TicketCloner.Api.Tests.Infrastructure;

namespace TicketCloner.Api.Tests;

public class SourceReadTests
{
    private static (TestApp App, StubAtlassian Stub) Sut()
    {
        var stub = new StubAtlassian(SourceTenantFake.Handler);
        return (new TestApp(stub, signedIn: true), stub);
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
        Assert.StartsWith("project = SRC", response.Jql);
    }

    [Fact]
    public async Task Searching_for_something_shaped_like_a_key_matches_the_key_exactly()
    {
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<IssueListResponse>("/api/source/issues?search=SRC-1234");

        Assert.NotNull(response);
        Assert.Contains("key = SRC-1234", response.Jql);
        Assert.DoesNotContain("summary ~", response.Jql);
    }

    [Theory]
    [InlineData("SRC-1234,SRC-5678")]
    [InlineData("SRC-1234, SRC-5678")]
    [InlineData("SRC-1234 SRC-5678")]
    [InlineData("SRC-1234;SRC-5678")]
    [InlineData("SRC-1234\nSRC-5678")]
    [InlineData("SRC-1234  SRC-5678  ")]
    public async Task Several_keys_at_once_become_one_IN_clause(string search)
    {
        // Pasting a list is how a batch gets picked out in one go, and it comes
        // from every direction - a comma-separated line, a column out of a
        // spreadsheet, whatever somebody had to hand.
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<IssueListResponse>(
            $"/api/source/issues?search={Uri.EscapeDataString(search)}");

        Assert.NotNull(response);
        Assert.Contains("key IN (SRC-1234, SRC-5678)", response.Jql);
        Assert.DoesNotContain("summary ~", response.Jql);
    }

    [Fact]
    public async Task The_same_key_twice_is_only_asked_for_once()
    {
        // Otherwise a careless paste turns into two copies of one ticket.
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<IssueListResponse>(
            "/api/source/issues?search=SRC-1234,%20SRC-1234");

        Assert.NotNull(response);
        Assert.Contains("key = SRC-1234", response.Jql);
        Assert.DoesNotContain("IN", response.Jql[response.Jql.IndexOf("key", StringComparison.Ordinal)..]);
    }

    [Fact]
    public async Task A_list_with_one_non_key_in_it_falls_back_to_a_text_search()
    {
        // Copying the subset it happened to recognise would be worse than
        // searching for the words: silently doing less than asked.
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<IssueListResponse>(
            "/api/source/issues?search=SRC-1234,%20example%20portal");

        Assert.NotNull(response);
        Assert.DoesNotContain("key IN", response.Jql);
        Assert.Contains("summary ~", response.Jql);
    }

    [Fact]
    public async Task A_key_from_another_project_is_not_treated_as_a_key()
    {
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<IssueListResponse>(
            "/api/source/issues?search=TGT-1234,%20SRC-1234");

        Assert.NotNull(response);
        Assert.DoesNotContain("key IN", response.Jql);
        Assert.Contains("summary ~", response.Jql);
    }

    [Fact]
    public async Task Searching_for_free_text_matches_the_summary()
    {
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<IssueListResponse>("/api/source/issues?search=input");

        Assert.NotNull(response);
        Assert.Contains("summary ~ \"input\"", response.Jql);
    }

    [Fact]
    public async Task A_quote_in_the_search_term_cannot_break_out_of_the_jql()
    {
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<IssueListResponse>(
            "/api/source/issues?search=%22%20OR%20project%20%3D%20TGT");

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
        Assert.Equal("SRC-1234", first.Key);
        Assert.Equal("Bug", first.IssueType);
        Assert.Equal("Open", first.Status);
        Assert.Equal("Example Reporter", first.Reporter);
        Assert.Null(first.Assignee);

        // Absolute, and pointing at the SOURCE site - a bare key is meaningless
        // once it is sitting in the target's UI.
        Assert.Equal("https://source.example.invalid/browse/SRC-1234", first.Url);
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
        // customfield_70010 on the source is a different field on the target,
        // so the name is the only thing worth mapping on.
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var issue = await client.GetFromJsonAsync<SourceIssue>("/api/source/issues/SRC-1234");

        Assert.NotNull(issue);

        var storyPoints = issue.Fields.Single(field => field.FieldId == "customfield_70010");
        Assert.Equal("Story point estimate", storyPoints.Name);
        Assert.True(storyPoints.IsCustom);

        // The issue call specifically - not the comments call, which shares the
        // /issue/SRC-1234 prefix.
        var issueRequest = stub.Requests.Single(request => request.Path.EndsWith("/issue/SRC-1234"));
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

        var issue = await client.GetFromJsonAsync<SourceIssue>("/api/source/issues/SRC-1234");

        Assert.NotNull(issue);
        Assert.DoesNotContain(issue.Fields, field => field.FieldId == "customfield_70016");
        Assert.DoesNotContain(issue.Fields, field => field.FieldId == "customfield_70012");
    }

    [Fact]
    public async Task Fields_already_surfaced_as_properties_are_not_repeated()
    {
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var issue = await client.GetFromJsonAsync<SourceIssue>("/api/source/issues/SRC-1234");

        Assert.NotNull(issue);
        Assert.DoesNotContain(issue.Fields, field => field.FieldId is "summary" or "description" or "attachment");

        Assert.Equal("Example portal rejects valid input", issue.Summary);
        Assert.Equal(["example", "input"], issue.Labels);
    }

    [Fact]
    public async Task Description_adf_is_carried_through_untouched()
    {
        // It is rewritten later, before create. Reading must not flatten it or
        // the rewriter has nothing to work with.
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var issue = await client.GetFromJsonAsync<SourceIssue>("/api/source/issues/SRC-1234");

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

        var issue = await client.GetFromJsonAsync<SourceIssue>("/api/source/issues/SRC-1234");

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

        var issue = await client.GetFromJsonAsync<SourceIssue>("/api/source/issues/SRC-1234");

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

        var issue = await client.GetFromJsonAsync<SourceIssue>("/api/source/issues/SRC-1234");

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
    public async Task Source_reads_all_go_to_the_source_cloud_id()
    {
        // The grant reaches both sites, which is the only way this proves
        // anything: every call a source read makes has to pick the source one.
        var stub = new StubAtlassian(SourceTenantFake.Handler);
        using var app = new TestApp(stub, signedIn: true);
        using var client = app.CreateClient();

        await client.GetAsync("/api/source/issues/SRC-1234");

        Assert.NotEmpty(stub.Requests);
        Assert.All(stub.Requests, request =>
        {
            Assert.Equal(Tenant.Source, request.Tenant);
            Assert.True(request.CarriedBearer(TestApp.AccessToken));
        });
    }
}
