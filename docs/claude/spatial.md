# `geography` / `geometry` data types

Spatial values are parsed instances, stored in SQL Server's own UDT serialization.
WKT parsing (with real's validation failures), canonical WKT rendering, per-value SRID, Z / M ordinates, EMPTY instances, the OGC binary encodings, the constructor family and the whole structural member surface all ship.
So do all three measures for both spatial types — area, length and distance, planar and round-earth — and the whole topological surface of both: `geometry`'s eight predicates plus `STRelate`, `geography`'s six, `STIsValid` for each, and the Msg 24144 gate an invalid instance puts on most instance methods.
So do the derived-point members each type carries alone — `geometry`'s `STCentroid` / `STPointOnSurface` / `STIsSimple` and `geography`'s `EnvelopeAngle` / `EnvelopeCenter`.
So do the [constructive operations](#constructive-operations): the four set operations, the buffers, `ShortestLineTo`, `Reduce` and `MakeValid` for both types, `geometry`'s envelope, hull and boundary, `geography`'s hull, and the four spatial aggregates.
So do the [curved kinds](#curved-shapes) — `CIRCULARSTRING`, `COMPOUNDCURVE` and `CURVEPOLYGON` for both types — with their linearization, and [`FULLGLOBE`](#fullglobe).
The rest — GML, `IsValidDetailed`'s report on an invalid instance and a handful of members — parse cleanly and raise `NotSupportedException` at execute; see [Not modeled yet](#not-modeled-yet).

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
  A curved leaf also carries each figure's `SpatialFigureType` (line, arc or composite) and a composite figure's `SpatialSegmentType` run, which are the version 2 serialization's figure attributes and segment table; a `COMPOUNDCURVE` stores the point two elements share once.
  Carries `IsEmpty` (recursive, matching `STIsEmpty()`), `PointCount`, `Dimension` and `Coordinates()` (the order `STPointN()` indexes).
- `SpatialGeometry` — `Srid` + `Root`, plus `WithSrid` for the settable `STSrid` and `ValidateSrid` for the 0..999999 domain (Msg 24100 outside it).

**Factories**: `SqlValue.FromGeography` / `FromGeometry` / `FromSpatial(value, isGeography)`.
`SqlValue.FromString` routes a spatial target through the WKT reader, which is what makes `CAST(@nvarchar AS geometry)` parse text.

## WKT — `Storage/Spatial/SpatialWktReader.cs` + `SpatialWktWriter.cs`

The reader accepts the full 2D/Z/M grammar: all eleven shape kinds, `EMPTY` at any level, both MULTIPOINT spellings (`(0 0, 1 1)` and `((0 0), (1 1))`, with the first element fixing the form for the rest), a literal `NULL` in the Z slot, and case-insensitive labels.
Labels match as a **prefix**, not as a greedy word — which is why `POINTX(1 2)` reports a missing `(` rather than an unknown label, exactly as real does.

The writer emits real's canonical spelling: a space between label and body (`POINT (1 2)`), `", "` between coordinates and between members, `EMPTY` for a shape with no coordinates, and Multi\* members without their own label.
Ordinates use .NET Framework's round-trip `"R"` form under the invariant culture, which is what real emits — `1.50` → `1.5`, `1e10` → `10000000000`, `1e30` → `1E+30`, `0.000001` → `1E-06`.
That form is fifteen significant digits when they round-trip and seventeen otherwise, which modern .NET's `"R"` (the shortest round-tripping form) is not: a computed vertex prints `4.0000000000000071` on real, where the shortest form is `4.000000000000007`.

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
| 24305 / 24306 | a `geography` polygon ring with fewer than four points / whose ends differ — `geography` numbers every ring from 1 where `geometry` names the exterior one; a `CURVEPOLYGON` ring written `EMPTY` still reports `geometry`'s 24118 / 24120 on both types |
| 24212 | a `CIRCULARSTRING` of a single point |
| 24214 | an arc whose three points disagree on Z, a missing Z included |
| 24134 | a `COMPOUNDCURVE` element that doesn't start exactly where the previous one ended, Z and M included |
| 24300 / 24301 | a `CURVEPOLYGON` ring written `CIRCULARSTRING EMPTY` / `COMPOUNDCURVE EMPTY` — real's builder reporting a missing figure / segment |
| 24150 | `FULLGLOBE` as a `GEOMETRYCOLLECTION` member |

Two of these carry probe-derived position arithmetic that isn't worth deriving from first principles:

- **24141** reports the index *after* the offending token when the reader consumed one (`POINT(1 X)` → position 9), and the index *of* the character when it's a delimiter the reader didn't consume (`POINT(1)` → position 7).
- **24142** reports the offset itself for a single-character expectation, and one past it for a label expectation whenever the remaining input is longer than the label.
  The echoed text is the label's width of input when that much remains, and a single character when it doesn't (`STPointFromText('PO')` → `at position 0. The input has "P"`).

The curved kinds read with real's grammar (probed 2026-09-29): a `CIRCULARSTRING` reads its points in pairs after the first, so `CIRCULARSTRING(0 0, 1 1)` is 24142 expecting the `,` of the second pair; a `COMPOUNDCURVE` or `CURVEPOLYGON` element opening with a `C` must be a labelled `CIRCULARSTRING` (or, for a ring, `COMPOUNDCURVE`) and anything else must be a bare `(`, which is why `COMPOUNDCURVE(LINESTRING(…))` expects `(` and `COMPOUNDCURVE(COMPOUNDCURVE(…))` expects `CIRCULARSTRING`.
The Multi\* kinds take no curved member.

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

**Version 2** (the MS-SSCLRT serialization spec's layout, byte-checked against SQL Server 2025 on 2026-09-29) is what real writes exactly when version 1 can't hold the instance: any curved member anywhere, `FULLGLOBE`, or a `geography` instance no cap below a hemisphere holds.
The last also sets property bit `0x20` (larger than a hemisphere) — so a clockwise `geography` square serializes as version 2 with properties `0x24`, as does a line reaching 90° from its points' summed direction — and every version 2 instance reports `MinDbCompatibilityLevel()` 110.
Its figure attribute is the figure's own type — `0x01` line (a point figure and every plain polygon ring included), `0x02` arc, `0x03` composite — and when a composite figure exists the shape table is followed by a segment count and one byte per segment: `0x00` line, `0x01` arc, `0x02` a line opening a written element, `0x03` an arc opening one.
The shortcut bodies are version 1 only.
`FULLGLOBE` is a shape of type `0x0B` with no figures.
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
A curve writes as the ISO records: a `CIRCULARSTRING` (type 8) like a `LINESTRING`, a `COMPOUNDCURVE` (9) as a count of `LINESTRING` / `CIRCULARSTRING` element records each repeating the point it shares, and a `CURVEPOLYGON` (10) as a count of ring records of whichever of the three kinds each ring is.
`FULLGLOBE` is the bodiless type code 126; its shape-table code 11 read as well-known binary is 24115, like any code naming no shape.

## Members — `Parser/Expressions/SpatialMethodCall.cs`

One catalog (`Members`) holds every member with its **form** (property vs method), its **owning type** (both / geography-only / geometry-only) and its result type.
Real enforces all three, and so does the simulator:

- a **method name written without parentheses** → **Msg 6592** (`Could not find property or field '…' for type '…'`), and so does a property the other spatial type owns (`Lat` on geometry, `STX` on geography)
- a **property written with parentheses** (`.Lat()`) → **Msg 6506**, the CLR *method*-not-found error, which is also what a method the other spatial type owns (`NumRings()` on geometry) reports; real emits 6506 *without* a trailing period, unlike 6592

**Properties** (no argument list): `STSrid`, `STX` / `STY` (geometry), `Lat` / `Long` (geography), `Z`, `M`, `HasZ`, `HasM`.

**Methods that evaluate**: `ToString`, `STAsText`, `AsTextZM`, `STAsBinary`, `AsBinaryZM`, `STGeometryType`, `STDimension`, `STNumPoints`, `STPointN`, `STStartPoint`, `STEndPoint`, `STIsClosed`, `STIsEmpty`, `STIsRing`, `STNumGeometries`, `STGeometryN`, `STExteriorRing`, `STNumInteriorRing`, `STInteriorRingN`, `NumRings` / `RingN` (geography), `InstanceOf`, `MinDbCompatibilityLevel`, `ReorientObject`, plus the measures, the [topological predicates](#topological-predicates-the-de-9im-engine) and `STIsValid`, `geometry`'s [`STCentroid` / `STPointOnSurface`](#representative-points-stcentroid-and-stpointonsurface) and [`STIsSimple`](#simplicity--stissimple), `geography`'s [`EnvelopeAngle` / `EnvelopeCenter`](#the-bounding-cap-envelopecenter-and-envelopeangle), and the [constructive operations](#constructive-operations).

The catalog carries a fourth column beside form / scope / result: whether the member refuses a stored-but-invalid instance, which is the [Msg 24144 gate](#validity--stisvalid-and-msg-24144).
`STIsValid` itself is a member of **both** spatial types, unlike `STIsSimple` / `STTouches` / `STCrosses` / `STRelate` / `STCentroid` / `STPointOnSurface` / `STEnvelope`, which are `geometry`-only, and `EnvelopeAngle` / `EnvelopeCenter` / `NumRings` / `RingN` / `ReorientObject`, which are `geography`-only — naming one on the wrong receiver is **Msg 6506**, real's CLR method-not-found error, since the method genuinely isn't on that class.

Semantics worth pinning, all probe-confirmed:

- An **index below 1** raises (24102 `STPointN` / 24103 `STGeometryN` / 24104 ring), while an index **above the count** reads as NULL.
  Geography's `RingN` reports 24104 under `STInteriorRingN`'s name, matching real.
  24102 and 24103 differ from each other by one word in real's wording ("This number" vs "The number"), reproduced verbatim.
- **`STDimension()`** is -1 for any EMPTY instance, and a collection reports the largest dimension among its non-empty members.
- **`STNumGeometries()`** is the member count for a collection, and 1 / 0 for a non-empty / empty instance of any other kind.
- **`STIsEmpty()`** is recursive — `GEOMETRYCOLLECTION(POINT EMPTY)` is empty; adding one non-empty member makes it not.
- **Ordinate properties** are defined only on a non-empty Point; everything else reads NULL.
- **`STIsClosed()`** is false for a Point, a MultiPoint, an EMPTY instance and a mixed GeometryCollection; otherwise every figure must start and end at the same point.
- **`InstanceOf`** matches case-insensitively against the instance's own kind plus its supertypes, where the root is **`Geometry` for both spatial types** — `Geography` is not a name real recognizes — and `FullGlobe` is not a `Surface`.
  A name outside the OGC hierarchy raises **Msg 24105** rather than answering false, and `FullGlobe` is outside it on `geometry` specifically.
- **`MinDbCompatibilityLevel()`** is 110 for an instance that needs [version 2](#spatialbinarycodec--the-udt-serialization) of the serialization and 100 otherwise.
- **`STStartPoint()` / `STEndPoint()`** are the instance's first and last points in `STPointN` order whatever its kind, a collection's included; **`STNumInteriorRing()`** is NULL on anything but a polygon.
- **`ReorientObject()`** reverses polygon rings and leaves lines and points as written.
- A **NULL receiver** yields NULL from every member rather than raising.

**`STSrid` is settable**: `SET @g.STSrid = 4326` re-stamps the instance, parsed in `Simulation.Set.cs`.
Assigning any other spatial property raises **Msg 6595** (`… because it is read only`); a NULL variable is **Msg 5302**, which ends the batch; a NULL right-hand side raises the bare `System.ArgumentNullException` real emits *with no 24xxx code*; an SRID real refuses raises as a constructor's does (below).

**SRIDs**: a `geometry` takes any SRID in 0..999999 (24100 outside it), while a `geography` takes only the 393 reference systems `sys.spatial_reference_systems` lists on real (24204 for any other — 0, 1, 999999 and -1 included; probed 2026-10-05 against SQL Server 2025); `SpatialGeometry.ValidateSrid` holds the list.

**Probed 2026-10-05 against SQL Server 2025**, a member name the receiver's type lacks is **Msg 6506** written as a method and **Msg 6592** as a property wherever the parse knows the receiver is spatial — a constructor, a member, a variable — including a static name `geography::` / `geometry::` doesn't have; `IsNull` (the CLR type's `INullable` property) reads 0.
An argument of the other spatial type is **Msg 206** while the statement compiles, and so is assigning one spatial type to the other in an `INSERT`, `UPDATE` or `SET`.
`ToString()` is `nvarchar(max)` as `STAsText()` is, so `LEN` of it is `bigint`.

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
The numeric parameters — `Point`'s three and every constructor's SRID — refuse NULL with **Msg 6569** (probed 2026-10-05 against SQL Server 2025), while a NULL text or binary argument yields NULL.
A CAST from bytes too short for what their header promises raises real's bare `System.FormatException: One of the identified items was in an invalid format.` with no 24xxx code, and `TRY_CAST` / `TRY_CONVERT` answer NULL for text that doesn't read as a spatial value (but still raise for bytes).
`UnionAggregate`, `EnvelopeAggregate`, `CollectionAggregate` and `ConvexHullAggregate` parse as [aggregates](#the-spatial-aggregates) instead.
Every other static method raises `NotSupportedException` at Run.

## Parser — `Simulation/Simulation.Spatial.cs`

```
CREATE SPATIAL INDEX name ON table(col)
    [USING {GEOMETRY_GRID | GEOMETRY_AUTO_GRID | GEOGRAPHY_GRID | GEOGRAPHY_AUTO_GRID}]
    [WITH (
        BOUNDING_BOX = ( xmin, ymin, xmax, ymax ) | ( XMIN = v, … )
        | GRIDS = ( level, level, level, level ) | ( LEVEL_n = level, … )
        | CELLS_PER_OBJECT = n
        | <relational index option>
        [, …]
    )]
    [ON filegroup]
```

- Stored in `HeapTable.SpatialIndexes`; `index_id` is one past the table's highest spatial id, from 384000 (probed 2026-09-26).
- Real's checks fall in tiers, which `TryParseCreateSpatial`'s remarks list in order (probed 2026-10-05 against SQL Server 2025): the option grammar while the batch compiles, then the table, column and clustered primary key, then the name and the session's SET options, and only then the tessellation parameters.
- With no `USING`, `GRIDS` picks the plain GRID scheme and its absence the AUTO one; an AUTO scheme records no levels, a GRID one MEDIUM for every level left out, and `CELLS_PER_OBJECT` defaults to 8, 12 or 16 by scheme; the scheme is stored upper-cased.
- `DROP_EXISTING = ON` replaces the index of the name, a rollback undoes the create, `ALTER INDEX … DISABLE | REBUILD | REORGANIZE` toggles `is_disabled`, and the index blocks its column's drop as an ordinary index does (Msg 5074).
- `DROP INDEX name ON table` drops one; the old `table.name` refusal (Msg 3749, state 2 for a spatial index) and the primary-key lock-in are shared with XML indexes — see [`xml.md`](xml.md#catalog-views-in-builtinresourcescs).

Statement dispatch: `Spatial` added to `ContextualKeyword` enum; CREATE SPATIAL routes via `UnquotedString { ContextualKeyword: ContextualKeyword.Spatial }`.
`INDEX` is reserved, so the sub-keyword check uses `Keyword.Index`.

## Catalog views in `BuiltInResources.cs`

**`sys.spatial_indexes`** (23-col, probe-confirmed): `object_id` / `name` / `index_id` / `type` (=4) / `type_desc` (`SPATIAL`) / `is_unique` (false) / `data_space_id` (1) / `ignore_dup_key` / `is_primary_key` / `is_unique_constraint` / `fill_factor` / `is_padded` / `is_disabled` / `is_hypothetical` / `is_ignored_in_optimization` / `allow_row_locks` (true) / `allow_page_locks` (true) / `spatial_index_type` (3 geometry / 4 geography) / `spatial_index_type_desc` (`GEOMETRY` / `GEOGRAPHY`) / `tessellation_scheme` / `has_filter` / `filter_definition` / `auto_created`.

**`sys.spatial_index_tessellations`** (16-col, probe-confirmed): `object_id` / `index_id` / `tessellation_scheme` / `bounding_box_xmin`/`ymin`/`xmax`/`ymax` / `level_1_grid` + `level_1_grid_desc` / … / `level_4_grid` + `level_4_grid_desc` / `cells_per_object`.
An AUTO scheme's levels surface as NULL; `level_*_grid_desc` translates 1/2/3 codes to `LOW` / `MEDIUM` / `HIGH`.

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

## Representative points: `STCentroid` and `STPointOnSurface`

Both are `geometry`-only — naming either on a `geography` receiver is **Msg 6506** — and both refuse an invalid instance with Msg 24144.
They live in `Storage/Spatial/SpatialCentroid.cs`.

**`STCentroid()`** answers only for a `Polygon` or a `MultiPolygon`.
A point, a line, a MultiPoint, a MultiLineString and — probe-confirmed — a `GEOMETRYCOLLECTION` whose every member is a polygon all read **NULL**, as does an empty instance of any kind.
The answer is the moment sum over the rings, the exterior ring adding and every interior ring subtracting whichever way each was written, so a 10×10 square holding a 2×2 hole centres at `488/96`.

**`STPointOnSurface()`** answers for every kind, and each has its own rule:

| Kind | Real's answer |
| --- | --- |
| Point | the point |
| LineString | the midpoint of its **first segment**, not its halfway point — `LINESTRING(0 0, 10 0, 11 0)` is `POINT (5 0)` |
| MultiPoint / MultiLineString | its **first** member's answer |
| Polygon | the centroid of the **ear at the exterior ring's topmost — then rightmost — vertex** |
| MultiPolygon | the same rule inside the member reaching **furthest right, then furthest up** |
| GeometryCollection | the polygon rule over every polygon member it holds, at any depth; with no polygon, its **last** member's answer |

The two orderings genuinely differ — a polygon picks its vertex topmost-first while a MultiPolygon picks its member rightmost-first — and each is what the probes force.
`POLYGON((0 0, 4 0, 4 1, 1 1, 1 4, 0 4, 0 0))` answers from its narrow upper arm, which only the topmost-first reading gives; a MultiPolygon pairing a tall left member with a small right one answers from the right one however the two are written, which only the rightmost-first reading gives.
The pick is geometric rather than positional, so rotating a ring or writing it the other way round doesn't move the answer.

### Divergences

Real's own values carry float noise a few ulps wide — a 4×2 rectangle centres at `POINT (2.0000000000000071 1.0000000000000036)` where the exact arithmetic says `(2, 1)` — so the simulator's answers differ from real's in the last bits of every areal case, as they do for the [measures](#planar-measures).

Beyond that, the **ear rule is real's own for a polygon with no interior ring, and not always for one with**.
Where the ear at the topmost vertex isn't a triangle of the polygon's own interior — or where real's triangulation simply cut elsewhere — the simulator falls back to a scanline point: the rightmost interior span of the horizontal line just below the topmost vertex.
That keeps the guarantee real's answer carries (the point lies in the instance, verified against the rings) without matching real's pick.
Over a 32-shape sweep diffed cell by cell against SQL Server 2025 (2026-08-02) the four members here agree on **124 of 128** cells; all four disagreements are `STPointOnSurface`, three on polygons with holes and one on a concave hexagon where real answers from an interior triangle rather than an ear.
Reproducing those would mean reproducing real's hole bridging and triangulation order, which one data point per arrangement doesn't fix.

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

## The bounding cap: `EnvelopeCenter` and `EnvelopeAngle`

`geography`-only — naming either on a `geometry` receiver is **Msg 6506** — and both refuse an invalid instance with Msg 24144.
`Storage/Spatial/SpatialEnvelope.cs` computes them.

Both run on the **unit sphere**, reading each coordinate's latitude as a spherical angle rather than a geodetic one.
`EnvelopeCenter()` is the normalized **sum of the instance's points as unit vectors** and `EnvelopeAngle()` is the greatest angle from that centre to any of them, in degrees.
A 1° square at the equator is what identifies the model: real centres it at latitude `0.50001903822621641` and reports `0.70711575561904183`, which is what the vector mean gives — the coordinate midpoint would sit at latitude 0.5 exactly, and a minimal enclosing cap would be narrower.

Three rules ride on top, all probe-derived:

- A **closed figure's repeated last point** takes no part in the sum, while an ordinary repeated vertex does: `LINESTRING(0 0, 0 0, 10 0)` centres a third of the way along at longitude `3.3295630553023212`, and the retraced triangle `LINESTRING(0 0, 10 0, 10 10, 0 0)` centres on its three distinct vertices.
- An instance whose greatest angle **reaches 90°** reports the angle as **180** — real's way of saying no cap below a hemisphere holds it — while the centre still reports the bearing it found.
  A polygon naming more than half the globe — a ring wound clockwise round a small region — reports 180 too, centred on the north pole (probed 2026-09-29).
- A summed direction that **cancels** leaves no bearing at all, and real answers `POINT (0 90)`, the north pole.
  The fold is a tolerance rather than exact cancellation: two points 1.75e-8 apart in summed magnitude still answer with their own bearing, and 1.75e-9 apart answer the pole, so the simulator folds below 1e-8.

Each angle is taken from the cross and dot products together, since an arccosine alone reads a point at the centre as 1.5e-8 radians away where real answers 0.

An **empty** instance reads NULL from both.
Over a 20-shape sweep against SQL Server 2025 (2026-08-02) all 40 cells agree, the worst absolute difference being 2e-12.

## Topological predicates: the DE-9IM engine

`geometry`'s eight predicates — `STIntersects`, `STContains`, `STWithin`, `STTouches`, `STCrosses`, `STOverlaps`, `STDisjoint`, `STEquals` — plus `STRelate` evaluate over a hand-rolled planar engine in `Storage/Spatial/SpatialTopology.cs` + `SpatialRelate.cs`.
There is no external dependency: the engine is straight-edge computational geometry over the existing parsed value model.

Each predicate is a **mask over the DE-9IM matrix**, which is exactly how real exposes them — `STRelate(other, pattern)` is the raw matcher, and probing it one cell at a time is how the reference matrices below were harvested.

### Building the matrix

`SpatialRelate.Matrix` computes the nine intersection dimensions directly rather than through a labelled overlay.
Each operand flattens into three OGC component classes — isolated points, line segments, polygon rings — and then:

1. Every segment from both operands is **noded** against every other (an x-sweep with an active list keeps the pairwise scan near-linear), giving a set of nodes and non-crossing edge pieces.
   Isolated points join the node set so no piece has one in its interior.
   A crossing is rounded to a double and can land a hair off both lines, so it is recorded as lying on the two segments that made it rather than re-tested — re-testing lost it, and two lines crossing at `(7.508…, 3.354…)` read as disjoint.
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

## Round-earth topology: `geography`'s predicates

`geography` exposes **six** predicates — `STIntersects`, `STContains`, `STWithin`, `STDisjoint`, `STEquals`, `STOverlaps` — and they evaluate over a round-earth DE-9IM engine in `Storage/Spatial/SpatialGeodeticRelate.cs` + `SpatialGeodeticTopology.cs`.
`STTouches` / `STCrosses` / `STRelate` / `STIsSimple` are not members of `SqlGeography` at all, and naming one is **Msg 6506** (see [Members](#members--parserexpressionsspatialmethodcallcs)).

The construction is the [planar engine](#topological-predicates-the-de-9im-engine)'s: every edge is noded against every other, and the arrangement's nodes, edge pieces and adjacent faces are each classified against both operands, a node contributing dimension 0 to its cell, a piece 1 and a face 2, with the exterior/exterior cell 2 unconditionally.
The predicates are the same masks over the same matrix, so only their four round-earth primitives are new.
The matrix itself stays internal, since real exposes no `STRelate` here to read it through.

### The four primitives

- **Two edges meet where their planes do.**
  A great elliptic arc is cut from a plane through the ellipsoid's centre, so two arcs' planes intersect in a line whose two surface points are the only places the arcs can touch.
  That one fact carries the crossing test, the intersection collection and the properly-cross test.
  Arcs sharing a plane have no such line and are compared by **span** instead: the endpoints of either that lie on the other are the shared boundary, and two distinct ones mean a one-dimensional overlap where one means a touch.
- **Sidedness is a step, not a determinant.** The point a short way off an edge is computed on the surface — `up × tangent` is the left — and then classified by the same containment test everything else uses, which keeps one definition of "inside" for the whole engine.
- **Containment is a crossing count.** A ring set alternates interior and exterior across every edge, so the parity of boundary crossings along the arc from a point known to be inside settles which face a query point is in.
  The known-inside point is a step off the **left** of a ring, which is where a geography ring puts its interior — that is what makes a clockwise square the whole globe less itself rather than an error, and what a lone azimuth-sum winding cannot express: a winding number seen from a point is the same for the region and for its antipodal image, so it names the wrong face for exactly the shapes whose interior is the unbounded one.
  A count is refused as unreliable when the path grazes a ring vertex or runs along an edge, and the question is re-asked from another interior point; three are kept per polygon.
- **Nodes snap.** Two arcs meeting at a vertex both operands wrote produce a point computed from two plane normals, landing a nanometre or so off the vertex itself, where the planar engine's arithmetic is exact. Anything within **0.1 mm** of an existing node folds onto it, through a cell-hashed lookup.

Each polygon carries its **own** interior reference, so overlapping members of a `GEOMETRYCOLLECTION` each answer for themselves rather than sharing one parity.

### Cost

An extent shortcut skips the arrangement when two operands' bounding spheres miss — but only when both operands **stay inside** their own extents, which a clockwise ring does not.
Every pairwise scan runs through an x-sweep with an active list, and the sidedness of a piece lying along one of an operand's own ring edges is read from that ring's direction rather than from a global containment test, which is what keeps a self-comparison of a many-vertex border from being quadratic.
A 2,000-vertex polygon answers `STContains` in well under a second and `STIsValid` in tens of milliseconds.

### Agreement with real

Two shape squares were driven through SQL Server 2025 and diffed cell by cell (2026-08-02): a 50-shape square of **2,500** ordered pairs mixing points, lines, polygons, holes, complements, antimeridian and polar shapes, and a 36-shape square of **1,296** pairs in *generic position* — coordinates offset so nothing lies exactly on anything else's boundary.

| Square | Pairs agreeing | Predicate bits agreeing |
| --- | --- | --- |
| 50 shapes, many touching exactly | 2,476 / 2,500 = **99.04%** | 14,966 / 15,000 = **99.77%** |
| 36 shapes, generic position | 1,294 / 1,296 = **99.85%** | 7,774 / 7,776 = **99.97%** |

Every remaining disagreement falls in one of the two classes under [Divergences](#divergences), and both are real contradicting itself.

## Validity — `STIsValid` and Msg 24144

Real stores a malformed-but-parseable instance happily and then refuses to *operate* on it.
`Storage/Spatial/SpatialValidator.cs` implements the planar rules, probe-derived and diffed 64-for-64 against the reference.
Real judges them on the [precision grid](#the-precision-grid) spanning the instance's own extent, and so does the validator: a line that retraces itself exactly in its written coordinates can miss itself by a grid step once snapped, so `LINESTRING(12 2, 6 16, 9 9)` is valid while `LINESTRING(0 0, 4 4, 2 2)` is not.
Over 800 generated line, multiline and ring cases built to sit on those edges, 796 agree with SQL Server 2025 (2026-09-28); the four left are near-retraces whose outcome on real doesn't follow the grid offset's size or sign.

- A **Point** or **MultiPoint** is always valid, repeated coordinates included.
- A **LineString** is invalid when any two of its segments share a one-dimensional stretch, or when it ends on a repeated vertex it reaches from *below* in sweep order (least y, then least x): `LINESTRING(0 0, 2 0, 2 0)` and `LINESTRING(2 5, 15 7, 15 7)` are invalid while `LINESTRING(2 0, 0 0, 0 0)` and `LINESTRING(15 7.5, 2 5, 2 5)` are valid.
  Crossing itself at a point costs simplicity, not validity, and a repeated vertex anywhere but the end is fine — so `LINESTRING(0 0, 2 0, 2 0, 4 0)` is valid.
- A **MultiLineString** adds: no two members may share a one-dimensional stretch. Meeting at a point is fine.
- A **Polygon**'s rings must each enclose area and be simple, must not cross or share a one-dimensional stretch with each other, must hold every interior ring inside the exterior one without nesting interior rings, and must leave the interior **connected**.
  Connectivity is the ring-touch graph: rings are nodes, each distinct point where two of them meet is an edge, and a cycle is exactly a chain of touches that pinches the interior in two — a hole meeting the shell twice is invalid, a hole meeting it once is not.
  Consecutive repeated vertices collapse before the ring checks, so `POLYGON((0 0, 4 0, 4 4, 0 4, 0 0, 0 0))` is valid and `POLYGON((0 0, 2 0, 2 0, 0 0, 0 0))` is not — but a ring's doubled closing vertex falls under the line rule, so the same square written from its top-right corner, `POLYGON((2 2, 0 2, 0 0, 2 0, 2 2, 2 2))`, is invalid.
- A **MultiPolygon**'s members may touch at points but may not overlap, share a one-dimensional stretch, or contain one another.
- A **GeometryCollection** is valid exactly when every member is; members may overlap each other freely.

### Round-earth validity

`geography` asks the same question over great elliptic edges, in `Storage/Spatial/SpatialGeodeticValidator.cs`.
The rule list is the planar one's with three differences, all probe-derived:

- **Ring orientation is load-bearing.** Every ring of a polygon must agree on which region the polygon names — read off the first ring's left side, with each other ring then having to find that region on its own left and something else on its right.
  A hole wound *with* its shell is invalid where planar validity doesn't look at orientation at all; a lone ring wound "backwards" is valid and names the complementary region.
- **Retracing means something different**, because the edges are arcs.
  `LINESTRING(0 0, 2 2, 1 1)` is invalid as `geometry` — the second segment runs back along the first — and valid as `geography`, since the arc from (0,0) to (2,2) doesn't pass through (1,1).
  A figure that stops on the vertex it already sits on is valid here too (`LINESTRING(0 0, 2 0, 2 0)`, which the planar rule rejects), because the repeat collapses before the edge checks rather than after; what a line may not do is have two of its edges share a one-dimensional stretch.
- **A ring that revisits one of its own vertices splits into lobes**, and the ordinary ring rules then decide.
  A lobe nested inside the main one and wound the other way is a hole that happens to meet its shell, which is valid; a lobe beside it, or a nested one wound the same way, is not.
  This is what real accepts on genuine coastline data — one WideWorldImporters border traces back to its own start vertex — and the four arrangements were probed one by one.

An edge whose endpoints are **antipodal** never reaches validity at all: no plane contains just those two points, so no arc joins them, and real refuses the instance while *constructing* it with **Msg 24206** (`The specified input cannot be accepted because it contains an edge with antipodal points…`), reported under its own `Microsoft.SqlServer.Types.GLArgumentException` rather than a `System.` type.
The refusal is an **angular tolerance of 1e-8 radians**, not exact equality: from `POINT(0 0)`, an edge reaching latitude 5.7e-7° past the antimeridian raises where 5.8e-7° is accepted.
The check runs on the WKT and well-known-binary construction paths; a pair of antipodal points that no edge joins (`MULTIPOINT((0 0), (180 0))`) is fine.

**Msg 24144** (`This operation cannot be completed because the instance is not valid…`, wrapped in the usual [Msg 6522 envelope](#the-msg-6522-wrapper)) is what most of the instance surface reports against an invalid instance — and the split is sharp, and the same for both spatial types.
Real *tolerates* invalidity in `STAsText` / `ToString` / `AsTextZM` / `STAsBinary` / `AsBinaryZM`, the ordinate reads (`STX` / `STY` / `Lat` / `Long` / `Z` / `M` / `HasZ` / `HasM`), `STSrid`, `STIsEmpty`, `STIsRing`, `STLength`, `MinDbCompatibilityLevel`, `MakeValid` and `STIsValid` itself.
Everything else — `STArea`, `STDimension`, `STGeometryType`, `STNumPoints`, `STPointN`, `STStartPoint` / `STEndPoint`, `STIsClosed`, `STNumGeometries`, `STGeometryN`, `STExteriorRing`, `STNumInteriorRing` / `STInteriorRingN`, `NumRings` / `RingN`, `ReorientObject`, `EnvelopeAngle` / `EnvelopeCenter`, `STCentroid`, `STPointOnSurface`, `STIsSimple`, `InstanceOf`, `STDistance`, every predicate, `STRelate`, and every constructive operation — raises.
The gate is a `ValidityGate` flag on the member catalog in `Parser/Expressions/SpatialMethodCall.cs`, asked in the terms of the receiver's own spatial type, and validity is computed once per instance and cached, because a stored value is decoded once and read many times.
An invalid **argument** raises the same way an invalid receiver does.

Round-earth validity was diffed against the reference over **67** shapes spanning both spatial types' rule differences, and over all **190** `geography` borders of WideWorldImporters — exact on every one.

## Simplicity — `STIsSimple`

Simplicity is what validity stops short of: a self-crossing `LINESTRING` is a valid instance and not a simple one.
`geometry`-only — `SqlGeography` has no such member, so naming it there is Msg 6506 — and gated on validity like everything else structural, so `Storage/Spatial/SpatialSimplicity.cs` never sees the one-dimensional overlaps validity already rejects.

- A **MultiPoint** is simple when no two of its points coincide.
- A **curve** — a line figure or a polygon ring — is simple when consecutive segments meet only at their shared vertex and no other pair meets at all, with a **closed** figure's first and last segments counting as consecutive.
  So a ring written as a `LINESTRING` is simple, while one that runs back over its own start and carries on isn't, and a line whose end lands in the interior of an earlier segment isn't either.
- Two **different figures** of one instance may meet only at a point that is a **boundary point of both** — an endpoint of an open figure.
  A ring is closed and so has none, which is why two members of a `MULTIPOLYGON` touching at a single point are valid and *not* simple, and why a hole meeting its shell at one point is too.
  Two lines meeting end to end are simple; one running into another's interior isn't.
- A **GeometryCollection** is simple exactly when every member is.
  Real never compares one member against another, so two crossing lines are simple as a collection and not as a `MULTILINESTRING`.
- An **empty** instance is simple, and a NULL receiver reads NULL.

Diffed against SQL Server 2025 over the same 32-shape sweep as the [representative points](#representative-points-stcentroid-and-stpointonsurface) — exact on every one.

## Constructive operations

The set operations, the envelope, hull and boundary, the buffers, `Reduce`, `MakeValid` and the four spatial aggregates all compute on real's precision grid over one planar overlay engine, with `geography` reaching the same engine through a projection.
The code is `Storage/Spatial/SpatialPrecisionGrid.cs`, `SpatialOverlay.cs`, `SpatialResultBuilder.cs`, `SpatialConstructive.cs`, `SpatialBuffer.cs` and `SpatialGeodeticConstructive.cs`.
Every rule below was probed against SQL Server 2025 on 2026-09-28, most of them by diffing thousands of randomly generated cases.

### The precision grid

Real snaps every coordinate onto an integer grid of 2^48 steps per axis across the operands' combined extent, computes there, and maps the result back.
Each axis has its own scale `s = 2^48 / (max - min)` and an integer offset `C = round(centre · s)`, and a coordinate lands at `trunc(x·s - C + 0.5)`.
An extent small against its distance from the origin — where `centre · s` passes the doubles' integers — anchors on the centre itself instead, `trunc((x - centre)·s + 0.5)`; the grid step then falls below the coordinates' own precision, and real's output there is plain decimal arithmetic (`1000000.001` stays `1000000.001`).
That truncation is not a rounding: a scaled value below the centre with any fraction lands one step high, which is why a vertex the operation computed below the middle of the extent drifts up by one grid step — `STDifference` cutting a 10-unit square along x = 0 reports the cut at `4.2632564145606011E-14`, one step of a 12-unit extent.

A result vertex that is one of the inputs' own vertices comes back as that vertex's original coordinates, which is what keeps untouched corners exact.
A vertex the operation computed comes back through `X·g + C·g` with the step `g = (max - min) / 2^48`, and real does not round a crossing onto the grid first: it computes the crossing in floating point on the grid and maps that value back, so `LINESTRING(0 0,10 10)` crossing `LINESTRING(0 10,10 0)` lands at `5.0000000000000178`, half a grid step off the centre.
Both segments are taken from their upper end in sweep order, and the one whose upper end is higher is followed: `P = A + t·(B - A)` with `t = ((C - A) × (D - C)) / ((B - A) × (D - C))`.
Eleven formulations were tried against 400 random oblique crossings (2026-09-28) — the exact rational point, the determinant form, fused multiply-adds, each choice of base segment and direction, `X / s` against `X·g` for the way back — and this one reproduces 312 to the last digit, where the next best reproduces 246 and the exact rational point 148; no formulation tried explains the other 88, which differ by an ulp or two (see [Divergences](#divergences-1)).
`MakeValid` and the buffers restore nothing: every vertex they write has been through the grid.

### The overlay engine

Both operands flatten into isolated points, line segments and polygon rings on the grid, and the segments are noded against each other.
A segment splits wherever a vertex lies exactly on it, and is re-routed through the grid point of every rounded crossing whose unit square it passes through — snap rounding, which leaves no two edges crossing except at a shared vertex however the rounding falls.
Real doesn't join a line ending a hair off another, and splitting only at exactly coincident vertices matches that.

Each edge of the arrangement is then labelled with whether each operand's area holds its two sides, by even-odd parity along a ray cast from the edge's midpoint, a ring running along the edge itself flipping the parity between the sides.
A ring edge an operand's rounding folds back over itself cancels out of that parity rather than counting twice.
A collection whose polygon members might overlap has them unioned into one area first, and their rings still node the arrangement, so a result ring keeps a vertex wherever a member's edge met it, as real's does.

The operation then reads its result off the labels: an edge with the result's area on exactly one side is boundary, an edge on a line component survives by the operation's rule, and a node survives as a point where only points meet.
Intersection keeps whatever lies in both operands' closures, so two squares sharing an edge intersect in that edge and two touching at a corner in that point.

### Output order

Real writes its components in **descending sweep order** of each component's lowest vertex, where lowest means least y and then least x — so `MULTIPOINT ((1 2), (2 1))` lists the higher point first, and of two polygons the one reaching lower comes last.
Inside a polygon the rings run the other way: each ring starts at its own lowest vertex, the shell counter-clockwise and each hole clockwise, and the holes are listed lowest first.
A single kind of component yields that kind or its Multi form; mixed kinds yield a `GEOMETRYCOLLECTION` of the components; nothing at all yields `GEOMETRYCOLLECTION EMPTY`.
Z and M are dropped.

A line's **direction** comes from the sweep that builds it, not from the input, and no endpoint rule describes it.
Nodes are visited from the top of the sweep order down, and each run of edges grows downward:

- At a node, the edges arriving from above pair up among themselves in the sweep order of their far ends, the edges leading down pair up likewise, and a leftover of each kind pair with each other — so at a `+` the north edge pairs with the east and the south with the west, and at a `T` the two lower edges pair while the stem ends its own run.
- A pair leading down starts a run whose tail follows the edge at the smaller angle from the +x axis; an unpaired edge leading down starts a run at its tail.
- A pair arriving from above joins two runs: a tail meeting a head concatenates them as they stand, and two like ends put the run arriving at the larger angle first.
- Every node where a line edge meets anything stays a vertex, so a point on a line becomes one of its vertices.

The rule reproduces the direction of all 122 untouched polylines in a random sweep, including the ones whose direction no endpoint or orientation rule explains, and the pairing at crossings.

Noding repeats until no two pieces cross: re-routing a piece through a crossing's grid point moves it by up to half a step, which can carry it across a vertex it passed a hair from, and a piece left crossing is labelled by one midpoint while its ends lie on different sides of the other operand — the boundary then stops closing, which surfaced as a crash on one `geography` difference of the randomized corpus.

### Envelope, hull and boundary

`STEnvelope()` is the bounding rectangle from its lower-left corner, counter-clockwise.
An axis the instance doesn't span widens by `2e-8` of the coordinate either way (`1e-8` at zero), so a point's envelope is `POLYGON ((0.99999998 1.99999996, 1.00000002 1.99999996, …))`.

`STConvexHull()` is a Graham scan on the precision grid, pivoting on the vertex reaching furthest right (then lowest) and starting the ring there; the rest sort counter-clockwise around it, only the furthest of any lining up with the pivot is kept, and a vertex lying exactly on a hull edge stays.
On the grid that last rule only bites where an edge stays exactly straight after snapping — an axis-parallel one — which is why real keeps a midpoint on a diagonal hull edge and drops one on a vertical edge through the pivot.
Two distinct vertices hull to a line from the pivot, one to a point.
All 200 hulls of a random integer point sweep match real byte for byte.

`STBoundary()` of a polygon is its rings as lines, each written the way an overlay writes that polygon's ring, all in descending order; of a line, the mod-2 endpoint set in the order the sweep writes the lines, each line's start before its end; of a point, empty.
A collection holding an area answers with the boundary of the union of its members, so a line crossing into a polygon ends where it meets the ring.

### Buffers

`STBuffer(d)` is `BufferWithTolerance(d, 0.001, 1)`, and real builds a buffer as `BufferWithCurves` does and then linearizes it:

- A point becomes four quarter arcs starting at the bottom.
- A segment contributes the band either side of it, and a vertex where a line or ring turns contributes the arc between its edges' offsets on the side that opens up.
- A line's free end gets a cap of two arcs meeting straight ahead, each stopping an angle δ short of the side offsets, with a straight chord bridging the gap: δ is π/512 while the buffer's reach from the origin is 4 to 16 distances, and doubles with every fourfold growth of that ratio.
- The curve polygon's ring starts at the outline's lowest point, and where that falls inside an arc the arc splits there: at the point nearest straight down of a lattice of δ steps from a cap's forward direction, or of 1/256 of a join's sweep from its start (fitted over 72 cap directions and 30 joins).
- Each arc splits into a power-of-two count of equal steps, the fewest whose deviation *estimate* `r·θ²/8` — the sagitta's leading term, not the sagitta — stays within the tolerance: every step-count threshold real shows sits on that estimate to eight digits (bisected 2026-09-28), so a point's buffer has 128 sides at the default tolerance whatever the distance, 8 at a tolerance of 0.1 and 256 at 0.0001.
  `relative` scales the tolerance by the distance.

A zero distance returns the instance as it stands, unnormalized; a negative one erodes an area by the same pieces and empties a point or a line.

A tolerance that isn't positive is **Msg 6522** carrying 24108, and a NULL argument to either form is **Msg 6569**.

#### `geography` buffers

Real builds a `geography` point's buffer as `BufferWithCurves` answers — a `CURVEPOLYGON` of two half circles through four control points — and linearizes it (probed 2026-09-29 against SQL Server 2025):

- The control points sit at 45°, 135°, 225° and 315° about the point on the unit sphere, reading latitude as a spherical angle, each at its own angle so it lies the distance away along the great elliptic arc; the northern pair and the southern pair differ off the equator.
  The ring starts at the north-east one and the first half ends at the south-west one.
- A **circular arc** on `geography` is a circle in a gnomonic plane, stepped at equal angles about its centre — the linearized points identify the plane to about 1e-10° on arcs 40° across.
  Points are read as the directions of their positions on the ellipsoid, and the plane touches the unit sphere where the geocentric latitude has the tangent `tan L / (1 - e²)`, `L` being the latitude of the circumcentre of the arc's three points read on the unit sphere with latitude as a spherical angle.
  A circle drawn on the unit sphere instead misses real's vertices by up to half a percent of the distance; a circle in real's plane lands on them.
- Each half takes the same power-of-two step count, the larger of two.
  One follows the tolerance — the fewest steps whose estimate `K·d·θ²` stays within it, `K` being 0.13866 at the equator and growing as `(1 - e²sin²φ)^-1.5`, and never more than 64 however small the tolerance.
  The other follows the distance alone — the fewest whose `|sin 2ρ|/2·(1 - e²sin²φ)^1.5·θ²/8` stays within 1e-6, with `ρ` the distance in radians of the major semi-axis — which is what gives a 30 km buffer 256 sides, a 100 km one 512, and a 10,000 km one, whose `ρ` sits at a right angle, only 128.
  Both were fitted to real's step-count thresholds, bisected to twelve digits, and hold them to within 3e-4 of the threshold distance.
- A single step a half leaves a ring retracing one chord, and real answers that chord as a `LINESTRING`.
- A distance past about 19,883 km is **Msg 6522** carrying 24207 (`The specified buffer distance exceeds the full globe.`), bisected to the millimetre and the same for points and lines.

A **line or polygon** buffers as the union of the pieces it sweeps, taken through one projection: a band either side of each edge whose short sides pass through the edge's own ends, an arc filling each turn on its outer side, a half-circle cap at a line's free ends, and for a polygon the polygon itself.
A band's sides follow the curve at the distance from the edge, stepped where it bows off the chord joining its ends by more than the tolerance; the arcs are real's circular arcs at a half circle's step density.
A negative distance erodes a polygon by the bands of all its rings and empties a line.
Real's own outline for these is a curve polygon whose sides it writes sometimes as a chord, sometimes as two chords through the edge's midpoint and sometimes as an arc, by a rule the probes don't pin down, and whose ring start follows its curve construction rather than any ordering pass — so a line's or polygon's buffer matches real's area, not its vertices (see [Divergences](#divergences-1)).
Several points buffer separately and union, which real does with curves instead, so a multipoint's ring is split where real's curve union restarts it and carries more vertices than real's.

### Reduce and MakeValid

`Reduce(tolerance)` is Douglas–Peucker on every line and ring, measuring a vertex's distance to the *segment* between the kept ends and keeping it only when that distance exceeds the tolerance — so a tolerance of 0 still drops an exactly collinear vertex, and a vertex beyond the end of the chord survives a tolerance a line distance would have let go.
A ring is anchored at its first vertex, a collection of one kind comes back as that kind's Multi form, and a ring that collapses below four vertices leaves the result to `MakeValid`.
A negative tolerance is **Msg 6522** carrying 24125.

`MakeValid()` returns a valid instance untouched and rebuilds an invalid one through the overlay: each polygon's area is the even-odd parity of its rings, so a bow-tie splits into its two triangles and an overlapping multipolygon keeps only what one member covers; every ring also stands as a line, so the stretch of a ring enclosing nothing survives as a line; and lines are noded where they cross or retrace.

`geography`'s `Reduce` measures a vertex's distance to the great elliptic arc in metres, and its `MakeValid` rebuilds through the same gnomonic projection as the set operations.

### ShortestLineTo and Filter

`ShortestLineTo(other)` runs from the receiver's point nearest the other instance to the other's point nearest it, and is `LINESTRING EMPTY` where the two meet and NULL for an empty operand.
Ties go to the first pair found walking the receiver's pieces and, for each, the other's; real breaks some ties the other way, apparently on the last bit of the two distances, and writes a foot on a segment with a last digit the simulator's `A + t·(B - A)` doesn't always reproduce (`3.0000000000000004` for 3).
Over 165 random pairs (2026-09-28) 118 match exactly, 43 within 1e-9 and 4 are ties real resolves differently.

`geography`'s runs between the points where the least great elliptic distance is reached.
A foot on an edge is found where the distance's slope along the edge crosses zero, not where the distance is least: a minimum is flat, so a search on the value stalls at the square root of its rounding — metres of foot — where the slope's zero is sharp.
Real writes both ends through a unit vector and back, reading latitude as a spherical angle and converting by one multiplication with `π/180` and `180/π` each way, so an input vertex comes back with its last digit disturbed: `POINT (1 1)` ends a line as `0.99999999999999978 1` (probed 2026-09-29).
`double.DegreesToRadians` multiplies by π and then divides, which rounds differently in the last place, so the conversion is spelled out.

`Filter(other)` is `STIntersects` for both types, which is Microsoft's documented contract when no spatial index serves the query.

### The spatial aggregates

`geometry::UnionAggregate(col)` and its siblings parse as aggregates — a static method name followed by one argument — and bind like a CLR parameter of the type, so a string converts and an `int` is **Msg 206**.
They skip NULL without the Msg 8153 warning, answer NULL for a group with no non-NULL row or whose rows don't share one SRID, and refuse an invalid row with Msg 6522 naming the aggregate's own class (`GeometryUnionAggregate`) rather than the type.
`UnionAggregate` folds `STUnion` over the rows in arrival order, so a lone row still comes back normalized; `geometry`'s `EnvelopeAggregate` and both `ConvexHullAggregate`s read every row's vertices at once; `CollectionAggregate` gathers the rows as members, flattening a collection row by one level.

`geography`'s `EnvelopeAggregate` is a curve polygon — `BufferWithCurves`'s two half circles through control points at 45°, 135°, 225° and 315° — about a cap merged row by row (fitted 2026-09-29 against SQL Server 2025):

- Each row's cap is its `EnvelopeCenter()` and `EnvelopeAngle()`, and the running cap merges with it into the smallest cap holding both, in arrival order; with that centre all four of real's control points sit at one great elliptic distance, which the vector mean of every row's points misses by up to a quarter of the radius.
- The distance is the cap's angle times `a²/b`, the polar radius of curvature — the ratio to `a` is 1.00336409 on every case larger than a few metres — with a floor of √3·10⁻⁹ radians, which is what a lone point answers (0.0110844 m).
- A cap reaching a hemisphere, a clockwise polygon's included, answers `FULLGLOBE`, and nothing but empty rows answers `GEOMETRYCOLLECTION EMPTY`.

43 of 66 random groups agree within 1e-9 of the coordinates and the rest within about 1e-7 relative, real's control points carrying its own great elliptic distance residual.

### Round earth

`geography`'s set operations and hull run the planar engine on a **gnomonic projection** centred on the normalized sum of the operands' directions, or, where that leaves a vertex 89.5° or more away, on the centre of the smallest cap holding them.
A `geography` edge is cut from the ellipsoid by the plane through its ends and the centre, and the gnomonic projection maps every such plane onto a straight line, so the planar crossings are the arcs' crossings: `POLYGON((0 0,10 0,10 10,0 10,0 0))` meets the square from (5 5) at latitude 10.0374 on longitude 5, where the top edge bows north of its written latitude.
Input vertices come back as written; a computed vertex unprojects to geodetic latitude.

Real writes a `geography` set operation's result in the sweep order of a plane whose primary axis is the **Earth-centred y axis** — the direction of longitude 90° on the equator — pointing along it for a result centred north of the equator and against it for one centred on or south of it (probed 2026-09-29 over small squares and diamonds across the globe).
So near the prime meridian in the north a ring starts at its westernmost, then northernmost vertex, while at longitude 90° it starts at its northernmost, beyond longitude 150° at its easternmost, and south of the equator the whole picture turns half round.
A mixed collection lists its points and lines, in that sweep's order, ahead of its polygons.
The simulator orders the finished result by re-reading it through the planar engine in the gnomonic plane at the result's centre with those axes; where two vertices a hair apart meet on that re-read's grid it falls back to the plain turned longitude / latitude frame, which maps back by arithmetic.
A hull still pivots on its southernmost, then westernmost vertex.

A polygon naming more than a hemisphere — a ring wound clockwise around a small region — is the complement of the small polygons its reversed rings enclose, and the set operation is rewritten over that complement so every projection holds only small regions: `¬X ∩ B = B − X`, `¬X ∪ B = ¬(X − B)` with `B`'s lines inside `X` alongside, `¬X − B = ¬(X ∪ B)`, `B − ¬X = B ∩ X`, `¬X △ B = ¬(X △ B)`, and with both sides complemented `¬X ∩ ¬Y = ¬(X ∪ Y)`, `¬X ∪ ¬Y = ¬(X ∩ Y)`, `¬X − ¬Y = Y − X`, `¬X △ ¬Y = X △ Y`.
A complemented result is one polygon whose rings are the result's shells reversed, beside each of its holes as a polygon of its own; the other side's lines are overlaid with the small region so they node its rings where they cross, as real's do.
Real answers the complement of nothing as `FULLGLOBE` (probed 2026-09-29), which the value model doesn't hold.

### Agreement with real

Randomly generated cases diffed against SQL Server 2025 (2026-09-28), counting an exact text match and a match within 1e-9 of the largest coordinate with the same structure:

| Corpus | Exact | Within tolerance |
| --- | --- | --- |
| 3,000 set operations over mixed points, lines, polygons, multis and collections | 88.2% | 99.7% |
| 2,000 set operations between polygons, holes and concave rings included | 90.5% | 99.8% |
| 1,500 envelopes, hulls and boundaries | 98.7% | 99.7% |
| 300 `Reduce` calls | 99.3% | 99.3% |
| 300 buffers of points, lines and polygons | 20.7% | 78.7% |
| 400 `geography` set operations | 57.0% | 90.8% |
| 400 more, held out while the ordering was fitted | 60.3% | 94.3% |
| 600 `geography` set operations reaching up to 170° across, a third over a clockwise ring | 64.8% | 66.5% |
| 200 `geography` point buffers, 1 m to 1,500 km | 0% | 90.5% |
| 120 `geography` point and multipoint buffers | 0% | 80.8% |
| 300 `geography` `ShortestLineTo` calls | 72.7% | 90.0% |
| 300 `geography` line and polygon buffers | 12.3% | 12.7% |

The `geography` rows were measured 2026-09-29.
The exact line and polygon buffers are the negative ones that erode to nothing; the rest agree with real on area — half of them within 3e-5 of real's, nine in ten within 5e-4 — and not on vertices.

Nearly every case outside tolerance falls in a class under [Divergences](#divergences-1).

## Curved shapes

`CIRCULARSTRING`, `COMPOUNDCURVE` and `CURVEPOLYGON` hold circular arcs, each through three points, beside straight segments; `Storage/Spatial/SpatialCurves.cs` walks, measures and linearizes them.
Every rule below was probed against SQL Server 2025 on 2026-09-29.

### Reading a curve as written

A `CIRCULARSTRING` of 2n + 1 points is n arcs sharing endpoints; a `COMPOUNDCURVE`'s segments are its elements' line segments and arcs; a `CURVEPOLYGON`'s rings are any of the three figure kinds.
`STNumCurves()` counts a `CIRCULARSTRING`'s arcs and a `COMPOUNDCURVE`'s or a `LINESTRING`'s segments (NULL on the other kinds), and `STCurveN(n)` returns one as a three-point `CIRCULARSTRING` or a two-point `LINESTRING` — checking its index (24151) before the instance's validity, as the ring members answer NULL for a ringless kind before it.
The point members (`STNumPoints`, `STPointN`, `STStartPoint`, `STEndPoint`), `STIsClosed`, `STDimension`, `STGeometryType`, `InstanceOf` and the ring members read the written points; a `CURVEPOLYGON`'s ring comes back as the kind it was written as.

Control points that fix no circle — collinear, or two of them coinciding — make a straight segment from the first to the third.
An arc ending where it starts is a point: `geometry` reports it invalid (and its `STLength` 0), `geography` accepts it.

### Measures

`geometry`'s `STLength()` sums `r·θ` over the arcs and `STArea()` adds each arc's segment of the circle, `r²(θ − sin θ)/2`, to the shoelace sum over the segment ends; 600 of 600 random cases agree with real within 1e-9, the rest being real's own last-digit noise — it reports a circle of radius 2 two units in the last place above 4π.
`STDistance` measures to the arcs themselves — zero where the linearization meets or contains, else the least over pairs of points, segments and arcs — which is what real answers: `√26 − 2` from `POINT(1 5)` to the circle of radius 2 about (2, 0), where a linearization would be long by its sagitta.

`geography`'s arc is real's circle in a gnomonic plane (see [`geography` buffers](#geography-buffers)).
Its measures come from two linearizations, 2,048 and 1,024 steps an arc, extrapolated to cancel the step's square (Richardson), which lands within about 5e-9 of real's length and area — the order of real's own great elliptic residual.

### Linearization

`CurveToLineWithTolerance(tolerance, relative)` cuts each arc into the fewest power-of-two equal steps whose deviation *estimate* `r·θ²/8` stays within the tolerance — the rule the buffers follow — and a relative tolerance scales by the larger side of the instance's true bounding box (arcs' bulges included).
`STCurveToLine()` is the relative tolerance 0.001, so a half circle alone is 32 steps while one sharing a collection with a far point is coarser.
Those rules reproduce 400 of 400 random step counts; the vertices agree within 1e-9 and to the last digit on 19 of 150 arcs, real's rotation arithmetic not pinned.
A tolerance that isn't positive is 24152, a NULL argument Msg 6569.
Z and M are dropped, nothing left is `GEOMETRYCOLLECTION EMPTY`, and a tolerance coarse enough to fold a ring onto itself comes back through `MakeValid`, as real's does.

On `geography` the estimate uses the arc's radius in metres on its gnomonic plane, never more than 2,048 steps, and a relative tolerance scales by the instance's `EnvelopeAngle()` as a distance along the equator: 374 of 380 step counts agree, the rest where real's own `EnvelopeAngle` of a curve — a few percent off its linearization's, by a rule the probes didn't pin — tips the scale.

### What the operations read

Every member without a curve path of its own reads a linearization of **2,048 steps an arc**, whatever the arc's size: `STPointOnSurface` answers from the first of 2,048 steps on a 1° arc and a 350° one alike.
So the predicates, set operations, hull, boundary, centroid, validity and the envelope-cap members work on that polyline, and a quarter turn landing on a step lands exactly, which keeps an axis-aligned middle control point on it — `CIRCULARSTRING(0 0, 1 1, 2 0)` meets `POINT(1 1)`, while a point on the circle between steps, `POINT(1.6 0.8)`, doesn't, on real as here.

`STEnvelope()` of a curved instance is its true bounding box with each arc's extent scaled away from zero by one part in 10¹² and padded on every side by a millionth of the box's larger side — even for a `CURVEPOLYGON` whose rings are all straight (`(-4E-06 -4E-06, 4.000004 …)` for a 4-unit square).
`Reduce` keeps every genuine arc as written and reduces only what is straight: a curve with no genuine arc left comes back as its plain kind (`CIRCULARSTRING(0 0, 1 1, 2 2)` → `LINESTRING (0 0, 2 2)`), a `COMPOUNDCURVE` of arcs alone as a `CIRCULARSTRING`, and a collection keeps its members' kinds.

`STBuffer` sweeps a `geometry` curve arc by arc — the ring between the circles the distance inside and outside each arc, bands along its straight segments and fans where the direction turns — so the outline has real's density (261 points to real's 265 for a half circle) and its area agrees to 1e-5; a `geography` curve is read at the buffer's own tolerance.
`BufferWithCurves` of a point is the curve polygon real answers — two half circles through the bottom, right, top and left for `geometry`, through the buffer's control points at 45°, 135°, 225° and 315° for `geography` — and of anything else the linearized buffer (see [Divergences](#divergences-1)).

### Agreement with real

Probed 2026-09-29 against SQL Server 2025, counting an exact text match and a match within 1e-9 of the largest coordinate:

| Corpus | Exact | Within tolerance |
| --- | --- | --- |
| 1,120 member calls over 20 `geometry` curves, collections and empties | 87.7% | 94.5% |
| 510 parse, validity and serialization cases on both types | 93.3% | 94.9% |
| 400 `CurveToLineWithTolerance` step counts | 100% | 100% |
| 600 curve lengths and areas | 16.5% | 100% |
| 380 `geography` linearization step counts | 98.4% | 98.4% |

The member calls outside tolerance are all real's curve-producing outputs — `BufferWithCurves` of a line or polygon, `STConvexHull`, `STBoundary` and the set operations, which real answers with arcs — and the buffers' point counts; the parse cases are `IsValidDetailed`'s report and `MakeValid` over an invalid curve, and the `isValid` bit.

## FULLGLOBE

`FULLGLOBE` is the whole-earth `geography` instance, with no points: version 2 of the serialization with a bodiless shape of type `0x0B`, well-known binary type 126, `STArea()` real's constant `510065621710996.44`, `STLength()` and `STNumPoints()` 0, `STIsEmpty()` false, `STDimension()` 2, `EnvelopeAngle()` 180, and `InstanceOf('Surface')` false (probed 2026-09-29).
It contains and intersects every non-empty instance and lies within only itself; it buffers, hulls and reduces to itself; its union with anything is itself, its intersection the other operand, and its difference with a single-ring polygon that polygon reversed, while anything less `FULLGLOBE` is empty.
A set operation whose result covers the globe — a square and its clockwise complement — answers it, and so does `EnvelopeAggregate` once its cap reaches a hemisphere.
37 of 37 probed cases agree.

## Where a spatial column can't go

Neither type is comparable, so both are refused in every slot that orders, groups or dedups: **Msg 249** in `ORDER BY` / `GROUP BY` (the one message in the family that names the offending clause), **Msg 421** under `DISTINCT`, **Msg 5335** as an operand of `UNION` / `INTERSECT` / `EXCEPT`, and **Msg 6210** followed by **Msg 8117** from `MAX` / `MIN`.
`COUNT` counts them, `COUNT(DISTINCT …)` doesn't.
Tabulated against the other non-comparable types in [`legacy-lob.md`](legacy-lob.md#where-the-types-cant-go).

## The property form of a spatial column

`SELECT Location.Lat FROM t` reads the property off the column, and so does the three-part `t.Location.Lat` / `q.Location.Lat` spelling through a source's own qualifier.
Nothing in the syntax separates that from an `alias.column` reference, so the parser asks the **query scope** (`ParserContext.ScopeSources`, the FROM sources of the level being parsed) whether the qualifier is a spatial column, and the answer decides:

- the qualifier is a spatial column and the whole dotted name isn't itself a column → the property reading, whatever the leaf is, so an unrecognized member is real's **Msg 6592** rather than a column failure;
- **both** bind — a source aliased like the spatial column, carrying a column named like the member — → **Msg 326** (`Multi-part identifier 'Location.Lat' is ambiguous. Both columns 'Location' and 'Location.Lat' exist.`), and where the leaf names no member at all the column reading wins silently;
- neither binds → an ordinary column reference, which reports the ordinary resolution error.

A property written with parentheses (`Location.Lat()`) routes to the method form so it reports Msg 6506, matching real, and the four-part `dbo.t.Location.Lat` stays a column reference because real refuses it (Msg 4104).
The method form (`Location.STAsText()`) has always worked everywhere, scope or no scope, since its argument list disambiguates it.

**Not modeled yet**: the property form only reaches sites where a query scope is installed — a SELECT's projection, WHERE and ORDER BY, a JOIN's ON, and a single-table UPDATE's or DELETE's WHERE.
A scope-less site (an UPDATE's SET list, where real takes `SET loc.STSrid = 3857` as a mutator, a CHECK constraint, a computed column) still reads the two-part name as a column.
A dotted name that binds neither way reports **Msg 207** where real reports **Msg 4104** for any unbindable multi-part name, which is a general column-resolution difference rather than a spatial one.

## Not modeled yet

- **`STRelate`'s matrix on `geography`** — the round-earth engine computes the nine cells but nothing reads them out, since real exposes no `STRelate` there to compare a matrix against.
  The six predicates are masks over it; a `geography`-shaped `STRelate` would need a probe oracle that doesn't exist.
- **Curve-producing results** — real answers `STConvexHull`, `STBoundary`, the set operations and `MakeValid` over a curved instance, and `BufferWithCurves` of anything but a point, with arcs of its own (`STUnion` of a `CIRCULARSTRING` and a point keeps the arc, reversed); the simulator answers the linearization, which matches real's area and extent but not its text.
- **`IsValidDetailed()` on an invalid instance** — a valid one answers real's `24400: Valid`, but the 24400-series reason an invalid one reports (`24406: Not valid because curve (1) degenerates to a point.`, `24413: … two overlapping edges …`) raises `NotSupportedException`.
- **`geography` constructive operations spanning a hemisphere** — operands that no cap narrower than 89.5° holds, other than through a single polygon larger than a hemisphere or `FULLGLOBE`, raise `NotSupportedException`.
  A buffer reaching 80° from its instance does too, and so does the complement of anything but a single-ring polygon taken from `FULLGLOBE`.
- **A spatial column's property form outside a query scope** — see [The property form of a spatial column](#the-property-form-of-a-spatial-column) for what ships and what an UPDATE's SET list, a CHECK constraint and a computed column still read as a two-part column name.
- **`STPointOnSurface` over a polygon with a hole** — real bridges the hole into the ring before clipping the ear (`POINT (6.0000000000000142 9.3333333333333357)` for a 10-unit square around a 6-unit hole); the simulator falls back to a scanline point, `POINT (5 9)`, which is on the surface but not real's (probed 2026-10-03 against SQL Server 2025).
- **`MinDbCompatibilityLevel()` of an invalid `geography` instance** is 110 on real (`LINESTRING(0 0, 1 1, 0 0)`), where the simulator answers by the serialization version alone and reads 100; an invalid `geometry` reads 100 on both (probed 2026-10-03 against SQL Server 2025).
- **GML** — `AsGml` / `STAsGML`, and the `GeomFromGml` constructors.
- **SRID-aware operations** — the SRID is tracked per value, reported, and compared between two operands (a mismatch reads NULL, as on real), but nothing transforms between reference systems and every `geography` SRID measures on WGS 84.
  Real carries a per-SRID ellipsoid, so the same polygon under SRID 104001 (the unit sphere) measures in radians squared there and in metres squared here.
- **Spatial-index query-planner integration** — the index parses cleanly but never accelerates anything.
- **`sys.spatial_reference_systems` seed data** (~390 EPSG/ESRI rows) — the SRIDs are known for validation, the WKT payload isn't carried.
- **A WKT number error's position** for a comma inside a point: real reports `POINT(1,2)` at position 9 with `,2` as the input, the simulator at 7 with `,` (probed 2026-10-05 against SQL Server 2025).
- **A spatial aggregate over an untyped `NULL`** (`geometry::UnionAggregate(null)`) — real answers NULL, the simulator raises Msg 8117.

## Divergences

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
- **Two residual classes in the round-earth predicate matrix**, over the squares measured under [Agreement with real](#agreement-with-real).
  Both are real disagreeing with itself, and both have a planar counterpart above:
  1. **A point or stretch lying exactly on a polygon boundary.** Real's round-earth boundary classification is not exact, and which way it falls depends on whether the arithmetic happens to be exact for those coordinates.
     `POINT(1 1)` on the meridian edge of `POLYGON((1 0, 3 0, 3 2, 1 2, 1 0))` reads as *within* it, while `POINT(0 2)` on the meridian edge of `POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))` correctly reads as boundary — the second sits at longitude 0, where the Cartesian arithmetic is exact.
     `POINT(2 2)` is a written **vertex** of `POLYGON((0 2, 2 2, 2 4, 0 4, 0 2))` and reads as *disjoint* from it although `STDistance` on the same pair is 0.
     The same tolerance is what makes two polygons sharing only a boundary stretch report `STOverlaps` true.
     The simulator classifies exactly; every disagreement in the 50-shape square is this class.
  2. **A `GEOMETRYCOLLECTION` containing a `POLYGON`**, the same class as the planar engine's first.
     A collection whose point member lies in a complement polygon's interior reports `STIntersects` true and `STOverlaps` false, which no consistent matrix supports; the simulator answers by the masks.
     This is the whole residue of the generic-position square.
- **A self-touching ring is valid on `geography` and not on `geometry`.**
  Real accepts one on **both** types when the second lobe nests inside the first with the opposite winding — the arrangement one WideWorldImporters border carries — and the round-earth validator splits the ring into lobes to match it, while the planar validator still rejects any ring that meets itself.
  The two disagree on such a ring until the lobe split reaches `SpatialValidator` as well.
- **The serialized `isValid` property bit is always set** — a curve that degenerates to a point included.
  Real clears it for a stored-but-invalid instance — a bowtie polygon serializes with properties `0x00` where a square gets `0x04` — and the encoder doesn't, so an invalid instance's bytes differ from real's in that one bit.
  `STIsValid()` and the Msg 24144 gate read the shape tree rather than the bit, so behavior on such an instance is right; only the byte form isn't.
  Quantified end-to-end by importing a simulator-exported WWI-Standard bacpac into the live reference and byte-comparing against the original database: **189 of 190 `Countries.Border` values byte-identical**, the single divergent row being WWI's one stored-invalid Border.
  All 5,000 sampled `Cities.Location` points byte-identical.
  Real also *validates on deserialize*: handing it a payload whose `isValid` bit claims validity for a shape that isn't raises a bare `System.FormatException` inside Msg 6522, which the decoder doesn't reproduce either.
- **The last digits of an oblique crossing.**
  Real computes a crossing in floating point on the grid with an arithmetic the probes don't pin down; the simulator's formula reproduces every axis-aligned crossing and 78% of oblique ones, and the rest differ by one or two units in the last place — nearly the whole of the "within tolerance" share in [Agreement with real](#agreement-with-real).
- **A crossing landing on a vertex.**
  Where one operand's edge passes exactly through another's vertex, real's floating-point crossing can land a few units off that vertex and keep both as separate vertices a hair apart, where the simulator's exact test finds the one vertex.
  A sliver a few grid steps wide that real keeps as a polygon is a line or nothing here for the same reason.
- **The buffers' last digits, and a cap crossing its own line.**
  A buffer's vertex layout matches real's, but its coordinates carry noise of a few units in the fourteenth or fifteenth digit that the simulator's single grid reproduces for axis-parallel outlines and not in general.
  Where a cap or join runs back over the band of another segment — a hairpin, a line doubling back — real's outline meets it at points a little way from the simulator's.
- **A collection's boundary** takes some of its ring vertices through the grid on real, where the simulator restores them.
- **`geography`'s last digits and the order of a result far across the globe.**
  Real's round-earth crossings carry noise of about 1e-13 degrees, and over results spanning tens of degrees its line directions, and the ring starts of a complemented result, follow no rule the y-axis sweep reproduces.
- **`geography` buffers of lines and polygons match real's area, not its vertices.**
  Real's outline comes from its curve construction, whose side representation and ring start weren't pinned; the simulator's sweeps the same shape through different vertices, and a multipoint's circles union as polygons where real unions curves.
  The point buffers' last digits follow real's control points, whose placement differs from an exact great elliptic distance by about 1e-8 of the distance — real's own distance residual — which past a few hundred kilometres reaches the ninth significant digit.
- **`geography` `ShortestLineTo`'s foot points** carry real's distance residual too: real's foot sits where its own slightly-off distance is least, up to about 1e-8 degrees from the exact one on a long edge.
- **Near-retrace validity.**
  A handful of lines retracing themselves to within a grid step are valid on real in a way neither the size nor the sign of the snapped offset predicts, so an operation real answers over one can raise 24144 here.
- **A curve's linearized vertices and a curve envelope's last digits.**
  Real's arc arithmetic isn't pinned: the step counts match everywhere, and the vertices and the scaled extents a curve's `STEnvelope` pads carry real's rounding a few units in the last place differently.
- **`ReorientObject` of a curve polygon** reverses each ring as written, where real also restarts an arc ring at a different arc — no rule was found over three probes — and real regroups a polygon's rings into separate polygons, which neither curved nor plain rings reproduce here.
- **`geography` curves' `EnvelopeAngle` and `EnvelopeAggregate`** read the linearization's bounding cap, where real's for a curve differs by a few percent — which is also what tips the relative `CurveToLineWithTolerance` step counts that disagree.
  `EnvelopeAggregate`'s control points carry real's great elliptic distance residual, about 1e-7 relative.
- **Msg 6522 omits the .NET stack-frame block** — see [The Msg 6522 wrapper](#the-msg-6522-wrapper).
- **The in-process reader surfaces a spatial column as its WKT** (`SqlType.ClrType` is `string`), where real SqlClient hands back the UDT bytes (or a `SqlGeography` when `Microsoft.SqlServer.Types` is loaded).
  The TDS path is faithful — it writes the serialization.
