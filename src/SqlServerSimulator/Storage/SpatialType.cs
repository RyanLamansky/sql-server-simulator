using SqlServerSimulator.Storage.Spatial;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Shared base for <see cref="GeographySqlType"/> + <see cref="GeometrySqlType"/>.
/// A value of either stores SQL Server's spatial UDT serialization — the same
/// bytes real writes — produced from the parsed instance by
/// <see cref="SpatialBinaryCodec"/>. <see cref="IsLob"/> is true so the row
/// encoder routes the payload through the off-row LOB chain, matching
/// <c>nvarchar(MAX)</c>.
/// </summary>
/// <remarks>
/// The in-memory representation is the parsed <see cref="SpatialGeometry"/>,
/// not the bytes: instance methods read the shape tree directly, and the byte
/// form is materialized only at the storage, <c>varbinary</c>-cast and wire
/// boundaries. <see cref="SqlType.ClrType"/> stays <see cref="string"/>
/// because the in-process reader surfaces a spatial column as its WKT.
/// </remarks>
internal abstract class SpatialSqlType() : SqlType(SqlTypeCategory.String)
{
    public override Type ClrType => typeof(string);

    public override bool IsFixedLength => false;

    /// <summary>True for <c>geography</c> — selects the round-earth axis order and the latitude-domain check.</summary>
    public abstract bool IsGeography { get; }

    /// <summary>
    /// The CLR type name real reports in its member-not-found errors
    /// (Msg 6592 / 6506), which name the client-side spatial classes.
    /// </summary>
    public string ClrTypeName => this.IsGeography
        ? "Microsoft.SqlServer.Types.SqlGeography"
        : "Microsoft.SqlServer.Types.SqlGeometry";

    /// <summary>
    /// True — spatial values store off-row in a LOB chain. Routes column
    /// declaration through the LOB branch so <see cref="SqlType.MaxLengthSentinel"/>
    /// applies, matching the real-server max_length=-1 reporting in sys.columns.
    /// </summary>
    public override bool IsLob => true;

    public override int GetVariableByteCount(SqlValue value) => value.AsSpatial.Encoded(this.IsGeography).Length;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        var bytes = value.AsSpatial.Encoded(this.IsGeography);
        bytes.CopyTo(destination);
        return bytes.Length;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source) =>
        SqlValue.FromSpatial(SpatialBinaryCodec.Decode(source, this.IsGeography), this.IsGeography);

    /// <summary>
    /// Accepts either the WKT text or the UDT bytes: an ADO.NET parameter can
    /// arrive as a string from in-process code or as the serialization from a
    /// wire client.
    /// </summary>
    public override SqlValue ConvertParameter(object raw) => SqlValue.FromSpatial(
        raw switch
        {
            byte[] bytes => SpatialBinaryCodec.Decode(bytes, this.IsGeography),
            string text => SpatialWktReader.Read(text, SpatialGeometry.DefaultSridFor(this.IsGeography), this.IsGeography),
            _ => throw new ArgumentException($"Cannot convert {raw.GetType()} to {this.SqlServerName}.", nameof(raw)),
        },
        this.IsGeography);
}

/// <summary>
/// SQL Server's <c>geography</c> type — round-earth spatial values bound to a
/// Spatial Reference Identifier (SRID, default 4326). Identity is
/// <c>system_type_id = 240</c>, <c>user_type_id = 130</c>.
/// </summary>
/// <remarks>
/// Coordinates are held in WKT order (longitude, latitude) like every other
/// spatial value; the reversed (latitude, longitude) order is a property of
/// the binary serialization alone, applied by
/// <see cref="SpatialBinaryCodec"/>.
/// </remarks>
internal sealed class GeographySqlType() : SpatialSqlType
{
    public override string SqlServerName => "geography";

    public override bool IsGeography => true;

    public override string ToString() => "geography";
}

/// <summary>
/// SQL Server's <c>geometry</c> type — flat-earth spatial values. Identity is
/// <c>system_type_id = 240</c>, <c>user_type_id = 129</c>. See
/// <see cref="GeographySqlType"/> for the shared representation.
/// </summary>
internal sealed class GeometrySqlType() : SpatialSqlType
{
    public override string SqlServerName => "geometry";

    public override bool IsGeography => false;

    public override string ToString() => "geometry";
}
