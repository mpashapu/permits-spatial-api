using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Permits.Api.Contracts;
using Permits.Api.Data;
using Permits.Api.Geo;

namespace Permits.Api.Endpoints;

public static class PermitEndpoints
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 1_000;
    public const int MaxGeoJsonLimit = 5_000;
    private const double DefaultRadiusMeters = 500;

    // Optional parameters can't be omitted inside expression trees, so DistanceMeters is passed explicitly.
    private static readonly Expression<Func<Permit, PermitDto>> ToDto = p => new PermitDto(
        p.PermitNumber, p.Address, p.Folio, p.PermitType, p.Description, p.ResidentialCommercial, p.Status,
        p.IssuedDate, p.EstimatedValue, p.ContractorName, p.Location.X, p.Location.Y, null);

    public static IEndpointRouteBuilder MapPermitEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/permits").WithTags("Permits");

        group.MapGet("/near", Near)
            .WithName("PermitsNear")
            .WithSummary("Permits within a radius of a point, nearest first");
        group.MapPost("/within", Within)
            .WithName("PermitsWithin")
            .WithSummary("Permits inside a GeoJSON Polygon or MultiPolygon (either ring orientation)");
        group.MapGet("/stats", Stats)
            .WithName("PermitStats")
            .WithSummary("Permit counts and total estimated value by type inside a bounding box");
        group.MapGet("/geojson", GeoJson)
            .WithName("PermitsGeoJson")
            .WithSummary("Permits inside a bounding box as a GeoJSON FeatureCollection, for map clients");
        group.MapGet("/{permitNumber}", GetByNumber)
            .WithName("GetPermit")
            .WithSummary("A single permit by permit number");

        return app;
    }

    private static async Task<Results<Ok<PermitListResponse>, ValidationProblem>> Near(
        [AsParameters] NearQuery query, PermitsDbContext db, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (query.Lat is null || query.Lon is null || !GeoValidation.IsValidLonLat(query.Lon.Value, query.Lat.Value))
        {
            errors["lat"] = ["lat and lon are required, in decimal degrees (lat -90 to 90, lon -180 to 180)."];
        }
        var radius = query.RadiusMeters ?? DefaultRadiusMeters;
        if (radius is <= 0 or > GeoValidation.MaxRadiusMeters)
        {
            errors["radiusMeters"] = [$"radiusMeters must be greater than 0 and at most {GeoValidation.MaxRadiusMeters}."];
        }
        var limit = ValidateFilters(query.Type, query.IssuedFrom, query.IssuedTo, query.Limit, DefaultLimit, MaxLimit, errors);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var origin = GeoFactory.Point(query.Lon!.Value, query.Lat!.Value);

        // geography STDistance: meters on the WGS84 ellipsoid, served by the spatial index
        var permits = await db.Permits.AsNoTracking()
            .Where(p => p.Location.IsWithinDistance(origin, radius))
            .ApplyFilters(query.Type, query.IssuedFrom, query.IssuedTo)
            .OrderBy(p => p.Location.Distance(origin))
            .Take(limit)
            .Select(p => new PermitDto(
                p.PermitNumber, p.Address, p.Folio, p.PermitType, p.Description, p.ResidentialCommercial, p.Status,
                p.IssuedDate, p.EstimatedValue, p.ContractorName, p.Location.X, p.Location.Y,
                p.Location.Distance(origin)))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new PermitListResponse(permits.Count, permits));
    }

    private static async Task<Results<Ok<PermitListResponse>, ValidationProblem>> Within(
        [FromBody] JsonElement body, [AsParameters] FilterQuery query, PermitsDbContext db,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var area = GeoValidation.PrepareSearchArea(ReadGeometry(body), errors);
        var limit = ValidateFilters(query.Type, query.IssuedFrom, query.IssuedTo, query.Limit, DefaultLimit, MaxLimit, errors);
        if (errors.Count > 0 || area is null)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var permits = await db.Permits.AsNoTracking()
            .Where(p => p.Location.Intersects(area))
            .ApplyFilters(query.Type, query.IssuedFrom, query.IssuedTo)
            .OrderByDescending(p => p.IssuedDate)
            .ThenBy(p => p.PermitNumber)
            .Take(limit)
            .Select(ToDto)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new PermitListResponse(permits.Count, permits));
    }

    private static async Task<Results<Ok<StatsResponse>, ValidationProblem>> Stats(
        [AsParameters] BboxQuery query, PermitsDbContext db, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!GeoValidation.TryParseBbox(query.Bbox, out var envelope, out var bboxError))
        {
            errors["bbox"] = [bboxError];
        }
        ValidateFilters(query.Type, query.IssuedFrom, query.IssuedTo, query.Limit, DefaultLimit, MaxLimit, errors);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var area = GeoValidation.ToPolygon(envelope);
        var rows = await db.Permits.AsNoTracking()
            .Where(p => p.Location.Intersects(area))
            .ApplyFilters(query.Type, query.IssuedFrom, query.IssuedTo)
            .GroupBy(p => p.PermitType)
            .Select(g => new { Type = g.Key, Count = g.Count(), Total = g.Sum(p => p.EstimatedValue) ?? 0m })
            .ToListAsync(cancellationToken);

        var byType = rows
            .OrderByDescending(r => r.Count)
            .Select(r => new TypeStat(r.Type, r.Count, r.Total))
            .ToList();
        return TypedResults.Ok(new StatsResponse(
            byType.Sum(s => s.Count), byType.Sum(s => s.TotalEstimatedValue), byType));
    }

    private static async Task<Results<Ok<GeoJsonFeatureCollection>, ValidationProblem>> GeoJson(
        [AsParameters] BboxQuery query, PermitsDbContext db, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!GeoValidation.TryParseBbox(query.Bbox, out var envelope, out var bboxError))
        {
            errors["bbox"] = [bboxError];
        }
        var limit = ValidateFilters(query.Type, query.IssuedFrom, query.IssuedTo, query.Limit, MaxLimit, MaxGeoJsonLimit, errors);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var area = GeoValidation.ToPolygon(envelope);
        var permits = await db.Permits.AsNoTracking()
            .Where(p => p.Location.Intersects(area))
            .ApplyFilters(query.Type, query.IssuedFrom, query.IssuedTo)
            .OrderBy(p => p.Id)
            .Take(limit)
            .Select(ToDto)
            .ToListAsync(cancellationToken);

        var features = permits
            .Select(p => new GeoJsonFeature(p.PermitNumber, new GeoJsonPoint([p.Longitude, p.Latitude]), p))
            .ToList();
        return TypedResults.Ok(new GeoJsonFeatureCollection(features));
    }

    private static async Task<Results<Ok<PermitDto>, NotFound>> GetByNumber(
        string permitNumber, PermitsDbContext db, CancellationToken cancellationToken)
    {
        var permit = await db.Permits.AsNoTracking()
            .Where(p => p.PermitNumber == permitNumber)
            .Select(ToDto)
            .FirstOrDefaultAsync(cancellationToken);
        return permit is null ? TypedResults.NotFound() : TypedResults.Ok(permit);
    }

    private static Geometry? ReadGeometry(JsonElement body)
    {
        // Accept a bare geometry or a Feature wrapping one
        var json = body.ValueKind == JsonValueKind.Object &&
                   body.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                   type.GetString() == "Feature" &&
                   body.TryGetProperty("geometry", out var inner)
            ? inner
            : body;
        try
        {
            return json.Deserialize<Geometry>(GeoFactory.GeoJsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null; // PrepareSearchArea reports the error
        }
    }

    private static int ValidateFilters(string? type, DateOnly? from, DateOnly? to, int? limit,
        int defaultLimit, int maxLimit, Dictionary<string, string[]> errors)
    {
        if (type is { Length: > 10 })
        {
            errors["type"] = ["type is a short code such as BLDG, ELEC or PLUM."];
        }
        if (from is { } start && to is { } end && start > end)
        {
            errors["issuedFrom"] = ["issuedFrom must be on or before issuedTo."];
        }
        var value = limit ?? defaultLimit;
        if (value < 1 || value > maxLimit)
        {
            errors["limit"] = [$"limit must be between 1 and {maxLimit}."];
        }
        return value;
    }

    private static IQueryable<Permit> ApplyFilters(this IQueryable<Permit> query, string? type, DateOnly? from, DateOnly? to)
    {
        if (!string.IsNullOrWhiteSpace(type))
        {
            var code = type.Trim().ToUpperInvariant();
            query = query.Where(p => p.PermitType == code);
        }
        if (from is { } start)
        {
            query = query.Where(p => p.IssuedDate >= start);
        }
        if (to is { } end)
        {
            query = query.Where(p => p.IssuedDate <= end);
        }
        return query;
    }
}

public sealed record GeoJsonPoint(double[] Coordinates)
{
    public string Type => "Point";
}

public sealed record GeoJsonFeature(string Id, GeoJsonPoint Geometry, PermitDto Properties)
{
    public string Type => "Feature";
}

public sealed record GeoJsonFeatureCollection(IReadOnlyList<GeoJsonFeature> Features)
{
    public string Type => "FeatureCollection";
}
