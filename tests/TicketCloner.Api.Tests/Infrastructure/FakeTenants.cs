using System.Net;

namespace TicketCloner.Api.Tests.Infrastructure;

/// <summary>
/// Both tenants, including the write routes. Configurable per test, because the
/// interesting apply cases are the ones where something goes wrong partway.
/// </summary>
public sealed class FakeTenants
{
    /// <summary>Non-null makes the duplicate search return a candidate. Whether
    /// it counts as a duplicate depends on the two values below.</summary>
    public string? ExistingCopyKey { get; set; }

    /// <summary>The candidate's summary. Defaults to the source issue's, so a
    /// bare ExistingCopyKey reads as a genuine duplicate.</summary>
    public string ExistingCopySummary { get; set; } = "Example portal rejects valid input";

    /// <summary>What the candidate carries in External Issue ID. Defaults to the
    /// source issue's URL, which is what apply writes.</summary>
    public string ExistingCopyReference { get; set; } =
        "https://source.example.invalid/browse/SRC-1234";

    /// <summary>accountIds that exist on the TARGET. Anything else 404s, which
    /// is what drives the fallback to the running account.</summary>
    public HashSet<string> KnownTargetAccountIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string RunningAccountId { get; set; } = "acc-running";

    public string CreatedKey { get; set; } = "TGT-9001";

    /// <summary>Non-null makes the epic search find one already on the target.</summary>
    public string? ExistingEpicKey { get; set; }

    public string ExistingEpicSummary { get; set; } = "Example portal improvements";

    /// <summary>Every create in this run, in order. An epic and its child both
    /// land here, which is how the tests tell them apart.</summary>
    public List<string> CreatedKeys { get; } = [];

    /// <summary>
    /// Active sprints the target board reports. One is the ordinary case; zero
    /// and two are the ones the feature has to refuse rather than guess at.
    /// </summary>
    public List<(int Id, string Name)> ActiveSprints { get; } = [(3946, "Example Sprint 5")];

    /// <summary>
    /// The rest of the target board: what the unfiltered list returns beside
    /// the active ones. Ids, names and dates from the real board 61 - including
    /// the repeated name, which is why nothing may match a sprint by name.
    /// </summary>
    public List<(int Id, string Name, string State, string? Start)> OtherSprints { get; } =
    [
        (126, "TGT Sprint 1", "closed", "2022-07-04T00:00:00.000Z"),
        (252, "TGT Sprint 1", "closed", "2023-08-07T00:00:00.000Z"),
        (3940, "Example Sprint 4", "closed", "2026-08-24T00:00:00.000Z"),
        (3952, "Example Sprint 6", "future", "2026-09-21T00:00:00.000Z"),
    ];

    /// <summary>Makes the sprint PUT fail, once the copy already exists.</summary>
    public bool FailSprintUpdate { get; set; }

    /// <summary>Every sprint update written to the target, in order.</summary>
    public List<string> FieldUpdates { get; } = [];

    public bool FailCreate { get; set; }

    public bool FailComments { get; set; }

    public bool FailAttachments { get; set; }

    /// <summary>Makes the second description pass fail, once the attachments
    /// are already up - the copy exists and only its images are wrong.</summary>
    public bool FailDescriptionUpdate { get; set; }

    /// <summary>Every description written to the target, in order. There are
    /// two on any issue whose description embeds an attachment.</summary>
    public List<string> DescriptionsWritten { get; } = [];

    /// <summary>Source keys the source tenant no longer returns.</summary>
    public HashSet<string> MissingSourceKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Func<HttpRequestMessage, HttpResponseMessage> Handler => request =>
    {
        var isSource = StubAtlassian.IsSource(request);
        var path = request.RequestUri!.AbsolutePath;

        return isSource ? Source(request, path) : Target(request, path);
    };

    private HttpResponseMessage Source(HttpRequestMessage request, string path)
    {
        if (MissingSourceKeys.Any(key => path.Contains($"/issue/{key}", StringComparison.OrdinalIgnoreCase)))
        {
            return Error(HttpStatusCode.NotFound, "Issue does not exist or you do not have permission to see it.");
        }

        if (path.Contains("/attachment/content/", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("pretend png bytes"u8.ToArray()),
            };
        }

        return SourceTenantFake.Handler(request);
    }

