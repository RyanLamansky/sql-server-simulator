# `geography` / `geometry` data types

Spatial values are parsed instances, stored in SQL Server's own UDT serialization.
WKT parsing (with real's validation failures), canonical WKT rendering, per-value SRID, Z / M ordinates, EMPTY instances, the OGC binary encodings, the constructor family and the whole structural member surface all ship.
So do all three measures for both spatial types — area, length and distance, planar and round-earth — and, for `geometry`, the whole topological surface: the eight predicates, `STRelate`, `STIsValid`, and the Msg 24144 gate an invalid instance puts on most instance methods.
What doesn't yet is the round-earth topology and the constructive operations (`STUnion` / `STBuffer` / …), which parse cleanly and raise `NotSupportedException` at execute — see [Not modeled yet](#not-modeled-yet).

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

**Methods that evaluate**: `ToString`, `STAsText`, `AsTextZM`, `STAsBinary`, `AsBinaryZM`, `STGeometryType`, `STDimension`, `STNumPoints`, `STPointN`, `STStartPoint`, `STEndPoint`, `STIsClosed`, `STIsEmpty`, `STIsRing`, `STNumGeometries`, `STGeometryN`, `STExteriorRing`, `STNumInteriorRing`, `STInteriorRingN`, `NumRings` / `RingN` (geography), `InstanceOf`, `MinDbCompatibilityLevel`, `ReorientObject`, plus the measures and — on `geometry` — the [topological predicates](#topological-predicates-the-de-9im-engine) and `STIsValid`.

The catalog carries a fourth column beside form / scope / result: whether the member refuses a stored-but-invalid instance, which is the [Msg 24144 gate](#validity--stisvalid-and-msg-24144).
`STIsValid` itself is a member of **both** spatial types, unlike `STIsSimple` / `STTouches` / `STCrosses` / `STRelate`, which are `geometry`-only.

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

`geography`'s `STLength()`, `STArea()` and `STDistance()` all measure along the **great elliptic arc** — the curve cut from the ellipsoid by the plane through the two points and the ellipsoid's centre.
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
The arc is held in that ellipse's own principal frame (`GreatEllipticArc`), which is what lets the area and closest-approach work below ask for a point partway along it.

**Accuracy**: the arc is computed exactly, so the residual against real is *real's own* approximation.
Across the probed set the worst relative error is around **1e-8**: 59 mm over a quarter meridian, 11 mm over 10° of equator (where the section is a circle and the simulator's own quadrature is exact), 3.2 mm over Seattle → Paris and 0.1 mm over Tokyo → New York.
Real's drift there doesn't grow monotonically with the distance — one degree of equator is short by 5.2e-10 where ten degrees is long by 1.0e-8.
Tests assert a relative tolerance rather than equality, per case.

Two endpoints that **coincide or are exactly antipodal** define no plane.
A coincident pair measures 0; an antipodal one measures **half the meridian ellipse's perimeter** — `POINT(0 0)` to `POINT(180 0)` is 20003931.458 m on real, the smallest central section's half-perimeter and not the equator's, which is 33 km longer.
Detecting the case needs a relative test rather than a zero one, because `sin(π)` is 1.2e-16 rather than 0, so an exactly antipodal pair still crosses to a normal at the noise floor.

Operands with different SRIDs, and an empty operand, both read NULL rather than raising — matching real.
A **non-spatial argument is read as well-known text**, which is what real does with `.STDistance('POINT(3 4)')`.

### Ellipsoidal polygon area

`geography`'s `STArea()` integrates the ellipsoid's own surface element over the region its great elliptic edges bound (`Storage/Spatial/SpatialEllipsoidArea.cs`).
The element depends on latitude alone, so its antiderivative

```
AreaBelow(φ) = a²(1-e²) · [ sinφ / (2(1-e²sin²φ)) + atanh(e·sinφ) / 2e ]
```

— the area between the equator and a parallel, per radian of longitude — turns the double integral into a line integral by Green's theorem: a ring encloses `-∮ AreaBelow(φ) dλ`.
Each edge contributes that integral over its own great elliptic track, taken by Gauss-Legendre in the section's parameter with the panel count following the longitude span.

**How the model was identified** (2026-08-02), following the method the length work used — hypothesis, high-precision probe, residual analysis.
A 0.01° square at the equator measures `1230907.2048772429` on real, while the exact **parallel-bounded quadrangle** — the closed-form ellipsoidal answer — is `1230907.2018475635`.
The 3.03e-3 m² gap is not noise and not an ellipsoid constant: it is exactly the poleward bulge of a great elliptic top edge, whose midpoint sits `(Δλ²/8)·sinφ·cosφ` above the parallel.
Reproducing the bulge closes it.

One simplification makes the edge model unambiguous: the ground track of a central plane section satisfies `tanφ = K·cos(λ - λ₀)`, and so does a great circle drawn on *any* latitude that is a fixed monotone rescaling of the geodetic one.
So the great elliptic arc, a great circle over geocentric latitude, and a great circle over reduced latitude all trace the **same** curve through the same two points — there is nothing to choose between them, and only a genuine geodesic (whose track carries an O(f) longitude correction) would differ.

**Residuals against real**, over a probed matrix of squares, country quads, equator-crossing and southern polygons, holes, multipolygons, slivers, bands and polar caps:

| Case class | Agreement |
| --- | --- |
| 0.01° squares (equator, 60°N, southern, longitude-rotated) | 8e-11 |
| a 20°-wide band at 40–50°N | 2.5e-13 |
| country-sized quad, equator-crossing square, thin sliver, hole, multipolygon | 6e-12 … 1e-10 |
| a 1°×0.5° polygon at 89°N | 5e-12 |
| a 360-vertex cap around the pole | 9e-10 |
| pole-to-pole strip, 90°-wide band at the equator, octant, hemisphere | 1.5e-8 … 2.2e-8 |
| 90°-wide band at 80–85°N | 5.4e-6 |
| four-vertex cap around the pole, 90°-wide band at 89°N | 1.1e-4 |

The pattern is real's, not the model's: accuracy degrades with an edge's **longitude span**, and worse the nearer the pole that edge runs — the same cap that differs by 1.1e-4 with four vertices comes back to 9e-10 with 360.
Real is not self-consistent there either: its `FULLGLOBE` constant is within 2.6e-11 of the exact surface area, while the hemisphere it computes from an equatorial ring is 1.7e-8 short of half of it.
The simulator computes its model exactly and lets the difference stand; tests carry a per-case tolerance for that reason.

**Ring orientation is read**, unlike the planar measure: a `geography` ring's interior lies to the **left** of the direction it is written, so the clockwise spelling of a square names everything else and measures the surface area less its own (probe-confirmed).
A polygon's rings are summed signed, so a hole wound against its shell subtracts, and a negative total folds into the complement.
A ring that **encircles a pole** never closes in longitude — its edges sweep a full turn — and the boundary is completed along the pole itself, whose contribution is the whole polar zone; a ring with a **pole as a vertex** traverses from the meridian it arrived on to the one it leaves by.
Multipolygon and GeometryCollection members sum, and a shape of the wrong dimension measures 0.

### Closest approach

`STDistance` measures between instances of any shape for both spatial types.
The answer has the same shape in both: **zero** where the two meet or one contains the other, and otherwise the least distance over their component pairs — isolated points, edges, and each operand's rings for the containment test.
A point inside a polygon's **hole** is outside the polygon and measures to the hole's ring, matching real.

- **`geometry`** reuses the predicate engine's flattening (`SpatialRelateOperand`) and adds the straight-edge primitives to `SpatialTopology`: point-to-segment through the clamped perpendicular foot, segment-to-segment as zero-if-they-meet else the nearest of four endpoint approaches, and even-odd point-in-rings for containment.
  Every probed value is reproduced exactly, `√2` and all.
- **`geography`** minimizes along the arcs.
  Point-to-arc is a golden-section search over the arc parameter; arc-to-arc tests for a crossing first — the two section planes meet in a line through the centre, whose two surface points are the only places the arcs can meet — and otherwise alternates one-dimensional searches from the best endpoint seed.
  Containment is the **winding** of the rings seen from the point: in the frame that puts the point at a pole, a ring that encircles it turns through a full revolution, and summing over every ring handles holes without a separate rule.
  Probed agreement is 1e-11 or better on most of the matrix; the cases that sit at real's own ~1e-8 arc-length residual are the ones whose answer runs a long way along one great ellipse.

An instance the other **runs through** answers exactly 0, not the residual a search leaves behind.
The minimum of a distance that reaches zero has a kink rather than a curve, so a golden section converges on it linearly and stops a few microns short; the zero is recognized structurally instead — a point in an arc's plane and between its endpoints is *on* it — which also settles two arcs sharing a great ellipse, since an endpoint of one then lies on the other.
Arcs that share a plane are deliberately excluded from the crossing test for the same reason the recognition is needed: they cross to a direction that is pure roundoff, and normalizing that noise would name an arbitrary surface point that could fall inside both spans and report a meeting that isn't there.

Two numerical choices are worth naming.
The search runs on the **chord** rather than the arc length and only the winner is measured properly: a chord and the surface distance it stands for are related by a factor depending on the chord alone as long as the section's curvature holds still, so the two share a minimizer to within the flattening — worth ~1e-12 relative on the value, three orders below real's own error, for a search step costing two trigonometric evaluations instead of a whole elliptic integral.
And the pairs worth searching are picked out by a **chord pre-pass**: a chord runs through the ellipsoid, so it never exceeds the surface distance and `c·(1 + c²/6b²)` never falls short of it; one cheap pass takes the shortest chord over every pair and the exact pass measures only those clearing that threshold.
Without it a scan starting from an infinite bound measures the whole first row exactly — for two 2,000-vertex borders that is thousands of searches a chord rules out in a few flops each, and it was the difference between 13 s and 0.6 s.

## Topological predicates: the DE-9IM engine

`geometry`'s eight predicates — `STIntersects`, `STContains`, `STWithin`, `STTouches`, `STCrosses`, `STOverlaps`, `STDisjoint`, `STEquals` — plus `STRelate` evaluate over a hand-rolled planar engine in `Storage/Spatial/SpatialTopology.cs` + `SpatialRelate.cs`.
There is no external dependency: the engine is straight-edge computational geometry over the existing parsed value model.

Each predicate is a **mask over the DE-9IM matrix**, which is exactly how real exposes them — `STRelate(other, pattern)` is the raw matcher, and probing it one cell at a time is how the reference matrices below were harvested.

### Building the matrix

`SpatialRelate.Matrix` computes the nine intersection dimensions directly rather than through a labelled overlay.
Each operand flattens into three OGC component classes — isolated points, line segments, polygon rings — and then:

1. Every segment from both operands is **noded** against every other (an x-sweep with an active list keeps the pairwise scan near-linear), giving a set of nodes and non-crossing edge pieces.
   Isolated points join the node set so no piece has one in its interior.
2. Each **node** is classified against both operands and contributes dimension **0** to its cell.
3. Each **edge piece**'s midpoint is classified the same way and contributes dimension **1**.
4. Each edge piece's two **faces** contribute dimension **2**. A face is interior or exterior, never boundary; where the piece runs along one of the operand's ring edges the covering ring's orientation names which side is which, and otherwise both sides read the same as the midpoint.
5. The exterior/exterior cell is **2** unconditionally, the plane being unbounded — real reports it as 2 even for two empty instances.

Operands whose extents don't meet skip the arrangement entirely: each one's interior and boundary sit whole in the other's exterior, so the four outer cells are the operands' own dimensions and the rest are empty.
That is the shape of a spatial filter that misses, and it keeps a many-vertex border from paying for an arrangement it can't need — a 2,000-vertex polygon answers a miss in single-digit milliseconds against a couple of hundred for a hit.

Semantics worth pinning, all probe-confirmed against SQL Server 2025:

- **Interior and boundary are the per-class unions**, not a normalized point set.
  In `GEOMETRYCOLLECTION(POINT(0 0), LINESTRING(0 0, 2 2))` the origin is reported by real as *both* interior (the point member) and boundary (the line's endpoint), and the matrix carries both.
- A line's boundary follows the **mod-2 rule** across every line figure in the instance: a vertex two figures share is not a boundary point, one three figures share is.
  A point in that boundary set is not in the line interior even where another figure runs through it.
- **Z and M take no part** in any predicate.
- An **empty** operand puts everything in its exterior — it is disjoint from every instance including another empty one, and intersects, contains and touches nothing.

### The predicate masks

| Predicate | Rule |
| --- | --- |
| `STDisjoint` | `FF*FF****` |
| `STIntersects` | the negation of `STDisjoint` |
| `STContains` | `T*****FF*` |
| `STWithin` | `T*F**F***` |
| `STEquals` | `T*F**FFF*`, or **both operands empty** — real answers true for `POINT EMPTY` against `POLYGON EMPTY` although no mask matches |
| `STTouches` | interior/interior empty **and** any of interior/boundary, boundary/interior, boundary/boundary non-empty; always false when both operands are zero-dimensional |
| `STCrosses` | `T*T***T**`, gated to **dim(receiver) < dim(argument)**, plus interior/interior = 0 for a line-on-line pair. Real does **not** symmetrize it: a line crossing a polygon answers true, the polygon answers false |
| `STOverlaps` | same dimension only — `1*T***T**` for a one-dimensional pair (two lines meeting at a point overlap nothing), `T*T***T**` otherwise |

The dimension the gates read is `STDimension`'s: 0 / 1 / 2, or -1 for an empty instance, and the largest among a collection's non-empty members.

### Result shape

A predicate yields `bit`.
NULL propagates from either side, and — as with `STDistance` — operands in **different spatial reference systems** read NULL rather than raising.
A non-spatial argument is read as well-known text, which is what real does with `.STContains('POINT(2 2)')`.
`STRelate` validates its pattern before it looks at the operands: nine characters (**Msg 24109**, counting a NULL as zero) drawn from `0 1 2 T F *` (**Msg 24110**, case-sensitive, reporting the zero-based position).

### Arithmetic and tolerance

Every test runs in `double`, and the orientation determinant carries a **relative error filter**: a determinant no larger than the roundoff bound of its own two products (Shewchuk's `(3 + 16ε)ε` static filter) reads as *collinear*.
That is what real does, and it is neither exact arithmetic nor a fixed epsilon:

- `POINT(1.1666666666666665 0.5)` against `LINESTRING(0 0, 7 3)` has a naive cross product of 4.4e-16 — exact arithmetic says off the line, real says **on** it.
- `POINT(1 1e-18)` against `LINESTRING(0 0, 2 0)`, where the determinant is computed with no roundoff at all, is **off** the line — and so is every offset down to the denormal floor.

Coordinates otherwise compare exactly.

## Validity — `STIsValid` and Msg 24144

Real stores a malformed-but-parseable instance happily and then refuses to *operate* on it.
`Storage/Spatial/SpatialValidator.cs` implements the rules, probe-derived and diffed 64-for-64 against the reference:

- A **Point** or **MultiPoint** is always valid, repeated coordinates included.
- A **LineString** is invalid when its last two vertices coincide, or when any two of its segments share a one-dimensional stretch.
  Crossing itself at a point costs simplicity, not validity, and a repeated vertex anywhere but the end is fine — so `LINESTRING(0 0, 2 0, 2 0, 4 0)` is valid while `LINESTRING(0 0, 2 0, 2 0)` is not.
- A **MultiLineString** adds: no two members may share a one-dimensional stretch. Meeting at a point is fine.
- A **Polygon**'s rings must each enclose area and be simple, must not cross or share a one-dimensional stretch with each other, must hold every interior ring inside the exterior one without nesting interior rings, and must leave the interior **connected**.
  Connectivity is the ring-touch graph: rings are nodes, each distinct point where two of them meet is an edge, and a cycle is exactly a chain of touches that pinches the interior in two — a hole meeting the shell twice is invalid, a hole meeting it once is not.
  Consecutive repeated vertices collapse before the ring checks, so `POLYGON((0 0, 4 0, 4 4, 0 4, 0 0, 0 0))` is valid and `POLYGON((0 0, 2 0, 2 0, 0 0, 0 0))` is not.
- A **MultiPolygon**'s members may touch at points but may not overlap, share a one-dimensional stretch, or contain one another.
- A **GeometryCollection** is valid exactly when every member is; members may overlap each other freely.

**Msg 24144** (`This operation cannot be completed because the instance is not valid…`, wrapped in the usual [Msg 6522 envelope](#the-msg-6522-wrapper)) is what most of the instance surface reports against an invalid instance — and the split is sharp.
Real *tolerates* invalidity in `STAsText` / `ToString` / `AsTextZM` / `STAsBinary` / `AsBinaryZM`, the ordinate reads (`STX` / `STY` / `Z` / `M` / `HasZ` / `HasM`), `STSrid`, `STIsEmpty`, `STIsRing`, `STLength`, `MinDbCompatibilityLevel`, `MakeValid` and `STIsValid` itself.
Everything else — `STArea`, `STDimension`, `STGeometryType`, `STNumPoints`, `STPointN`, `STStartPoint` / `STEndPoint`, `STIsClosed`, `STNumGeometries`, `STGeometryN`, `STExteriorRing`, `STNumInteriorRing` / `STInteriorRingN`, `InstanceOf`, `STDistance`, every predicate, `STRelate`, and every constructive operation — raises.
The gate is a `ValidityGate` flag on the member catalog in `Parser/Expressions/SpatialMethodCall.cs`, and validity is computed once per instance and cached, because a stored value is decoded once and read many times.
An invalid **argument** raises the same way an invalid receiver does.

## Not modeled yet

- **`geography`'s topological predicates** — `STIntersects` / `STContains` / `STWithin` / `STDisjoint` / `STEquals` / `STOverlaps`, and `STIsValid`; those seven are the whole surface real exposes there, since `STTouches` / `STCrosses` / `STRelate` / `STIsSimple` are `geometry`-only members that report Msg 6506 on a `geography` receiver.
  The planar engine doesn't transfer: a round-earth edge is a great elliptic arc, so segment intersection, point-in-ring and ring orientation all become spherical problems, and real's own answers differ — `LINESTRING(0 0, 2 2)` and `LINESTRING(0 0, 1 1, 2 2)` are equal as `geometry` and **not** equal as `geography`, because the arc from (0,0) to (2,2) doesn't pass through (1,1).
  A planar approximation would answer the second case wrongly, so the seven raise `NotSupportedException` naming `geography` instead.
  The Msg 24144 gate follows the same line: it fires for `geometry`, where the validator exists, and not for `geography`.
- **`STCentroid` / `STPointOnSurface` / `EnvelopeAngle` / `EnvelopeCenter`.**
- **`STIsSimple`** — validity stops short of the self-intersection classification simplicity asks for (a self-crossing LineString is valid and not simple).
- **Constructive operations** — `STUnion` / `STIntersection` / `STDifference` / `STSymDifference` / `STBuffer` / `STConvexHull` / `STBoundary` / `STEnvelope` / `MakeValid` / `Reduce` / `Filter` / `ShortestLineTo` / `BufferWithTolerance` / `BufferWithCurves` / `CurveToLineWithTolerance`.
- **A spatial column's property form** — `Location.Lat` reads as a two-part *column* name, because nothing in the syntax distinguishes the two and `t.Lat` is far more likely to be a column.
  Dispatch is limited to receivers that can't be a table qualifier (a constructor call, a variable, a parenthesized expression); telling the two apart for a column needs binder support.
  The method form (`Location.STAsText()`) works everywhere, since the argument list disambiguates.
- **Curved shapes and FULLGLOBE** — `CIRCULARSTRING` / `COMPOUNDCURVE` / `CURVEPOLYGON` / `FULLGLOBE` are recognized labels (real accepts them, so reporting them as unknown would be the wrong error) that raise `NotSupportedException` naming the kind.
- **GML** — `AsGml` / `STAsGML`, and the `GeomFromGml` constructors.
- **SRID-aware operations** — the SRID is tracked per value, reported, and compared between two operands (a mismatch reads NULL, as on real), but nothing transforms between reference systems and every `geography` SRID measures on WGS 84.
  Real carries a per-SRID ellipsoid, so the same polygon under SRID 104001 (the unit sphere) measures in radians squared there and in metres squared here.
- **Spatial-index query-planner integration** — the index parses cleanly but never accelerates anything.
- **`sys.spatial_reference_systems` seed data** (~390 EPSG/ESRI rows).
- **`ALTER SPATIAL INDEX`** (REORGANIZE / REBUILD).

## Divergences

- **A `geography` measure answers where real reports Msg 24144.**
  Real validates a round-earth instance before measuring it — `POLYGON((0 0,1 0,1 1,0 1,0 0),(0.2 0.2, 0.8 0.2, 0.8 0.8, 0.2 0.8, 0.2 0.2))`, whose hole is wound the same way as its shell, raises 24144 from `STArea()` — while the simulator's validator is the planar one and the [24144 gate](#validity--stisvalid-and-msg-24144) fires for `geometry` only, so the same instance measures the region its ring orientations describe.
  Round-earth validity arrives with the round-earth topology.
- **A `GEOMETRYCOLLECTION`'s round-earth area carries real's float noise.**
  Real sums a collection's members with visible drift — a square that measures `12308776255.868843` on its own measures `12308776246.986383` as a collection member, 8.9 m² lower — the same noise the [planar measures](#planar-measures) show.
  The simulator's answer is the same either way.
- **Three residual classes in the predicate matrix.**
  A 71-shape square — 5,041 ordered pairs, each compared cell-by-cell and predicate-by-predicate against SQL Server 2025 (2026-08-02) — agrees on 99.4% of the pairs that avoid the three classes below, and every remaining disagreement is real answering something its own definitions don't support:
  1. **A `GEOMETRYCOLLECTION` containing a `POLYGON`.** Real's `STRelate` loses the polygon's boundary there and reports boundary points as interior or exterior — `POINT(2 0)` reads as exterior of `GEOMETRYCOLLECTION(POINT(9 9), POLYGON((0 0,4 0,4 4,0 4,0 0)))` while `STIntersects` on the same pair answers true, so real contradicts itself.
     The simulator reports the OGC answer, which also keeps its matrix and its predicates consistent.
  2. **Coordinate snapping below about 1e-14 of the extent.** Real folds a point that close to a segment endpoint onto it (`POINT(1 1e-15)` reads as *on* a polygon's boundary rather than inside it) and folds a denormal coordinate to zero (`POINT(0 5e-324)` equals `POINT(0 0)`); the simulator compares coordinates exactly.
     The [orientation filter](#arithmetic-and-tolerance) covers the near-collinearity half of real's tolerance and is matched; this end-of-segment half is not.
  3. **Endpoint touches on a diagonal segment.** Real's segment intersection misses some and invents others — `LINESTRING(0 0, 2 2)` and `LINESTRING(1 -1, 1 1)` read as *disjoint* although they share (1,1), while `LINESTRING(0 0, 4 4)` and `LINESTRING(2 0, 2 2)` correctly touch; conversely `LINESTRING(1 1, 3 3)` and `LINESTRING(2 0, 2 2)` read as *crossing* where they touch.
     Real is inconsistent with itself across these, so there is no rule to reproduce.
- **The serialized `isValid` property bit is always set.**
  Real clears it for a stored-but-invalid instance — a bowtie polygon serializes with properties `0x00` where a square gets `0x04` — and the encoder doesn't, so an invalid instance's bytes differ from real's in that one bit.
  `STIsValid()` and the Msg 24144 gate read the shape tree rather than the bit, so behavior on such an instance is right; only the byte form isn't.
  Quantified end-to-end by importing a simulator-exported WWI-Standard bacpac into the live reference and byte-comparing against the original database: **189 of 190 `Countries.Border` values byte-identical**, the single divergent row being WWI's one stored-invalid Border.
  All 5,000 sampled `Cities.Location` points byte-identical.
  Real also *validates on deserialize*: handing it a payload whose `isValid` bit claims validity for a shape that isn't raises a bare `System.FormatException` inside Msg 6522, which the decoder doesn't reproduce either.
- **Msg 6522 omits the .NET stack-frame block** — see [The Msg 6522 wrapper](#the-msg-6522-wrapper).
- **The in-process reader surfaces a spatial column as its WKT** (`SqlType.ClrType` is `string`), where real SqlClient hands back the UDT bytes (or a `SqlGeography` when `Microsoft.SqlServer.Types` is loaded).
  The TDS path is faithful — it writes the serialization.
