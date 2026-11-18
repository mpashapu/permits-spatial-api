using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using Permits.Api.Geo;

namespace Permits.Api.Tests;

public sealed class GeoValidationTests
{
    [Theory]
    [InlineData(-80.19, 25.76, true)]
    [InlineData(25.76, -80.19, true)] // a swapped Miami point is still in range
    [InlineData(-200, 25, false)]
    [InlineData(-80, 95, false)]
    [InlineData(double.NaN, 25, false)]
    public void IsValidLonLat(double lon, double lat, bool expected) =>
        Assert.Equal(expected, GeoValidation.IsValidLonLat(lon, lat));

    [Fact]
    public void ParsesBbox()
    {
        Assert.True(GeoValidation.TryParseBbox("-80.3, 25.7, -80.1, 25.9", out var envelope, out _));
        Assert.Equal(-80.3, envelope.MinX);
        Assert.Equal(25.9, envelope.MaxY);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1,2,3")]
    [InlineData("a,b,c,d")]
    [InlineData("-80.1,25.7,-80.3,25.9")] // min > max
    [InlineData("-82,25,-80,26")] // wider than the 1 degree limit
    [InlineData("-80.3,25.7,-80.1,95")] // latitude out of range
    public void RejectsBadBbox(string? value)
    {
        Assert.False(GeoValidation.TryParseBbox(value, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void BboxPolygonIsCounterClockwise()
    {
        var polygon = GeoValidation.ToPolygon(new Envelope(-80.3, -80.1, 25.7, 25.9));
        Assert.True(Orientation.IsCCW(polygon.Shell.Coordinates));
        Assert.Equal(GeoFactory.Wgs84, polygon.SRID);
    }

    [Fact]
    public void ClockwiseShellIsReversedForSqlServer()
    {
        var clockwise = Square(-80.2, 25.7, 0.01, clockwise: true);
        Assert.False(Orientation.IsCCW(clockwise.Shell.Coordinates));

        var errors = new Dictionary<string, string[]>();
        var prepared = Assert.IsType<Polygon>(GeoValidation.PrepareSearchArea(clockwise, errors));

        Assert.Empty(errors);
        Assert.True(Orientation.IsCCW(prepared.Shell.Coordinates));
        Assert.Equal(clockwise.Area, prepared.Area, precision: 12);
        Assert.Equal(GeoFactory.Wgs84, prepared.SRID);
    }

    [Fact]
    public void HolesAreMadeClockwise()
    {
        var shell = Square(-80.2, 25.7, 0.02, clockwise: false).Shell;
        var hole = Square(-80.195, 25.705, 0.005, clockwise: false).Shell; // wrong orientation for a hole
        var polygon = GeoFactory.Instance.CreatePolygon(shell, [hole]);

        var prepared = (Polygon)GeoValidation.PrepareSearchArea(polygon, new Dictionary<string, string[]>())!;
        Assert.True(Orientation.IsCCW(prepared.Shell.Coordinates));
        Assert.False(Orientation.IsCCW(prepared.Holes[0].Coordinates));
    }

    [Fact]
    public void RejectsNonPolygons()
    {
        var errors = new Dictionary<string, string[]>();
        Assert.Null(GeoValidation.PrepareSearchArea(GeoFactory.Point(-80.2, 25.7), errors));
        Assert.Contains("geometry", errors.Keys);
    }

    [Fact]
    public void RejectsSelfIntersectingPolygon()
    {
        // Bow tie
        var bowTie = GeoFactory.Instance.CreatePolygon(
        [
            new Coordinate(-80.2, 25.7), new Coordinate(-80.1, 25.8),
            new Coordinate(-80.1, 25.7), new Coordinate(-80.2, 25.8), new Coordinate(-80.2, 25.7),
        ]);
        var errors = new Dictionary<string, string[]>();
        Assert.Null(GeoValidation.PrepareSearchArea(bowTie, errors));
        Assert.Contains("not valid", errors["geometry"][0]);
    }

    [Fact]
    public void RejectsOversizedArea()
    {
        var errors = new Dictionary<string, string[]>();
        Assert.Null(GeoValidation.PrepareSearchArea(Square(-82, 25, 2, clockwise: false), errors));
        Assert.Contains("span", errors["geometry"][0]);
    }

    internal static Polygon Square(double minLon, double minLat, double size, bool clockwise)
    {
        Coordinate[] ccw =
        [
            new(minLon, minLat), new(minLon + size, minLat), new(minLon + size, minLat + size),
            new(minLon, minLat + size), new(minLon, minLat),
        ];
        return GeoFactory.Instance.CreatePolygon(clockwise ? Enumerable.Reverse(ccw).ToArray() : ccw);
    }
}
