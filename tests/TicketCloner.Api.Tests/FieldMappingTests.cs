using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TicketCloner.Api.Configuration;
using TicketCloner.Api.Contracts;
using TicketCloner.Api.Mapping;

namespace TicketCloner.Api.Tests;

/// <summary>
/// The mapping rules, exercised with no HTTP at all. The mapper takes a
/// source-issue DTO and a create screen and knows nothing about tenants, which
/// is exactly what makes this possible.
/// </summary>
public class FieldMappingTests
{
    private static FieldMapper Mapper(MappingOptions? options = null) =>
        new(Options.Create(options ?? new MappingOptions()));

    private static readonly TargetIssueType Bug = new("10001", "Bug", false);

    private static SourceIssue Issue(
        string issueType = "Bug",
        string? priority = null,
        SourceUser? reporter = null,
        params SourceFieldValue[] fields) =>
        new(
            Key: "SRC-1234",
            Url: "https://source.example.invalid/browse/SRC-1234",
            IssueType: issueType,
            Summary: "Example portal rejects valid input",
            Status: "Open",
            Priority: priority,
            Reporter: reporter,
            Assignee: null,
            Created: DateTimeOffset.UtcNow,
            Updated: DateTimeOffset.UtcNow,
            Labels: [],
            Parent: null,
            Description: null,
            Fields: fields,
            Comments: [],
            Attachments: []);

    private static TargetField Field(
        string id,
        string name,
        string? type = "string",
        string? custom = null,
        bool required = false,
        bool hasDefault = false,
        bool isArray = false,
        params AllowedValue[] allowed) =>
        new(id, name, required, hasDefault, type, custom, isArray, allowed);

    private static readonly TargetField Summary = Field("summary", "Summary", required: true);

    private static MappingPlan Plan(FieldMapper mapper, SourceIssue issue, params TargetField[] createScreen) =>
        mapper.Build(issue, "TGT", "Target Project (TGT)", Bug, "chosen for the test", createScreen);

    private static MappingRow Row(MappingPlan plan, string name) =>
        plan.Rows.Single(row => row.Name == name);

    // ------------------------------------------------------------ structural

    [Fact]
    public void Project_and_issue_type_are_supplied_by_the_plan_and_never_block()
    {
        // The live TGT create screen lists both as required with no default.
        // Neither is read from the source and neither was ever in doubt, so a
        // plan reporting them missing blocks every ticket in the project for
        // no reason - which is exactly what it did.
        var plan = Plan(Mapper(), Issue(),
            Summary,
            Field("project", "Project", type: "project", required: true),
            Field("issuetype", "Issue Type", type: "issuetype", required: true));

        Assert.True(plan.CanCreate);
        Assert.Empty(plan.Blockers);
        Assert.Equal(MappingStatus.Mapped, Row(plan, "Project").Status);
        Assert.Equal(MappingStatus.Mapped, Row(plan, "Issue Type").Status);
    }

    [Fact]
    public void The_project_row_names_the_destination_rather_than_only_keying_it()
    {
        // The tenant names are inverted: 'TGT' alone reads as the other site.
        var row = Row(Plan(Mapper(), Issue(), Summary), "Project");

        Assert.Equal("Target Project (TGT)", row.SourceValue?.GetValue<string>());
        Assert.Equal("TGT", row.MappedValue?["key"]?.GetValue<string>());
    }

    [Fact]
    public void The_issue_type_row_carries_the_source_type_and_the_id_that_gets_written()
    {
        var row = Row(Plan(Mapper(), Issue(issueType: "New Feature"), Summary), "Issue Type");

        Assert.Equal("New Feature", row.SourceValue?.GetValue<string>());
        Assert.Equal("10001", row.MappedValue?["id"]?.GetValue<string>());
        Assert.Equal("chosen for the test", row.Reason);
    }

    // ------------------------------------------------------------ issue type

    [Fact]
    public void An_exact_name_match_is_used_when_no_rule_covers_the_type()
    {
        var resolution = Mapper().ResolveIssueType("Bug", [Bug, new("10002", "Story", false)]);

        Assert.Equal("10001", resolution.IssueType?.Id);
        Assert.Contains("same name", resolution.Reason);
    }

