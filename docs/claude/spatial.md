# `geography` / `geometry` data types

Skip-with-diagnostic. DDL + catalog views + spatial-typed columns + WKT-form round-trip + spatial-index parse-and-discard all ship; OGC + Microsoft-extension methods raise `NotSupportedException` at execute except `.ToString()` which returns the stored WKT.

The sole AW spatial column (`Person.Address.SpatialLocation`, geography) loads as a first-class spatial-typed column rather than degrading to `varbinary(MAX)`.

## Storage

**`GeographySqlType`** + **`GeometrySqlType`** — singletons in `Storage/SpatialType.cs`. Both inherit `SpatialSqlType : SqlType(SqlTypeCategory.String)`:
- `SqlServerName` = `geography` / `geometry`
- `SystemTypeId=240` (shared with hierarchyid; CLR-UDT family)
- `UserTypeId=130` / `129` respectively
- `IsLob=true`

**Payload encoding**: raw UTF-16 LE of the constructed WKT string — simulator-specific degraded-mode form. Round-trip via `CAST AS nvarchar(MAX)` returns the WKT verbatim; `geography::Parse('POINT(0 0)')` stores `POINT(0 0)`. The byte form on disk is **not** SQL Server's documented binary CLR-UDT representation — same deferred-byte-form gap as [`hierarchyid`](hierarchyid.md).

**Factories**: `SqlValue.FromGeography` / `FromGeometry`. `SqlValue.FromString` routes the spatial branches when called with a `SpatialSqlType` target.

## Method-call surface — `Parser/Expressions/SpatialMethodCall.cs`

Broad closed accept-list (~70 names) covering every OGC predicate / accessor / constructor exposed on both geography and geometry, plus Microsoft extensions (`Lat` / `Long` / `MakeValid` / `Reduce` / `Filter` / `BufferWithTolerance` / `STSrid` / etc.).

- **Parses cleanly** so CREATE VIEW / CREATE PROCEDURE bodies that reference spatial methods store verbatim.
- **Runtime** raises `NotSupportedException` with `"Spatial instance method '.NAME()' is not modeled."` — except `.ToString()`, which returns the stored WKT as `nvarchar(MAX)`.
- **Static result-type inference still applies** so projection-schema resolution works at parser level: `.STDistance` / `.STArea` / `.STLength` → float; `.STContains` / `.STIntersects` / `.STIsValid` → bit; `.STAsText` / `.STGeometryType` / `.ToString` → nvarchar(MAX); `.STAsBinary` → varbinary(MAX); `.STSrid` / `.STNumGeometries` / etc. → int; constructors → same spatial type as receiver.

**`.ToString()` collision with hierarchyid**: `HierarchyIdMethodCall.Run` detects a spatial receiver at runtime and returns the WKT through the spatial path instead, avoiding a dispatch-order regression.

## Static-call surface — `Parser/Expressions/SpatialStaticCall.cs`

`geography::` and `geometry::` type-scope dispatched alongside `hierarchyid::` in `Expression.cs`'s `::` operator handling.

