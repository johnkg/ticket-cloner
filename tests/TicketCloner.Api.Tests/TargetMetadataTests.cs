using System.Net;
using System.Net.Http.Json;
using TicketCloner.Api.Atlassian;
using TicketCloner.Api.Contracts;
using TicketCloner.Api.Tests.Infrastructure;

namespace TicketCloner.Api.Tests;

public class TargetMetadataTests
{
    private static Dictionary<string, string?> Mapping() => new()
    {
        ["Mapping:Priorities:None"] = "Medium",
    };

    private static (TestApp App, StubAtlassian Stub) Sut()
    {
        var stub = new StubAtlassian(BothTenantsFake.Handler);
        return (new TestApp(stub, Mapping(), signedIn: true), stub);
    }

    [Fact]
    public async Task Issue_types_are_read_from_the_target_create_meta()
    {
        var (app, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var types = await client.GetFromJsonAsync<List<TargetIssueType>>("/api/target/issuetypes");

        Assert.NotNull(types);
        Assert.Equal(["Bug", "Story", "Task", "Epic"], types.Select(type => type.Name));
    }

    [Fact]
    public async Task The_create_screen_is_read_across_every_page()
    {
        // createmeta pages and defaults to 50. Without the loop a project this
        // size is silently truncated, which understates the create screen and
        // makes the mapper drop fields that were actually available.
        var (app, stub) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var fields = await client.GetFromJsonAsync<List<TargetField>>("/api/target/issuetypes/10001/fields");

        Assert.NotNull(fields);
        Assert.Equal(14, fields.Count);

        // Page two only, so its absence would mean the loop never ran.
        Assert.Contains(fields, field => field.Name == "Customer");
        Assert.Equal(2, stub.Requests.Count(request => request.Path.Contains("/createmeta/")));
    }

    [Fact]
    public async Task Array_fields_report_their_element_type_not_just_array()
    {
        var (app, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var fields = await client.GetFromJsonAsync<List<TargetField>>("/api/target/issuetypes/10001/fields");

        var labels = fields!.Single(field => field.FieldId == "labels");
        Assert.True(labels.IsArray);
        Assert.Equal("string", labels.SchemaType);
    }

    [Fact]
    public async Task Select_options_are_read_from_value_as_well_as_name()
    {
        // System fields say "name"; custom select options say "value".
        var (app, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var fields = await client.GetFromJsonAsync<List<TargetField>>("/api/target/issuetypes/10001/fields");
        Assert.NotNull(fields);

        var customerField = fields.Single(field => field.Name == "Customer");
        Assert.Equal(["TGT", "Example Customer"], customerField.AllowedValues.Select(allowed => allowed.Name));

        var priority = fields.Single(field => field.FieldId == "priority");
        Assert.Contains(priority.AllowedValues, allowed => allowed.Name == "Medium");
    }

    [Fact]
    public async Task Issue_types_come_from_issueTypes_and_fields_from_fields()
    {
        // The two sibling createmeta endpoints do not agree with each other,
        // and neither uses the "values" that most Jira paginated endpoints do.
        var (app, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var types = await client.GetFromJsonAsync<List<TargetIssueType>>("/api/target/issuetypes");
        var fields = await client.GetFromJsonAsync<List<TargetField>>("/api/target/issuetypes/10001/fields");

        Assert.NotEmpty(types!);
        Assert.NotEmpty(fields!);
    }

    [Fact]
    public async Task An_unrecognised_page_shape_fails_loudly_instead_of_reading_as_empty()
    {
        // This is the defect that reached production use: reading the wrong
        // property returned an empty list with no error, which downstream looks
        // exactly like "this project has no issue types" and produced a plan
        // that was tidy and wrong.
        var stub = new StubAtlassian(request =>
            request.RequestUri!.AbsolutePath.Contains("/createmeta/")
                ? StubAtlassian.Json("""{"startAt":0,"maxResults":50,"total":9,"values":[]}""")
                : BothTenantsFake.Handler(request));

        using var app = new TestApp(stub, Mapping(), signedIn: true);
        using var client = app.CreateClient();

        var response = await client.GetAsync("/api/target/issuetypes");

        Assert.False(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Target_metadata_requires_target_credentials()
    {
        var stub = new StubAtlassian(BothTenantsFake.Handler);
        using var app = new TestApp(stub);
        using var client = app.CreateClient();

        var response = await client.GetAsync("/api/target/issuetypes");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(stub.Requests);
    }
}

public class PreviewTests
{
    private static Dictionary<string, string?> Configured(params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Mapping:Priorities:None"] = "Medium",
        };

        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        return settings;
    }

    private static (TestApp App, StubAtlassian Stub) Sut(params (string Key, string Value)[] extra)
    {
        var stub = new StubAtlassian(BothTenantsFake.Handler);
        return (new TestApp(stub, Configured(extra), signedIn: true), stub);
    }

    [Fact]
    public async Task A_plan_reads_both_tenants_and_writes_to_neither()
    {
        var (app, stub) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await client.GetFromJsonAsync<MappingPlan>("/api/preview/SRC-1234");

        Assert.NotNull(plan);
        Assert.Equal("SRC-1234", plan.SourceKey);
        Assert.Equal("TGT", plan.TargetProjectKey);
        Assert.Equal("Bug", plan.TargetIssueTypeName);

        // Every outbound call was a read.
        Assert.All(stub.Requests, request =>
            Assert.True(request.Method == HttpMethod.Get || request.Path.Contains("/search/jql")));
    }

    [Fact]
    public async Task Project_and_issue_type_do_not_block_a_plan()
    {
        // Against a create screen shaped like the live one, where both are
        // required with no default. The plan supplies both, so neither may
        // reach the blocker list - this blocked every ticket in the project.
        var (app, _) = Sut(("Mapping:Constants:Customer", "TGT"));
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await client.GetFromJsonAsync<MappingPlan>("/api/preview/SRC-1234");

        Assert.True(plan!.CanCreate);
        Assert.Equal("Target Project (TGT)", plan.TargetProjectName);
        Assert.All(
            plan.Rows.Where(row => row.Name is "Project" or "Issue Type"),
            row => Assert.Equal(MappingStatus.Mapped, row.Status));
    }

    [Fact]
    public async Task Sprint_is_dropped_even_though_both_tenants_agree_on_its_type()
    {
        // Both sides are com.pyxis.greenhopper.jira:gh-sprint, so the type
        // check passes and the value looks mappable. It is not: the id names a
        // sprint on the SOURCE board, and the target answers the create with
        // 400 "Specify a valid value for Sprint". Seen live on a real source issue.
        var (app, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await client.GetFromJsonAsync<MappingPlan>("/api/preview/SRC-1234");

        var row = plan!.Rows.Single(row => row.Name == "Sprint");
        Assert.Equal(MappingStatus.Dropped, row.Status);
        Assert.Null(row.TargetFieldId);
        Assert.Contains("belongs to the tenant that issued it", row.Reason);
    }

    [Fact]
    public async Task The_workaround_type_clash_shows_up_as_unmappable()
    {
        // Real Phase 0 finding: source Workaround is a textfield, target
        // Workaround is a textarea.
        var (app, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await client.GetFromJsonAsync<MappingPlan>("/api/preview/SRC-1234");

        var row = plan!.Rows.Single(row => row.Name == "Workaround");
        Assert.Equal(MappingStatus.Unmappable, row.Status);
        Assert.Contains("the type differs", row.Reason);
    }

    [Fact]
    public async Task Story_points_carry_over_onto_the_targets_own_field_id()
    {
        var (app, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await client.GetFromJsonAsync<MappingPlan>("/api/preview/SRC-1234");

        var row = plan!.Rows.Single(row => row.Name == "Story point estimate");
        Assert.Equal(MappingStatus.Mapped, row.Status);
        Assert.Equal("customfield_70010", row.SourceFieldId);
        Assert.Equal("customfield_70007", row.TargetFieldId);
    }

    [Fact]
    public async Task A_required_field_with_no_source_value_blocks_the_plan()
    {
        var (app, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await client.GetFromJsonAsync<MappingPlan>("/api/preview/SRC-1234");

        Assert.False(plan!.CanCreate);
        Assert.Contains(plan.Blockers, blocker => blocker.Contains("Customer"));
    }

    [Fact]
    public async Task Configuring_the_constant_unblocks_the_plan()
    {
        var (app, _) = Sut(("Mapping:Constants:Customer", "TGT"));
        using var _app = app;
        using var client = app.CreateClient();

        var plan = await client.GetFromJsonAsync<MappingPlan>("/api/preview/SRC-1234");

        Assert.True(plan!.CanCreate);
        Assert.Empty(plan.Blockers);
    }

    [Fact]
    public async Task An_unknown_issue_type_is_refused_rather_than_guessed()
    {
        var (app, _) = Sut();
        using var _app = app;
        using var client = app.CreateClient();

        var response = await client.GetAsync("/api/preview/SRC-1234?issueType=Spike");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("no creatable issue type named 'Spike'", body);

        // And it says what could have been used, so the fix is a config edit
        // rather than another round of guessing.
        Assert.Contains("Creatable there: Bug, Story, Task", body);
    }

    [Fact]
    public async Task Previewing_needs_a_grant_that_reaches_both_tenants()
    {
        // Source only. Preview reads one tenant and writes to neither, but it
        // needs the target's create screen, so half a grant is not enough.
        var stub = new StubAtlassian(BothTenantsFake.Handler);
        using var app = new TestApp(stub, signedIn: true, grants: [Tenant.Source]);
        using var client = app.CreateClient();

        var response = await client.GetAsync("/api/preview/SRC-1234");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(stub.Requests);
    }
}
