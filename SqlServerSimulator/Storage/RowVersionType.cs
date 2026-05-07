using System.Buffers.Binary;

namespace SqlServerSimulator.Storage;

/// <summary>
/// SQL Server's <c>rowversion</c> (legacy synonym <c>timestamp</c>): 8-byte
/// fixed-length big-endian unsigned counter, automatically generated and
/// bumped by the database on every INSERT / UPDATE that touches a row in a
/// table carrying this column. The value is implicitly <c>NOT NULL</c>,
/// can't be supplied explicitly (Msg 273 on INSERT, Msg 272 on UPDATE),
/// and a table may declare at most one (Msg 2738). Used by EF Core's
/// <c>[Timestamp]</c> attribute for optimistic concurrency.
/// </summary>
/// <remarks>
/// Wire format is 8 bytes big-endian, but in-memory storage is the raw
/// <see cref="long"/> counter held in <see cref="SqlValue"/>'s primitive
/// slot — no per-row <c>byte[]</c> allocation in the encode / decode hot
/// path; the bytes only materialize when a caller actually needs them
/// (CAST to <c>varbinary</c>, <c>ToObject</c> at the wire boundary,
/// debug-display rendering). The shared counter behind the values is
/// <see cref="Simulation.AllocateRowVersion"/>: monotonic per-simulation,
/// mirroring SQL Server's database-scoped <c>@@DBTS</c>.
/// </remarks>
internal sealed class RowVersionSqlType() : SqlType(SqlTypeCategory.Other)
{
    public override bool IsFixedLength => true;

    public override int FixedLength => 8;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        BinaryPrimitives.WriteInt64BigEndian(destination, value.RowVersionCounter);
        return 8;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source) =>
        SqlValue.FromRowVersion(BinaryPrimitives.ReadInt64BigEndian(source));

    public override SqlValue ConvertParameter(object raw) =>
        SqlValue.FromRowVersion(BinaryPrimitives.ReadInt64BigEndian((byte[])raw));

    /// <summary>
    /// SQL Server reports the type as <c>timestamp</c> in
    /// <c>information_schema.columns</c> and in SqlClient's
    /// <c>DataTypeName</c>, even when declared with the modern
    /// <c>rowversion</c> keyword. The simulator matches that wire-name
    /// fidelity here.
    /// </summary>
    public override string ToString() => "timestamp";
}
