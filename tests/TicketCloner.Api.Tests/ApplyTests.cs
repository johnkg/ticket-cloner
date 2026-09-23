using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using TicketCloner.Api.Atlassian;
using TicketCloner.Api.Contracts;
using TicketCloner.Api.Tests.Infrastructure;

namespace TicketCloner.Api.Tests;

public class ApplyTests
{
    private static Dictionary<string, string?> Configured() => new()
    {
        ["Mapping:Priorities:None"] = "Medium",
        ["Mapping:Constants:Customer"] = "TGT",
    };

    private static (TestApp App, StubAtlassian Stub, FakeTenants Tenants) Sut(
        params (string Key, string Value)[] extra)
    {
        var tenants = new FakeTenants();
        var stub = new StubAtlassian(tenants.Handler);

        var settings = Configured();
        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        return (new TestApp(stub, settings, signedIn: true), stub, tenants);
    }

    private static async Task<MappingPlan> PlanFor(HttpClient client, string key = "SRC-1234")
    {
        var plan = await client.GetFromJsonAsync<MappingPlan>($"/api/preview/{key}");
        Assert.NotNull(plan);
        return plan;
    }

    private static async Task<ApplyResponse> Apply(HttpClient client, params MappingPlan[] plans)
    {
        var response = await client.PostAsJsonAsync("/api/apply", new ApplyRequest(plans));
        response.EnsureSuccessStatusCode();

        var applied = await response.Content.ReadFromJsonAsync<ApplyResponse>();
        Assert.NotNull(applied);
        return applied;
    }

