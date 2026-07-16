# `geography` / `geometry` data types

Skip-with-diagnostic on the *method* surface. DDL + catalog views + spatial-typed columns + WKT-form round-trip + spatial-index parse-and-discard all ship; OGC + Microsoft-extension methods raise `NotSupportedException` at execute except `.ToString()` which returns the stored WKT. The **binary CLR-UDT serialization** (the `CAST(... AS varbinary(max))` byte form / the TDS UDT wire form DacFx exports) ships byte-identically for the modeled 2D shapes — see [Binary CLR-UDT serialization](#binary-clr-udt-serialization) below.

The sole AW spatial column (`Person.Address.SpatialLocation`, geography) loads as a first-class spatial-typed column rather than degrading to `varbinary(MAX)`.

## Storage

**`GeographySqlType`** + **`GeometrySqlType`** — singletons in `Storage/SpatialType.cs`. Both inherit `SpatialSqlType : SqlType(SqlTypeCategory.String)`:
- `SqlServerName` = `geography` / `geometry`
- `SystemTypeId=240` (shared with hierarchyid; CLR-UDT family)
- `UserTypeId=130` / `129` respectively
- `IsLob=true`

**Payload encoding (on disk)**: raw UTF-16 LE of the constructed WKT string — simulator-specific degraded-mode form. Round-trip via `CAST AS nvarchar(MAX)` returns the WKT verbatim; `geography::Parse('POINT(0 0)')` stores `POINT(0 0)`. The on-disk byte form is **not** SQL Server's binary CLR-UDT representation (unlike `hierarchyid`, whose on-disk form is still deferred). The CLR-UDT bytes are synthesized on demand from the stored WKT by [`SpatialWkbEncoder`](#binary-clr-udt-serialization) whenever the value is projected as `varbinary(max)` or streamed over the wire.

**No per-value SRID**: `SqlValue` carries only the WKT string, not the value's SRID (`STGeomFromText`'s SRID argument parses-and-discards). The binary encoder therefore stamps the type default — 4326 for geography, 0 for geometry. WWI's spatial columns are all SRID 4326 geography, so this is lossless in practice; a `geometry` value constructed with a non-zero SRID would export as SRID 0.

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

## Binary CLR-UDT serialization

`Storage/Bacpac/SpatialWkbDecoder` + `SpatialWkbEncoder` are a decode/encode pair for Microsoft's spatial CLR-UDT binary format (a.k.a. "MS spatial binary" — despite the `Wkb` names, **not** OGC WKB). The decoder feeds the BACPAC loader (real bytes → WKT); the encoder feeds the `varbinary(max)` CAST projection and the TDS UDT wire form (stored WKT → real bytes). Byte-parity was probe-anchored against SQL Server 2025 (`CAST(geography::STGeomFromText(N'…', srid) AS varbinary(max))`, 2026-07-16) for every 2D shape class plus genuine WWI `StateProvinces.Border` values.

**Layout** (4-byte SRID + 1-byte version `0x01` + 1-byte serialization-properties bitfield, then a shortcut body or the full `numPoints + points[] + numFigures + figures[] + numShapes + shapes[]` tables):

- **Single `POINT`** → properties `0x0C` (isValid | isSinglePoint), one coordinate pair, no tables (22 bytes).
- **Single-segment `LINESTRING`** (exactly two points) → properties `0x14` (isValid | isSingleLineSegment), the two pairs, **no count and no tables**. Real uses this only for a 2-point line; a 3+-point line takes the full layout. (The decoder previously mis-read this shortcut as a `numPoints`-prefixed variable-length form — a latent bug, never exercised because AW/WWI have no 2-point lines; fixed alongside the encoder so the pair round-trips.)
- **Everything else** → properties `0x04` (isValid) + full tables.

**Figure attributes (version 1)**: point / line figures `0x01`; a polygon's exterior (first) ring `0x02`, interior rings `0x00`. **Shapes** are laid out depth-first (pre-order); a shape's `figureOffset` is the index of the first figure in its subtree (a leaf's is its own), the root's parent offset is `-1`. **Axis order**: geography binary stores `(lat, long)` while WKT is `(long lat)`; geometry stores `(x, y)` throughout.

**isValid bit** (`0x04`) — probed divergence: real sets it for a valid instance and *clears* it for a stored-but-invalid one (WWI's `Countries.Border` are stored invalid — `STGeometryType()` raises 24144 on them — so real emits properties `0x00`). The simulator stores WKT and cannot revalidate, so it **always sets isValid**. Every valid WWI shape (`Cities.Location`, `StateProvinces.Border`) is byte-identical; a stored-invalid instance diverges only in this one bit, and real still `CAST`s the bytes back (value-parse compatibility holds).

Both are 2D-only: the decoder falls back to `SqlValue.Null` for unknown versions and Z/M variants (resilient — one bad row doesn't fail the BCP file); the encoder raises `NotSupportedException` for EMPTY / Z / M / unrecognized shapes (WWI has none).

**`DATALENGTH` over a spatial value** measures this serialization (a 2D point = 22 bytes, probe-confirmed against WWI `Cities.Location`), not the stored WKT text — `DataLength.Run` special-cases `SpatialSqlType` through the encoder while the storage layer's `GetVariableByteCount` keeps sizing the WKT it actually stores. Load-bearing for DacFx bacpac export: the bulk reader's `DATALENGTH([geoCol])` companion becomes the BCP length prefix for the wire value bytes, and a WKT-text length there desyncs every geography-bearing table on re-import.

## Known gaps

- **WKT/WKB parsing for validation** — currently any string is accepted as a "WKT" payload.
- **OGC method evaluation pipeline** (`.STDistance` / `.STIntersects` / `.STArea` / etc.).
- **SRID tracking + transformation** — SRID parses-and-discards through `Parse` / `STGeomFromText`.
- **Spatial-index query-planner integration** — the index parses cleanly but never accelerates anything.
- **`sys.spatial_reference_systems` seed data** (~390 EPSG/ESRI rows).
- **`ALTER SPATIAL INDEX`** (REORGANIZE / REBUILD).
- **Z / M coordinates + EMPTY geometries** in the binary encoder (the decoder skips them too — 2D non-empty is the modeled subset).
- **Per-value SRID tracking** — the encoder stamps the type default (4326 geography / 0 geometry); a geometry value with a non-zero SRID exports as SRID 0.

Byte-identical CAST/wire encoding for the modeled 2D shapes **now ships** (see [Binary CLR-UDT serialization](#binary-clr-udt-serialization)); `hierarchyid` remains on the deferred byte-form list.
