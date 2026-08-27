using System.Net;
using System.Text.Json.Nodes;

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

    public string ExistingEpicSummary { get; set; } = "Example portal overhaul";

    /// <summary>Every create in this run, in order. An epic and its child both
    /// land here, which is how the tests tell them apart.</summary>
    public List<string> CreatedKeys { get; } = [];

    /// <summary>
    /// Active sprints the target board reports. One is the ordinary case; zero
    /// and two are the ones the feature has to refuse rather than guess at.
    /// </summary>
    public List<(int Id, string Name)> ActiveSprints { get; } = [(3946, "RAY.2026.Q3.S5")];

    /// <summary>
    /// The rest of the target board: what the unfiltered list returns beside
    /// the active ones. Ids, names and dates from the real board 202 - including
    /// the repeated name, which is why nothing may match a sprint by name.
    /// </summary>
    public List<(int Id, string Name, string State, string? Start)> OtherSprints { get; } =
    [
        (126, "TGT Sprint 1", "closed", "2022-07-04T00:00:00.000Z"),
        (252, "TGT Sprint 1", "closed", "2023-08-07T00:00:00.000Z"),
        (3940, "RAY.2026.Q3.S4", "closed", "2026-08-24T00:00:00.000Z"),
        (3952, "RAY.2026.Q3.S6", "future", "2026-09-21T00:00:00.000Z"),
    ];

    /// <summary>
    /// What the existing copy holds now, field id to raw JSON. What is NOT here
    /// reads back as null, which a refresh sees as "the copy has nothing".
    /// </summary>
    public Dictionary<string, string> ExistingCopyFields { get; } = new()
    {
        ["summary"] = "\"Example portal rejects valid input\"",
        ["labels"] = "[\"copied\"]",
    };

    /// <summary>
    /// Field ids the EDIT screen offers. Not the same set as the create screen -
    /// a field can be creatable and not editable, and that is what this proves.
    /// </summary>
    public List<string> EditableFieldIds { get; } =
        ["summary", "description", "labels", "priority", "customfield_70007", "customfield_70008"];

    /// <summary>
    /// Files already on the existing copy. The default mirrors a copy made
    /// while the original had one screenshot, at the same size the source
    /// reports - so a refresh takes it for the same file and uploads nothing.
    /// </summary>
    public List<(string Id, string FileName, long Size, string Created)> ExistingCopyAttachments { get; } =
        [("70001", "screenshot.png", 84213, "2026-08-11T09:00:00.000+1000")];

    /// <summary>Files uploaded to the copy during a refresh, in order.</summary>
    public List<string> RefreshUploads { get; } = [];

    /// <summary>
    /// An extra node spliced into the SOURCE issue's description, as raw JSON.
    /// For proving what happens to a node shape the rewriter does not know -
    /// which is not hypothetical: mediaInline was one until 25/09/2026.
    /// </summary>
    public string? SourceDescriptionExtra { get; set; }

    /// <summary>Makes GET editmeta fail, so a refresh has to cope without it.</summary>
    public bool FailEditMeta { get; set; }

    /// <summary>Makes the refresh PUT fail.</summary>
    public bool FailRefresh { get; set; }

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

        var response = SourceTenantFake.Handler(request);

        return SourceDescriptionExtra is null ? response : WithExtraNode(response);
    }

    /// <summary>Splices SourceDescriptionExtra into the description's content.</summary>
    private HttpResponseMessage WithExtraNode(HttpResponseMessage response)
    {
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var issue = JsonNode.Parse(body);

        if (issue?["fields"]?["description"]?["content"] is not JsonArray content)
        {
            return response;
        }

        content.Add(JsonNode.Parse(SourceDescriptionExtra!));

        return StubAtlassian.Json(issue!.ToJsonString());
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

        // The EDIT screen. "fields" is an OBJECT keyed by field id here, not
        // the array createmeta returns, and it does not page.
        if (path.EndsWith("/editmeta", StringComparison.OrdinalIgnoreCase))
        {
            if (FailEditMeta)
            {
                return Error(HttpStatusCode.Forbidden, "no permission to edit this issue");
            }

            var fields = string.Join(",", EditableFieldIds.Select(id => $"\"{id}\": {{ \"required\": false }}"));
            return StubAtlassian.Json($$"""{ "fields": { {{fields}} } }""");
        }

        // One issue by key. A refresh reads the copy this way - its current
        // field values, and separately the files it already holds.
        if (request.Method == HttpMethod.Get &&
            path.Contains("/rest/api/3/issue/", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith("/editmeta", StringComparison.OrdinalIgnoreCase))
        {
            if (request.RequestUri!.Query.Contains("fields=attachment", StringComparison.OrdinalIgnoreCase))
            {
                var files = string.Join(",", ExistingCopyAttachments.Select(file =>
                    $$"""
                      {
                        "id": "{{file.Id}}",
                        "filename": "{{file.FileName}}",
                        "size": {{file.Size}},
                        "created": "{{file.Created}}"
                      }
                      """));

                return StubAtlassian.Json(
                    $$"""{ "key": "{{ExistingCopyKey}}", "fields": { "attachment": [{{files}}] } }""");
            }

            var held = string.Join(",", ExistingCopyFields.Select(field => $"\"{field.Key}\": {field.Value}"));
            return StubAtlassian.Json($$"""{ "key": "{{ExistingCopyKey}}", "fields": { {{held}} } }""");
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
            // An upload to the EXISTING copy is a refresh putting an image
            // there before the description can point at it. Recorded so a test
            // can prove it happened, and added to what the copy holds so the
            // re-read afterwards finds it.
            if (ExistingCopyKey is not null &&
                path.Contains($"/issue/{ExistingCopyKey}/", StringComparison.OrdinalIgnoreCase) &&
                !FailAttachments)
            {
                var uploadedName = PendingUploadName ?? "screenshot.png";
                RefreshUploads.Add(uploadedName);

                var id = $"7{9000 + RefreshUploads.Count}";
                ExistingCopyAttachments.Add((id, uploadedName, PendingUploadSize, "2026-09-25T10:00:00.000+1000"));

                return StubAtlassian.Json(
                    $$"""
                      [{
                        "id": "{{id}}",
                        "filename": "{{uploadedName}}",
                        "content": "https://api.atlassian.com/ex/jira/cloud-target/rest/api/3/attachment/content/{{id}}"
                      }]
                      """);
            }

            return FailAttachments
                ? Error(HttpStatusCode.RequestEntityTooLarge, "attachment exceeds the size cap")
                // The id is deliberately nothing like the media node's UUID:
                // rewriting has to go through the file name to connect them.
                // 'content' is the canonical URL Jira returns, and what an
                // external media node ends up pointing at.
                // "content" echoes the base the call came in on, which under
                // OAuth is api.atlassian.com - a host Jira's own renderer
                // cannot fetch. This fixture used to carry a site URL, captured
                // back when calls went straight to the tenant, and that stale
                // value is what let the broken build pass its own test.
                : StubAtlassian.Json(
                    """
                    [{
                      "id": "70001",
                      "filename": "screenshot.png",
                      "content": "https://api.atlassian.com/ex/jira/cloud-target/rest/api/3/attachment/content/70001"
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

            // Three different PUTs land here - the second description pass, the
            // sprint update and a refresh - and a test needs to fail one
            // without the others. The refresh is the one aimed at a key that
            // already existed rather than one this run created.
            var isSprint = body.Contains("customfield_70002", StringComparison.Ordinal);

            var isRefresh = !isSprint && ExistingCopyKey is not null &&
                path.Contains($"/issue/{ExistingCopyKey}", StringComparison.OrdinalIgnoreCase);

            if (isSprint && FailSprintUpdate)
            {
                // The real message, from the 400 this project already hit once.
                return Error(HttpStatusCode.BadRequest, "Specify a valid value for Sprint");
            }

            if (isRefresh && FailRefresh)
            {
                return Error(HttpStatusCode.BadRequest, "Field 'summary' cannot be set");
            }

            if (!isSprint && !isRefresh && FailDescriptionUpdate)
            {
                return Error(HttpStatusCode.BadRequest, "cannot update the description");
            }

            if (isSprint || isRefresh)
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

    /// <summary>What the next refresh upload is taken to be, so the fake can
    /// mirror the source's own file back onto the copy.</summary>
    public string? PendingUploadName { get; set; }

    public long PendingUploadSize { get; set; } = 84213;

    private static HttpResponseMessage Error(HttpStatusCode status, string detail) =>
        new(status) { Content = new StringContent($$"""{"errorMessages":["{{detail}}"]}""") };
}
