using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Permits.Api.Geo;

namespace Permits.Api.Import;

/// <summary>Parses one page of an ArcGIS REST query response (f=json, outSR=4326).</summary>
public static class EsriJsonParser
{
    public static PermitPage ParsePage(JsonDocument document, FieldMapping fields)
    {
        var root = document.RootElement;

        // ArcGIS reports most failures as HTTP 200 with an "error" object
        if (root.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
            int? code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var value) ? value : null;
            throw new ArcGisException(message ?? "ArcGIS returned an error.", code);
        }

        if (!root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
        {
            throw new ArcGisException("Response has no 'features' array. Is the URL a feature layer query?");
        }

        var exceeded = root.TryGetProperty("exceededTransferLimit", out var flag) && flag.ValueKind == JsonValueKind.True;
        var records = new List<PermitRecord>(features.GetArrayLength());
        var skipped = 0;
        foreach (var feature in features.EnumerateArray())
        {
            if (TryParseFeature(feature, fields, out var record))
            {
                records.Add(record);
            }
            else
            {
                skipped++;
            }
        }
        return new PermitPage(records, skipped, exceeded);
    }

    private static bool TryParseFeature(JsonElement feature, FieldMapping fields, [NotNullWhen(true)] out PermitRecord? record)
    {
        record = null;
        if (!feature.TryGetProperty("attributes", out var attributeElement) ||
            attributeElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // Field-name case varies between services
        var attributes = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in attributeElement.EnumerateObject())
        {
            attributes[property.Name] = property.Value;
        }

        var permitNumber = ReadString(attributes, fields.PermitNumber);
        if (permitNumber is null || !TryReadPoint(feature, out var longitude, out var latitude))
        {
            return false;
        }

        record = new PermitRecord(
            PermitNumber: permitNumber,
            Address: ReadString(attributes, fields.Address),
            Folio: ReadString(attributes, fields.Folio),
            PermitType: ReadString(attributes, fields.PermitType)?.ToUpperInvariant(),
            Description: ReadString(attributes, fields.Description),
            ResidentialCommercial: ReadString(attributes, fields.ResidentialCommercial),
            Status: ReadString(attributes, fields.Status),
            IssuedDate: ReadDate(attributes, fields.IssuedDate),
            EstimatedValue: ReadDecimal(attributes, fields.EstimatedValue),
            ContractorName: ReadString(attributes, fields.ContractorName),
            Longitude: longitude,
            Latitude: latitude);
        return true;
    }

    private static bool TryReadPoint(JsonElement feature, out double longitude, out double latitude)
    {
        longitude = latitude = double.NaN;
        if (!feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object ||
            !geometry.TryGetProperty("x", out var x) || !geometry.TryGetProperty("y", out var y) ||
            x.ValueKind != JsonValueKind.Number || y.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        // With outSR=4326, x is longitude and y is latitude
        longitude = x.GetDouble();
        latitude = y.GetDouble();
        return GeoValidation.IsValidLonLat(longitude, latitude) && !(longitude == 0 && latitude == 0);
    }

    internal static string? ReadString(Dictionary<string, JsonElement> attributes, string field)
    {
        if (!attributes.TryGetValue(field, out var value))
        {
            return null;
        }
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(), // e.g. folio numbers stored as numbers
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    internal static DateOnly? ReadDate(Dictionary<string, JsonElement> attributes, string field)
    {
        if (!attributes.TryGetValue(field, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            // Esri date fields are epoch milliseconds in UTC
            var epochMs = value.TryGetInt64(out var whole) ? whole : (long)value.GetDouble();
            return DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime);
        }

        if (value.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return DateOnly.FromDateTime(parsed);
        }
        return null;
    }

    internal static decimal? ReadDecimal(Dictionary<string, JsonElement> attributes, string field)
    {
        if (!attributes.TryGetValue(field, out var value))
        {
            return null;
        }

        decimal? number = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var d) => d,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };

        // Fit DECIMAL(14,2); drop values that are clearly bad data
        return number is >= 0 and < 1_000_000_000_000m ? Math.Round(number.Value, 2) : null;
    }
}
