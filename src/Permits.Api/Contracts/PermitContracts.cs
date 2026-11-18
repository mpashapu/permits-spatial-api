namespace Permits.Api.Contracts;

public sealed record PermitDto(
    string PermitNumber,
    string? Address,
    string? Folio,
    string? Type,
    string? Description,
    string? ResidentialCommercial,
    string? Status,
    DateOnly? IssuedDate,
    decimal? EstimatedValue,
    string? ContractorName,
    double Longitude,
    double Latitude,
    double? DistanceMeters = null);

public sealed record PermitListResponse(int Count, IReadOnlyList<PermitDto> Permits);

public sealed record TypeStat(string? Type, int Count, decimal TotalEstimatedValue);

public sealed record StatsResponse(int TotalPermits, decimal TotalEstimatedValue, IReadOnlyList<TypeStat> ByType);

/// <summary>Filters shared by every search endpoint.</summary>
public record FilterQuery(string? Type, DateOnly? IssuedFrom, DateOnly? IssuedTo, int? Limit);

public sealed record NearQuery(
    double? Lat,
    double? Lon,
    double? RadiusMeters,
    string? Type,
    DateOnly? IssuedFrom,
    DateOnly? IssuedTo,
    int? Limit);

public sealed record BboxQuery(string? Bbox, string? Type, DateOnly? IssuedFrom, DateOnly? IssuedTo, int? Limit);