    [Fact]
    public void A_configured_rule_beats_an_exact_name_match()
    {
        var mapper = Mapper(new MappingOptions
        {
            IssueTypes = new(StringComparer.OrdinalIgnoreCase) { ["Bug"] = "Story" },
        });

        var resolution = mapper.ResolveIssueType("Bug", [Bug, new("10002", "Story", false)]);

        Assert.Equal("Story", resolution.IssueType?.Name);
    }

    [Theory]
    [InlineData("Epic", "Epic")]
    [InlineData("Story", "Story")]
    [InlineData("Bug", "Bug")]
    [InlineData("Task", "Task")]
    [InlineData("Sub-task", "Sub-task")]
    [InlineData("Improvement", "Task")]
    [InlineData("New Feature", "Task")]
    [InlineData("Production Issue", "Task")]
    [InlineData("Config Change", "Task")]
    [InlineData("Something nobody has invented yet", "Task")]
    public void The_shared_types_match_by_name_and_everything_else_becomes_a_task(
        string source, string expected)
    {
        // The shipped rule, as configured in appsettings.json. The five types
        // both tenants share keep their identity; the four Phase 0 found with
        // no counterpart - and anything added later - become Task rather than
        // failing the plan.
        var mapper = Mapper(new MappingOptions { FallbackIssueType = "Task" });

        var available = new List<TargetIssueType>
        {
            new("10000", "Epic", false), new("10002", "Story", false), Bug,
            new("3", "Task", false), new("5", "Sub-task", true),
        };

        Assert.Equal(expected, mapper.ResolveIssueType(source, available).IssueType?.Name);
    }

    [Fact]
    public void An_explicit_rule_still_beats_the_fallback()
    {
        // The fallback is the blanket rule, not the only one: a type that needs
        // to land somewhere specific is still a one-line config entry.
        var mapper = Mapper(new MappingOptions
        {
            IssueTypes = new(StringComparer.OrdinalIgnoreCase) { ["Production Issue"] = "Bug" },
            FallbackIssueType = "Task",
        });

        var resolution = mapper.ResolveIssueType("Production Issue", [Bug, new("3", "Task", false)]);

        Assert.Equal("Bug", resolution.IssueType?.Name);
        Assert.Contains("by configuration", resolution.Reason);
    }

    [Fact]
    public void An_unmapped_type_fails_rather_than_guessing()
    {
        var resolution = Mapper().ResolveIssueType("Config Change", [Bug]);

        Assert.Null(resolution.IssueType);
        Assert.Contains("no mapping rule covers it", resolution.Reason);
    }

    [Fact]
    public void A_rule_pointing_at_an_uncreatable_type_says_what_can_be_created()
    {
        // Createmeta lists what the account can actually create, which is
        // narrower than the project's issue types: a type can exist on the
        // project and still be missing here. Naming the alternatives turns a
        // dead end into a one-line config fix.
        var mapper = Mapper(new MappingOptions
        {
            IssueTypes = new(StringComparer.OrdinalIgnoreCase) { ["New Feature"] = "Story" },
        });

        var resolution = mapper.ResolveIssueType("New Feature", [Bug, new("10003", "Task", false)]);

        Assert.Null(resolution.IssueType);
        Assert.Contains("cannot create an issue type of that name", resolution.Reason);
        Assert.Contains("Creatable there: Bug, Task", resolution.Reason);
    }

    [Fact]
    public void An_empty_create_screen_is_reported_as_a_permissions_problem()
    {
        var resolution = Mapper().ResolveIssueType("Bug", []);

        Assert.Null(resolution.IssueType);
        Assert.Contains("lacks Create Issues", resolution.Reason);
    }

    // ------------------------------------------------------------ fields

    [Fact]
    public void A_field_absent_from_the_create_screen_is_dropped_not_sent()
    {
        // Sending it would be a hard 400, so it is dropped and reported.
        var issue = Issue(fields: new SourceFieldValue(
            "customfield_70011", "Workaround", true, "string",
            "com.atlassian.jira.plugin.system.customfieldtypes:textfield",
            JsonValue.Create("Enter the example value without spaces")));

        var plan = Plan(Mapper(), issue, Summary);

        var row = Row(plan, "Workaround");
        Assert.Equal(MappingStatus.Dropped, row.Status);
        Assert.Contains("No field of this name", row.Reason);
        Assert.Null(row.MappedValue);
    }

