using Microsoft.EntityFrameworkCore;
using Permits.Api;
using Permits.Api.Data;
using Permits.Api.Endpoints;
using Permits.Api.Import;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Connection string is resolved when first needed, not at startup
builder.Services.AddDbContext<PermitsDbContext>((services, options) =>
    options.UseSqlServer(
        ConnectionStrings.Require(services.GetRequiredService<IConfiguration>()),
        sql => sql.UseNetTopologySuite()));

builder.Services.Configure<ImportOptions>(builder.Configuration.GetSection(ImportOptions.SectionName));
builder.Services.AddHttpClient<ArcGisFeatureLayerSource>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("permits-spatial-api/1.0");
});
builder.Services.AddScoped(services => new PermitImporter(
    ConnectionStrings.Require(services.GetRequiredService<IConfiguration>()),
    services.GetRequiredService<ILogger<PermitImporter>>()));

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .WithMethods("GET", "POST")
    .AllowAnyHeader()));

var app = builder.Build();

if (Commands.IsCommand(args))
{
    return await Commands.RunAsync(app, args);
}

if (app.Configuration.GetValue<bool>("Database:InitializeSchemaOnStartup"))
{
    await SchemaInitializer.ApplyAsync(ConnectionStrings.Require(app.Configuration));
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();

app.MapOpenApi();
app.MapScalarApiReference(); // interactive docs at /scalar
app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();
app.MapHealthChecks("/health");
app.MapPermitEndpoints();

await app.RunAsync();
return 0;

/// <summary>Exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program
{
}
