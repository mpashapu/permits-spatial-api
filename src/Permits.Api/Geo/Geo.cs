using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using NetTopologySuite;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Converters;

namespace Permits.Api.Geo;

public static class GeoFactory
{
    public const int Wgs84 = 4326;

    /// <summary>All geometries sent to SQL Server geography must carry SRID 4326.</summary>
    public static readonly GeometryFactory Instance =
        NtsGeometryServices.Instance.CreateGeometryFactory(srid: Wgs84);

    /// <summary>NTS coordinates are (X = longitude, Y = latitude). Mixing these up is the classic bug.</summary>
    public static Point Point(double longitude, double latitude) =>
        Instance.CreatePoint(new Coordinate(longitude, latitude));

    /// <summary>JSON options that read and write GeoJSON geometries.</summary>
    public static readonly JsonSerializerOptions GeoJsonOptions = CreateGeoJsonOptions();

    private static JsonSerializerOptions CreateGeoJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new GeoJsonConverterFactory(Instance));
        return options;
    }
}

public static class GeoValidation
{
    public const double MaxRadiusMeters = 5_000;
    public const double MaxSpanDegrees = 1.0; // keeps searches to roughly county size

    public static bool IsValidLonLat(double longitude, double latitude) =>
        double.IsFinite(longitude) && double.IsFinite(latitude) &&
        longitude is >= -180 and <= 180 && latitude is >= -90 and <= 90;

    /// <summary>Parses "minLon,minLat,maxLon,maxLat".</summary>
    public static bool TryParseBbox(string? value, out Envelope envelope, out string error)
    {
        envelope = new Envelope();
        error = string.Empty;
        var parts = value?.Split(',', StringSplitOptions.TrimEntries) ?? [];
        var numbers = new double[4];
        var parsed = parts.Length == 4;
        for (var i = 0; parsed && i < 4; i++)
        {
            parsed = double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]);
        }
        if (!parsed)
        {
            error = "Use bbox=minLon,minLat,maxLon,maxLat (decimal degrees).";
            return false;
        }

        var (minLon, minLat, maxLon, maxLat) = (numbers[0], numbers[1], numbers[2], numbers[3]);
        if (!IsValidLonLat(minLon, minLat) || !IsValidLonLat(maxLon, maxLat))
        {
            error = "Coordinates are out of range. Longitude comes first.";
            return false;
        }
        if (minLon >= maxLon || minLat >= maxLat)
        {
            error = "Min values must be smaller than max values.";
            return false;
        }
        if (maxLon - minLon > MaxSpanDegrees || maxLat - minLat > MaxSpanDegrees)
        {
            error = $"The box can span at most {MaxSpanDegrees} degree in each direction.";
            return false;
        }

        envelope = new Envelope(minLon, maxLon, minLat, maxLat);
        return true;
    }

    /// <summary>Counter-clockwise rectangle, ready for SQL Server geography.</summary>
    public static Polygon ToPolygon(Envelope e) =>
        GeoFactory.Instance.CreatePolygon(
        [
            new Coordinate(e.MinX, e.MinY),
            new Coordinate(e.MaxX, e.MinY),
            new Coordinate(e.MaxX, e.MaxY),
            new Coordinate(e.MinX, e.MaxY),
            new Coordinate(e.MinX, e.MinY),
        ]);

    /// <summary>
    /// Validates a client-supplied search area and fixes ring orientation.
    /// Returns null and fills <paramref name="errors"/> when the geometry can't be used.
    /// </summary>
    public static Geometry? PrepareSearchArea(Geometry? geometry, IDictionary<string, string[]> errors)
    {
        if (geometry is not (Polygon or MultiPolygon))
        {
            errors["geometry"] = ["Body must be a GeoJSON Polygon or MultiPolygon geometry."];
            return null;
        }
        if (geometry.Coordinates.Any(c => !IsValidLonLat(c.X, c.Y)))
        {
            errors["geometry"] = ["Coordinates must be [longitude, latitude] in WGS84."];
            return null;
        }
        if (!geometry.IsValid)
        {
            errors["geometry"] = ["Polygon is not valid (check for self-intersections or unclosed rings)."];
            return null;
        }
        var extent = geometry.EnvelopeInternal;
        if (extent.Width > MaxSpanDegrees || extent.Height > MaxSpanDegrees)
        {
            errors["geometry"] = [$"Search area can span at most {MaxSpanDegrees} degree in each direction."];
            return null;
        }

        Geometry oriented = geometry switch
        {
            Polygon polygon => OrientForSqlServer(polygon),
            MultiPolygon multi => GeoFactory.Instance.CreateMultiPolygon(
                multi.Geometries.Cast<Polygon>().Select(OrientForSqlServer).ToArray()),
            _ => throw new UnreachableException(),
        };
        oriented.SRID = GeoFactory.Wgs84;
        return oriented;
    }

    /// <summary>
    /// SQL Server geography uses the "left-hand rule": the outer ring must be
    /// counter-clockwise. A clockwise ring means "everything on Earth except
    /// this polygon", which silently returns the wrong rows (or errors for
    /// shapes larger than a hemisphere). GeoJSON clients send either
    /// orientation, so normalize: shell CCW, holes CW.
    /// </summary>
    public static Polygon OrientForSqlServer(Polygon polygon)
    {
        var shell = Orient(polygon.Shell, counterClockwise: true);
        var holes = polygon.Holes.Select(h => Orient(h, counterClockwise: false)).ToArray();
        return GeoFactory.Instance.CreatePolygon(shell, holes);
    }

    private static LinearRing Orient(LinearRing ring, bool counterClockwise)
    {
        var coordinates = ring.Coordinates;
        if (Orientation.IsCCW(coordinates) != counterClockwise)
        {
            // Enumerable.Reverse explicitly: in C# 14, array.Reverse() binds to the
            // in-place MemoryExtensions.Reverse(Span<T>), which returns void.
            coordinates = Enumerable.Reverse(coordinates).ToArray();
        }
        return GeoFactory.Instance.CreateLinearRing(coordinates);
    }
}
