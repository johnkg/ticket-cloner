using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TicketCloner.Api.Configuration;

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
public sealed class AtlassianClient(
    HttpClient http,
    Tenant tenant,
    IAtlassianCredential credentials,
    Uri siteUri,
    Uri apiBaseUri)
{
    /// <summary>
    /// JQL search is a POST that reads. Everything else that is not a GET
    /// modifies something, so on the source tenant it is refused outright.
    /// </summary>
    private static readonly string[] ReadOnlyPostPaths = ["rest/api/3/search"];

    public Tenant Tenant { get; } = tenant;

    /// <summary>
    /// Where the REST calls go. Not necessarily the site, and NOT the injected
    /// HttpClient's BaseAddress - the named clients carry none.
    ///
    /// Under OAuth this depends on which user is calling, because it embeds
    /// their cloud id. IHttpClientFactory's configure delegate cannot see the
    /// request scope, so a per-user BaseAddress on the client is not merely
    /// unwise but impossible; the URI is composed per call instead.
    /// </summary>
    public Uri ApiBaseUri { get; } = apiBaseUri.AbsoluteUri.EndsWith('/')
        ? apiBaseUri
        : new Uri(apiBaseUri.AbsoluteUri + "/");

    /// <summary>
    /// The site a person visits, which is what every link and every URL written
    /// into an issue must be built from.
    ///
    /// Under API-token auth this equals <see cref="BaseAddress"/>, which is why
    /// the two were one value for so long. Under OAuth the REST base becomes
    /// https://api.atlassian.com/ex/jira/{cloudId}/ - a host that needs a Bearer
    /// token and renders as nothing in a browser. Build a link from that and the
    /// copied description's images break exactly as they did before.
    /// </summary>
    public Uri SiteUri { get; } = siteUri;

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

        var request = new HttpRequestMessage(method, new Uri(ApiBaseUri, relative));
        request.Headers.Authorization = credentials.ToAuthorizationHeader();

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
        // Relative, no leading slash: ApiBaseUri carries the host and a leading
        // slash would silently discard any base path - which under OAuth is the
        // /ex/jira/{cloudId}/ segment, so every call would 404.
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
    /// The tool reads source-site and never writes to it. Enforcing that here
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
                $"Refusing {method} {relativePath} on the source tenant ({SiteUri.Host}). " +
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
    IOptions<AtlassianOptions> options,
    IHttpContextAccessor httpContextAccessor)
{
    public AtlassianClient For(Tenant tenant)
    {
        var access = Access(tenant);

        return new AtlassianClient(
            httpClientFactory.CreateClient(tenant.ClientName()),
            tenant,
            access.Credential,
            access.SiteUri,
            access.ApiBaseUri);
    }

    /// <summary>
    /// What AtlassianCredentialsFilter worked out before the handler ran. It is
    /// resolved there rather than here because an OAuth refresh has to be
    /// awaited and this must stay synchronous - six services call For() from
    /// constructors and field initialisers.
    /// </summary>
    private TenantAccess Access(Tenant tenant)
    {
        if (httpContextAccessor.HttpContext?.Items.TryGetValue(TenantAccess.ItemKey(tenant), out var stashed) is true &&
            stashed is TenantAccess resolved)
        {
            return resolved;
        }

        // No filter ran - there is no request, or the endpoint is unguarded.
        // There is nothing to authorise with outside a signed-in request, so
        // the client is built but carries no header; the site is still right
        // for anything that only wants a URL.
        return TenantAccess.None(Settings(tenant));
    }

    private TenantOptions Settings(Tenant tenant) =>
        tenant == Tenant.Source ? options.Value.Source : options.Value.Target;
}
