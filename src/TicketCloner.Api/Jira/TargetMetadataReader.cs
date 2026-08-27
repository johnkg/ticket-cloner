using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TicketCloner.Api.Atlassian;
using TicketCloner.Api.Configuration;
using TicketCloner.Api.Contracts;

namespace TicketCloner.Api.Jira;

/// <summary>
/// Reads the target project's create screen. This is what decides which fields
/// can be sent at all - anything absent from it is a hard 400.
/// </summary>
public sealed class TargetMetadataReader(
    AtlassianClientFactory clients,
    IOptions<AtlassianOptions> options)
{
    // Both createmeta endpoints page, and both default to 50. A project this
    // size is silently truncated without the loop, which understates the create
    // screen and makes the mapper drop fields that were actually available.
    private const int PageSize = 100;

    private TenantOptions Target => options.Value.Target;

    public async Task<IReadOnlyList<TargetIssueType>> GetIssueTypesAsync(CancellationToken cancellationToken)
    {
        var client = clients.For(Tenant.Target);
        var project = Uri.EscapeDataString(Target.ProjectKey);

        // The collection is "issueTypes", NOT the "values" most Jira paginated
        // endpoints use. Verified against the live TARGET_PROJECT project on 18/08/2026.
        var values = await ReadAllPagesAsync(
            client,
            "issueTypes",
            startAt => $"rest/api/3/issue/createmeta/{project}/issuetypes?startAt={startAt}&maxResults={PageSize}",
            cancellationToken);

        return values
            .Select(value => new TargetIssueType(
                Id: value?["id"]?.GetValue<string>() ?? "",
                Name: value?["name"]?.GetValue<string>() ?? "",
                Subtask: value?["subtask"]?.GetValue<bool>() ?? false))
            .Where(type => type.Id.Length > 0)
            .ToList();
    }

    public async Task<IReadOnlyList<TargetField>> GetFieldsAsync(
        string issueTypeId,
        CancellationToken cancellationToken)
    {
        var client = clients.For(Tenant.Target);
        var project = Uri.EscapeDataString(Target.ProjectKey);
        var typeId = Uri.EscapeDataString(issueTypeId);

        // "fields" here, where the issue-type list uses "issueTypes". The two
        // sibling endpoints do not agree with each other.
        var values = await ReadAllPagesAsync(
            client,
            "fields",
            startAt => $"rest/api/3/issue/createmeta/{project}/issuetypes/{typeId}?startAt={startAt}&maxResults={PageSize}",
            cancellationToken);

        return values.Select(ToField).Where(field => field.FieldId.Length > 0).ToList();
    }

    /// <param name="collectionProperty">
    /// Which property holds the page's items. It differs per endpoint, and
    /// reading the wrong one returns an empty list with no error at all - which
    /// reads downstream as "this project has no issue types" or "the create
    /// screen is empty", and produces a plan that looks tidy and is wrong. So a
    /// missing property throws rather than yielding nothing.
    /// </param>
    private static async Task<List<JsonNode?>> ReadAllPagesAsync(
        AtlassianClient client,
        string collectionProperty,
        Func<int, string> pathForStartAt,
        CancellationToken cancellationToken)
    {
        var all = new List<JsonNode?>();
        var startAt = 0;

        while (true)
        {
            var path = pathForStartAt(startAt);
            var page = await client.GetJsonAsync(path, cancellationToken);

            if (page?[collectionProperty] is not JsonArray values)
            {
                var got = page is JsonObject obj
                    ? string.Join(", ", obj.Select(property => property.Key))
                    : "nothing";

                throw new InvalidOperationException(
                    $"Expected a '{collectionProperty}' array from {path} but the response carried: {got}. " +
                    "Silently returning nothing here would look like an empty create screen.");
            }

            all.AddRange(values);

            var total = page["total"]?.GetValue<int>() ?? all.Count;
            startAt += values.Count;

            if (values.Count == 0 || startAt >= total)
            {
                return all;
            }
        }
    }

    private static TargetField ToField(JsonNode? value)
    {
        var schema = value?["schema"];
        var type = schema?["type"]?.GetValue<string>();

        return new TargetField(
            FieldId: value?["fieldId"]?.GetValue<string>() ?? "",
            Name: value?["name"]?.GetValue<string>() ?? "",
            Required: value?["required"]?.GetValue<bool>() ?? false,
            HasDefaultValue: value?["hasDefaultValue"]?.GetValue<bool>() ?? false,
            // For an array field the element type is what matters when
            // comparing against the source; "array" alone says nothing.
            SchemaType: type == "array" ? schema?["items"]?.GetValue<string>() ?? "array" : type,
            SchemaCustom: schema?["custom"]?.GetValue<string>(),
            IsArray: type == "array",
            AllowedValues: (value?["allowedValues"] as JsonArray ?? [])
                .Select(allowed => new AllowedValue(
                    Id: allowed?["id"]?.GetValue<string>() ?? "",
                    // System fields say "name"; custom select options say "value".
                    Name: allowed?["name"]?.GetValue<string>()
                          ?? allowed?["value"]?.GetValue<string>()
                          ?? ""))
                .Where(allowed => allowed.Name.Length > 0)
                .ToList());
    }
}