    private HttpResponseMessage Target(HttpRequestMessage request, string path)
    {
        if (path.Contains("/createmeta/", StringComparison.OrdinalIgnoreCase))
        {
            return TargetTenantFake.Handler(request);
        }

        // The agile API. Shaped from a real response captured by
        // scripts/probe-sprints.ps1 on 08/09/2026 - the collection is "values",
        // NOT the "issueTypes"/"fields" createmeta uses, and startAt, maxResults,
        // isLast and total all come with it.
        if (path.Contains("/rest/agile/1.0/board/", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith("/sprint", StringComparison.OrdinalIgnoreCase))
        {
            // ?state=active is what the apply step asks; the unfiltered call is
            // the picker's list and gets everything, active ones included.
            var active = ActiveSprints.Select(sprint => SprintJson(sprint.Id, sprint.Name, "active", "2026-09-07T03:49:44.978Z"));
            var rows = request.RequestUri!.Query.Contains("state=active", StringComparison.OrdinalIgnoreCase)
                ? active.ToList()
                : OtherSprints.Select(sprint => SprintJson(sprint.Id, sprint.Name, sprint.State, sprint.Start)).Concat(active).ToList();

            return StubAtlassian.Json(
                $$"""
                  {
                    "maxResults": 50,
                    "startAt": 0,
                    "total": {{rows.Count}},
                    "isLast": true,
                    "values": [{{string.Join(",", rows)}}]
                  }
                  """);
        }

        // One sprint by id - how a run that chose a sprint from the list looks
        // it up. Unknown ids 404 the way the real API does.
        if (path.Contains("/rest/agile/1.0/sprint/", StringComparison.OrdinalIgnoreCase))
        {
            var id = int.Parse(path[(path.LastIndexOf('/') + 1)..]);

            var known = ActiveSprints.Where(sprint => sprint.Id == id)
                .Select(sprint => SprintJson(sprint.Id, sprint.Name, "active", "2026-09-07T03:49:44.978Z"))
                .Concat(OtherSprints.Where(sprint => sprint.Id == id)
                    .Select(sprint => SprintJson(sprint.Id, sprint.Name, sprint.State, sprint.Start)))
                .FirstOrDefault();

            return known is null
                ? Error(HttpStatusCode.NotFound, $"Sprint does not exist or you do not have permission to view it.")
                : StubAtlassian.Json(known);
        }

        if (path.EndsWith("/rest/api/3/myself", StringComparison.OrdinalIgnoreCase))
        {
            return StubAtlassian.Json(
                $$"""{"accountId":"{{RunningAccountId}}","displayName":"Running Account","emailAddress":"run@example.com"}""");
        }

        if (path.EndsWith("/rest/api/3/user", StringComparison.OrdinalIgnoreCase))
        {
            var accountId = request.RequestUri!.Query
                .TrimStart('?')
                .Split('&')
                .Select(pair => pair.Split('=', 2))
                .Where(pair => pair is ["accountId", _])
                .Select(pair => Uri.UnescapeDataString(pair[1]))
                .FirstOrDefault() ?? "";

            return KnownTargetAccountIds.Contains(accountId)
                ? StubAtlassian.Json($$"""{"accountId":"{{accountId}}","displayName":"Known Person"}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        }

        if (path.EndsWith("/search/jql", StringComparison.OrdinalIgnoreCase))
        {
            var jql = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";

            // The epic lookup is its own search and must not be answered with
            // the ticket duplicate check's result.
            if (jql.Contains("issuetype = Epic", StringComparison.OrdinalIgnoreCase))
            {
                return StubAtlassian.Json(ExistingEpicKey is null
                    ? """{"issues":[],"isLast":true}"""
                    : $$"""
                      {
                        "issues": [{
                          "key": "{{ExistingEpicKey}}",
                          "fields": { "summary": "{{ExistingEpicSummary}}" }
                        }],
                        "isLast": true
                      }
                      """);
            }

            // Summary and External Issue ID both come back, because the check
            // narrows with JQL and then compares them exactly in code.
            return StubAtlassian.Json(ExistingCopyKey is null
                ? """{"issues":[],"isLast":true}"""
                : $$"""
                  {
                    "issues": [{
                      "key": "{{ExistingCopyKey}}",
                      "fields": {
                        "summary": "{{ExistingCopySummary}}",
                        "customfield_70008": "{{ExistingCopyReference}}"
                      }
                    }],
                    "isLast": true
                  }
                  """);
        }

        if (path.EndsWith("/attachments", StringComparison.OrdinalIgnoreCase))
        {
            return FailAttachments
                ? Error(HttpStatusCode.RequestEntityTooLarge, "attachment exceeds the size cap")
                // The id is deliberately nothing like the media node's UUID:
                // rewriting has to go through the file name to connect them.
                // 'content' is the canonical URL Jira returns, and what an
                // external media node ends up pointing at.
                : StubAtlassian.Json(
                    """
                    [{
                      "id": "70001",
                      "filename": "screenshot.png",
                      "content": "https://target.example.invalid/rest/api/3/attachment/content/70001"
                    }]
                    """);
        }

        if (path.EndsWith("/comment", StringComparison.OrdinalIgnoreCase))
        {
            return FailComments
                ? Error(HttpStatusCode.Forbidden, "no permission to comment")
                : StubAtlassian.Json("""{"id":"80001"}""");
        }

        if (path.EndsWith("/remotelink", StringComparison.OrdinalIgnoreCase))
        {
            return StubAtlassian.Json("""{"id":90001}""");
        }

        // The second description pass, once the attachments exist and the
        // media nodes have real target ids to point at.
        if (request.Method == HttpMethod.Put &&
            path.Contains("/rest/api/3/issue/", StringComparison.OrdinalIgnoreCase))
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";

            // Two different PUTs land here - the second description pass and the
            // sprint update - and a test needs to fail one without the other.
            var isSprint = body.Contains("customfield_70002", StringComparison.Ordinal);

            if (isSprint && FailSprintUpdate)
            {
                // The real message, from the 400 this project already hit once.
                return Error(HttpStatusCode.BadRequest, "Specify a valid value for Sprint");
            }

            if (!isSprint && FailDescriptionUpdate)
            {
                return Error(HttpStatusCode.BadRequest, "cannot update the description");
            }

            if (isSprint)
            {
                FieldUpdates.Add(body);
            }

            // Record only keeps PUTs carrying fields.description, so the sprint
            // update never shows up as a description write.
            Record(request);
            return new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new StringContent("") };
        }

        if (path.EndsWith("/rest/api/3/issue", StringComparison.OrdinalIgnoreCase))
        {
            if (FailCreate)
            {
                return Error(HttpStatusCode.BadRequest, "Field 'customfield_70005' cannot be set");
            }

            Record(request);

            // The first create keeps whatever CreatedKey says, so single-ticket
            // tests are unaffected; later ones get their own key.
            var key = CreatedKeys.Count == 0 ? CreatedKey : $"TGT-{9001 + CreatedKeys.Count}";
            CreatedKeys.Add(key);

            return StubAtlassian.Json($$"""{"id":"10500","key":"{{key}}"}""");
        }

        return Error(HttpStatusCode.NotFound, $"unexpected target path: {path}");
    }

    private void Record(HttpRequestMessage request)
    {
        var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
        var description = body is null ? null : System.Text.Json.Nodes.JsonNode.Parse(body)?["fields"]?["description"];

        if (description is not null)
        {
            DescriptionsWritten.Add(description.ToJsonString());
        }
    }

    private static string SprintJson(int id, string name, string state, string? start) =>
        $$"""
          {
            "id": {{id}},
            "state": "{{state}}",
            "name": "{{name}}",
            {{(start is null ? "" : $"\"startDate\": \"{start}\",")}}
            "originBoardId": 202,
            "goal": ""
          }
          """;

    private static HttpResponseMessage Error(HttpStatusCode status, string detail) =>
        new(status) { Content = new StringContent($$"""{"errorMessages":["{{detail}}"]}""") };
}
