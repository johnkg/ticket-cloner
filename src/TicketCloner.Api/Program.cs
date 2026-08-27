using Microsoft.Extensions.Options;
using Serilog;
using TicketCloner.Api.Atlassian;
using TicketCloner.Api.Configuration;
using TicketCloner.Api.Endpoints;
using TicketCloner.Api.Jira;
using TicketCloner.Api.Mapping;
using TicketCloner.Api.Adf;
using TicketCloner.Api.Apply;

var builder = WebApplication.CreateBuilder(args);

// Optional local override, kept for anyone who would rather not put a live
// credential in appsettings.json. Added LAST, so anything set here BEATS
// appsettings.json - which makes a stale copy of this file a real trap now that
// the credentials are meant to come from appsettings.json. Delete it, do not
// just empty it.
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

// Credentials are NOT validated: holding none is a legitimate state, because
// callers may bring their own headers.
builder.Services
    .AddOptions<StoredCredentialsOptions>()
    .Bind(builder.Configuration.GetSection(StoredCredentialsOptions.SectionName));

// Mapping rules are decisions somebody made, not discoveries. Missing entries
// surface as blockers in the plan rather than as guesses, so no validation here.
builder.Services
    .AddOptions<MappingOptions>()
    .Bind(builder.Configuration.GetSection(MappingOptions.SectionName));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CredentialsResolver>();
builder.Services.AddScoped<AtlassianClientFactory>();
builder.Services.AddScoped<SourceIssueReader>();
builder.Services.AddScoped<TargetMetadataReader>();
builder.Services.AddScoped<TargetIssueWriter>();
builder.Services.AddScoped<FieldMapper>();
builder.Services.AddScoped<MappingService>();
builder.Services.AddScoped<UserResolver>();
builder.Services.AddScoped<ApplyService>();
builder.Services.AddSingleton<AdfRewriter>();
builder.Services.AddTransient<RateLimitHandler>();

// Two named clients, each pinned to one site. One client with a swappable
// BaseAddress is the shape that eventually writes to the wrong tenant.
AddTenantClient(Tenant.Source);
AddTenantClient(Tenant.Target);

builder.Services.AddExceptionHandler<AtlassianExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

// Request logging must come BEFORE the exception handler. The other way round,
// every handled 404/409 is logged as an unhandled 500 and the log lies.
app.UseSerilogRequestLogging();
app.UseExceptionHandler();

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
    builder.Services.AddHttpClient(tenant.ClientName(), (services, client) =>
    {
        var options = services.GetRequiredService<IOptions<AtlassianOptions>>().Value;
        var settings = tenant == Tenant.Source ? options.Source : options.Target;

        client.BaseAddress = new Uri(settings.BaseUri.ToString().TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Accept.Add(new("application/json"));
    })
    // Copying runs in batches, which is the traffic shape that trips Jira's
    // limiter. Without this a run fails partway through for no actionable reason.
    .AddHttpMessageHandler<RateLimitHandler>();
}

// Exposed so WebApplicationFactory<Program> can host the real app in tests.
public partial class Program;
