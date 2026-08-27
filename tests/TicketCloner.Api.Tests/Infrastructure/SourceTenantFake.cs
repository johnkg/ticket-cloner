using System.Net;

namespace TicketCloner.Api.Tests.Infrastructure;

/// <summary>
/// Canned responses shaped like the real SOURCE_PROJECT project on source-company: custom field
/// ids that mean nothing on the target, an expand=names map, an email address
/// that is null because the tenant hides them, and comments that span pages.
/// </summary>
public static class SourceTenantFake
{
    public static Func<HttpRequestMessage, HttpResponseMessage> Handler => request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;

        if (path.EndsWith("/search/jql", StringComparison.OrdinalIgnoreCase))
        {
            return StubAtlassian.Json(SearchResults);
        }

        if (path.Contains("/comment", StringComparison.OrdinalIgnoreCase))
        {
            // Two pages, so the reader's paging loop is actually exercised.
            return StubAtlassian.Json(query.Contains("startAt=0") ? CommentsPageOne : CommentsPageTwo);
        }

        if (path.Contains("/issue/", StringComparison.OrdinalIgnoreCase))
        {
            return StubAtlassian.Json(Issue);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"unexpected path: {path}"),
        };
    };

    private const string SearchResults =
        """
        {
          "issues": [
            {
              "key": "SOURCE_PROJECT-1234",
              "fields": {
                "summary": "Broker portal rejects valid ABN",
                "issuetype": { "name": "Bug" },
                "status": { "name": "Open" },
                "priority": { "name": "High" },
                "reporter": { "accountId": "acc-1", "displayName": "Ray Tester", "emailAddress": null },
                "assignee": null,
                "updated": "2026-08-14T09:15:00.000+1000"
              }
            },
            {
              "key": "SOURCE_PROJECT-1235",
              "fields": {
                "summary": "Add serviceability calculator to the sidebar",
                "issuetype": { "name": "New Feature" },
                "status": { "name": "Refinement" },
                "priority": { "name": "Medium" },
                "reporter": { "accountId": "acc-2", "displayName": "Sam Analyst", "emailAddress": null },
                "assignee": { "accountId": "acc-3", "displayName": "Dev Person", "emailAddress": null },
                "updated": "2026-08-13T16:02:00.000+1000"
              }
            }
          ],
          "nextPageToken": "page-2-token",
          "isLast": false
        }
        """;

    private const string Issue =
        """
        {
          "key": "SOURCE_PROJECT-1234",
          "names": {
            "summary": "Summary",
            "customfield_EXAMPLE_ID": "Story point estimate",
            "customfield_13613": "Workaround",
            "customfield_SOURCE_ID": "Sprint",
            "customfield_13621": "Summary (Short)",
            "customfield_99999": "Never Populated",
            "labels": "Labels"
          },
          "schema": {
            "customfield_EXAMPLE_ID": { "type": "number", "custom": "com.pyxis.greenhopper.jira:jsw-story-points" },
            "customfield_13613": { "type": "string", "custom": "com.atlassian.jira.plugin.system.customfieldtypes:textfield" },
            "customfield_SOURCE_ID": { "type": "json", "custom": "com.pyxis.greenhopper.jira:gh-sprint" }
          },
          "fields": {
            "summary": "Broker portal rejects valid ABN",
            "issuetype": { "name": "Bug" },
            "status": { "name": "Open" },
            "priority": { "name": "High" },
            "reporter": { "accountId": "acc-1", "displayName": "Ray Tester", "emailAddress": null },
            "assignee": null,
            "created": "2026-08-10T11:00:00.000+1000",
            "updated": "2026-08-14T09:15:00.000+1000",
            "labels": ["broker", "abn"],
            "description": {
              "type": "doc",
              "version": 1,
              "content": [
                { "type": "paragraph", "content": [ { "type": "text", "text": "Steps to reproduce" } ] },
                {
                  "type": "mediaSingle",
                  "content": [
                    {
                      "type": "media",
                      "attrs": {
                        "id": "a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d",
                        "type": "file",
                        "alt": "screenshot.png",
                        "collection": "source-tenant-bucket"
                      }
                    }
                  ]
                }
              ]
            },
            "customfield_EXAMPLE_ID": 5,
            "customfield_13613": "Enter the ABN without spaces",
            "customfield_SOURCE_ID": [
              {
                "id": 8796,
                "name": "BAU.2026.Q3.S3",
                "state": "active",
                "boardId": 200
              }
            ],
            "customfield_13621": "",
            "customfield_99999": null,
            "attachment": [
              {
                "id": "50021",
                "filename": "screenshot.png",
                "mimeType": "image/png",
                "size": 84213,
                "created": "2026-08-10T11:05:00.000+1000",
                "author": { "accountId": "acc-1", "displayName": "Ray Tester", "emailAddress": null }
              }
            ]
          }
        }
        """;

    private const string CommentsPageOne =
        """
        {
          "startAt": 0,
          "maxResults": 100,
          "total": 2,
          "comments": [
            {
              "id": "9001",
              "author": { "accountId": "acc-1", "displayName": "Ray Tester", "emailAddress": null },
              "created": "2026-08-11T08:30:00.000+1000",
              "body": { "type": "doc", "version": 1, "content": [] }
            }
          ]
        }
        """;

    private const string CommentsPageTwo =
        """
        {
          "startAt": 1,
          "maxResults": 100,
          "total": 2,
          "comments": [
            {
              "id": "9002",
              "author": { "accountId": "acc-3", "displayName": "Dev Person", "emailAddress": null },
              "created": "2026-08-12T14:45:00.000+1000",
              "body": { "type": "doc", "version": 1, "content": [] }
            }
          ]
        }
        """;
}
