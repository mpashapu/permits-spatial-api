namespace Permits.Api.Import;

public sealed class ImportOptions
{
    public const string SectionName = "Import";

    /// <summary>ArcGIS feature layer URL, e.g. https://.../FeatureServer/0 or .../MapServer/0.</summary>
    public string? LayerUrl { get; set; }

    public string Where { get; set; } = "1=1";

    /// <summary>Records per request. Keep at or below the layer's maxRecordCount.</summary>
    public int PageSize { get; set; } = 1000;

    public string ObjectIdField { get; set; } = "OBJECTID";

    public FieldMapping Fields { get; set; } = new();
}

/// <summary>
/// Source field names. Defaults match Miami-Dade County's MDC.BuildingPermit layer;
/// override in configuration to import another jurisdiction's permits.
/// </summary>
public sealed class FieldMapping
{
    public string PermitNumber { get; set; } = "ID";
    public string Address { get; set; } = "ADDRESS";
    public string Folio { get; set; } = "FOLIO";
    public string PermitType { get; set; } = "TYPE";
    public string Description { get; set; } = "DESC1";
    public string ResidentialCommercial { get; set; } = "RESCOMM";
    public string Status { get; set; } = "STATUS";
    public string IssuedDate { get; set; } = "ISSUDATE";
    public string EstimatedValue { get; set; } = "ESTVALUE";
    public string ContractorName { get; set; } = "CONTRNAME";

    public IEnumerable<string> All() =>
    [
        PermitNumber, Address, Folio, PermitType, Description, ResidentialCommercial,
        Status, IssuedDate, EstimatedValue, ContractorName,
    ];
}

public sealed record PermitRecord(
    string PermitNumber,
    string? Address,
    string? Folio,
    string? PermitType,
    string? Description,
    string? ResidentialCommercial,
    string? Status,
    DateOnly? IssuedDate,
    decimal? EstimatedValue,
    string? ContractorName,
    double Longitude,
    double Latitude);

/// <param name="Records">Usable records on this page.</param>
/// <param name="Skipped">Features dropped for missing ids or geometry.</param>
/// <param name="ExceededTransferLimit">True when the server has more pages.</param>
public sealed record PermitPage(IReadOnlyList<PermitRecord> Records, int Skipped, bool ExceededTransferLimit);

public sealed record ImportResult(
    int Read,
    int Skipped,
    int DuplicatesDropped,
    int Inserted,
    int Updated,
    int Deleted);

public interface IPermitSource
{
    IAsyncEnumerable<PermitPage> ReadAsync(CancellationToken cancellationToken = default);
}

public sealed class ArcGisException(string message, int? code = null) : Exception(message)
{
    public int? Code { get; } = code;
}
