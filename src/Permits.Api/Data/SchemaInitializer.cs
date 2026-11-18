using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Permits.Api.Data;

/// <summary>Creates the database (if needed) and applies the embedded schema.sql.</summary>
public static partial class SchemaInitializer
{
    private const string ResourceName = "Permits.Api.Data.schema.sql";

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BatchSeparator();

    public static async Task ApplyAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseExistsAsync(connectionString, cancellationToken);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var batch in ReadBatches())
        {
            await using var command = new SqlCommand(batch, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public static IReadOnlyList<string> ReadBatches()
    {
        using var stream = typeof(SchemaInitializer).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} not found.");
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();
        return BatchSeparator()
            .Split(sql)
            .Select(batch => batch.Trim())
            .Where(batch => batch.Length > 0)
            .ToList();
    }

    private static async Task EnsureDatabaseExistsAsync(string connectionString, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var database = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(database) || database.Equals("master", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        builder.InitialCatalog = "master";
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        // EXEC() only accepts literals and variables, so build the statement first.
        // QUOTENAME guards against injection through the database name.
        await using var command = new SqlCommand(
            """
            IF DB_ID(@name) IS NULL
            BEGIN
                DECLARE @sql NVARCHAR(300) = N'CREATE DATABASE ' + QUOTENAME(@name);
                EXEC (@sql);
            END
            """, connection);
        command.Parameters.AddWithValue("@name", database);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
