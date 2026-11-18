using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Permits.Api.Import;

/// <summary>Pages through an ArcGIS feature layer with resultOffset / resultRecordCount.</summary>
public sealed class ArcGisFeatureLayerSource(
    HttpClient http,
    IOptions<ImportOptions> options,
    ILogger<ArcGisFeatureLayerSource> logger) : IPermitSource
{
    private const int MaxPages = 10_000;

    public async IAsyncEnumerable<PermitPage> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.LayerUrl))
        {
            throw new InvalidOperationException(
                "Import:LayerUrl is not configured. Set it to the feature layer URL (ending in /FeatureServer/0 or /MapServer/0).");
        }

        var offset = 0;
        for (var page = 1; page <= MaxPages; page++)
        {
            var url = BuildQueryUrl(settings, offset);
            using var response = await http.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var result = EsriJsonParser.ParsePage(document, settings.Fields);

            var returned = result.Records.Count + result.Skipped;
            logger.LogInformation("Page {Page}: {Count} features from offset {Offset}", page, returned, offset);
            yield return result;

            if (!result.ExceededTransferLimit || returned == 0)
            {
                yield break;
            }
            // Advance by what the server actually returned; it may cap below PageSize
            offset += returned;
        }

        throw new InvalidOperationException($"Stopped after {MaxPages} pages. Check the layer supports pagination.");
    }

    public static string BuildQueryUrl(ImportOptions settings, int offset)
    {
        var parameters = new Dictionary<string, string>
        {
            ["where"] = settings.Where,
            ["outFields"] = string.Join(',', settings.Fields.All().Append(settings.ObjectIdField).Distinct()),
            ["returnGeometry"] = "true",
            ["outSR"] = "4326", // reproject server-side (the county layer is State Plane feet)
            ["orderByFields"] = settings.ObjectIdField, // stable order is required for offset paging
            ["resultOffset"] = offset.ToString(CultureInfo.InvariantCulture),
            ["resultRecordCount"] = settings.PageSize.ToString(CultureInfo.InvariantCulture),
            ["f"] = "json",
        };
        var query = string.Join('&', parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return $"{settings.LayerUrl!.TrimEnd('/')}/query?{query}";
    }
}

/// <summary>Reads a saved Esri JSON response, e.g. data/sample-permits.json.</summary>
public sealed class FilePermitSource(string path, FieldMapping fields) : IPermitSource
{
    public async IAsyncEnumerable<PermitPage> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        yield return EsriJsonParser.ParsePage(document, fields);
    }
}
