using System.Data;
using Microsoft.Data.SqlClient;

namespace Permits.Api.Import;

/// <summary>
/// Loads permits with SqlBulkCopy into a staging table, then MERGEs them into
/// dbo.Permits in one transaction. The live table always mirrors the latest
/// complete pull, and readers never see a half-loaded state.
/// </summary>
public sealed class PermitImporter(string connectionString, ILogger<PermitImporter> logger)
{
    private static readonly (string Column, int MaxLength)[] TextColumns =
    [
        ("PermitNumber", 50), ("Address", 200), ("Folio", 20), ("PermitType", 10), ("Description", 400),
        ("ResidentialCommercial", 10), ("Status", 20), ("ContractorName", 200),
    ];

    // geography::Point takes LATITUDE first, unlike NTS/GeoJSON (longitude first).
    // EXISTS(... EXCEPT ...) is a NULL-safe "did anything change" check.
    private const string MergeSql = """
        DECLARE @changes TABLE (Action NVARCHAR(10) NOT NULL);

        WITH latest AS
        (
            SELECT *,
                   ROW_NUMBER() OVER (PARTITION BY PermitNumber ORDER BY IssuedDate DESC) AS rn
            FROM dbo.PermitsStaging
        )
        MERGE dbo.Permits WITH (HOLDLOCK) AS t
        USING (SELECT * FROM latest WHERE rn = 1) AS s
            ON t.PermitNumber = s.PermitNumber
        WHEN MATCHED AND EXISTS
        (
            SELECT s.Address, s.Folio, s.PermitType, s.Description, s.ResidentialCommercial, s.Status,
                   s.IssuedDate, s.EstimatedValue, s.ContractorName, s.Longitude, s.Latitude
            EXCEPT
            SELECT t.Address, t.Folio, t.PermitType, t.Description, t.ResidentialCommercial, t.Status,
                   t.IssuedDate, t.EstimatedValue, t.ContractorName, t.Location.Long, t.Location.Lat
        ) THEN
            UPDATE SET
                Address = s.Address, Folio = s.Folio, PermitType = s.PermitType,
                Description = s.Description, ResidentialCommercial = s.ResidentialCommercial,
                Status = s.Status, IssuedDate = s.IssuedDate, EstimatedValue = s.EstimatedValue,
                ContractorName = s.ContractorName,
                Location = geography::Point(s.Latitude, s.Longitude, 4326),
                SourceUpdatedUtc = SYSUTCDATETIME()
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (PermitNumber, Address, Folio, PermitType, Description, ResidentialCommercial, Status,
                    IssuedDate, EstimatedValue, ContractorName, Location)
            VALUES (s.PermitNumber, s.Address, s.Folio, s.PermitType, s.Description, s.ResidentialCommercial,
                    s.Status, s.IssuedDate, s.EstimatedValue, s.ContractorName,
                    geography::Point(s.Latitude, s.Longitude, 4326))
        WHEN NOT MATCHED BY SOURCE THEN
            DELETE
        OUTPUT $action INTO @changes;

        SELECT
            ISNULL(SUM(CASE WHEN Action = 'INSERT' THEN 1 ELSE 0 END), 0) AS Inserted,
            ISNULL(SUM(CASE WHEN Action = 'UPDATE' THEN 1 ELSE 0 END), 0) AS Updated,
            ISNULL(SUM(CASE WHEN Action = 'DELETE' THEN 1 ELSE 0 END), 0) AS Deleted
        FROM @changes;
        """;

    public async Task<ImportResult> ImportAsync(IPermitSource source, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await ExecuteAsync(connection, null, "TRUNCATE TABLE dbo.PermitsStaging;", cancellationToken);

        int read = 0, skipped = 0;
        await foreach (var page in source.ReadAsync(cancellationToken))
        {
            skipped += page.Skipped;
            if (page.Records.Count == 0)
            {
                continue;
            }
            using var bulk = new SqlBulkCopy(connection)
            {
                DestinationTableName = "dbo.PermitsStaging",
                BatchSize = 5_000,
                BulkCopyTimeout = 300,
            };
            var table = ToDataTable(page.Records);
            foreach (DataColumn column in table.Columns)
            {
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }
            await bulk.WriteToServerAsync(table, cancellationToken);
            read += page.Records.Count;
        }

        // Safety: an empty pull (service outage, bad filter) must not wipe the table
        if (read == 0)
        {
            throw new InvalidOperationException("Source returned no usable records; existing data left unchanged.");
        }

        var duplicates = await ScalarAsync(connection,
            "SELECT COUNT(*) - COUNT(DISTINCT PermitNumber) FROM dbo.PermitsStaging;", cancellationToken);

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var merge = new SqlCommand(MergeSql, connection, transaction) { CommandTimeout = 600 };
        int inserted, updated, deleted;
        await using (var reader = await merge.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            (inserted, updated, deleted) = (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
        }
        await transaction.CommitAsync(cancellationToken);

        var result = new ImportResult(read, skipped, duplicates, inserted, updated, deleted);
        logger.LogInformation(
            "Import complete: {Read} read, {Skipped} skipped, {Duplicates} duplicate ids, {Inserted} inserted, {Updated} updated, {Deleted} deleted",
            result.Read, result.Skipped, result.DuplicatesDropped, result.Inserted, result.Updated, result.Deleted);
        return result;
    }

    internal static DataTable ToDataTable(IReadOnlyList<PermitRecord> records)
    {
        var table = new DataTable();
        foreach (var (column, _) in TextColumns)
        {
            table.Columns.Add(column, typeof(string));
        }
        table.Columns.Add("IssuedDate", typeof(DateTime));
        table.Columns.Add("EstimatedValue", typeof(decimal));
        table.Columns.Add("Longitude", typeof(double));
        table.Columns.Add("Latitude", typeof(double));

        foreach (var r in records)
        {
            var row = table.NewRow();
            string?[] text = [r.PermitNumber, r.Address, r.Folio, r.PermitType, r.Description,
                r.ResidentialCommercial, r.Status, r.ContractorName];
            for (var i = 0; i < TextColumns.Length; i++)
            {
                row[TextColumns[i].Column] = Truncate(text[i], TextColumns[i].MaxLength) ?? (object)DBNull.Value;
            }
            row["IssuedDate"] = r.IssuedDate is { } date ? (object)date.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
            row["EstimatedValue"] = r.EstimatedValue is { } value ? (object)value : DBNull.Value;
            row["Longitude"] = r.Longitude;
            row["Latitude"] = r.Latitude;
            table.Rows.Add(row);
        }
        return table;
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length > maxLength ? value[..maxLength] : value;

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction? transaction, string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }
}
