using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using TicketCloner.Api.Contracts;
using TicketCloner.Api.Tests.Infrastructure;

namespace TicketCloner.Api.Tests;

public class ApplyTests
{
    private static Dictionary<string, string?> Configured() => new()
    {
        ["Credentials:SourceEmail"] = "source-account@example.com",
        ["Credentials:SourceApiToken"] = "source-only-token",
        ["Credentials:TargetEmail"] = "target-account@example.com",
        ["Credentials:TargetApiToken"] = "target-only-token",
        ["Mapping:Priorities:None"] = "Medium",
        ["Mapping:Constants:YOUR_COMPANY Client"] = "TARGET_PROJECT",
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

        return (new TestApp(stub, settings), stub, tenants);
    }

    private static async Task<MappingPlan> PlanFor(HttpClient client, string key = "SOURCE_PROJECT-1234")
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
        Assert.Equal("TARGET_PROJECT-9001", ticket.TargetKey);
        Assert.Equal("https://target-domain.atlassian.net/browse/TARGET_PROJECT-9001", ticket.TargetUrl);
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
        Assert.Equal("TARGET_PROJECT", body["project"]!["key"]!.GetValue<string>());
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
        Assert.DoesNotContain("customfield_11318", fields.Select(field => field.Key));
        Assert.Contains("customfield_11512", fields.Select(field => field.Key));
    }

    [Fact]
    public async Task The_source_key_is_written_to_the_provenance_field()
    {
        // A remote link would be the natural home for this, but remote links
        // are not JQL-searchable and this is the duplicate check.
        var (app, stub, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        await Apply(client, await PlanFor(client));

        var fields = JsonNode.Parse(CreateCall(stub).Body!)!["fields"]!;
        Assert.Equal("SOURCE_PROJECT-1234", fields["customfield_15000"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_missing_provenance_field_is_called_out_and_disables_the_duplicate_check()
    {
        // Without it there is no JQL-searchable record of where a copy came
        // from, so a second run would duplicate everything silently.
        var tenants = new FakeTenants();
        var stub = new StubAtlassian(tenants.Handler);

        var settings = Configured();
        settings["Atlassian:Target:ProvenanceFieldName"] = "Not A Real Field";

        using var app = new TestApp(stub, settings);
        using var client = app.CreateClient();

        var applied = await Apply(client, await PlanFor(client));

        Assert.Contains(applied.Tickets[0].Steps, step =>
            step.Step == "Provenance" && !step.Succeeded &&
            step.Detail.Contains("duplicate check cannot run"));

        Assert.DoesNotContain(stub.Requests, request => request.Path.EndsWith("/search/jql"));
    }

    [Fact]
    public async Task A_remote_link_points_back_at_the_source_issue()
    {
        var (app, stub, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        await Apply(client, await PlanFor(client));

        var remoteLink = stub.Requests.Single(request => request.Path.EndsWith("/remotelink"));
        Assert.Contains("https://source-domain.atlassian.net/browse/SOURCE_PROJECT-1234", remoteLink.Body);
    }

    // ---------------------------------------------------------------- duplicates

    [Fact]
    public async Task An_already_copied_ticket_is_skipped_rather_than_duplicated()
    {
        var (app, stub, tenants) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await PlanFor(client);
        tenants.ExistingCopyKey = "TARGET_PROJECT-8888";

        var applied = await Apply(client, plan);

        var ticket = Assert.Single(applied.Tickets);
        Assert.Equal(ApplyStatus.Skipped, ticket.Status);
        Assert.Contains("TARGET_PROJECT-8888", ticket.Summary);

        Assert.DoesNotContain(stub.Requests, request =>
            request.Method == HttpMethod.Post &&
            request.Path.EndsWith("/rest/api/3/issue", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_plan_with_blockers_is_refused_even_if_it_is_sent_back()
    {
        var tenants = new FakeTenants();
        var stub = new StubAtlassian(tenants.Handler);

        // No constant configured, so 'YOUR_COMPANY Client' blocks the plan.
        var settings = Configured();
        settings.Remove("Mapping:Constants:YOUR_COMPANY Client");

        using var app = new TestApp(stub, settings);
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
        Assert.Contains("Ray Tester wrote on 11/08/2026", comments[0].Body);
        Assert.Contains("Dev Person wrote on 12/08/2026", comments[1].Body);
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
            request.Host == "source-domain.atlassian.net" &&
            request.Path.Contains("/attachment/content/50021"));

        Assert.Contains(stub.Requests, request =>
            request.Host == "target-domain.atlassian.net" &&
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
        Assert.Equal("TARGET_PROJECT-9001", ticket.TargetKey);
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
            "https://source-domain.atlassian.net/browse/SOURCE_PROJECT-1234",
            fields["customfield_11595"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_source_url_field_and_the_provenance_field_are_not_the_same_field()
    {
        // The provenance field holds the bare key because the duplicate check
        // searches it; a URL there would match far too loosely.
        var (app, stub, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        await Apply(client, await PlanFor(client));

        var fields = JsonNode.Parse(CreateCall(stub).Body!)!["fields"]!;

        Assert.Equal("SOURCE_PROJECT-1234", fields["customfield_15000"]!.GetValue<string>());
        Assert.Contains("browse/SOURCE_PROJECT-1234", fields["customfield_11595"]!.GetValue<string>());
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
        Assert.Contains("customfield_10300", ticket.Summary);
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
        Assert.Equal("TARGET_PROJECT-9001", ticket.TargetKey);
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
        var doomed = good with { SourceKey = "SOURCE_PROJECT-9999" };

        tenants.MissingSourceKeys.Add("SOURCE_PROJECT-9999");

        var applied = await Apply(client, doomed, good);

        Assert.Equal(2, applied.Tickets.Count);

        Assert.Equal(ApplyStatus.Failed, applied.Tickets[0].Status);
        Assert.Equal(ApplyStatus.Created, applied.Tickets[1].Status);
        Assert.Equal("TARGET_PROJECT-9001", applied.Tickets[1].TargetKey);
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
        // Expect this to be the normal path: most SOURCE_PROJECT reporters have no
        // your-company account at all.
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
