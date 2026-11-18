using Microsoft.Extensions.Options;
using Permits.Api.Data;
using Permits.Api.Import;

namespace Permits.Api;

/// <summary>
/// Command-line modes that run instead of the web server:
///   init-db                 create the database and schema
///   import                  pull permits from Import:LayerUrl
///   import --sample PATH    load a saved Esri JSON file (e.g. data/sample-permits.json)
/// </summary>
public static class Commands
{
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] is "init-db" or "import";

    public static async Task<int> RunAsync(WebApplication app, string[] args)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Permits.Commands");
        try
        {
            var connectionString = ConnectionStrings.Require(app.Configuration);
            await SchemaInitializer.ApplyAsync(connectionString);
            logger.LogInformation("Schema is up to date.");

            if (args[0] == "init-db")
            {
                return 0;
            }

            await using var scope = app.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var samplePath = GetOption(args, "--sample");
            IPermitSource source = samplePath is not null
                ? new FilePermitSource(samplePath, services.GetRequiredService<IOptions<ImportOptions>>().Value.Fields)
                : services.GetRequiredService<ArcGisFeatureLayerSource>();

            await services.GetRequiredService<PermitImporter>().ImportAsync(source);
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Command '{Command}' failed", args[0]);
            return 1;
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
