using Eskrb.Api.Auth;
using Eskrb.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
// Every error response is RFC 9457 problem details with a human-readable `detail`, never an empty body.
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
{
    var pd = ctx.ProblemDetails;
    pd.Extensions.TryAdd("traceId", System.Diagnostics.Activity.Current?.Id ?? ctx.HttpContext.TraceIdentifier);
    if (!string.IsNullOrEmpty(pd.Detail)) return;
    pd.Detail = pd switch
    {
        // model-binding failures: flatten {"field": ["msg"]} into one readable sentence
        Microsoft.AspNetCore.Mvc.ValidationProblemDetails v when v.Errors.Count > 0 =>
            string.Join(" ", v.Errors.SelectMany(e => e.Value.Select(m => $"{e.Key}: {m}"))),
        _ => pd.Status switch
        {
            400 => "The request is malformed.",
            401 => "Missing or invalid bearer token. Send 'Authorization: Bearer <token>'.",
            403 => "Your account's role is not allowed to do this.",
            404 => "Not found, or you don't have access to it.",
            429 => "Too many requests. Wait a minute and try again.",
            405 => "This HTTP method is not allowed here.",
            415 => "Unsupported Content-Type for this endpoint (JSON bodies use application/json; photos use multipart/form-data).",
            500 => "Unexpected server error. Quote the traceId when reporting it.",
            _ => null,
        },
    };
});
builder.Services.AddSingleton<Eskrb.Api.Services.Media.IMediaStore, Eskrb.Api.Services.Media.DiskMediaStore>();
// Placeholder until an LLM provider is chosen (see Integrations/IExtractor.cs).
// LLM extraction: OpenAI when OpenAI:ApiKey is configured, otherwise the do-nothing extractor (notes saved, no flags).
if (!string.IsNullOrWhiteSpace(builder.Configuration["OpenAI:ApiKey"]))
    builder.Services.AddHttpClient<Eskrb.Api.Integrations.IExtractor, Eskrb.Api.Integrations.OpenAiExtractor>(c => c.Timeout = TimeSpan.FromSeconds(30));
else
    builder.Services.AddScoped<Eskrb.Api.Integrations.IExtractor, Eskrb.Api.Integrations.NullExtractor>();
// Push notifications (Expo). Only the admin screen sends for now; Notifications:Enabled=false is the kill switch.
builder.Services.AddHttpClient<Eskrb.Api.Integrations.INotificationService, Eskrb.Api.Integrations.ExpoPushService>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient<Eskrb.Api.Integrations.ISttKeyIssuer, Eskrb.Api.Integrations.SonioxKeyIssuer>(c => c.Timeout = TimeSpan.FromSeconds(10));
// Each key request costs money upstream: cap per patient (token bucket per user id).
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    // LLM-backed text notes cost money: cap per patient.
    o.AddPolicy("llm", http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("stt", http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 6, Window = TimeSpan.FromMinutes(1) }));
});
// Request log: method, path, status, duration only. Never headers/bodies (bearer tokens, health data).
builder.Services.AddHttpLogging(o =>
    o.LoggingFields = Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestMethod
        | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestPath
        | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.ResponseStatusCode
        | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.Duration);
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    // Relative server: Swagger UI resolves it against the page's own origin, so "Try it out" uses https when the page
    // was loaded over https (behind a TLS-terminating proxy the app itself only sees http and would emit http://).
    doc.Servers = [new Microsoft.OpenApi.OpenApiServer { Url = "/" }];
    doc.Components ??= new();
    doc.Components.SecuritySchemes ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiSecurityScheme>();
    doc.Components.SecuritySchemes["Bearer"] = new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.SecuritySchemeType.Http,
        Scheme = "bearer",
    };
    doc.Security =
    [
        new Microsoft.OpenApi.OpenApiSecurityRequirement
        {
            [new Microsoft.OpenApi.OpenApiSecuritySchemeReference("Bearer", doc)] = [],
        },
    ];
    return Task.CompletedTask;
}));

var connectionString = builder.Configuration.GetConnectionString("Default") ?? "Data Source=data/eskrb.db";
var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
if (Path.GetDirectoryName(dataSource) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
builder.Services.AddDbContext<EskrbDb>(o => o.UseSqlite(connectionString));

builder.Services
    .AddAuthentication(BearerDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, BearerTokenHandler>(BearerDefaults.Scheme, null);
builder.Services.AddAuthorization();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<EskrbDb>();
    var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Seeder");
    await Seeder.SeedAsync(db, app.Configuration, log);
}

app.UseExceptionHandler();   // unhandled exception -> 500 problem details (no stack trace, no internals)
app.UseStatusCodePages();    // bodyless 401/403/404/415/... -> problem details
app.UseHttpLogging();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapOpenApi();
app.UseSwaggerUI(o =>
{
    o.RoutePrefix = "";
    o.SwaggerEndpoint("/openapi/v1.json", "eSkrb API");
});

app.MapControllers();

app.Run();

// Exposed so integration tests can boot the app (WebApplicationFactory<Program>).
public partial class Program;
