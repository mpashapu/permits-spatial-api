using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Permits.Api.Contracts;
using Permits.Api.Data;
using Permits.Api.Import;
using Testcontainers.MsSql;

namespace Permits.Api.Tests.Integration;

/// <summary>
/// Starts SQL Server 2022 in Docker, applies the schema and imports the sample data once.
/// Run only the unit tests without Docker: dotnet test --filter "Category!=Integration"
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;
    public ImportResult FirstImport { get; private set; } = null!;
    public PermitsApiFactory Factory { get; private set; } = null!;

    public static string SamplePath => Path.Combine(AppContext.BaseDirectory, "data", "sample-permits.json");

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = "Permits",
        }.ConnectionString;

        await SchemaInitializer.ApplyAsync(ConnectionString);
        await SchemaInitializer.ApplyAsync(ConnectionString); // must be idempotent
        FirstImport = await ImportSampleAsync();
        Factory = new PermitsApiFactory(ConnectionString);
    }

    public Task<ImportResult> ImportSampleAsync() =>
        new PermitImporter(ConnectionString, NullLogger<PermitImporter>.Instance)
            .ImportAsync(new FilePermitSource(SamplePath, new FieldMapping()));

    public async Task DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }
        await _container.DisposeAsync();
    }
}

public sealed class PermitsApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Permits", connectionString);
        builder.UseSetting("Database:InitializeSchemaOnStartup", "false");
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SQL Server";
}

[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PermitApiTests(SqlServerFixture fixture)
{
    // SAMPLE-0001 is ~100 m east, SAMPLE-0002 ~299 m north, SAMPLE-0003 ~1,990 m north
    // (WGS84 ellipsoid distances computed with GeographicLib).
    private const string Origin = "lat=25.7617&lon=-80.1918";

    // Clockwise ring around the origin. GeoJSON allows either orientation;
    // SQL Server geography would treat this as "the whole Earth minus the box".
    private static readonly object ClockwiseSquare = new
    {
        type = "Polygon",
        coordinates = new[]
        {
            new[]
            {
                new[] { -80.2018, 25.7517 }, new[] { -80.2018, 25.7717 }, new[] { -80.1818, 25.7717 },
                new[] { -80.1818, 25.7517 }, new[] { -80.2018, 25.7517 },
            },
        },
    };

    private readonly HttpClient _client = fixture.Factory.CreateClient();

    [Fact]
    public void ImportLoadsSampleAndDropsDuplicatesAndBadRows()
    {
        var result = fixture.FirstImport;
        Assert.Equal(39, result.Read);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, result.DuplicatesDropped);
        Assert.Equal(38, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Deleted);
    }

    [Fact]
    public async Task ReimportingUnchangedDataChangesNothing()
    {
        var second = await fixture.ImportSampleAsync();
        Assert.Equal((0, 0, 0), (second.Inserted, second.Updated, second.Deleted));
    }

    [Fact]
    public async Task DuplicatePermitKeepsLatestIssuedRow()
    {
        var permit = await _client.GetFromJsonAsync<PermitDto>("/api/permits/SAMPLE-0010");
        Assert.Equal("FINAL", permit!.Status);
    }

    [Fact]
    public async Task NearReturnsNearestFirstWithDistancesInMeters()
    {
        var result = await _client.GetFromJsonAsync<PermitListResponse>($"/api/permits/near?{Origin}&radiusMeters=500");

        Assert.Collection(result!.Permits,
            p =>
            {
                Assert.Equal("SAMPLE-0001", p.PermitNumber);
                Assert.InRange(p.DistanceMeters!.Value, 98, 102);
                Assert.True(p.Longitude < -80, "X must be longitude");
            },
            p =>
            {
                Assert.Equal("SAMPLE-0002", p.PermitNumber);
                Assert.InRange(p.DistanceMeters!.Value, 296, 301);
            });
    }

    [Fact]
    public async Task NearAppliesRadiusAndTypeFilters()
    {
        var wide = await _client.GetFromJsonAsync<PermitListResponse>($"/api/permits/near?{Origin}&radiusMeters=2500");
        Assert.Equal(3, wide!.Count);

        var plumbing = await _client.GetFromJsonAsync<PermitListResponse>($"/api/permits/near?{Origin}&radiusMeters=2500&type=plum");
        Assert.Equal("SAMPLE-0003", Assert.Single(plumbing!.Permits).PermitNumber);
    }

    [Fact]
    public async Task WithinHandlesClockwisePolygon()
    {
        var response = await _client.PostAsJsonAsync("/api/permits/within", ClockwiseSquare);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PermitListResponse>();

        Assert.Equal(new[] { "SAMPLE-0001", "SAMPLE-0002" }, result!.Permits.Select(p => p.PermitNumber).Order());
    }

    [Fact]
    public async Task WithinAcceptsGeoJsonFeature()
    {
        var feature = new { type = "Feature", geometry = ClockwiseSquare, properties = new { } };
        var result = await (await _client.PostAsJsonAsync("/api/permits/within", feature))
            .Content.ReadFromJsonAsync<PermitListResponse>();
        Assert.Equal(2, result!.Count);
    }

    [Fact]
    public async Task WithinRejectsNonPolygon()
    {
        var response = await _client.PostAsJsonAsync("/api/permits/within",
            new { type = "Point", coordinates = new[] { -80.19, 25.76 } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task StatsSumAcrossTypes()
    {
        var stats = await _client.GetFromJsonAsync<StatsResponse>("/api/permits/stats?bbox=-80.6,25.4,-80.1,26.0");
        Assert.Equal(38, stats!.TotalPermits);
        Assert.Equal(38, stats.ByType.Sum(t => t.Count));
        Assert.True(stats.TotalEstimatedValue > 0);
    }

    [Fact]
    public async Task GeoJsonReturnsLonLatPoints()
    {
        using var document = JsonDocument.Parse(
            await _client.GetStringAsync("/api/permits/geojson?bbox=-80.2018,25.7517,-80.1818,25.7717"));
        var root = document.RootElement;

        Assert.Equal("FeatureCollection", root.GetProperty("type").GetString());
        var features = root.GetProperty("features");
        Assert.Equal(2, features.GetArrayLength());
        var coordinates = features[0].GetProperty("geometry").GetProperty("coordinates");
        Assert.InRange(coordinates[0].GetDouble(), -80.21, -80.18); // longitude first
        Assert.InRange(coordinates[1].GetDouble(), 25.75, 25.78);
    }

    [Theory]
    [InlineData("/api/permits/near?lat=200&lon=-80.19", "lat")]
    [InlineData("/api/permits/near?" + Origin + "&radiusMeters=50000", "radiusMeters")]
    [InlineData("/api/permits/near?" + Origin + "&limit=0", "limit")]
    [InlineData("/api/permits/stats?bbox=-82,25,-80,26", "bbox")]
    public async Task InvalidRequestsReturnValidationProblem(string url, string field)
    {
        var response = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnknownPermitIsNotFound() =>
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/permits/NOT-A-PERMIT")).StatusCode);

    [Fact]
    public async Task HealthCheckReachesDatabase() =>
        Assert.Equal("Healthy", await _client.GetStringAsync("/health"));
}