    private static CapturedRequest CreateCall(StubAtlassian stub) =>
        stub.Requests.Single(request =>
            request.Method == HttpMethod.Post &&
            request.Path.EndsWith("/rest/api/3/issue", StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- happy path

    [Fact]
    public async Task A_plan_becomes_an_issue_on_the_target()
    {
        var (app, stub, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var applied = await Apply(client, await PlanFor(client));

        var ticket = Assert.Single(applied.Tickets);
        Assert.Equal(ApplyStatus.Created, ticket.Status);
        Assert.Equal("TGT-9001", ticket.TargetKey);
        Assert.Equal("https://target.example.invalid/browse/TGT-9001", ticket.TargetUrl);
        Assert.Equal(1, applied.Created);
    }

    [Fact]
    public async Task The_create_carries_the_project_and_the_resolved_issue_type()
    {
        var (app, stub, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        await Apply(client, await PlanFor(client));

        var body = JsonNode.Parse(CreateCall(stub).Body!)!["fields"]!;
        Assert.Equal("TGT", body["project"]!["key"]!.GetValue<string>());
        Assert.Equal("10001", body["issuetype"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Only_mapped_rows_are_sent()
    {
        // Workaround is a type clash and came back Unmappable. Sending it would
        // either 400 or write the wrong shape of value, so it must not appear
        // however the plan was passed back.
        var (app, stub, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await PlanFor(client);
        Assert.Equal(MappingStatus.Unmappable, plan.Rows.Single(row => row.Name == "Workaround").Status);

        await Apply(client, plan);

        var fields = (JsonObject)JsonNode.Parse(CreateCall(stub).Body!)!["fields"]!;
        Assert.DoesNotContain("customfield_70006", fields.Select(field => field.Key));
        Assert.Contains("customfield_70007", fields.Select(field => field.Key));
    }

    [Fact]
    public async Task A_field_named_TGT_Source_Key_on_the_target_is_left_alone()
    {
        // The fake still offers one on the create screen, on purpose: it used
        // to receive the bare source key as a second record of origin, and was
        // dropped on 16/09/2026 because External Issue ID already answers that
        // question. Two fields answering it is how they drift apart. Nothing
        // may quietly start writing it again.
        var (app, stub, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var applied = await Apply(client, await PlanFor(client));

        var fields = (JsonObject)JsonNode.Parse(CreateCall(stub).Body!)!["fields"]!;
        Assert.DoesNotContain("customfield_70013", fields.Select(field => field.Key));
        Assert.DoesNotContain(applied.Tickets[0].Steps, step => step.Step == "Provenance");

        // And the duplicate check still ran without it.
        Assert.Contains(stub.Requests, request =>
            request.Tenant == Tenant.Target &&
            request.Path.EndsWith("/search/jql", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_remote_link_points_back_at_the_source_issue()
    {
        var (app, stub, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        await Apply(client, await PlanFor(client));

        var remoteLink = stub.Requests.Single(request => request.Path.EndsWith("/remotelink"));
        Assert.Contains("https://source.example.invalid/browse/SRC-1234", remoteLink.Body);
    }

    // ---------------------------------------------------------------- duplicates

    [Fact]
    public async Task An_already_copied_ticket_is_skipped_rather_than_duplicated()
    {
        var (app, stub, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await PlanFor(client);

        // Both halves agree by default: identical summary, and the External
        // Issue ID holds the source issue's URL.
        tenants.ExistingCopyKey = "TGT-8888";

        var applied = await Apply(client, plan);

        var ticket = Assert.Single(applied.Tickets);
        Assert.Equal(ApplyStatus.Skipped, ticket.Status);
        Assert.Contains("TGT-8888", ticket.Summary);
        Assert.Contains(ticket.Steps, step =>
            step.Step == "Duplicate check" && step.Detail.Contains("identical summary"));

        Assert.DoesNotContain(stub.Requests, request =>
            request.Method == HttpMethod.Post &&
            request.Path.EndsWith("/rest/api/3/issue", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_candidate_whose_summary_has_drifted_is_reported_but_not_treated_as_a_duplicate()
    {
        // Same origin, different summary. The rule is that both have to agree, so
        // this copy still goes ahead - but silently making a second one when
        // somebody merely reworded the first would be the wrong kind of quiet.
        var (app, stub, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await PlanFor(client);
        tenants.ExistingCopyKey = "TGT-8888";
        tenants.ExistingCopySummary = "Reworded by somebody on this side";

        var applied = await Apply(client, plan);

        var ticket = Assert.Single(applied.Tickets);
        Assert.NotEqual(ApplyStatus.Skipped, ticket.Status);
        Assert.Equal("TGT-9001", ticket.TargetKey);

        Assert.Contains(ticket.Steps, step =>
            step.Step == "Duplicate check" && !step.Succeeded &&
            step.Detail.Contains("TGT-8888") &&
            step.Detail.Contains("Reworded by somebody on this side"));
    }

    [Fact]
    public async Task A_candidate_pointing_at_a_different_source_is_not_a_duplicate()
    {
        // JQL only narrows - it is a tokenised text match, not equality - so a
        // candidate it returns still has to be checked properly.
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await PlanFor(client);
        tenants.ExistingCopyKey = "TGT-8888";
        tenants.ExistingCopyReference = "https://source.example.invalid/browse/SRC-9999";

        var applied = await Apply(client, plan);

        var ticket = Assert.Single(applied.Tickets);
        Assert.Equal("TGT-9001", ticket.TargetKey);
        Assert.Contains(ticket.Steps, step =>
            step.Step == "Duplicate check" && step.Succeeded && step.Detail.Contains("No existing copy"));
    }

    [Fact]
    public async Task A_copy_recorded_as_a_bare_key_rather_than_a_url_still_counts()
    {
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await PlanFor(client);
        tenants.ExistingCopyKey = "TGT-8888";
        tenants.ExistingCopyReference = "SRC-1234";

        var applied = await Apply(client, plan);

        Assert.Equal(ApplyStatus.Skipped, Assert.Single(applied.Tickets).Status);
    }

    [Fact]
    public async Task The_duplicate_search_narrows_on_the_field_and_the_source_key()
    {
        var (app, stub, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        await Apply(client, await PlanFor(client));

        var searches = stub.Requests.Where(request =>
            request.Tenant == Tenant.Target &&
            request.Path.EndsWith("/search/jql", StringComparison.OrdinalIgnoreCase)).ToList();

        // Twice on purpose: preview asks so the UI can show what it found, and
        // apply asks again rather than trusting a plan that has been through
        // the browser and may be describing a stale world.
        Assert.Equal(2, searches.Count);

        Assert.All(searches, search =>
        {
            Assert.Contains("External Issue ID", search.Body);
            Assert.Contains("SRC-1234", search.Body);
            Assert.Contains("project = TGT", search.Body);
        });
    }

    [Fact]
    public async Task The_preview_reports_an_existing_copy_with_a_link_to_it()
    {
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        tenants.ExistingCopyKey = "TGT-8888";

        var plan = await PlanFor(client);

        Assert.NotNull(plan.ExistingCopy);
        Assert.Equal("TGT-8888", plan.ExistingCopy.Key);
        Assert.True(plan.ExistingCopy.SummaryMatches);

        // Clickable, so somebody can go and check it really is a clone.
        Assert.Equal("https://target.example.invalid/browse/TGT-8888", plan.ExistingCopy.Url);
    }

    [Fact]
    public async Task A_preview_with_no_copy_on_the_target_says_so()
    {
        var (app, _, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        Assert.Null((await PlanFor(client)).ExistingCopy);
    }

    [Fact]
    public async Task Without_the_external_issue_id_field_the_check_says_it_cannot_run()
    {
        // Reporting that nothing was checked matters more than the check
        // itself: this is the state in which a re-run duplicates everything.
        var (app, stub, _) = Sut(("Atlassian:Target:SourceUrlFieldName", "Nothing Called This"));
        using var _app = app;
        using var client = app.CreateClient();

        var applied = await Apply(client, await PlanFor(client));

        Assert.Contains(applied.Tickets[0].Steps, step =>
            step.Step == "Duplicate check" && !step.Succeeded && step.Detail.Contains("Cannot run"));

        Assert.DoesNotContain(stub.Requests, request =>
            request.Tenant == Tenant.Target &&
            request.Path.EndsWith("/search/jql", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_plan_with_blockers_is_refused_even_if_it_is_sent_back()
    {
        var tenants = new FakeTenants();
        var stub = new StubAtlassian(tenants.Handler);

        // No constant configured, so 'Customer' blocks the plan.
        var settings = Configured();
        settings.Remove("Mapping:Constants:Customer");

        using var app = new TestApp(stub, settings, signedIn: true);
        using var client = app.CreateClient();

        var plan = await PlanFor(client);
        Assert.False(plan.CanCreate);

        var applied = await Apply(client, plan);

        Assert.Equal(ApplyStatus.Skipped, applied.Tickets[0].Status);
        Assert.Contains("blocker", applied.Tickets[0].Summary);
        Assert.DoesNotContain(stub.Requests, request =>
            request.Method == HttpMethod.Post &&
            request.Path.EndsWith("/rest/api/3/issue", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- comments

    [Fact]
    public async Task Comments_are_copied_and_attributed_to_their_original_author()
    {
        // Every comment is authored by the running token - Jira has no
        // impersonation on this route - so the body has to carry the truth.
        var (app, stub, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        await Apply(client, await PlanFor(client));

        var comments = stub.Requests.Where(request => request.Path.EndsWith("/comment") &&
                                                      request.Method == HttpMethod.Post).ToList();

        Assert.Equal(2, comments.Count);
        Assert.Contains("Example Reporter wrote on 11/08/2026", comments[0].Body);
        Assert.Contains("Example Developer wrote on 12/08/2026", comments[1].Body);
    }

    [Fact]
    public async Task Comments_can_be_left_out()
    {
        var (app, stub, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await PlanFor(client);

        var response = await client.PostAsJsonAsync("/api/apply",
            new ApplyRequest([plan], IncludeComments: false));
        response.EnsureSuccessStatusCode();

        Assert.DoesNotContain(stub.Requests, request =>
            request.Method == HttpMethod.Post && request.Path.EndsWith("/comment"));
    }

    // ---------------------------------------------------------------- attachments

    [Fact]
    public async Task Attachments_are_downloaded_from_the_source_and_re_uploaded_to_the_target()
    {
        var (app, stub, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        await Apply(client, await PlanFor(client));

        Assert.Contains(stub.Requests, request =>
            request.Tenant == Tenant.Source &&
            request.Path.Contains("/attachment/content/50021"));

        Assert.Contains(stub.Requests, request =>
            request.Tenant == Tenant.Target &&
            request.Path.EndsWith("/attachments"));
    }

    // ---------------------------------------------------------------- description images

    [Fact]
    public async Task An_image_in_the_description_survives_the_copy()
    {
        // The description embeds attachment 50021, which only exists on the
        // source. At create time there is nothing here to point the media node
        // at, so it comes out; once the file is re-uploaded as 70001 the
        // description is written a second time with the image back in place.
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var applied = await Apply(client, await PlanFor(client));

        Assert.Equal(2, tenants.DescriptionsWritten.Count);
        Assert.DoesNotContain("50021", tenants.DescriptionsWritten[0]);
        Assert.DoesNotContain("mediaSingle", tenants.DescriptionsWritten[0]);

        // Rewritten to the id the target issued, never the source's.
        Assert.Contains("70001", tenants.DescriptionsWritten[1]);
        Assert.DoesNotContain("50021", tenants.DescriptionsWritten[1]);

        var ticket = Assert.Single(applied.Tickets);
        Assert.Equal(ApplyStatus.Created, ticket.Status);
        Assert.Contains(ticket.Steps, step =>
            step.Step == "Description images" && step.Succeeded && step.Detail.Contains("Restored 1"));
    }

    [Fact]
    public async Task Links_and_media_urls_use_the_site_and_never_the_rest_base()
    {
        // OAuth sends the REST calls to api.atlassian.com. That host answers
        // nothing without a Bearer token, and Jira's renderer fetches a media
        // node's URL carrying no credential at all - so a URL built from the
        // REST base is a broken image, which is the bug this project already
        // fixed once and would otherwise reintroduce.
        var (app, stub, tenants) = Sut();

        using var _app = app;
        using var client = app.CreateClient();

        var applied = await Apply(client, await PlanFor(client));

        // The split is real: the write went out to the REST base, not the site.
        Assert.Equal("api.atlassian.com", CreateCall(stub).Host);
        Assert.StartsWith($"/ex/jira/{TestApp.TargetCloudId}/", CreateCall(stub).Path);

        // Everything a person or a renderer follows still points at the site.
        var description = tenants.DescriptionsWritten[1];
        Assert.Contains(
            "https://target.example.invalid/rest/api/3/attachment/content/70001", description);
        Assert.DoesNotContain("api.atlassian.com", description);

        var ticket = Assert.Single(applied.Tickets);
        Assert.Equal("https://target.example.invalid/browse/TGT-9001", ticket.TargetUrl);
    }

    [Fact]
    public async Task The_attachments_go_up_before_the_description_that_refers_to_them()
    {
        // Ordering is the whole fix: the media node has to be rewritten to an
        // id that does not exist until the upload has happened.
        var (app, stub, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        await Apply(client, await PlanFor(client));

        var upload = stub.Requests.ToList().FindIndex(request =>
            request.Path.EndsWith("/attachments", StringComparison.OrdinalIgnoreCase));

        var update = stub.Requests.ToList().FindIndex(request => request.Method == HttpMethod.Put);

        Assert.True(upload >= 0 && update >= 0);
        Assert.True(upload < update, "the attachment must be uploaded before the description references it");
    }

    [Fact]
    public async Task Skipping_attachments_leaves_the_images_out_and_says_so()
    {
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/apply", new ApplyRequest([await PlanFor(client)], IncludeAttachments: false));

        var applied = await response.Content.ReadFromJsonAsync<ApplyResponse>();

        // One write only: there was nothing to point the media node at, so the
        // second pass had no reason to run.
        Assert.Single(tenants.DescriptionsWritten);

        var ticket = Assert.Single(applied!.Tickets);
        Assert.Equal(ApplyStatus.CreatedWithProblems, ticket.Status);
        Assert.Contains(ticket.Steps, step =>
            step.Step == "Description images" && !step.Succeeded && step.Detail.Contains("1 image(s) are missing"));
    }

    [Fact]
    public async Task A_failed_description_update_still_leaves_the_copy_standing()
    {
        // The issue exists and its attachments are on it. Only the inline
        // images are wrong, so this is a problem to finish by hand, not a
        // failed copy.
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await PlanFor(client);
        tenants.FailDescriptionUpdate = true;

        var applied = await Apply(client, plan);

        var ticket = Assert.Single(applied.Tickets);
        Assert.Equal(ApplyStatus.CreatedWithProblems, ticket.Status);
        Assert.Equal("TGT-9001", ticket.TargetKey);
        Assert.Contains(ticket.Steps, step => step.Step == "Description images" && !step.Succeeded);
    }

    // ---------------------------------------------------------------- source url

    [Fact]
    public async Task The_source_url_is_written_to_the_external_issue_id_field()
    {
        var (app, stub, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        await Apply(client, await PlanFor(client));

        var fields = JsonNode.Parse(CreateCall(stub).Body!)!["fields"]!;

        Assert.Equal(
            "https://source.example.invalid/browse/SRC-1234",
            fields["customfield_70008"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_missing_source_url_field_is_reported_rather_than_guessed_at()
    {
        var (app, _, _) = Sut(("Atlassian:Target:SourceUrlFieldName", "Nothing Called This"));
        using var _app = app;
        using var client = app.CreateClient();

        var applied = await Apply(client, await PlanFor(client));

        var ticket = Assert.Single(applied.Tickets);
        Assert.Contains(ticket.Steps, step =>
            step.Step == "Source URL" && !step.Succeeded &&
            step.Detail.Contains("Nothing Called This"));
    }

    // ---------------------------------------------------------------- sprint

    private static Task<ApplyResponse> ApplyToSprint(HttpClient client, params MappingPlan[] plans) =>
        Send(client, new ApplyRequest(plans, AddToActiveSprint: true));

    private static async Task<ApplyResponse> Send(HttpClient client, ApplyRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/apply", request);
        response.EnsureSuccessStatusCode();

        var applied = await response.Content.ReadFromJsonAsync<ApplyResponse>();
        Assert.NotNull(applied);
        return applied;
    }

    private static IReadOnlyList<CapturedRequest> SprintReads(StubAtlassian stub) =>
        stub.Requests
            .Where(request => request.Path.Contains("/rest/agile/1.0/board/", StringComparison.OrdinalIgnoreCase))
            .ToList();

    [Fact]
    public async Task A_copy_can_be_put_in_the_target_boards_current_sprint()
    {
        var (app, stub, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var applied = await ApplyToSprint(client, await PlanFor(client));

        // A bare id, not the array the field reads back as.
        var update = Assert.Single(tenants.FieldUpdates);
        var fields = (JsonObject)JsonNode.Parse(update)!["fields"]!;
        Assert.Equal(3946, fields["customfield_70002"]!.GetValue<int>());

        var ticket = Assert.Single(applied.Tickets);
        Assert.Equal(ApplyStatus.Created, ticket.Status);
        Assert.Contains(ticket.Steps, step =>
            step.Step == "Sprint" && step.Succeeded && step.Detail.Contains("Example Sprint 5"));
    }

    [Fact]
    public async Task A_copy_can_be_put_in_a_chosen_target_sprint_instead_of_the_current_one()
    {
        // Chosen from the target board's own list, so the id is the target's.
        // The board is asked for THAT sprint, never for its active one.
        var (app, stub, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await PlanFor(client);
        var applied = await Send(client, new ApplyRequest([plan], SprintId: 3952));

        var update = Assert.Single(tenants.FieldUpdates);
        var fields = (JsonObject)JsonNode.Parse(update)!["fields"]!;
        Assert.Equal(3952, fields["customfield_70002"]!.GetValue<int>());

        var ticket = Assert.Single(applied.Tickets);
        Assert.Equal(ApplyStatus.Created, ticket.Status);
        Assert.Contains(ticket.Steps, step =>
            step.Step == "Sprint" && step.Succeeded && step.Detail.Contains("Example Sprint 6"));

        Assert.DoesNotContain(stub.Requests, request => request.Uri.Query.Contains("state=active"));
        Assert.Single(stub.Requests, request => request.Path.EndsWith("/sprint/3952"));
    }

    [Fact]
    public async Task A_chosen_sprint_wins_over_the_current_one_when_both_are_sent()
    {
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await PlanFor(client);
        await Send(client, new ApplyRequest([plan], AddToActiveSprint: true, SprintId: 3940));

        var update = Assert.Single(tenants.FieldUpdates);
        Assert.Equal(3940, JsonNode.Parse(update)!["fields"]!["customfield_70002"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_sprint_the_target_does_not_have_costs_the_sprint_and_not_the_copy()
    {
        // The id is looked up once, before the run, so a wrong one is reported
        // rather than turning into a 400 on every ticket.
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await PlanFor(client);
        var applied = await Send(client, new ApplyRequest([plan], SprintId: 999999));

        Assert.Empty(tenants.FieldUpdates);

        var ticket = Assert.Single(applied.Tickets);
        Assert.Equal("TGT-9001", ticket.TargetKey);
        Assert.Equal(ApplyStatus.CreatedWithProblems, ticket.Status);
        Assert.Contains(ticket.Steps, step =>
            step.Step == "Sprint" && !step.Succeeded && step.Detail.Contains("999999"));
    }

    [Fact]
    public async Task The_target_sprint_list_puts_the_current_one_first_then_newest_to_oldest()
    {
        var (app, _, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<SprintListResponse>("/api/target/sprints");

        Assert.NotNull(response);
        Assert.Equal(61, response.Board);

        // Active first even though the future sprint starts later; then by
        // start date, so the two "TGT Sprint 1"s land a year apart, in order.
        Assert.Equal([3946, 3952, 3940, 252, 126], response.Sprints.Select(sprint => sprint.Id));
        Assert.Equal("active", response.Sprints[0].State);
    }

    [Fact]
    public async Task The_source_sprint_is_still_dropped_when_the_target_one_is_set()
    {
        // Two different mechanisms that must not be confused. The source's
        // sprint is meaningless here and stays dropped; the target's own is
        // written afterwards, from the board rather than from the ticket.
        var (app, stub, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        await ApplyToSprint(client, await PlanFor(client));

        var created = (JsonObject)JsonNode.Parse(CreateCall(stub).Body!)!["fields"]!;
        Assert.DoesNotContain("customfield_70002", created.Select(field => field.Key));
    }

    [Fact]
    public async Task Nothing_asks_the_board_about_sprints_unless_it_was_asked_for()
    {
        var (app, stub, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var applied = await Apply(client, await PlanFor(client));

        Assert.Empty(SprintReads(stub));
        Assert.DoesNotContain(applied.Tickets[0].Steps, step => step.Step == "Sprint");
    }

    [Fact]
    public async Task The_board_is_asked_once_however_many_tickets_are_copied()
    {
        // The answer is the same for every ticket, and a run already makes a
        // lot of calls.
        var (app, stub, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await PlanFor(client);
        await Send(client, new ApplyRequest([plan, plan], SkipDuplicates: false, AddToActiveSprint: true));

        Assert.Single(SprintReads(stub));
    }

    [Fact]
    public async Task A_board_with_no_active_sprint_is_reported_rather_than_guessed()
    {
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        tenants.ActiveSprints.Clear();

        var applied = await ApplyToSprint(client, await PlanFor(client));

        var ticket = Assert.Single(applied.Tickets);
        Assert.Equal(ApplyStatus.CreatedWithProblems, ticket.Status);
        Assert.Contains(ticket.Steps, step =>
            step.Step == "Sprint" && !step.Succeeded && step.Detail.Contains("no active sprint"));
    }

    [Fact]
    public async Task Parallel_active_sprints_are_refused_rather_than_picked_from()
    {
        // A board can legitimately run two at once. Quietly taking the first
        // would put work in a sprint nobody chose.
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        tenants.ActiveSprints.Add((4001, "Example Sprint 6"));

        var applied = await ApplyToSprint(client, await PlanFor(client));

        var ticket = Assert.Single(applied.Tickets);
        Assert.Contains(ticket.Steps, step =>
            step.Step == "Sprint" && !step.Succeeded &&
            step.Detail.Contains("Example Sprint 5") && step.Detail.Contains("Example Sprint 6"));
    }

    [Fact]
    public async Task A_rejected_sprint_value_costs_the_sprint_and_not_the_copy()
    {
        // The reason this happens after the create rather than on it. On the
        // create, the same 400 would cost the whole ticket.
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        tenants.FailSprintUpdate = true;

        var applied = await ApplyToSprint(client, await PlanFor(client));

        var ticket = Assert.Single(applied.Tickets);
        Assert.Equal("TGT-9001", ticket.TargetKey);
        Assert.Equal(ApplyStatus.CreatedWithProblems, ticket.Status);
        Assert.Contains(ticket.Steps, step => step.Step == "Sprint" && !step.Succeeded);
    }

    [Fact]
    public async Task An_epic_created_for_a_child_is_not_put_in_the_sprint()
    {
        // An epic is a container, not work somebody picks up this fortnight.
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        await Send(client, new ApplyRequest(
            [await PlanFor(client, "SRC-4444")],
            CreateEpicsFor: ["SRC-4000"],
            AddToActiveSprint: true));

        // Two issues created, one sprint update - the child's.
        Assert.Equal(2, tenants.CreatedKeys.Count);
        Assert.Single(tenants.FieldUpdates);
    }

    // ---------------------------------------------------------------- epics

    private static async Task<ApplyResponse> ApplyWithEpics(
        HttpClient client, MappingPlan plan, params string[] epics)
    {
        var response = await client.PostAsJsonAsync(
            "/api/apply", new ApplyRequest([plan], CreateEpicsFor: epics));

        response.EnsureSuccessStatusCode();
        var applied = await response.Content.ReadFromJsonAsync<ApplyResponse>();
        Assert.NotNull(applied);
        return applied;
    }

    [Fact]
    public async Task A_child_of_an_epic_reports_the_epic_in_its_preview()
    {
        var (app, _, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await PlanFor(client, "SRC-4444");

        Assert.NotNull(plan.Epic);
        Assert.Equal("SRC-4000", plan.Epic.SourceKey);
        Assert.Equal("Example portal improvements", plan.Epic.Summary);

        // Nothing on the target matches it, so one has to be made.
        Assert.True(plan.Epic.WillCreate);
        Assert.Null(plan.Epic.ExistingCopy);
    }

    [Fact]
    public async Task An_issue_with_no_epic_parent_has_no_epic_on_its_plan()
    {
        var (app, _, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        Assert.Null((await PlanFor(client)).Epic);
    }

    [Fact]
    public async Task The_epic_is_created_first_and_the_child_is_parented_under_it()
    {
        var (app, stub, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var applied = await ApplyWithEpics(client, await PlanFor(client, "SRC-4444"), "SRC-4000");

        // Two creates: the epic, then the child.
        Assert.Equal(2, tenants.CreatedKeys.Count);
        Assert.Equal(["TGT-9001", "TGT-9002"], tenants.CreatedKeys);

        var creates = stub.Requests
            .Where(r => r.Method == HttpMethod.Post &&
                        r.Path.EndsWith("/rest/api/3/issue", StringComparison.OrdinalIgnoreCase))
            .Select(r => (JsonObject)JsonNode.Parse(r.Body!)!["fields"]!)
            .ToList();

        // The epic goes up with no parent of its own.
        Assert.DoesNotContain("parent", creates[0].Select(f => f.Key));

        // The child names the epic that was just created.
        Assert.Equal("TGT-9001", creates[1]["parent"]!["key"]!.GetValue<string>());

        var child = applied.Tickets.Single(ticket => ticket.SourceKey == "SRC-4444");
        Assert.Contains(child.Steps, step =>
            step.Step == "Epic" && step.Succeeded && step.Detail.Contains("TGT-9001"));

        // Both are reported, so the extra ticket is not invisible.
        Assert.Contains(applied.Tickets, ticket => ticket.SourceKey == "SRC-4000");
    }

    [Fact]
    public async Task An_epic_already_on_the_target_is_reused_rather_than_cloned()
    {
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        // Matched by summary, which is how an epic somebody made by hand on the
        // target - carrying no External Issue ID - still gets recognised.
        tenants.ExistingEpicKey = "TGT-5000";

        var applied = await ApplyWithEpics(client, await PlanFor(client, "SRC-4444"), "SRC-4000");

        Assert.Single(tenants.CreatedKeys);

        var child = Assert.Single(applied.Tickets);
        Assert.Contains(child.Steps, step =>
            step.Step == "Epic" && step.Succeeded && step.Detail.Contains("existing TGT-5000"));
    }

    [Fact]
    public async Task Several_children_of_one_epic_create_it_once()
    {
        var (app, _, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await PlanFor(client, "SRC-4444");

        // The same ticket twice stands in for two children of one epic: what is
        // being proved is that the epic is not made twice.
        var response = await client.PostAsJsonAsync(
            "/api/apply",
            new ApplyRequest([plan, plan], SkipDuplicates: false, CreateEpicsFor: ["SRC-4000"]));

        response.EnsureSuccessStatusCode();

        // One epic plus two children, not two epics.
        Assert.Equal(3, tenants.CreatedKeys.Count);
    }

    [Fact]
    public async Task An_epic_nobody_approved_leaves_the_copy_unparented_and_says_so()
    {
        var (app, stub, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        // No CreateEpicsFor: the epic is missing and stays missing.
        var applied = await Apply(client, await PlanFor(client, "SRC-4444"));

        Assert.Single(tenants.CreatedKeys);

        var fields = (JsonObject)JsonNode.Parse(CreateCall(stub).Body!)!["fields"]!;
        Assert.DoesNotContain("parent", fields.Select(f => f.Key));

        var ticket = Assert.Single(applied.Tickets);
        Assert.Equal(ApplyStatus.CreatedWithProblems, ticket.Status);
        Assert.Contains(ticket.Steps, step =>
            step.Step == "Epic" && !step.Succeeded && step.Detail.Contains("not selected"));
    }

    // ---------------------------------------------------------------- failures

    [Fact]
    public async Task A_failed_create_leaves_nothing_behind_and_reports_why()
    {
        var (app, stub, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await PlanFor(client);
        tenants.FailCreate = true;

        var applied = await Apply(client, plan);

        var ticket = Assert.Single(applied.Tickets);
        Assert.Equal(ApplyStatus.Failed, ticket.Status);
        Assert.Null(ticket.TargetKey);
        Assert.Contains("customfield_70005", ticket.Summary);
    }

    [Fact]
    public async Task A_failure_after_the_create_is_reported_without_pretending_the_copy_does_not_exist()
    {
        var (app, stub, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await PlanFor(client);
        tenants.FailComments = true;

        var applied = await Apply(client, plan);

        var ticket = Assert.Single(applied.Tickets);
        Assert.Equal(ApplyStatus.CreatedWithProblems, ticket.Status);
        Assert.Equal("TGT-9001", ticket.TargetKey);
        Assert.Contains(ticket.Steps, step => step.Step == "Comments" && !step.Succeeded);
    }

    [Fact]
    public async Task One_ticket_failing_does_not_sink_the_rest_of_the_batch()
    {
        // Tickets are independent, unlike a cherry-pick sequence where one
        // conflict strands everything after it.
        var (app, stub, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var good = await PlanFor(client);
        var doomed = good with { SourceKey = "SRC-9999" };

        tenants.MissingSourceKeys.Add("SRC-9999");

        var applied = await Apply(client, doomed, good);

        Assert.Equal(2, applied.Tickets.Count);

        Assert.Equal(ApplyStatus.Failed, applied.Tickets[0].Status);
        Assert.Equal(ApplyStatus.Created, applied.Tickets[1].Status);
        Assert.Equal("TGT-9001", applied.Tickets[1].TargetKey);
    }

    [Fact]
    public async Task Applying_needs_both_tenants_credentials()
    {
        var tenants = new FakeTenants();
        var stub = new StubAtlassian(tenants.Handler);
        using var app = new TestApp(stub);
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/apply", new ApplyRequest([]));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(stub.Requests);
    }

    // ---------------------------------------------------------------- users

    [Fact]
    public async Task An_unknown_reporter_falls_back_to_the_running_account()
    {
        // Expect this to be the normal path: most SRC reporters have no
        // target-site account at all.
        var (app, stub, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var applied = await Apply(client, await PlanFor(client));

        var fields = JsonNode.Parse(CreateCall(stub).Body!)!["fields"]!;
        Assert.Equal("acc-running", fields["reporter"]!["accountId"]!.GetValue<string>());

        Assert.Contains(applied.Tickets[0].Steps, step =>
            step.Step == "Reporter" && step.Detail.Contains("Fell back to the running account"));
    }

    [Fact]
    public async Task A_reporter_who_exists_on_both_tenants_keeps_their_identity()
    {
        var (app, stub, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        tenants.KnownTargetAccountIds.Add("acc-1");

        await Apply(client, await PlanFor(client));

        var fields = JsonNode.Parse(CreateCall(stub).Body!)!["fields"]!;
        Assert.Equal("acc-1", fields["reporter"]!["accountId"]!.GetValue<string>());
    }
}