- `Parse(wkt)` / `STGeomFromText(wkt, srid)` accept a single WKT-string argument (SRID parsed-and-discarded — simulator doesn't track per-value SRID).
- `Point(x, y, srid)` accepts numeric coordinates and synthesizes a `POINT (x y)` WKT.
- Every other static method raises `NotSupportedException` at Run.

## Parser — `Simulation/Simulation.Spatial.cs`

```
CREATE SPATIAL INDEX name ON table(col)
    [USING <scheme>]
    [WITH (
        BOUNDING_BOX = (xmin, ymin, xmax, ymax)
        | GRIDS = (level [, …])
        | CELLS_PER_OBJECT = n
        | <any other index option>
    )]
```

- Parses fully, stores in `HeapTable.SpatialIndexes`.
- Default tessellation scheme when no `USING` clause: `GEOMETRY_AUTO_GRID` (geometry col) / `GEOGRAPHY_AUTO_GRID` (geography col), matching probed real-server behavior.
- `GRIDS` level arguments accept either numeric codes (1/2/3) or named levels (`LOW` / `MEDIUM` / `HIGH`).
- Unknown options inside the `WITH` clause skip via balanced-paren consumption.
- Non-spatial column → `NotSupportedException`; duplicate index name → Msg 2714.

Statement dispatch: `Spatial` added to `ContextualKeyword` enum; CREATE SPATIAL routes via `UnquotedString { ContextualKeyword: ContextualKeyword.Spatial }`. `INDEX` is reserved, so the sub-keyword check uses `Keyword.Index`.

## Catalog views in `BuiltInResources.cs`

**`sys.spatial_indexes`** (23-col, probe-confirmed): `object_id` / `name` / `index_id` / `type` (=4) / `type_desc` (`SPATIAL`) / `is_unique` (false) / `data_space_id` (1) / `ignore_dup_key` / `is_primary_key` / `is_unique_constraint` / `fill_factor` / `is_padded` / `is_disabled` / `is_hypothetical` / `is_ignored_in_optimization` / `allow_row_locks` (true) / `allow_page_locks` (true) / `spatial_index_type` (3 geometry / 4 geography) / `spatial_index_type_desc` (`GEOMETRY` / `GEOGRAPHY`) / `tessellation_scheme` / `has_filter` / `filter_definition` / `auto_created`.

**`sys.spatial_index_tessellations`** (16-col, probe-confirmed): `object_id` / `index_id` / `tessellation_scheme` / `bounding_box_xmin`/`ymin`/`xmax`/`ymax` / `level_1_grid` + `level_1_grid_desc` / … / `level_4_grid` + `level_4_grid_desc` / `cells_per_object`. Unspecified GRIDS levels surface as NULL; `level_*_grid_desc` translates 1/2/3 codes to `LOW` / `MEDIUM` / `HIGH`.

**`sys.spatial_reference_systems`** (6-col): empty by default (real SQL Server pre-seeds ~390 EPSG/ESRI SRID rows; the simulator surfaces the column shape but skips the WKT-laden seed payload). `spatial_reference_id` / `authority_name` / `authorized_spatial_reference_id` / `well_known_text` / `unit_of_measure` / `unit_conversion_factor`.

`sys.types` rows for geography/geometry pre-existed (system_type_id=240, user_type_id=130 / 129); the `ResolveSimpleKeyword` arm + `GetSysColumnMetadata` `SpatialSqlType => (-1, 0, 0)` wiring closed the gap so `CREATE TABLE (g geography)` actually accepts them.

## BCP wire decoder

`SpatialWkbDecoder.TryDecodeSimplePoint` (`Storage/Bacpac/`) covers Microsoft's spatial UDT simple-point wire shape: 4-byte SRID + 1-byte version + 1-byte properties bitfield + 16-byte coordinates = 22 bytes total. Geography axis inversion honored (binary stores lat/long, WKT prints long/lat per OGC).

A separate full-shape path covers 2D shapes — Polygon / MultiPolygon / LineString / MultiPoint / MultiLineString / GeometryCollection. AW's `Person.Address` and WWI's spatial rows all decode end-to-end.

Falls back to `SqlValue.Null` for unknown versions and Z/M-coordinate variants — resilient: complex shapes don't fail the whole BCP file.

## Known gaps

- **WKT/WKB parsing for validation** — currently any string is accepted as a "WKT" payload.
- **OGC method evaluation pipeline** (`.STDistance` / `.STIntersects` / `.STArea` / etc.).
- **SRID tracking + transformation** — SRID parses-and-discards through `Parse` / `STGeomFromText`.
- **Spatial-index query-planner integration** — the index parses cleanly but never accelerates anything.
- **`sys.spatial_reference_systems` seed data** (~390 EPSG/ESRI rows).
- **`ALTER SPATIAL INDEX`** (REORGANIZE / REBUILD).
- **Byte-identical CAST encoding** for cross-engine binary transfer — same deferred byte-form gap as [`hierarchyid`](hierarchyid.md).
