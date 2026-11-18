using NetTopologySuite.Geometries;

namespace Permits.Api.Data;

public sealed class Permit
{
    public int Id { get; set; }
    public string PermitNumber { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? Folio { get; set; }
    public string? PermitType { get; set; }
    public string? Description { get; set; }
    public string? ResidentialCommercial { get; set; }
    public string? Status { get; set; }
    public DateOnly? IssuedDate { get; set; }
    public decimal? EstimatedValue { get; set; }
    public string? ContractorName { get; set; }

    /// <summary>WGS84 point (SRID 4326). X is longitude, Y is latitude.</summary>
    public Point Location { get; set; } = null!;

    public DateTime SourceUpdatedUtc { get; set; }
}
