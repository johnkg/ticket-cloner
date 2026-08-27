using System.Net;

namespace TicketCloner.Api.Tests.Infrastructure;

/// <summary>
/// The TGT project's create screen on target-site, shaped like the real one:
/// a Workaround field whose type differs from the source's, a required select
/// with no default, project and issuetype required exactly as the live one
/// reports them, and enough fields to force createmeta's paging.
/// </summary>
public static class TargetTenantFake
{
    public static Func<HttpRequestMessage, HttpResponseMessage> Handler => request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;

        // Preview now asks whether a copy already exists. Nothing here by
        // default; FakeTenants is where a test says otherwise.
        if (path.EndsWith("/search/jql", StringComparison.OrdinalIgnoreCase))
        {
            return StubAtlassian.Json("""{"issues":[],"isLast":true}""");
        }

        if (path.Contains("/createmeta/", StringComparison.OrdinalIgnoreCase))
        {
            // .../issuetypes/{id} is the field list; .../issuetypes is the list
            // of types.
            var isFieldList = path.TrimEnd('/').Split('/') is [.., "issuetypes", _];

            if (!isFieldList)
            {
                return StubAtlassian.Json(IssueTypes);
            }

            // Deliberately two pages: the endpoint defaults to 50 per page and
            // a project this size is silently truncated without the loop.
            return StubAtlassian.Json(query.Contains("startAt=0") ? FieldsPageOne : FieldsPageTwo);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"unexpected target path: {path}"),
        };
    };

    // Keys copied from a real TGT response: the issue-type list is under
    // "issueTypes" and the field list under "fields". Neither is "values",
    // which is what the reader used to look for - and a fake that agreed with
    // the reader is how the whole suite passed against a broken assumption.
    private const string IssueTypes =
        """
        {
          "startAt": 0,
          "maxResults": 100,
          "total": 3,
          "issueTypes": [
            { "id": "10001", "name": "Bug", "subtask": false },
            { "id": "10002", "name": "Story", "subtask": false },
            { "id": "10003", "name": "Task", "subtask": false },
            { "id": "10004", "name": "Epic", "subtask": false }
          ]
        }
        """;

    private const string FieldsPageOne =
        """
        {
          "startAt": 0,
          "maxResults": 7,
          "total": 14,
          "fields": [
            {
              "fieldId": "project",
              "name": "Project",
              "required": true,
              "hasDefaultValue": false,
              "schema": { "type": "project", "system": "project" },
              "allowedValues": [
                { "id": "20000", "key": "TGT", "name": "Target Project" }
              ]
            },
            {
              "fieldId": "issuetype",
              "name": "Issue Type",
              "required": true,
              "hasDefaultValue": false,
              "schema": { "type": "issuetype", "system": "issuetype" },
              "allowedValues": [
                { "id": "10001", "name": "Bug" }
              ]
            },
            {
              "fieldId": "parent",
              "name": "Parent",
              "required": false,
              "hasDefaultValue": false,
              "schema": { "type": "issuelink", "system": "parent" }
            },
            {
              "fieldId": "summary",
              "name": "Summary",
              "required": true,
              "hasDefaultValue": false,
              "schema": { "type": "string", "system": "summary" }
            },
            {
              "fieldId": "description",
              "name": "Description",
              "required": false,
              "hasDefaultValue": false,
              "schema": { "type": "doc", "system": "description" }
            },
            {
              "fieldId": "priority",
              "name": "Priority",
              "required": false,
              "hasDefaultValue": true,
              "schema": { "type": "priority", "system": "priority" },
              "allowedValues": [
                { "id": "1", "name": "Critical" },
                { "id": "2", "name": "High" },
                { "id": "3", "name": "Medium" },
                { "id": "4", "name": "Low" },
                { "id": "5", "name": "Trivial" }
              ]
            },
            {
              "fieldId": "reporter",
              "name": "Reporter",
              "required": false,
              "hasDefaultValue": false,
              "schema": { "type": "user", "system": "reporter" }
            }
          ]
        }
        """;

    /// <summary>
    /// Only reachable if the paging loop runs. It carries the Workaround type
    /// clash and the required field with no default, so a truncated read would
    /// produce a plan that looks clean and is wrong.
    /// </summary>
    private const string FieldsPageTwo =
        """
        {
          "startAt": 7,
          "maxResults": 7,
          "total": 14,
          "fields": [
            {
              "fieldId": "customfield_70013",
              "name": "TGT Source Key",
              "required": false,
              "hasDefaultValue": false,
              "schema": { "type": "string", "custom": "com.atlassian.jira.plugin.system.customfieldtypes:textfield" }
            },
            {
              "fieldId": "labels",
              "name": "Labels",
              "required": false,
              "hasDefaultValue": false,
              "schema": { "type": "array", "items": "string", "system": "labels" }
            },
            {
              "fieldId": "customfield_70006",
              "name": "Workaround",
              "required": false,
              "hasDefaultValue": false,
              "schema": { "type": "string", "custom": "com.atlassian.jira.plugin.system.customfieldtypes:textarea" }
            },
            {
              "fieldId": "customfield_70007",
              "name": "Story point estimate",
              "required": false,
              "hasDefaultValue": false,
              "schema": { "type": "number", "custom": "com.pyxis.greenhopper.jira:jsw-story-points" }
            },
            {
              "fieldId": "customfield_70008",
              "name": "External Issue ID",
              "required": false,
              "hasDefaultValue": false,
              "schema": { "type": "string", "custom": "com.atlassian.jira.plugin.system.customfieldtypes:textfield" }
            },
            {
              "fieldId": "customfield_70002",
              "name": "Sprint",
              "required": false,
              "hasDefaultValue": false,
              "schema": { "type": "json", "custom": "com.pyxis.greenhopper.jira:gh-sprint" }
            },
            {
              "fieldId": "customfield_70005",
              "name": "Customer",
              "required": true,
              "hasDefaultValue": false,
              "schema": { "type": "option", "custom": "com.atlassian.jira.plugin.system.customfieldtypes:select" },
              "allowedValues": [
                { "id": "20001", "value": "TGT" },
                { "id": "20002", "value": "Example Customer" }
              ]
            }
          ]
        }
        """;
}

/// <summary>Routes by tenant, so one handler can stand in for both.</summary>
public static class BothTenantsFake
{
    public static Func<HttpRequestMessage, HttpResponseMessage> Handler => request =>
        StubAtlassian.IsSource(request)
            ? SourceTenantFake.Handler(request)
            : TargetTenantFake.Handler(request);
}
