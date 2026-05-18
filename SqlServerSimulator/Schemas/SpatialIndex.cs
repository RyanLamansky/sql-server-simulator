namespace SqlServerSimulator.Schemas;

/// <summary>
/// One spatial index attached to a heap table. Created via
/// <c>CREATE SPATIAL INDEX name ON table(col) [USING GEOMETRY_GRID |
/// GEOGRAPHY_GRID | GEOMETRY_AUTO_GRID | GEOGRAPHY_AUTO_GRID]
/// [WITH (BOUNDING_BOX = (xmin, ymin, xmax, ymax) | GRIDS = (...) |
/// CELLS_PER_OBJECT = n | ...)]</c>. The simulator never accelerates
/// spatial queries through these — they're catalog-view metadata only.
/// Surfaced by <c>sys.spatial_indexes</c> (per-index row in the regular
/// sys.indexes shape with 4 spatial-specific extras) and
/// <c>sys.spatial_index_tessellations</c> (per-index bounding-box + grid
/// levels). Each instance carries the captured DDL options verbatim so the
/// two catalog views can project them without re-parsing.
/// </summary>
internal sealed class SpatialIndex(
    int objectId,
    string name,
    int indexId,
    int columnOrdinal,
    SpatialIndexKind kind,
    string tessellationScheme,
    double? boundingBoxXmin,
    double? boundingBoxYmin,
    double? boundingBoxXmax,
    double? boundingBoxYmax,
    short? level1Grid,
    short? level2Grid,
    short? level3Grid,
    short? level4Grid,
    int? cellsPerObject)
{
    public readonly int ObjectId = objectId;
    public readonly string Name = name;
    public readonly int IndexId = indexId;

    /// <summary>1-based column ordinal (matches real SQL Server's <c>column_id</c>).</summary>
    public readonly int ColumnOrdinal = columnOrdinal;

    /// <summary>
    /// Discriminator that picks geography-vs-geometry tessellation. Real SQL
    /// Server reports this as <c>spatial_index_type</c> (3 / 4) +
    /// <c>spatial_index_type_desc</c> ('GEOMETRY' / 'GEOGRAPHY').
    /// </summary>
    public readonly SpatialIndexKind Kind = kind;

    /// <summary>
    /// <c>tessellation_scheme</c> — one of <c>GEOMETRY_GRID</c> /
    /// <c>GEOGRAPHY_GRID</c> / <c>GEOMETRY_AUTO_GRID</c> /
    /// <c>GEOGRAPHY_AUTO_GRID</c>. Defaults to the *_AUTO_GRID variant when
    /// no USING clause is provided, matching real SQL Server's probe-confirmed
    /// behavior (2026-05-15).
    /// </summary>
    public readonly string TessellationScheme = tessellationScheme;

    public readonly double? BoundingBoxXmin = boundingBoxXmin;
    public readonly double? BoundingBoxYmin = boundingBoxYmin;
    public readonly double? BoundingBoxXmax = boundingBoxXmax;
    public readonly double? BoundingBoxYmax = boundingBoxYmax;

    public readonly short? Level1Grid = level1Grid;
    public readonly short? Level2Grid = level2Grid;
    public readonly short? Level3Grid = level3Grid;
    public readonly short? Level4Grid = level4Grid;

    public readonly int? CellsPerObject = cellsPerObject;
}

/// <summary>
/// Discriminator for <see cref="SpatialIndex.Kind"/>; drives the
/// <c>spatial_index_type</c> (3 / 4) and <c>spatial_index_type_desc</c>
/// ('GEOMETRY' / 'GEOGRAPHY') columns of <c>sys.spatial_indexes</c>.
/// Probe-confirmed against SQL Server 2025 (2026-05-15) — the int codes
/// aren't documented but show up consistently for indexed geometry and
/// geography columns respectively.
/// </summary>
internal enum SpatialIndexKind
{
    Geometry = 3,
    Geography = 4,
}
