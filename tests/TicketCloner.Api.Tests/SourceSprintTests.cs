using System.Net;
using System.Net.Http.Json;
using TicketCloner.Api.Atlassian;
using TicketCloner.Api.Contracts;
using TicketCloner.Api.Tests.Infrastructure;

namespace TicketCloner.Api.Tests;

/// <summary>
/// Picking a batch by SOURCE sprint. The source's sprint ids are read from its
/// own board and used only to search that board - the mapper still drops the
/// Sprint field on every copy, and nothing here changes that.
/// </summary>
public class SourceSprintTests
{
    private static (TestApp App, StubAtlassian Stub) Sut(params (string Key, string? Value)[] settings)
    {
        var stub = new StubAtlassian(SourceTenantFake.Handler);
        var configuration = settings.ToDictionary(pair => pair.Key, pair => pair.Value);

        return (new TestApp(stub, configuration, signedIn: true), stub);
    }

    [Fact]
    public async Task Sprints_come_from_the_source_board_across_every_page()
    {
        // The fake serves two pages of two. Stopping at the first would list
        // half the board and look complete.
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<SprintListResponse>("/api/source/sprints");

        Assert.NotNull(response);
        Assert.Equal(200, response.Board);
        Assert.Equal(4, response.Sprints.Count);

        var pages = stub.Requests.Where(request => request.Path.Contains("/board/101/sprint")).ToList();
        Assert.Equal(2, pages.Count);
        Assert.All(pages, page => Assert.Equal(Tenant.Source, page.Tenant));
        Assert.Contains("startAt=2", pages[1].Uri.Query);
    }

    [Fact]
    public async Task Newest_first_so_the_sprint_somebody_wants_is_near_the_top()
    {
        var (app, _) = Sut();
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<SprintListResponse>("/api/source/sprints");

        Assert.NotNull(response);
        Assert.Equal([8003, 8002, 8001, 8800], response.Sprints.Select(sprint => sprint.Id));
    }

    [Fact]
    public async Task A_name_search_narrows_the_list_and_ignores_case()
    {
        var (app, _) = Sut();
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<SprintListResponse>("/api/source/sprints?search=sprint%201");

        Assert.NotNull(response);
        Assert.Equal(2, response.Sprints.Count);
        Assert.All(response.Sprints, sprint => Assert.Contains("Sprint 1", sprint.Name));
    }

    [Fact]
    public async Task Repeated_names_come_back_as_separate_sprints_with_their_own_ids()
    {
        // "SRC Sprint 1" exists twice on the fake board, a year apart. A list
        // keyed by name would show one and search the wrong one.
        var (app, _) = Sut();
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<SprintListResponse>(
            "/api/source/sprints?search=SRC%20Sprint%201");

        Assert.NotNull(response);
        var ids = response.Sprints.Select(sprint => sprint.Id).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(response.Sprints, sprint => Assert.NotNull(sprint.StartDate));
    }

    [Fact]
    public async Task A_chosen_sprint_narrows_the_ticket_search_without_replacing_it()
    {
        var (app, stub) = Sut();
        using var _ = app;
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<IssueListResponse>(
            "/api/source/issues?sprint=8003&search=portal");

        Assert.NotNull(response);
        Assert.Contains("sprint = 8003", response.Jql);
        Assert.Contains("summary ~ \"portal\"", response.Jql);

        // The Xray exclusion is not lost by adding a clause.
        Assert.Contains("issuetype NOT IN", response.Jql);

        // And it went upstream as JQL, not just into the echo.
        Assert.Contains("sprint = 8003", stub.RequestTo("search/jql").Body);
    }

    [Fact]
    public async Task No_sprint_means_no_sprint_clause()
    {
        var (app, _) = Sut();
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<IssueListResponse>("/api/source/issues");

        Assert.NotNull(response);
        Assert.DoesNotContain("sprint", response.Jql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_source_with_no_board_says_so_rather_than_listing_nothing()
    {
        var (app, stub) = Sut(("Atlassian:Source:BoardId", "0"));
        using var _ = app;
        using var client = app.CreateClient();

        var response = await client.GetAsync("/api/source/sprints");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("No board is configured", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(stub.Requests, request => request.Path.Contains("/sprint"));
    }

    [Fact]
    public async Task Listing_sprints_needs_the_source_and_nothing_else()
    {
        // Half a grant is enough here: this reads one board on one tenant.
        var stub = new StubAtlassian(SourceTenantFake.Handler);
        using var app = new TestApp(stub, signedIn: true, grants: [Tenant.Source]);
        using var client = app.CreateClient();

        var response = await client.GetAsync("/api/source/sprints");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(stub.Requests, request => Assert.Equal(Tenant.Source, request.Tenant));
    }
}
