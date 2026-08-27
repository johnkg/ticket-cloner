using System.Net.Http.Json;
using System.Text.Json.Nodes;
using TicketCloner.Api.Contracts;
using TicketCloner.Api.Tests.Infrastructure;

namespace TicketCloner.Api.Tests;

/// <summary>
/// Refreshing an existing copy from its original.
///
/// Not sync, and these tests are where that line is held: the refresh is
/// one-way, opt-in per ticket, never touches what the target owns, and writes
/// only what actually differs.
/// </summary>
public class CopyRefreshTests
{
    /// <summary>A fake whose target already holds a copy of SRC-1234.</summary>
    private static (TestApp App, StubAtlassian Stub, FakeTenants Tenants) Sut()
    {
        var tenants = new FakeTenants { ExistingCopyKey = "TGT-8001" };
        var stub = new StubAtlassian(tenants.Handler);

        return (new TestApp(stub, Configured(), signedIn: true), stub, tenants);
    }

    private static Dictionary<string, string?> Configured() => new()
    {
        ["Atlassian:Source:BaseUrl"] = "https://source.example.invalid",
        ["Atlassian:Source:ProjectKey"] = "SRC",
        ["Atlassian:Target:BaseUrl"] = "https://target.example.invalid",
        ["Atlassian:Target:ProjectKey"] = "TGT",
        ["Mapping:FallbackIssueType"] = "Task",

        // Without this the plan carries a blocker and nothing can be CREATED -
        // which does not stop a refresh, but does stop copying again, because
        // copying again really is a create.
        ["Mapping:Constants:Customer"] = "TGT",
    };

    private static async Task<MappingPlan> PlanFor(HttpClient client, string key = "SRC-1234")
    {
        var plan = await client.GetFromJsonAsync<MappingPlan>($"/api/preview/{key}");
        Assert.NotNull(plan);
        return plan;
    }

    private static async Task<ApplyResponse> Refresh(HttpClient client, MappingPlan plan)
    {
        var response = await client.PostAsJsonAsync("/api/apply",
            new ApplyRequest([plan], UpdateExisting: [plan.SourceKey]));

        response.EnsureSuccessStatusCode();

        var applied = await response.Content.ReadFromJsonAsync<ApplyResponse>();
        Assert.NotNull(applied);
        return applied;
    }

    private static JsonObject FieldsOf(string body) =>
        (JsonObject)JsonNode.Parse(body)!["fields"]!;

    // ------------------------------------------------------------ the plan

