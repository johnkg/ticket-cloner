using System.Net;

namespace TicketCloner.Api.Tests.Infrastructure;

/// <summary>
/// Canned responses shaped like the real SRC project on source-site: custom field
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

        // The source board's sprints. Two pages of two, so the reader's paging
        // loop is exercised, and a repeated name on purpose: a board reuses
        // sprint names, and the id is the only thing that tells them apart.
        if (path.Contains("/rest/agile/1.0/board/101/sprint", StringComparison.OrdinalIgnoreCase))
        {
            return StubAtlassian.Json(query.Contains("startAt=0") ? SprintsPageOne : SprintsPageTwo);
        }

        if (path.Contains("/comment", StringComparison.OrdinalIgnoreCase))
        {
            // Two pages, so the reader's paging loop is actually exercised.
            return StubAtlassian.Json(query.Contains("startAt=0") ? CommentsPageOne : CommentsPageTwo);
        }

        if (path.Contains("/issue/", StringComparison.OrdinalIgnoreCase))
        {
            // Routed by key, so an epic and its child can both be read.
            if (path.Contains("SRC-4000", StringComparison.OrdinalIgnoreCase))
            {
                return StubAtlassian.Json(Epic);
            }

            return StubAtlassian.Json(
                path.Contains("SRC-4444", StringComparison.OrdinalIgnoreCase) ? ChildOfEpic : Issue);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"unexpected path: {path}"),
        };
    };

    private const string SprintsPageOne =
        """
        {
          "maxResults": 2,
          "startAt": 0,
          "total": 4,
          "isLast": false,
          "values": [
            {
              "id": 8001,
              "state": "closed",
              "name": "SRC Sprint 1",
              "startDate": "2025-01-06T00:00:00.000Z",
              "endDate": "2025-01-17T00:00:00.000Z",
              "originBoardId": 101
            },
            {
              "id": 8002,
              "state": "closed",
              "name": "SRC Sprint 2",
              "startDate": "2025-01-20T00:00:00.000Z",
              "endDate": "2025-01-31T00:00:00.000Z",
              "originBoardId": 101
            }
          ]
        }
        """;

    private const string SprintsPageTwo =
        """
        {
          "maxResults": 2,
          "startAt": 2,
          "total": 4,
          "isLast": true,
          "values": [
            {
              "id": 8003,
              "state": "active",
              "name": "SRC Sprint 1",
              "startDate": "2026-08-10T00:00:00.000Z",
              "endDate": "2026-08-21T00:00:00.000Z",
              "originBoardId": 101
            },
            {
              "id": 8800,
              "state": "future",
              "name": "Hardening",
              "originBoardId": 101
            }
          ]
        }
        """;

    private const string SearchResults =
        """
        {
          "issues": [
            {
              "key": "SRC-1234",
              "fields": {
                "summary": "Example portal rejects valid input",
                "issuetype": { "name": "Bug" },
                "status": { "name": "Open" },
                "priority": { "name": "High" },
                "reporter": { "accountId": "acc-1", "displayName": "Example Reporter", "emailAddress": null },
                "assignee": null,
                "updated": "2026-08-14T09:15:00.000+1000"
              }
            },
            {
              "key": "SRC-1235",
              "fields": {
                "summary": "Add example calculator to the sidebar",
                "issuetype": { "name": "New Feature" },
                "status": { "name": "Refinement" },
                "priority": { "name": "Medium" },
                "reporter": { "accountId": "acc-2", "displayName": "Example Analyst", "emailAddress": null },
                "assignee": { "accountId": "acc-3", "displayName": "Example Developer", "emailAddress": null },
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
          "key": "SRC-1234",
          "names": {
            "summary": "Summary",
            "customfield_70010": "Story point estimate",
            "customfield_70011": "Workaround",
            "customfield_70003": "Sprint",
            "customfield_70012": "Summary (Short)",
            "customfield_70016": "Never Populated",
            "labels": "Labels"
          },
          "schema": {
            "customfield_70010": { "type": "number", "custom": "com.pyxis.greenhopper.jira:jsw-story-points" },
            "customfield_70011": { "type": "string", "custom": "com.atlassian.jira.plugin.system.customfieldtypes:textfield" },
            "customfield_70003": { "type": "json", "custom": "com.pyxis.greenhopper.jira:gh-sprint" }
          },
          "fields": {
            "summary": "Example portal rejects valid input",
            "issuetype": { "name": "Bug" },
            "status": { "name": "Open" },
            "priority": { "name": "High" },
            "reporter": { "accountId": "acc-1", "displayName": "Example Reporter", "emailAddress": null },
            "assignee": null,
            "created": "2026-08-10T11:00:00.000+1000",
            "updated": "2026-08-14T09:15:00.000+1000",
            "labels": ["example", "input"],
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
            "customfield_70010": 5,
            "customfield_70011": "Enter the example value without spaces",
            "customfield_70003": [
              {
                "id": 8003,
                "name": "Example Sprint 3",
                "state": "active",
                "boardId": 101
              }
            ],
            "customfield_70012": "",
            "customfield_70016": null,
            "attachment": [
              {
                "id": "50021",
                "filename": "screenshot.png",
                "mimeType": "image/png",
                "size": 84213,
                "created": "2026-08-10T11:05:00.000+1000",
                "author": { "accountId": "acc-1", "displayName": "Example Reporter", "emailAddress": null }
              }
            ]
          }
        }
        """;

    /// <summary>An Epic, which is what a parent has to be for any of this to fire.</summary>
    private const string Epic =
        """
        {
          "key": "SRC-4000",
          "names": { "summary": "Summary", "issuetype": "Issue Type" },
          "schema": {},
          "fields": {
            "summary": "Example portal improvements",
            "issuetype": { "name": "Epic" },
            "status": { "name": "Open" },
            "priority": { "name": "High" },
            "reporter": { "accountId": "acc-1", "displayName": "Example Reporter", "emailAddress": null },
            "assignee": null,
            "created": "2026-08-01T09:00:00.000+1000",
            "updated": "2026-08-02T09:00:00.000+1000",
            "labels": [],
            "parent": null,
            "description": null,
            "attachment": []
          }
        }
        """;

    /// <summary>A Story sitting under that epic.</summary>
    private const string ChildOfEpic =
        """
        {
          "key": "SRC-4444",
          "names": { "summary": "Summary", "issuetype": "Issue Type" },
          "schema": {},
          "fields": {
            "summary": "Example portal rejects valid input",
            "issuetype": { "name": "Story" },
            "status": { "name": "Open" },
            "priority": { "name": "High" },
            "reporter": { "accountId": "acc-1", "displayName": "Example Reporter", "emailAddress": null },
            "assignee": null,
            "created": "2026-08-10T11:00:00.000+1000",
            "updated": "2026-08-14T09:15:00.000+1000",
            "labels": [],
            "parent": {
              "id": "9001",
              "key": "SRC-4000",
              "fields": {
                "summary": "Example portal improvements",
                "issuetype": { "name": "Epic" }
              }
            },
            "description": null,
            "attachment": []
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
              "author": { "accountId": "acc-1", "displayName": "Example Reporter", "emailAddress": null },
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
              "author": { "accountId": "acc-3", "displayName": "Example Developer", "emailAddress": null },
              "created": "2026-08-12T14:45:00.000+1000",
              "body": { "type": "doc", "version": 1, "content": [] }
            }
          ]
        }
        """;
}
