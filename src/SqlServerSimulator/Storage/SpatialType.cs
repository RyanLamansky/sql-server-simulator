using System.Text;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Shared base for <see cref="GeographySqlType"/> + <see cref="GeometrySqlType"/>.
/// Both store the constructed WKT (Well-Known Text) form of the spatial value
/// as raw UTF-16 LE — the simulator's degraded-mode encoding for the
/// skip-with-diagnostic bacpac stance. <see cref="IsLob"/> is true so the
/// row encoder routes payload through the off-row LOB chain, matching
/// <c>nvarchar(MAX)</c>.
/// </summary>
/// <remarks>
/// Real SQL Server stores spatial values as a binary CLR-UDT internal
/// (WKB-derived). The simulator keeps the WKT instead so <c>.ToString()</c>
/// works without an OGC engine and CAST to varchar/varbinary round-trips
/// reversibly. <see cref="HierarchyIdSqlType"/> documents the same
/// "byte form is simulator-specific; replaced when the BACPAC loader bundle
/// implements wire-format encoding" deferral.
/// </remarks>
internal abstract class SpatialSqlType() : SqlType(SqlTypeCategory.String)
{
    public override Type ClrType => typeof(string);

    public override bool IsFixedLength => false;

    /// <summary>
    /// True — spatial values store off-row in a LOB chain. Routes column
    /// declaration through the LOB branch so <see cref="SqlType.MaxLengthSentinel"/>
    /// applies, matching the real-server max_length=-1 reporting in sys.columns.
    /// </summary>
    public override bool IsLob => true;

    public override int GetVariableByteCount(SqlValue value) => Encoding.Unicode.GetByteCount(value.AsString);

    public override int Encode(SqlValue value, Span<byte> destination) => Encoding.Unicode.GetBytes(value.AsString, destination);
}

/// <summary>
/// SQL Server's <c>geography</c> type — round-earth spatial values bound to
/// a Spatial Reference Identifier (SRID, default 4326). The simulator ships
/// the type identity (<c>system_type_id = 240</c>, <c>user_type_id = 130</c>),
/// raw-WKT round-trip, and the parse-cleanly + throw-at-execute method-call
/// surface. OGC + Microsoft-extension instance methods raise
/// <see cref="NotSupportedException"/> at <c>Run</c> except <c>.ToString()</c>,
/// which returns the stored WKT.
/// </summary>
/// <remarks>
/// Construction via <c>geography::Parse(wkt)</c> /
/// <c>geography::STGeomFromText(wkt, srid)</c> works at parse + execute;
/// the result is a <c>geography</c>-typed value carrying the WKT string.
/// Real OGC predicates (<c>.STDistance</c>, <c>.STIntersects</c>, etc.) parse
/// cleanly so CREATE VIEW / CREATE PROCEDURE bodies that reference them store
/// verbatim, matching the skip-with-diagnostic stance documented in
/// <c>docs/claude/spatial.md</c>.
/// </remarks>
internal sealed class GeographySqlType() : SpatialSqlType
{
    public override string SqlServerName => "geography";

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromGeography(Encoding.Unicode.GetString(source));

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromGeography((string)raw);

    public override string ToString() => "geography";
}

/// <summary>
/// SQL Server's <c>geometry</c> type — flat-Earth spatial values. Same
/// implementation strategy as <see cref="GeographySqlType"/>; identity is
/// <c>system_type_id = 240</c>, <c>user_type_id = 129</c>. See
/// <see cref="GeographySqlType"/> for the method-call dispatch contract.
/// </summary>
internal sealed class GeometrySqlType() : SpatialSqlType
{
    public override string SqlServerName => "geometry";

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromGeometry(Encoding.Unicode.GetString(source));

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromGeometry((string)raw);

    public override string ToString() => "geometry";
}
