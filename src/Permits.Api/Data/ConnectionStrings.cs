using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Permits.Api.Data;

public static class ConnectionStrings
{
    public const string Name = "Permits";

    /// <summary>
    /// Read at use time rather than at startup, so tests and containers can
    /// supply the value through any configuration source.
    /// </summary>
    public static string Require(IConfiguration configuration) =>
        configuration.GetConnectionString(Name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Connection string '{Name}' is not configured. Set ConnectionStrings__{Name}.");
}

public sealed class DatabaseHealthCheck(PermitsDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Cannot connect to SQL Server.");
}