    [Fact]
    public void A_name_match_with_a_differing_type_is_unmappable_not_mapped()
    {
        // The dangerous case. It looks mappable and would either 400 or write
        // the wrong shape of value.
        var issue = Issue(fields: new SourceFieldValue(
            "customfield_70011", "Workaround", true, "string",
            "com.atlassian.jira.plugin.system.customfieldtypes:textfield",
            JsonValue.Create("restart the service")));

        var plan = Plan(Mapper(), issue, Summary,
            Field("customfield_70006", "Workaround",
                custom: "com.atlassian.jira.plugin.system.customfieldtypes:textarea"));

        var row = Row(plan, "Workaround");
        Assert.Equal(MappingStatus.Unmappable, row.Status);
        Assert.Contains("the type differs", row.Reason);
        Assert.Null(row.MappedValue);
    }

    [Theory]
    // Both sides carry the same greenhopper type, so every check the mapper
    // makes says yes - and the value is an issue key belonging to the source.
    [InlineData("Epic Link", "any", "com.pyxis.greenhopper.jira:gh-epic-link")]
    // The source reports an array of issue links where the target takes one.
    [InlineData("Parent", "array", null)]
    public void A_reference_to_an_issue_on_the_other_tenant_is_never_mapped(
        string name, string type, string? custom)
    {
        // Seen live: Epic Link mapped clean and would have written "SRC-6942"
        // onto the copy - a key that names nothing on the target, or names
        // something else entirely.
        var source = new SourceFieldValue("customfield_70004", name, true, type, custom,
            JsonValue.Create("SRC-6942"));

        var plan = Plan(Mapper(), Issue(fields: source),
            Summary,
            Field("customfield_70003", name, type: type, custom: custom));

        var row = Row(plan, name);
        Assert.Equal(MappingStatus.Dropped, row.Status);
        Assert.Null(row.TargetFieldId);
        Assert.Null(row.MappedValue);
    }

    [Theory]
    // The two Phase 0 found, neither of them the target's type.
    [InlineData("string", "com.atlassian.jira.plugin.system.customfieldtypes:textfield")]
    [InlineData("option", "com.atlassian.jira.plugin.system.customfieldtypes:select")]
    // The third it missed, which IS the target's type - so the name matches,
    // the type agrees, every check says yes, and the create comes back 400
    // "Specify a valid value for Sprint". Seen live copying a real source issue.
    [InlineData("json", "com.pyxis.greenhopper.jira:gh-sprint")]
    public void Sprint_is_dropped_whatever_type_the_source_carries(string type, string custom)
    {
        // Matching on type is not enough here and never could be: a sprint id
        // belongs to the tenant that issued it, so there is no correct value to
        // map it to. The only safe answer is to drop it and say so.
        var issue = Issue(fields: new SourceFieldValue(
            "customfield_70003", "Sprint", true, type, custom, JsonValue.Create("Sprint 42")));

        var plan = Plan(Mapper(), issue, Summary,
            Field("customfield_70002", "Sprint", type: "json",
                custom: "com.pyxis.greenhopper.jira:gh-sprint", isArray: true));

        var row = Row(plan, "Sprint");
        Assert.Equal(MappingStatus.Dropped, row.Status);
        Assert.Null(row.TargetFieldId);
        Assert.Null(row.MappedValue);
        Assert.Contains("belongs to the tenant that issued it", row.Reason);
    }

    [Fact]
    public void A_matching_name_and_type_carries_over()
    {
        var issue = Issue(fields: new SourceFieldValue(
            "customfield_70010", "Story point estimate", true, "number",
            "com.pyxis.greenhopper.jira:jsw-story-points", JsonValue.Create(5)));

        var plan = Plan(Mapper(), issue, Summary,
            Field("customfield_70007", "Story point estimate", "number",
                "com.pyxis.greenhopper.jira:jsw-story-points"));

        var row = Row(plan, "Story point estimate");
        Assert.Equal(MappingStatus.Mapped, row.Status);

        // Mapped onto the TARGET's field id, not the source's.
        Assert.Equal("customfield_70007", row.TargetFieldId);
        Assert.Equal(5, row.MappedValue!.GetValue<int>());
    }

