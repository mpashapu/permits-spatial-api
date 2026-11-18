using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Permits.Api.Import;

namespace Permits.Api.Tests;

public sealed class EsriJsonParserTests
{
    private static PermitPage Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return EsriJsonParser.ParsePage(document, new FieldMapping());
    }

    [Fact]
    public void ParsesFeatureAttributesAndGeometry()
    {
        var page = Parse("""
        {
          "features": [{
            "attributes": { "ID": " 2026012345 ", "ADDRESS": "1 MAIN ST", "FOLIO": 3069260260010,
                            "TYPE": "bldg", "DESC1": "ROOF", "ISSUDATE": 1772427600000,
                            "ESTVALUE": "15000.456", "CONTRNAME": null },
            "geometry": { "x": -80.19, "y": 25.76 }
          }],
          "exceededTransferLimit": true
        }
        """);

        var record = Assert.Single(page.Records);
        Assert.True(page.ExceededTransferLimit);
        Assert.Equal("2026012345", record.PermitNumber);
        Assert.Equal("3069260260010", record.Folio); // numeric folio kept as text
        Assert.Equal("BLDG", record.PermitType);
        Assert.Equal(new DateOnly(2026, 3, 2), record.IssuedDate); // epoch ms, UTC
        Assert.Equal(15000.46m, record.EstimatedValue);
        Assert.Null(record.ContractorName);
        Assert.Equal((-80.19, 25.76), (record.Longitude, record.Latitude));
    }

    [Fact]
    public void FieldNamesAreCaseInsensitive()
    {
        var page = Parse("""{"features":[{"attributes":{"id":"A1"},"geometry":{"x":-80.1,"y":25.7}}]}""");
        Assert.Equal("A1", Assert.Single(page.Records).PermitNumber);
    }

    [Theory]
    [InlineData("""{"attributes":{"ID":"A1"},"geometry":null}""")]
    [InlineData("""{"attributes":{"ID":"A1"},"geometry":{"x":"NaN","y":25.7}}""")]
    [InlineData("""{"attributes":{"ID":"A1"},"geometry":{"x":0,"y":0}}""")]
    [InlineData("""{"attributes":{"ID":""},"geometry":{"x":-80.1,"y":25.7}}""")]
    [InlineData("""{"attributes":{"ID":"A1"},"geometry":{"x":2954111.5,"y":523111.2}}""")] // State Plane feet: outSR missing
    public void SkipsUnusableFeatures(string feature)
    {
        var page = Parse("{\"features\":[" + feature + "]}");
        Assert.Empty(page.Records);
        Assert.Equal(1, page.Skipped);
    }

    [Fact]
    public void ArcGisErrorWithHttp200Throws()
    {
        var error = Assert.Throws<ArcGisException>(() =>
            Parse("""{"error":{"code":400,"message":"Invalid or missing input parameters."}}"""));
        Assert.Equal(400, error.Code);
    }

    [Fact]
    public async Task SampleFileParses()
    {
        var source = new FilePermitSource(Path.Combine(AppContext.BaseDirectory, "data", "sample-permits.json"), new FieldMapping());
        var pages = new List<PermitPage>();
        await foreach (var page in source.ReadAsync())
        {
            pages.Add(page);
        }

        var only = Assert.Single(pages);
        Assert.Equal(39, only.Records.Count); // 38 permits + 1 duplicate id
        Assert.Equal(1, only.Skipped); // one feature without geometry
    }
}

public sealed class ArcGisFeatureLayerSourceTests
{
    private const string LayerUrl = "https://gis.example.gov/arcgis/rest/services/Permits/FeatureServer/0";

    [Fact]
    public void QueryUrlRequestsWgs84AndStableOrder()
    {
        var url = ArcGisFeatureLayerSource.BuildQueryUrl(new ImportOptions { LayerUrl = LayerUrl + "/" }, 2000);

        Assert.StartsWith(LayerUrl + "/query?", url);
        Assert.Contains("outSR=4326", url);
        Assert.Contains("orderByFields=OBJECTID", url);
        Assert.Contains("resultOffset=2000", url);
        Assert.Contains("where=1%3D1", url);
        Assert.Contains("outFields=ID%2CADDRESS", url);
    }

    [Fact]
    public async Task PagesUntilTransferLimitIsNotExceeded()
    {
        var handler = new QueueHandler(
            Page(start: 1, count: 2, more: true),
            Page(start: 3, count: 2, more: true),
            Page(start: 5, count: 1, more: false));
        var source = new ArcGisFeatureLayerSource(
            new HttpClient(handler),
            Options.Create(new ImportOptions { LayerUrl = LayerUrl, PageSize = 2 }),
            NullLogger<ArcGisFeatureLayerSource>.Instance);

        var total = 0;
        await foreach (var page in source.ReadAsync())
        {
            total += page.Records.Count;
        }

        Assert.Equal(5, total);
        Assert.Collection(handler.Requests,
            u => Assert.Contains("resultOffset=0", u),
            u => Assert.Contains("resultOffset=2", u),
            u => Assert.Contains("resultOffset=4", u));
    }

    [Fact]
    public async Task MissingLayerUrlFailsClearly()
    {
        var source = new ArcGisFeatureLayerSource(new HttpClient(new QueueHandler()),
            Options.Create(new ImportOptions()), NullLogger<ArcGisFeatureLayerSource>.Instance);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in source.ReadAsync())
            {
            }
        });
        Assert.Contains("LayerUrl", error.Message);
    }

    private static string Page(int start, int count, bool more)
    {
        // Plain concatenation: raw interpolated strings ending in "}}" don't compile
        var features = Enumerable.Range(start, count).Select(i =>
            "{\"attributes\":{\"OBJECTID\":" + i + ",\"ID\":\"P" + i + "\"},\"geometry\":{\"x\":-80.2,\"y\":25.7}}");
        return "{\"features\":[" + string.Join(",", features) + "],\"exceededTransferLimit\":" +
               (more ? "true" : "false") + "}";
    }

    private sealed class QueueHandler(params string[] bodies) : HttpMessageHandler
    {
        private readonly Queue<string> _bodies = new(bodies);
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_bodies.Dequeue(), Encoding.UTF8, "application/json"),
            });
        }
    }
}

public sealed class PermitImporterTests
{
    [Fact]
    public void DataTableTruncatesLongTextAndKeepsNulls()
    {
        var record = new PermitRecord(
            "P1", new string('A', 250), null, "BLDG", null, null, null, null, null, null, -80.2, 25.7);

        var table = PermitImporter.ToDataTable([record]);
        var row = table.Rows[0];

        Assert.Equal(200, ((string)row["Address"]).Length);
        Assert.Equal(DBNull.Value, row["Folio"]);
        Assert.Equal(DBNull.Value, row["IssuedDate"]);
        Assert.Equal(-80.2, row["Longitude"]);
    }
}