    [Fact]
    public async Task A_preview_of_an_already_copied_issue_carries_what_a_refresh_would_change()
    {
        var (app, _, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await PlanFor(client);

        Assert.NotNull(plan.ExistingCopy);
        Assert.NotNull(plan.Update);
        Assert.Equal("TGT-8001", plan.Update.TargetKey);
        Assert.True(plan.Update.HasChanges);
    }

    [Fact]
    public async Task A_field_the_copy_already_matches_is_not_offered_as_a_change()
    {
        // The fake's copy holds the same summary as the source issue.
        var (app, _, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await PlanFor(client);

        var summary = Assert.Single(plan.Update!.Rows, row => row.Name == "Summary");
        Assert.Equal(UpdateAction.Unchanged, summary.Action);
    }

    [Fact]
    public async Task A_field_the_original_has_moved_on_is_offered_as_a_change()
    {
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        // The copy's labels are stale: the source carries example and input.
        tenants.ExistingCopyFields["labels"] = """["something-else"]""";

        var plan = await PlanFor(client);

        var labels = Assert.Single(plan.Update!.Rows, row => row.Name == "Labels");
        Assert.Equal(UpdateAction.Update, labels.Action);
        Assert.Contains("example", labels.NewValue!.ToJsonString());
        Assert.Contains("something-else", labels.CurrentValue!.ToJsonString());
    }

    [Fact]
    public async Task A_copy_whose_summary_has_drifted_is_not_refreshable_at_all()
    {
        // Already reported as "not a duplicate, because both have to agree".
        // Silently rewriting it would be the same mistake in the other
        // direction - it may not be a copy of this issue any more.
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        tenants.ExistingCopySummary = "Something somebody renamed";

        var plan = await PlanFor(client);

        Assert.NotNull(plan.ExistingCopy);
        Assert.False(plan.ExistingCopy.SummaryMatches);
        Assert.Null(plan.Update);
    }

    [Fact]
    public async Task An_issue_with_no_copy_has_nothing_to_refresh()
    {
        var tenants = new FakeTenants();
        var stub = new StubAtlassian(tenants.Handler);
        using var app = new TestApp(stub, Configured(), signedIn: true);
        using var client = app.CreateClient();

        var plan = await PlanFor(client);

        Assert.Null(plan.ExistingCopy);
        Assert.Null(plan.Update);
    }

    // -------------------------------------------------- what is never touched

    [Fact]
    public async Task The_fields_the_copy_owns_are_never_offered_as_changes()
    {
        // A copy somebody has picked up has its own status, assignee and place
        // on the board. Refreshing the wording must not drag it backwards
        // through the workflow or take it off the person working it.
        var (app, _, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await PlanFor(client);

        foreach (var owned in plan.Update!.Rows.Where(row =>
                     row.TargetFieldId is "assignee" or "reporter" or "status" or "issuetype"))
        {
            Assert.Equal(UpdateAction.TargetOwns, owned.Action);
        }
    }

    [Fact]
    public async Task A_field_on_the_create_screen_but_not_the_edit_screen_is_refused()
    {
        // Sending one Jira will not take fails the whole PUT, so every other
        // field on the refresh would be lost with it.
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        tenants.ExistingCopyFields["labels"] = """["stale"]""";
        tenants.EditableFieldIds.Remove("labels");

        var plan = await PlanFor(client);

        var labels = Assert.Single(plan.Update!.Rows, row => row.Name == "Labels");
        Assert.Equal(UpdateAction.NotEditable, labels.Action);
    }

    [Fact]
    public async Task An_unreadable_edit_screen_does_not_refuse_every_field()
    {
        // editmeta is a check, not a gate. Losing it should cost the check, not
        // the whole refresh - Jira still refuses anything it will not take.
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        tenants.ExistingCopyFields["labels"] = """["stale"]""";
        tenants.FailEditMeta = true;

        var plan = await PlanFor(client);

        var labels = Assert.Single(plan.Update!.Rows, row => row.Name == "Labels");
        Assert.Equal(UpdateAction.Update, labels.Action);
    }

    // ------------------------------------------------- the description

    [Fact]
    public async Task A_description_whose_images_are_all_on_the_copy_needs_no_upload()
    {
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var description = Assert.Single(
            (await PlanFor(client)).Update!.Rows, row => row.TargetFieldId == "description");

        // The copy holds screenshot.png at the same size, so it is taken to be
        // the same file and nothing needs uploading.
        Assert.Equal(UpdateAction.Update, description.Action);
        Assert.Contains("wording has changed", description.Reason);
        Assert.Empty(tenants.RefreshUploads);
    }

    [Fact]
    public async Task The_rewritten_description_points_at_the_site_not_the_rest_base()
    {
        // The bug that broke every copied image: api.atlassian.com answers
        // nothing to Jira's renderer, which carries no credential.
        var (app, _, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var description = Assert.Single(
            (await PlanFor(client)).Update!.Rows, row => row.TargetFieldId == "description");

        var adf = description.NewValue!.ToJsonString();

        Assert.Contains("https://target.example.invalid/rest/api/3/attachment/content/70001", adf);
        Assert.DoesNotContain("api.atlassian.com", adf);
    }

    [Fact]
    public async Task An_image_added_to_the_original_is_named_in_the_plan()
    {
        // The case worth getting right: what "changed" in a description is very
        // often a new screenshot.
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        tenants.ExistingCopyAttachments.Clear();

        var description = Assert.Single(
            (await PlanFor(client)).Update!.Rows, row => row.TargetFieldId == "description");

        Assert.Equal(UpdateAction.Update, description.Action);
        Assert.Contains("1 new image", description.Reason);
        Assert.Contains("screenshot.png", description.Reason);
    }

    [Fact]
    public async Task An_image_added_to_the_original_is_uploaded_before_the_description_points_at_it()
    {
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        // The copy has no files at all - every image in the description is new
        // to it, which is what happens when a screenshot is added upstream.
        tenants.ExistingCopyAttachments.Clear();

        var applied = await Refresh(client, await PlanFor(client));

        Assert.Equal(["screenshot.png"], tenants.RefreshUploads);

        var written = FieldsOf(Assert.Single(tenants.FieldUpdates));
        var adf = written["description"]!.ToJsonString();

        // Pointed at the file that was just uploaded, on the SITE.
        Assert.Contains("https://target.example.invalid/rest/api/3/attachment/content/79001", adf);
        Assert.DoesNotContain("api.atlassian.com", adf);

        var ticket = Assert.Single(applied.Tickets);
        Assert.Equal(ApplyStatus.Updated, ticket.Status);
        Assert.Contains(ticket.Steps, step =>
            step.Step == "Description images" && step.Succeeded && step.Detail.Contains("screenshot.png"));
    }

    [Fact]
    public async Task An_image_replaced_on_the_original_is_uploaded_again()
    {
        // Same name, different size. Size is a weak test, but it is the only
        // one both sides report without downloading every byte - and it catches
        // a screenshot being redone, which is the ordinary case.
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        tenants.ExistingCopyAttachments.Clear();
        tenants.ExistingCopyAttachments.Add(("70001", "screenshot.png", 11, "2026-08-11T09:00:00.000+1000"));

        var description = Assert.Single(
            (await PlanFor(client)).Update!.Rows, row => row.TargetFieldId == "description");

        Assert.Contains("1 changed image", description.Reason);

        await Refresh(client, await PlanFor(client));

        Assert.Equal(["screenshot.png"], tenants.RefreshUploads);

        // The newer of the two same-named files is the one pointed at.
        var adf = FieldsOf(Assert.Single(tenants.FieldUpdates))["description"]!.ToJsonString();
        Assert.Contains("/attachment/content/79001", adf);
        Assert.DoesNotContain("/attachment/content/70001", adf);
    }

    [Fact]
    public async Task A_description_is_left_alone_when_an_image_cannot_be_uploaded()
    {
        // Text with holes in it is worse than a refresh that says it stopped:
        // a picture quietly disappearing from a live ticket is not a smaller
        // failure than not refreshing at all.
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        tenants.ExistingCopyAttachments.Clear();
        tenants.ExistingCopyFields["labels"] = """["stale"]""";
        tenants.FailAttachments = true;

        var applied = await Refresh(client, await PlanFor(client));

        var written = FieldsOf(Assert.Single(tenants.FieldUpdates));

        Assert.DoesNotContain("description", written.Select(field => field.Key));
        Assert.Contains("labels", written.Select(field => field.Key));

        Assert.Contains(Assert.Single(applied.Tickets).Steps, step =>
            step.Step == "Description images" && !step.Succeeded);
    }

    // ----------------------------------------------------------- the write

    [Fact]
    public async Task A_refresh_writes_only_what_differs()
    {
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        tenants.ExistingCopyFields["labels"] = """["stale"]""";

        var applied = await Refresh(client, await PlanFor(client));

        var written = FieldsOf(Assert.Single(tenants.FieldUpdates));

        Assert.Contains("labels", written.Select(field => field.Key));
        Assert.DoesNotContain("summary", written.Select(field => field.Key));
        Assert.DoesNotContain("assignee", written.Select(field => field.Key));

        var ticket = Assert.Single(applied.Tickets);
        Assert.Equal(ApplyStatus.Updated, ticket.Status);
        Assert.Equal("TGT-8001", ticket.TargetKey);
        Assert.Equal(1, applied.Updated);
    }

    [Fact]
    public async Task A_refresh_creates_nothing()
    {
        var (app, stub, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        tenants.ExistingCopyFields["labels"] = """["stale"]""";

        await Refresh(client, await PlanFor(client));

        Assert.DoesNotContain(stub.Requests, request =>
            request.Method == HttpMethod.Post &&
            request.Path.EndsWith("/rest/api/3/issue", StringComparison.OrdinalIgnoreCase));

        Assert.Empty(tenants.CreatedKeys);
    }

    [Fact]
    public async Task Nothing_is_written_when_the_copy_already_matches()
    {
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        // Make the copy hold exactly what the source would write, whatever the
        // fixture happens to carry. That also exercises the comparison itself:
        // these values go in as the WRITE shape and are read back as the read
        // shape, and a refresh must see no difference between them.
        foreach (var row in (await PlanFor(client)).Update!.Rows
                     .Where(row => row.Action == UpdateAction.Update))
        {
            tenants.ExistingCopyFields[row.TargetFieldId] = row.NewValue!.ToJsonString();
        }

        var applied = await Refresh(client, await PlanFor(client));

        Assert.Empty(tenants.FieldUpdates);

        var ticket = Assert.Single(applied.Tickets);
        Assert.Equal(ApplyStatus.Skipped, ticket.Status);
        Assert.Contains("already matches", ticket.Summary);
    }

    [Fact]
    public async Task A_ticket_not_named_for_refresh_is_still_skipped_as_a_duplicate()
    {
        // The opt-in is per ticket on purpose: a run-wide "refresh everything"
        // would rewrite copies nobody looked at.
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        tenants.ExistingCopyFields["labels"] = """["stale"]""";

        var response = await client.PostAsJsonAsync("/api/apply",
            new ApplyRequest([await PlanFor(client)]));

        var applied = await response.Content.ReadFromJsonAsync<ApplyResponse>();

        Assert.Empty(tenants.FieldUpdates);
        Assert.Equal(ApplyStatus.Skipped, Assert.Single(applied!.Tickets).Status);
    }

    [Fact]
    public async Task A_description_still_holding_a_source_file_reference_is_refused()
    {
        // The guard behind the rewriter, not the rewriter itself. The set of
        // node types that carry an attachment reference is not closed -
        // mediaInline was missed until it rejected a whole PUT - so the
        // finished document is asked directly. An unknown type costs one
        // refused field; left in, it costs every field in the request.
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        tenants.ExistingCopyFields["labels"] = """["stale"]""";

        // A media node shape the rewriter does not know about, carrying a file
        // reference from the source tenant's media store.
        tenants.SourceDescriptionExtra = """
            { "type": "mediaSomethingNew", "attrs": { "type": "file", "id": "1ca7ce94-fb48" } }
            """;

        var applied = await Refresh(client, await PlanFor(client));

        var written = FieldsOf(Assert.Single(tenants.FieldUpdates));

        Assert.DoesNotContain("description", written.Select(field => field.Key));
        Assert.Contains("labels", written.Select(field => field.Key));

        Assert.Contains(Assert.Single(applied.Tickets).Steps, step =>
            step.Step == "Description images" && !step.Succeeded &&
            step.Detail.Contains("mediaSomethingNew"));
    }

    // ------------------------------------------------------- copying again

    [Fact]
    public async Task An_already_copied_issue_can_be_copied_again_on_purpose()
    {
        // The escape hatch for a copy that went wrong - which is how the
        // api.atlassian.com image bug gets retested without touching the
        // ticket that has it.
        var (app, stub, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/apply",
            new ApplyRequest([await PlanFor(client)], CopyAgainFor: ["SRC-1234"]));

        var applied = await response.Content.ReadFromJsonAsync<ApplyResponse>();

        var ticket = Assert.Single(applied!.Tickets);
        Assert.Equal(ApplyStatus.Created, ticket.Status);
        Assert.NotEqual("TGT-8001", ticket.TargetKey);

        Assert.Contains(stub.Requests, request =>
            request.Method == HttpMethod.Post &&
            request.Path.EndsWith("/rest/api/3/issue", StringComparison.OrdinalIgnoreCase));

        Assert.Single(tenants.CreatedKeys);
    }

    [Fact]
    public async Task A_deliberate_duplicate_says_so_in_the_results()
    {
        // Months later, "why are there two of these?" should be answerable
        // from the run that made them.
        var (app, _, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/apply",
            new ApplyRequest([await PlanFor(client)], CopyAgainFor: ["SRC-1234"]));

        var applied = await response.Content.ReadFromJsonAsync<ApplyResponse>();

        Assert.Contains(Assert.Single(applied!.Tickets).Steps, step =>
            step.Step == "Duplicate check" &&
            step.Detail.Contains("on purpose") &&
            step.Detail.Contains("TGT-8001"));
    }

    [Fact]
    public async Task A_ticket_not_named_for_copying_again_is_still_skipped()
    {
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/apply",
            new ApplyRequest([await PlanFor(client)]));

        var applied = await response.Content.ReadFromJsonAsync<ApplyResponse>();

        Assert.Equal(ApplyStatus.Skipped, Assert.Single(applied!.Tickets).Status);
        Assert.Empty(tenants.CreatedKeys);
    }

    [Fact]
    public async Task Refreshing_and_copying_again_at_once_is_refused_rather_than_guessed_at()
    {
        // Opposite answers to the same question, and one of the guesses creates
        // a ticket nobody asked for.
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/apply",
            new ApplyRequest([await PlanFor(client)],
                UpdateExisting: ["SRC-1234"], CopyAgainFor: ["SRC-1234"]));

        var applied = await response.Content.ReadFromJsonAsync<ApplyResponse>();

        var ticket = Assert.Single(applied!.Tickets);
        Assert.Equal(ApplyStatus.Skipped, ticket.Status);
        Assert.Contains("opposites", ticket.Summary);

        Assert.Empty(tenants.CreatedKeys);
        Assert.Empty(tenants.FieldUpdates);
    }

    [Fact]
    public async Task A_failed_refresh_says_nothing_was_written()
    {
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        tenants.ExistingCopyFields["labels"] = """["stale"]""";
        tenants.FailRefresh = true;

        var applied = await Refresh(client, await PlanFor(client));

        var ticket = Assert.Single(applied.Tickets);
        Assert.Equal(ApplyStatus.Failed, ticket.Status);
        Assert.Contains(ticket.Steps, step =>
            step.Step == "Refresh" && !step.Succeeded && step.Detail.Contains("Nothing was written"));
    }

    [Fact]
    public async Task A_copy_that_stopped_being_this_issues_copy_is_not_refreshed()
    {
        // The plan has been through the browser. Between preview and apply the
        // copy can be renamed, repointed or deleted, and the refresh must not
        // write into whatever is there now.
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        tenants.ExistingCopyFields["labels"] = """["stale"]""";

        var plan = await PlanFor(client);

        // Somebody reworded the copy after the preview was taken.
        tenants.ExistingCopySummary = "Renamed after the preview";

        var applied = await Refresh(client, plan);

        Assert.Empty(tenants.FieldUpdates);

        var ticket = Assert.Single(applied.Tickets);
        Assert.Equal(ApplyStatus.Skipped, ticket.Status);
        Assert.Contains("no longer the copy", ticket.Summary);
    }
}
