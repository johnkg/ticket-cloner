using Microsoft.Extensions.Options;
using Serilog;
using TicketCloner.Api.Atlassian;
using TicketCloner.Api.Configuration;
using TicketCloner.Api.Endpoints;
using TicketCloner.Api.Jira;
using TicketCloner.Api.Mapping;
using TicketCloner.Api.Adf;
using TicketCloner.Api.Apply;
using TicketCloner.Api.OAuth;

var builder = WebApplication.CreateBuilder(args);

// Where the OAuth client secret goes. Added LAST, so anything set here beats
// appsettings.json - and it is gitignored and excluded from publish, which
// appsettings.json is not.
//
// This is the ONLY supported place for a secret now. Atlassian API tokens are
// no longer read from configuration at all; sign-in is OAuth, and the
// X-{tenant}-Email / X-{tenant}-Token headers remain for a caller that brings
// its own credential.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services));

// The tenants must be complete before anything serves - a half-configured
// tenant should fail at startup, not at the first request.
builder.Services
    .AddOptions<AtlassianOptions>()
    .Bind(builder.Configuration.GetSection(AtlassianOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<AtlassianOptions>, AtlassianOptionsValidator>();

// Mapping rules are decisions somebody made, not discoveries. Missing entries
// surface as blockers in the plan rather than as guesses, so no validation here.
builder.Services
    .AddOptions<MappingOptions>()
    .Bind(builder.Configuration.GetSection(MappingOptions.SectionName));

// Not validated either: an instance with no registered 3LO app is a legitimate
// state, and this tool still runs on API tokens.
builder.Services
    .AddOptions<OAuthOptions>()
    .Bind(builder.Configuration.GetSection(OAuthOptions.SectionName));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AtlassianClientFactory>();
builder.Services.AddScoped<SourceIssueReader>();
builder.Services.AddScoped<TargetMetadataReader>();
builder.Services.AddScoped<SprintReader>();
builder.Services.AddScoped<TargetIssueWriter>();
builder.Services.AddScoped<ExistingCopyFinder>();
builder.Services.AddScoped<FieldMapper>();
builder.Services.AddScoped<MappingService>();
builder.Services.AddScoped<UserResolver>();
builder.Services.AddScoped<ApplyService>();
builder.Services.AddSingleton<AdfRewriter>();
builder.Services.AddTransient<RateLimitHandler>();

// Injected rather than read off DateTimeOffset.UtcNow, so a test can sit a
// token either side of its expiry without waiting an hour to find out.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ITokenStore, InMemoryTokenStore>();
builder.Services.AddSingleton<TokenRefreshLocks>();
builder.Services.AddScoped<TokenProvider>();
builder.Services.AddScoped<OAuthSession>();
builder.Services.AddScoped<TenantAccessResolver>();

// A third named client, pointed at Atlassian's authorisation server rather than
// at either tenant. Separate for the same reason source and target are separate:
// it must never carry a tenant credential.
builder.Services.AddHttpClient<AtlassianOAuthClient>((services, client) =>
{
    var oauth = services.GetRequiredService<IOptions<OAuthOptions>>().Value;

    client.BaseAddress = new Uri(oauth.AuthorizationServer.TrimEnd('/') + "/");
    client.DefaultRequestHeaders.Accept.Add(new("application/json"));
});

// Two named clients, each pinned to one site. One client with a swappable
// BaseAddress is the shape that eventually writes to the wrong tenant.
AddTenantClient(Tenant.Source);
AddTenantClient(Tenant.Target);

builder.Services.AddExceptionHandler<AtlassianExceptionHandler>();
builder.Services.AddProblemDetails();

// The session cookie is what reaches a tenant, and it is only marked Secure on
// https - an unredirected http hop would carry it in clear.
//
// GOTCHA: with no port configured the redirect middleware tries to discover one
// from the server's own bindings, and when it cannot it logs a warning and lets
// the request through UNREDIRECTED. A site with no HTTPS binding turns the whole
// thing into a silent no-op, so the port is configured rather than discovered.
var httpsPort = builder.Configuration.GetValue<int?>("Https:Port");

if (httpsPort is not null)
{
    builder.Services.AddHttpsRedirection(options => options.HttpsPort = httpsPort);
}

var app = builder.Build();

// Request logging must come BEFORE the exception handler. The other way round,
// every handled 404/409 is logged as an unhandled 500 and the log lies.
app.UseSerilogRequestLogging();
app.UseExceptionHandler();

// Development is exempt: the http profile carries no certificate, and a test
// host would answer 307 to everything instead of running the thing under test.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// In development the SPA is served by Vite on 5174 and these do nothing useful;
// after publish the built SPA lands in wwwroot and they serve it same-origin.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapApi();

// Anything under /api that no endpoint claimed must 404. Without this the
// fallback below answers a mistyped API path with 200 and the SPA shell, which
// looks to the caller like a successful request returning nonsense HTML.
app.Map("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();
return;

void AddTenantClient(Tenant tenant)
{
    builder.Services.AddHttpClient(tenant.ClientName(), client =>
    {
        // No BaseAddress. Under OAuth the REST base embeds the signed-in user's
        // cloud id, and this delegate is handed the ROOT provider - it cannot
        // see the request scope, so a per-user base address here is impossible
        // rather than merely unwise. AtlassianClient composes an absolute URI
        // per call from the base the credentials filter resolved.
        client.DefaultRequestHeaders.Accept.Add(new("application/json"));
    })
    // Copying runs in batches, which is the traffic shape that trips Jira's
    // limiter. Without this a run fails partway through for no actionable reason.
    .AddHttpMessageHandler<RateLimitHandler>();
}

// Exposed so WebApplicationFactory<Program> can host the real app in tests.
public partial class Program;
