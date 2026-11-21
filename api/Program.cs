using Eskrb.Api.Auth;
using Eskrb.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSingleton<Eskrb.Api.Services.Media.IMediaStore, Eskrb.Api.Services.Media.DiskMediaStore>();
// Placeholder until an LLM provider is chosen (see Integrations/IExtractor.cs).
builder.Services.AddScoped<Eskrb.Api.Integrations.IExtractor, Eskrb.Api.Integrations.NullExtractor>();
// Request log: method, path, status, duration only. Never headers/bodies (bearer tokens, health data).
builder.Services.AddHttpLogging(o =>
    o.LoggingFields = Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestMethod
        | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestPath
        | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.ResponseStatusCode
        | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.Duration);
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
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

app.UseHttpLogging();

app.UseAuthentication();
app.UseAuthorization();

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
