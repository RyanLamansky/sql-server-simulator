# `geography` / `geometry` data types

Spatial values are parsed instances, stored in SQL Server's own UDT serialization.
WKT parsing (with real's validation failures), canonical WKT rendering, per-value SRID, Z / M ordinates, EMPTY instances, the OGC binary encodings, the constructor family and the whole structural member surface all ship.
What doesn't yet is *evaluation*: the measures (`STArea` / `STLength` / `STDistance`), the topological predicates (`STIntersects` / `STContains` / …) and the constructive operations (`STUnion` / `STBuffer` / …) parse cleanly and raise `NotSupportedException` at execute — see [Not modeled yet](#not-modeled-yet).

The sole AW spatial column (`Person.Address.SpatialLocation`, geography) loads as a first-class spatial-typed column rather than degrading to `varbinary(MAX)`.

## Storage

**`GeographySqlType`** + **`GeometrySqlType`** — singletons in `Storage/SpatialType.cs`.
Both inherit `SpatialSqlType : SqlType(SqlTypeCategory.String)`:

- `SqlServerName` = `geography` / `geometry`
- `SystemTypeId=240` (shared with hierarchyid; CLR-UDT family)
- `UserTypeId=130` / `129` respectively
- `IsLob=true`
- `IsGeography` selects the round-earth axis order and the latitude-domain check
- `ClrTypeName` is the `Microsoft.SqlServer.Types.SqlGeography` / `SqlGeometry` name real quotes in its member-not-found errors

**In-memory representation**: the parsed `SpatialGeometry` (SRID + shape tree), held in the `SqlValue`'s reference slot and reached through `SqlValue.AsSpatial`.
Instance members read the tree directly; the byte form is materialized only at the storage, `varbinary`-cast and wire boundaries, and cached per instance by `SpatialGeometry.Encoded(isGeography)` because the row encoder asks for the length and the bytes separately.

**On-disk payload**: the spatial UDT serialization — the same bytes real writes.
`SqlValue.AsString` renders WKT on demand, so `CAST(g AS nvarchar(MAX))` and the in-process reader still surface the text (`SqlType.ClrType` stays `string`).

**Model** (`Storage/Spatial/SpatialGeometry.cs`):

- `SpatialCoordinate` — `X` / `Y` / `Z?` / `M?`, always in **WKT axis order**, so for geography that is (longitude, latitude).
  Only the binary codec knows about the reversed storage order.
  Negative zero folds onto positive zero at construction, matching real.
- `SpatialShape` — `Type` + `Figures` (point runs: one per polygon ring, exterior first) + `Children` (members of Multi\* / GeometryCollection).
  The split mirrors the figure and shape tables of the binary form, so the codec walks the tree without an intermediate representation.
  Carries `IsEmpty` (recursive, matching `STIsEmpty()`), `PointCount`, `Dimension` and `Coordinates()` (the order `STPointN()` indexes).
- `SpatialGeometry` — `Srid` + `Root`, plus `WithSrid` for the settable `STSrid` and `ValidateSrid` for the 0..999999 domain (Msg 24100 outside it).

**Factories**: `SqlValue.FromGeography` / `FromGeometry` / `FromSpatial(value, isGeography)`.
`SqlValue.FromString` routes a spatial target through the WKT reader, which is what makes `CAST(@nvarchar AS geometry)` parse text.

## WKT — `Storage/Spatial/SpatialWktReader.cs` + `SpatialWktWriter.cs`

The reader accepts the full 2D/Z/M grammar: all seven shape kinds, `EMPTY` at any level, both MULTIPOINT spellings (`(0 0, 1 1)` and `((0 0), (1 1))`, with the first element fixing the form for the rest), a literal `NULL` in the Z slot, and case-insensitive labels.
Labels match as a **prefix**, not as a greedy word — which is why `POINTX(1 2)` reports a missing `(` rather than an unknown label, exactly as real does.

The writer emits real's canonical spelling: a space between label and body (`POINT (1 2)`), `", "` between coordinates and between members, `EMPTY` for a shape with no coordinates, and Multi\* members without their own label.
Ordinates use .NET's round-trip `"R"` form under the invariant culture, which is what real emits — `1.50` → `1.5`, `1e10` → `10000000000`, `1e30` → `1E+30`, `0.000001` → `1E-06`.

`ToString()` and `AsTextZM()` carry Z and M; `STAsText()` drops them.
A point whose Z is absent but whose M is present writes the Z slot as the literal `NULL` (`POINT (1 2 NULL 4)`), the same spelling the reader accepts.

### Parse failures

Every one is real's own, wrapped in Msg 6522 (see [The Msg 6522 wrapper](#the-msg-6522-wrapper)).

| Code | Raised when |
| --- | --- |
| 24111 | trailing content after a complete instance (`POINT(1 2)X`) |
| 24112 | empty or all-whitespace input |
| 24114 | the leading word isn't a recognized label — real echoes the *whole* remaining input, not just the word |
| 24117 | a LineString with fewer than two points |
| 24118 / 24120 | a polygon exterior / numbered interior ring with fewer than four points |
| 24119 / 24121 | a polygon exterior / numbered interior ring whose first and last points differ |
| 24141 | a coordinate slot holding something that isn't a number |
| 24142 | a required literal isn't there — a label for the per-kind constructors, or a `(` |
| 24201 | a `geography` latitude outside ±90 (longitude has no equivalent check; real accepts any value) |
| 24209 | the input stopped mid-shape |
| 24303 | `FULLGLOBE` on `geometry`, which real rejects as an invalid OpenGis type |

Two of these carry probe-derived position arithmetic that isn't worth deriving from first principles:

- **24141** reports the index *after* the offending token when the reader consumed one (`POINT(1 X)` → position 9), and the index *of* the character when it's a delimiter the reader didn't consume (`POINT(1)` → position 7).
- **24142** reports the offset itself for a single-character expectation, and one past it for a label expectation whenever the remaining input is longer than the label.
  The echoed text is the label's width of input when that much remains, and a single character when it doesn't (`STPointFromText('PO')` → `at position 0. The input has "P"`).

## Binary encodings

Two distinct formats, and the names in `Storage/Spatial/` keep them apart.

### `SpatialBinaryCodec` — the UDT serialization

The byte form a value takes on disk, in `CAST(… AS varbinary(max))`, and on the TDS wire.
**Not** OGC WKB despite the resemblance.
Byte parity was probe-anchored against SQL Server 2025 for every 2D shape class plus genuine WWI `StateProvinces.Border` values; `SpatialBinaryCodecEncodeTests` / `SpatialBinaryCodecDecodeTests` in `*.Tests.Internal` hold those bytes.

**Layout**: 4-byte SRID + 1-byte version `0x01` + 1-byte properties bitfield, then either a shortcut body or the full tables `numPoints + points[] + z[] + m[] + numFigures + figures[] + numShapes + shapes[]`.

- **Single `POINT`** → properties `0x0C` (isValid | isSinglePoint), ordinates interleaved, no tables (22 bytes in 2D).
- **Single-segment `LINESTRING`** (exactly two points) → properties `0x14` (isValid | isSingleLineSegment), the two coordinates, no count and no tables.
  Real uses this only for a 2-point line; a 3+-point line takes the full layout.
- **Everything else** → properties `0x04` (isValid) + full tables.
- `0x01` / `0x02` add Z / M. The shortcut bodies interleave the extra ordinates per point; the full layout stores them as separate per-ordinate arrays after the coordinate pairs.

**Figure attributes (version 1)**: point / line figures `0x01`; a polygon's exterior (first) ring `0x02`, interior rings `0x00`.
**Shapes** are laid out depth-first; a shape's `figureOffset` is the index of the first figure anywhere in its subtree, and `-1` when its subtree has none — which is how an EMPTY instance is expressed.
The root's parent offset is `-1`.
**Axis order**: geography binary stores `(lat, long)` while the model and WKT hold `(long lat)`; geometry stores `(x, y)` throughout.

`TryDecode` swallows a malformed / unknown-version / unmodeled-shape payload and returns null so the BACPAC row loader can fall back to `SqlValue.Null` rather than failing an import; `Decode` raises, which is what the `CAST`-from-`varbinary` path wants.

**`DATALENGTH` over a spatial value** measures this serialization (a 2D point = 22 bytes, probe-confirmed against WWI `Cities.Location`).
Load-bearing for DacFx bacpac export: the bulk reader's `DATALENGTH([geoCol])` companion becomes the BCP length prefix for the wire value bytes.

### `SpatialWkb` — OGC / ISO well-known binary

What `STAsBinary()` / `AsBinaryZM()` produce and the `ST<Kind>FromWKB` constructors consume.
Every record is `[1-byte byte order][4-byte type]` plus a body; the simulator writes little-endian and reads either.
Z and M ride the ISO type codes (`+1000` / `+2000` / `+3000`), which is what `AsBinaryZM()` emits — `STAsBinary()` drops them and writes the plain 2D codes.
Coordinates are in WKT axis order for both spatial types, so a geography point writes (longitude, latitude) even though it stores the reverse.

## Members — `Parser/Expressions/SpatialMethodCall.cs`

One catalog (`Members`) holds every member with its **form** (property vs method), its **owning type** (both / geography-only / geometry-only) and its result type.
Real enforces all three, and so does the simulator:

- a method name written without parentheses, or a property written with them → **Msg 6592** (`Could not find property or field '…' for type '…'`)
- a property the other spatial type owns (`Lat` on geometry, `STX` on geography) → **Msg 6592**
- a method the other spatial type owns (`NumRings()` on geometry) → **Msg 6506**, which real emits *without* a trailing period, unlike 6592

**Properties** (no argument list): `STSrid`, `STX` / `STY` (geometry), `Lat` / `Long` (geography), `Z`, `M`, `HasZ`, `HasM`.

**Methods that evaluate**: `ToString`, `STAsText`, `AsTextZM`, `STAsBinary`, `AsBinaryZM`, `STGeometryType`, `STDimension`, `STNumPoints`, `STPointN`, `STStartPoint`, `STEndPoint`, `STIsClosed`, `STIsEmpty`, `STIsRing`, `STNumGeometries`, `STGeometryN`, `STExteriorRing`, `STNumInteriorRing`, `STInteriorRingN`, `NumRings` / `RingN` (geography), `InstanceOf`, `MinDbCompatibilityLevel`, `ReorientObject`.

Semantics worth pinning, all probe-confirmed:

- An **index below 1** raises (24102 `STPointN` / 24103 `STGeometryN` / 24104 ring), while an index **above the count** reads as NULL.
  Geography's `RingN` reports 24104 under `STInteriorRingN`'s name, matching real.
  24102 and 24103 differ from each other by one word in real's wording ("This number" vs "The number"), reproduced verbatim.
- **`STDimension()`** is -1 for any EMPTY instance, and a collection reports the largest dimension among its non-empty members.
- **`STNumGeometries()`** is the member count for a collection, and 1 / 0 for a non-empty / empty instance of any other kind.
- **`STIsEmpty()`** is recursive — `GEOMETRYCOLLECTION(POINT EMPTY)` is empty; adding one non-empty member makes it not.
- **Ordinate properties** are defined only on a non-empty Point; everything else reads NULL.
- **`STIsClosed()`** is false for a Point, a MultiPoint, an EMPTY instance and a mixed GeometryCollection; otherwise every figure must start and end at the same point.
- **`InstanceOf`** matches case-insensitively against the instance's own kind plus its supertypes, where the root is **`Geometry` for both spatial types** — `Geography` is not a name real recognizes.
  A name outside the OGC hierarchy raises **Msg 24105** rather than answering false, and `FullGlobe` is outside it on `geometry` specifically.
- **`MinDbCompatibilityLevel()`** returns 100 for every shape the simulator models.
- A **NULL receiver** yields NULL from every member rather than raising.

**`STSrid` is settable**: `SET @g.STSrid = 4326` re-stamps the instance, parsed in `Simulation.Set.cs`.
Assigning any other spatial property raises **Msg 6595** (`… because it is read only`); a NULL right-hand side raises the bare `System.ArgumentNullException` real emits *with no 24xxx code*; an SRID outside 0..999999 raises 24100.

**`.ToString()` collision with hierarchyid**: `HierarchyIdMethodCall.Run` detects a spatial receiver at runtime and routes to the spatial path.

### The Msg 6522 wrapper

Every spatial failure reaches a client as Msg 6522, built by `SimulatedSqlException.SpatialFailure`:

```
A .NET Framework error occurred during execution of user-defined routine or aggregate "geometry": <CR><LF>
System.FormatException: 24114: <message><CR><LF>
System.FormatException: <CR><LF>
.
```

The line endings are CRLF (real's, not the repo's LF convention), the exception-type name varies by failure class (`FormatException` for malformed input, `ArgumentOutOfRangeException` for an index below 1, `ArgumentException` for an invalid argument), and an argument failure adds a `Parameter name: n` line.

The CRLF is **not** a Windows artifact, which is the obvious thing to suspect since the text comes from .NET exception formatting inside the server process.
Cross-checked 2026-07-30 against SQL Server 2025 on both platforms — Windows 10 Pro (17.00.1125) and Linux / Ubuntu 24.04 (17.00.4065) — over 46 cases spanning every failure class's full message, both binary encodings and the float formatting: **byte-identical on both**, CRLF included.
So the hardcoded `\r\n` is right everywhere, and nothing else in this feature is platform-sensitive either.

**Divergence**: real appends the .NET stack frames of its own spatial assembly between the repeated exception-type line and the closing `.`; the simulator stops at that line, since the frames name internal Microsoft methods with no counterpart here.
Everything through the `24nnn: ` message is reproduced verbatim.

## Constructors — `Parser/Expressions/SpatialStaticCall.cs`

`geography::` and `geometry::` type-scope dispatched alongside `hierarchyid::` in `Expression.cs`'s `::` operator handling.

- `Parse(wkt)` — SRID defaults to 4326 (geography) / 0 (geometry).
- `STGeomFromText(wkt, srid)` and the per-kind `ST<Kind>FromText` family (`STPointFromText`, `STLineFromText`, `STPolyFromText`, `STMPointFromText`, `STMLineFromText`, `STMPolyFromText`, `STGeomCollFromText`), each binding only its own label and reporting Msg 24142 for any other.
- `STGeomFromWKB(bytes, srid)` and the matching per-kind `ST<Kind>FromWKB` family.
- `Point(x, y, srid)` — coordinates in the type's own order: `(x, y)` for geometry, `(latitude, longitude)` for geography, both spelled in WKT's (longitude, latitude) order on the way out.

Argument counts are checked at parse time as real checks them — **Msg 174**, severity 15, naming the function with the *caller's* casing (unlike the built-in function path, which lowercases).
Every other static method raises `NotSupportedException` at Run.

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

Statement dispatch: `Spatial` added to `ContextualKeyword` enum; CREATE SPATIAL routes via `UnquotedString { ContextualKeyword: ContextualKeyword.Spatial }`.
`INDEX` is reserved, so the sub-keyword check uses `Keyword.Index`.

## Catalog views in `BuiltInResources.cs`

**`sys.spatial_indexes`** (23-col, probe-confirmed): `object_id` / `name` / `index_id` / `type` (=4) / `type_desc` (`SPATIAL`) / `is_unique` (false) / `data_space_id` (1) / `ignore_dup_key` / `is_primary_key` / `is_unique_constraint` / `fill_factor` / `is_padded` / `is_disabled` / `is_hypothetical` / `is_ignored_in_optimization` / `allow_row_locks` (true) / `allow_page_locks` (true) / `spatial_index_type` (3 geometry / 4 geography) / `spatial_index_type_desc` (`GEOMETRY` / `GEOGRAPHY`) / `tessellation_scheme` / `has_filter` / `filter_definition` / `auto_created`.

**`sys.spatial_index_tessellations`** (16-col, probe-confirmed): `object_id` / `index_id` / `tessellation_scheme` / `bounding_box_xmin`/`ymin`/`xmax`/`ymax` / `level_1_grid` + `level_1_grid_desc` / … / `level_4_grid` + `level_4_grid_desc` / `cells_per_object`.
Unspecified GRIDS levels surface as NULL; `level_*_grid_desc` translates 1/2/3 codes to `LOW` / `MEDIUM` / `HIGH`.

**`sys.spatial_reference_systems`** (6-col): empty by default (real SQL Server pre-seeds ~390 EPSG/ESRI SRID rows; the simulator surfaces the column shape but skips the WKT-laden seed payload).
`spatial_reference_id` / `authority_name` / `authorized_spatial_reference_id` / `well_known_text` / `unit_of_measure` / `unit_conversion_factor`.

`sys.types` rows for geography/geometry carry `system_type_id=240`, `user_type_id=130` / `129`; the `ResolveSimpleKeyword` arm + `GetSysColumnMetadata` `SpatialSqlType => (-1, 0, 0)` wiring is what lets `CREATE TABLE (g geography)` accept them.

## Planar measures

`geometry`'s `STArea()` and `STLength()` evaluate in `Storage/Spatial/SpatialMeasures.cs`, exactly and for every shape kind (probe-confirmed 2026-07-31).
A polygon's area is its exterior ring less its interior rings, summed over Multi\* and GeometryCollection members; ring orientation is irrelevant because each ring's shoelace sum is taken absolute.
A polygon's **length is its boundary** — the perimeter of all its rings — so the ring walk is shared with the line walk.
A shape of the wrong dimension measures **0**, not NULL: a Point has neither length nor area.

**Divergence**: real accumulates a GeometryCollection's area with visible float noise — `GEOMETRYCOLLECTION(POLYGON((0 0,0 2,2 2,2 0,0 0)), LINESTRING(0 0,3 4))` measures `3.9999999999999076` where the simulator returns exactly `4`.
Matching it would mean reproducing real's internal summation order; the same noise shows in `STCentroid` and `STPointOnSurface`.

## Round-earth measures: the great elliptic arc

`geography`'s `STLength()` and point-to-point `STDistance()` measure along the **great elliptic arc** — the curve cut from the ellipsoid by the plane through the two points and the ellipsoid's centre.
Real does **not** use the geodesic, which is the assumption any stock implementation starts from, and the difference is measurable.

Measured 2026-07-31 against a Vincenty geodesic (accurate to well under a millimetre at these distances):

| Path | Vincenty geodesic | Real | Difference |
| --- | --- | --- | --- |
| (0,0) → (1,0), meridian | 110574.388558 | 110574.388493 | 0.065 mm |
| (0,0) → (1,1), oblique | 156899.568291 | 156899.567965 | 0.33 mm |
| equator → pole | 10001965.729312 | 10001965.670183 | 59 mm |
| Seattle → Paris | 8064120.203344 | 8064123.530151 | **3.3 m** |

The pattern identifies the curve.
Along a meridian, and from equator to pole, the great elliptic arc and the geodesic **coincide** — so the difference there is rounding.
On an oblique intercontinental path they genuinely part, and the great elliptic arc is the **longer** of the two, which is the direction real's value sits.
Recomputing Seattle → Paris as a great elliptic arc closes the 3.3 m gap to **3.2 mm**.

**Implementation** (`Storage/Spatial/SpatialGreatElliptic.cs`): convert both endpoints to geocentric Cartesian on the ellipsoid; take the central plane through them; restrict the ellipsoid's quadratic form `diag(1/a², 1/a², 1/b²)` to that plane, giving a 2×2 symmetric matrix whose principal axes are the section ellipse's semi-axes; find each endpoint's parameter angle on that ellipse; integrate `√(a₁²sin²t + a₂²cos²t)` between them — an incomplete elliptic integral of the second kind — by composite 20-node Gauss-Legendre.

**Accuracy**: the arc is computed exactly, so the residual against real is *real's own* approximation.
Across the probed set the worst relative error is **5.9e-9** (59 mm over a quarter meridian); Seattle → Paris lands within 3.2 mm, Tokyo → New York within 0.1 mm, and the equator is exact because that section is a circle.
Tests assert a 1e-8 relative tolerance rather than equality.

`STDistance` is modeled **between two points**; other shape pairs need closest-approach geometry and raise.
Operands with different SRIDs, and an empty operand, both read NULL rather than raising — matching real.


## Not modeled yet

- **Ellipsoidal polygon area** — `geography`'s `STArea`. The round-earth *length* and point-to-point *distance* ship (see [Round-earth measures](#round-earth-measures-the-great-elliptic-arc)); area is the companion problem and needs the spherical-excess-plus-ellipsoidal-correction treatment rather than the arc integral.
- **`STDistance` between shapes that aren't both points**, which needs closest-approach geometry.
- **`STCentroid` / `STPointOnSurface` / `EnvelopeAngle` / `EnvelopeCenter`.**
- **Topological predicates** — `STIntersects` / `STContains` / `STWithin` / `STDisjoint` / `STTouches` / `STCrosses` / `STOverlaps` / `STEquals` / `STRelate`, and the validity pair `STIsValid` / `STIsSimple`.
  Real raises **24144** from most methods on a stored-but-invalid instance, which is part of the same DE-9IM machinery.
- **Constructive operations** — `STUnion` / `STIntersection` / `STDifference` / `STSymDifference` / `STBuffer` / `STConvexHull` / `STBoundary` / `STEnvelope` / `MakeValid` / `Reduce` / `Filter` / `ShortestLineTo` / `BufferWithTolerance` / `BufferWithCurves` / `CurveToLineWithTolerance`.
- **A spatial column's property form** — `Location.Lat` reads as a two-part *column* name, because nothing in the syntax distinguishes the two and `t.Lat` is far more likely to be a column.
  Dispatch is limited to receivers that can't be a table qualifier (a constructor call, a variable, a parenthesized expression); telling the two apart for a column needs binder support.
  The method form (`Location.STAsText()`) works everywhere, since the argument list disambiguates.
- **Curved shapes and FULLGLOBE** — `CIRCULARSTRING` / `COMPOUNDCURVE` / `CURVEPOLYGON` / `FULLGLOBE` are recognized labels (real accepts them, so reporting them as unknown would be the wrong error) that raise `NotSupportedException` naming the kind.
- **GML** — `AsGml` / `STAsGML`, and the `GeomFromGml` constructors.
- **SRID-aware operations** — the SRID is tracked per value and reported, but nothing transforms between reference systems, and no operation yet compares two operands' SRIDs (real returns NULL from `STDistance` across a mismatch).
- **Spatial-index query-planner integration** — the index parses cleanly but never accelerates anything.
- **`sys.spatial_reference_systems` seed data** (~390 EPSG/ESRI rows).
- **`ALTER SPATIAL INDEX`** (REORGANIZE / REBUILD).

## Divergences

- **The `isValid` property bit is always set.**
  Real sets it for a valid instance and clears it for a stored-but-invalid one; the simulator has no topological validator to clear it with.
  Quantified end-to-end by importing a simulator-exported WWI-Standard bacpac into the live reference and byte-comparing against the original database: **189 of 190 `Countries.Border` values byte-identical**, the single divergent row being WWI's one stored-invalid Border (`STIsValid() = 0` on the original; spatial methods on it raise 24144).
  On the imported copy that row reports `STIsValid() = 1` and methods evaluate instead of throwing — the divergence's full observable consequence is that one row, one bit, and error-vs-answer method behavior on it.
  All 5,000 sampled `Cities.Location` points byte-identical.
- **Msg 6522 omits the .NET stack-frame block** — see [The Msg 6522 wrapper](#the-msg-6522-wrapper).
- **The in-process reader surfaces a spatial column as its WKT** (`SqlType.ClrType` is `string`), where real SqlClient hands back the UDT bytes (or a `SqlGeography` when `Microsoft.SqlServer.Types` is loaded).
  The TDS path is faithful — it writes the serialization.