    [Fact]
    public void Select_options_are_matched_by_name_and_the_id_is_regenerated()
    {
        // Option ids are per-tenant. Carrying the source's id across would
        // either 400 or select something unrelated.
        var issue = Issue(fields: new SourceFieldValue(
            "customfield_70009", "LW Severity", true, "option",
            "com.atlassian.jira.plugin.system.customfieldtypes:select",
            new JsonObject { ["id"] = "99999", ["value"] = "Major" }));

        var plan = Plan(Mapper(), issue, Summary,
            Field("customfield_70015", "LW Severity", "option",
                "com.atlassian.jira.plugin.system.customfieldtypes:select",
                allowed: [new AllowedValue("31", "Minor"), new AllowedValue("32", "Major")]));

        var row = Row(plan, "LW Severity");
        Assert.Equal(MappingStatus.Mapped, row.Status);
        Assert.Equal("32", row.MappedValue!["id"]!.GetValue<string>());
    }

    [Fact]
    public void An_option_with_no_counterpart_reports_what_is_allowed()
    {
        var issue = Issue(fields: new SourceFieldValue(
            "customfield_70009", "LW Severity", true, "option",
            "com.atlassian.jira.plugin.system.customfieldtypes:select",
            new JsonObject { ["value"] = "Catastrophic" }));

        var plan = Plan(Mapper(), issue, Summary,
            Field("customfield_70015", "LW Severity", "option",
                "com.atlassian.jira.plugin.system.customfieldtypes:select",
                allowed: [new AllowedValue("31", "Minor"), new AllowedValue("32", "Major")]));

        var row = Row(plan, "LW Severity");
        Assert.Equal(MappingStatus.Unmappable, row.Status);
        Assert.Contains("'Catastrophic'", row.Reason);
        Assert.Equal(["Minor", "Major"], row.AllowedValues);
    }

    [Fact]
    public void Multi_selects_map_every_option()
    {
        var issue = Issue(fields: new SourceFieldValue(
            "customfield_70001", "Applies to", true, "option",
            "com.atlassian.jira.plugin.system.customfieldtypes:multicheckboxes",
            new JsonArray(new JsonObject { ["value"] = "Web" }, new JsonObject { ["value"] = "Mobile" })));

        var plan = Plan(Mapper(), issue, Summary,
            Field("customfield_70014", "Applies to", "option",
                "com.atlassian.jira.plugin.system.customfieldtypes:multicheckboxes",
                isArray: true,
                allowed: [new AllowedValue("1", "Web"), new AllowedValue("2", "Mobile")]));

        var row = Row(plan, "Applies to");
        Assert.Equal(MappingStatus.Mapped, row.Status);

        var mapped = Assert.IsType<JsonArray>(row.MappedValue);
        Assert.Equal(["1", "2"], mapped.Select(item => item!["id"]!.GetValue<string>()));
    }

    [Fact]
    public void Fields_that_cannot_be_set_on_create_are_dropped()
    {
        var issue = Issue(fields: new SourceFieldValue(
            "resolution", "Resolution", false, "resolution", null,
            new JsonObject { ["name"] = "Fixed" }));

        var plan = Plan(Mapper(), issue, Summary, Field("resolution", "Resolution", "resolution"));

        var row = Row(plan, "Resolution");
        Assert.Equal(MappingStatus.Dropped, row.Status);
        Assert.Contains("Not settable when an issue is created", row.Reason);
    }

    // ------------------------------------------------------------ priority

    [Fact]
    public void Priority_none_maps_to_medium_by_configuration()
    {
        var mapper = Mapper(new MappingOptions
        {
            Priorities = new(StringComparer.OrdinalIgnoreCase) { ["None"] = "Medium" },
        });

        var plan = Plan(mapper, Issue(priority: "None"), Summary,
            Field("priority", "Priority", "priority",
                allowed: [new AllowedValue("3", "Medium"), new AllowedValue("2", "High")]));

        var row = Row(plan, "Priority");
        Assert.Equal(MappingStatus.Mapped, row.Status);
        Assert.Equal("3", row.MappedValue!["id"]!.GetValue<string>());
        Assert.Contains("mapped to 'Medium' by configuration", row.Reason);
    }

