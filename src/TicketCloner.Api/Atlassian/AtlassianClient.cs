using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace TicketCloner.Api.Atlassian;

/// <summary>
/// One tenant's Atlassian REST client, bound at construction to that tenant's
/// HttpClient AND that tenant's credential.
///
/// The binding is the point. There is no method on this type that accepts a
/// credential, so there is no call site at which the target's token can be
/// attached to a source request - the mistake is unrepresentable rather than
/// merely discouraged.
/// </summary>
public sealed class AtlassianClient(HttpClient http, Tenant tenant, AtlassianCredentials credentials)
{
    /// <summary>
    /// JQL search is a POST that reads. Everything else that is not a GET
    /// modifies something, so on the source tenant it is refused outright.
    /// </summary>
    private static readonly string[] ReadOnlyPostPaths = ["rest/api/3/search"];

    public Tenant Tenant { get; } = tenant;

    public Uri BaseAddress => http.BaseAddress!;

    public Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken) =>
        SendAsync<T>(HttpMethod.Get, path, content: null, cancellationToken);

    public Task<T?> PostAsync<T>(string path, object body, CancellationToken cancellationToken) =>
        SendAsync<T>(HttpMethod.Post, path, JsonContent.Create(body), cancellationToken);

    public Task<T?> PutAsync<T>(string path, object body, CancellationToken cancellationToken) =>
        SendAsync<T>(HttpMethod.Put, path, JsonContent.Create(body), cancellationToken);

    public Task<JsonNode?> GetJsonAsync(string path, CancellationToken cancellationToken) =>
        GetAsync<JsonNode>(path, cancellationToken);

    /// <summary>Attachment content, which is not JSON.</summary>
    public async Task<byte[]> GetBytesAsync(string path, CancellationToken cancellationToken)
    {
        using var request = Build(HttpMethod.Get, path);
        using var response = await http.SendAsync(request, cancellationToken);

        await ThrowIfFailedAsync(response, HttpMethod.Get, path, cancellationToken);

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <summary>
    /// Attachment upload. Jira requires <c>X-Atlassian-Token: no-check</c> on
    /// this route and rejects the request outright without it.
    /// </summary>
    public async Task<T?> PostMultipartAsync<T>(
        string path,
        MultipartFormDataContent content,
        CancellationToken cancellationToken)
    {
        using var request = Build(HttpMethod.Post, path);
        request.Content = content;
        request.Headers.Add("X-Atlassian-Token", "no-check");

        using var response = await http.SendAsync(request, cancellationToken);

        await ThrowIfFailedAsync(response, HttpMethod.Post, path, cancellationToken);

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    private HttpRequestMessage Build(HttpMethod method, string path)
    {
        var relative = path.TrimStart('/');
        GuardAgainstWritingToTheSource(method, relative);

        var request = new HttpRequestMessage(method, relative);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Basic", credentials.ToBasicParameter());

        return request;
    }

    private async Task ThrowIfFailedAsync(
        HttpResponseMessage response,
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new AtlassianApiException(Tenant, method, path, response.StatusCode, body);
    }

    private async Task<T?> SendAsync<T>(
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        // Relative, no leading slash: BaseAddress carries the host and a leading
        // slash would silently discard any base path.
        using var request = Build(method, path);
        request.Content = content;

        using var response = await http.SendAsync(request, cancellationToken);

        await ThrowIfFailedAsync(response, method, path, cancellationToken);

        // 204 on some write routes, and ReadFromJsonAsync throws on an empty body.
        return response.Content.Headers.ContentLength == 0
            ? default
            : await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    /// <summary>
    /// The tool reads source-company and never writes to it. Enforcing that here
    /// rather than by convention means a future endpoint cannot quietly acquire
    /// the ability, however it is wired up.
    /// </summary>
    private void GuardAgainstWritingToTheSource(HttpMethod method, string relativePath)
    {
        if (Tenant != Tenant.Source || method == HttpMethod.Get)
        {
            return;
        }

        var isReadOnlyPost = method == HttpMethod.Post &&
            ReadOnlyPostPaths.Any(prefix =>
                relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (!isReadOnlyPost)
        {
            throw new InvalidOperationException(
                $"Refusing {method} {relativePath} on the source tenant ({BaseAddress.Host}). " +
                "TicketCloner only ever reads from the source.");
        }
    }
}

/// <summary>
/// Builds a client for a tenant, pairing the right named HttpClient with the
/// right credential. Scoped, because credentials come from the current request.
/// </summary>
public sealed class AtlassianClientFactory(
    IHttpClientFactory httpClientFactory,
    CredentialsResolver credentials)
{
    public AtlassianClient For(Tenant tenant) => new(
        httpClientFactory.CreateClient(tenant.ClientName()),
        tenant,
        credentials.For(tenant));
}
