# Permits Spatial API

![CI](https://github.com/mpashapu/permits-spatial-api/actions/workflows/ci.yml/badge.svg)

An **ASP.NET Core 10** REST API for spatial searches over building permits,
backed by **SQL Server geography**. It includes an importer that pulls permits
from any **ArcGIS feature layer** and keeps a local copy in sync. The defaults
target Miami-Dade County's public building permit layer.

- *"Which permits were issued within 500 m of this address?"*
- *"Everything inside this neighborhood polygon since January"*
- *"Permit counts and construction value by type in this area"*

## Stack

| Layer | Technology |
|---|---|
| API | ASP.NET Core 10 minimal APIs, typed results, ProblemDetails validation errors |
| Data | SQL Server 2022 `geography` + spatial index, EF Core 10 + NetTopologySuite |
| Import | ArcGIS REST paging → `SqlBulkCopy` staging → single-transaction `MERGE` |
| Docs | Built-in OpenAPI 3.1 document + Scalar UI at `/scalar` |
| Tests | xUnit unit tests, plus integration tests against real SQL Server via Testcontainers |
| Ops | Dockerfile (non-root), Docker Compose, health check, GitHub Actions CI |

## Endpoints

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/permits/near?lat=&lon=&radiusMeters=` | Permits within a radius (max 5 km), nearest first, with distance in meters |
| POST | `/api/permits/within` | Permits inside a GeoJSON Polygon/MultiPolygon (or a Feature wrapping one) |
| GET | `/api/permits/stats?bbox=` | Count and total estimated value by permit type in a bounding box |
| GET | `/api/permits/geojson?bbox=` | GeoJSON FeatureCollection for web maps (ArcGIS JS, Leaflet, MapLibre) |
| GET | `/api/permits/{permitNumber}` | One permit |
| GET | `/health` | Database connectivity |

Search endpoints also accept `type` (e.g. `BLDG`, `ELEC`, `PLUM`),
`issuedFrom`, `issuedTo` (`yyyy-MM-dd`) and `limit`. Invalid input returns
`400` with field-level errors:

```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": { "radiusMeters": ["radiusMeters must be greater than 0 and at most 5000."] }
}
```

More examples are in [`http/permits.http`](http/permits.http).

## Run it

**Docker (easiest):**

```bash
docker compose up -d --build
docker compose run --rm api import --sample data/sample-permits.json
```

Open http://localhost:8080/scalar and try `/api/permits/near?lat=25.7617&lon=-80.1918`.

**Locally** (needs the .NET 10 SDK and a SQL Server; the dev connection string
points at `localhost,1433`):

```bash
docker compose up -d sqlserver
dotnet run --project src/Permits.Api -- import --sample data/sample-permits.json
dotnet run --project src/Permits.Api
```

**Real data:** set the layer URL and run the importer. On the Miami-Dade
County Open Data hub, open the building permit dataset, find its API /
GeoService link, and copy the layer URL (ending in `/FeatureServer/0` or
`/MapServer/0`).

```bash
export Import__LayerUrl="https://<server>/.../FeatureServer/0"
dotnet run --project src/Permits.Api -- import
```

Field names are configurable under `Import:Fields`, so the same importer
works for another city's or county's permit layer.

> `data/sample-permits.json` is **synthetic** (IDs `SAMPLE-0001` and up) for
> demos and tests. It contains no real permits.

## Design notes

**Why `geography`, not `geometry`.** Permits are stored in WGS84 as
`geography`, so `STDistance` returns meters on the ellipsoid with no
projection math. A spatial index (`GEOGRAPHY_AUTO_GRID`) serves the radius
and polygon filters.

**Polygon ring orientation.** SQL Server geography follows the left-hand
rule: an outer ring drawn clockwise means *the whole Earth except this
shape*. GeoJSON clients send either orientation, so the API normalizes rings
(shell counter-clockwise, holes clockwise) before querying. Without this, a
user-drawn polygon can silently return almost every row. An integration test
posts a clockwise polygon to prove it.

**Longitude/latitude order.** NetTopologySuite and GeoJSON use
`(longitude, latitude)`; T-SQL `geography::Point` takes `(latitude,
longitude)`. Both conventions appear in this codebase, each commented where
it's used, and tests check the output order.

**Import pipeline.** The importer:

1. Pages through the ArcGIS layer with `resultOffset`, ordered by `OBJECTID`
   so pages are stable, requesting `outSR=4326` so the server reprojects from
   State Plane feet.
2. Treats ArcGIS errors returned as HTTP 200 as failures.
3. Skips features without an ID or usable geometry and counts them.
4. Bulk-copies rows into a staging table (plain lon/lat columns, no spatial
   types needed client-side).
5. Runs one `MERGE` in a transaction: it keeps the latest row per permit
   number, updates only rows that changed (a NULL-safe `EXISTS … EXCEPT`
   check), inserts new permits, and removes permits no longer published.

Readers never see a half-loaded table. An empty pull (outage or bad filter)
aborts instead of deleting everything. Re-running with unchanged data makes
zero changes, which an integration test confirms.

**Schema as SQL.** `Data/schema.sql` is idempotent T-SQL, embedded in the
assembly and applied with `init-db` (or on startup in development). It
creates the database if missing, the tables, and the spatial and covering
indexes.

## Tests

```bash
dotnet test                                    # everything (Docker required)
dotnet test --filter "Category!=Integration"   # unit tests only, no Docker
```

- **Unit:** coordinate and bbox validation, ring orientation (including
  holes), invalid and oversized polygons, Esri JSON parsing (epoch dates,
  numeric folios, case-insensitive fields, State Plane coordinates rejected),
  pagination, and staging-table conversion.
- **Integration:** starts SQL Server 2022 in Docker, applies the schema twice
  (idempotence), imports the sample, then exercises every endpoint, including
  expected distances computed independently with GeographicLib.

## Project layout

```
src/Permits.Api/
├── Program.cs              Composition root; `init-db` / `import` commands
├── Endpoints/              Minimal API handlers and query composition
├── Geo/                    SRID 4326 factory, validation, ring orientation
├── Import/                 ArcGIS paging source, Esri JSON parser, bulk importer
├── Data/                   EF Core model, schema.sql, schema initializer
└── Contracts/              Request/response records
tests/Permits.Api.Tests/    Unit + Testcontainers integration tests
data/sample-permits.json    Synthetic sample data
```

## License

[MIT](LICENSE) © 2026 Malla Reddy Pashapu. Permit data published by
Miami-Dade County is subject to the county's terms of use.