    [Fact]
    public void An_unknown_priority_is_unmappable_and_lists_the_alternatives()
    {
        var plan = Plan(Mapper(), Issue(priority: "Blocker"), Summary,
            Field("priority", "Priority", "priority",
                allowed: [new AllowedValue("3", "Medium"), new AllowedValue("2", "High")]));

        var row = Row(plan, "Priority");
        Assert.Equal(MappingStatus.Unmappable, row.Status);
        Assert.Equal(["Medium", "High"], row.AllowedValues);
    }

    // ------------------------------------------------------------ required

    [Fact]
    public void A_required_target_field_with_no_source_value_and_no_default_blocks_the_create()
    {
        var plan = Plan(Mapper(), Issue(), Summary,
            Field("customfield_70005", "Customer", "option",
                "com.atlassian.jira.plugin.system.customfieldtypes:select",
                required: true));

        var row = Row(plan, "Customer");
        Assert.Equal(MappingStatus.MissingRequired, row.Status);

        Assert.False(plan.CanCreate);
        Assert.Contains(plan.Blockers, blocker => blocker.Contains("Mapping:Constants:Customer"));
    }

    [Fact]
    public void A_configured_constant_satisfies_a_required_field()
    {
        var mapper = Mapper(new MappingOptions
        {
            Constants = new(StringComparer.OrdinalIgnoreCase) { ["Customer"] = "TGT" },
        });

        var plan = Plan(mapper, Issue(), Summary,
            Field("customfield_70005", "Customer", "option",
                "com.atlassian.jira.plugin.system.customfieldtypes:select",
                required: true));

        var row = Row(plan, "Customer");
        Assert.Equal(MappingStatus.Mapped, row.Status);
        Assert.Equal("TGT", row.MappedValue!.GetValue<string>());
        Assert.True(plan.CanCreate);
    }

    [Fact]
    public void A_required_field_with_a_default_does_not_block()
    {
        var plan = Plan(Mapper(), Issue(), Summary,
            Field("customfield_70001", "Change type", "option", required: true, hasDefault: true));

        Assert.Equal(MappingStatus.Dropped, Row(plan, "Change type").Status);
        Assert.True(plan.CanCreate);
    }

    [Fact]
    public void An_unmappable_value_does_not_block_the_create()
    {
        // It is dropped, reported, and finished by hand. Only a missing
        // required field stops the create outright.
        var issue = Issue(priority: "Blocker");

        var plan = Plan(Mapper(), issue, Summary,
            Field("priority", "Priority", "priority", allowed: [new AllowedValue("3", "Medium")]));

        Assert.Equal(MappingStatus.Unmappable, Row(plan, "Priority").Status);
        Assert.True(plan.CanCreate);
    }

    // ------------------------------------------------------------ users

    [Fact]
    public void Reporter_carries_its_account_id_across()
    {
        var issue = Issue(reporter: new SourceUser("acc-1", "Example Reporter", null));

        var plan = Plan(Mapper(), issue, Summary, Field("reporter", "Reporter", "user"));

        var row = Row(plan, "Reporter");
        Assert.Equal(MappingStatus.Mapped, row.Status);
        Assert.Equal("acc-1", row.MappedValue!["accountId"]!.GetValue<string>());
        Assert.Contains("Falls back to the running account", row.Reason);
    }

    // ------------------------------------------------------------ ordering

    [Fact]
    public void Rows_that_need_attention_come_first()
    {
        var issue = Issue(priority: "Blocker", fields: new SourceFieldValue(
            "customfield_70010", "Story point estimate", true, "number",
            "com.pyxis.greenhopper.jira:jsw-story-points", JsonValue.Create(5)));

        var plan = Plan(Mapper(), issue, Summary,
            Field("priority", "Priority", "priority", allowed: [new AllowedValue("3", "Medium")]),
            Field("customfield_70007", "Story point estimate", "number",
                "com.pyxis.greenhopper.jira:jsw-story-points"),
            Field("customfield_70005", "Customer", "option", required: true));

        Assert.Equal(MappingStatus.MissingRequired, plan.Rows[0].Status);
        Assert.Equal(MappingStatus.Unmappable, plan.Rows[1].Status);
    }
}
