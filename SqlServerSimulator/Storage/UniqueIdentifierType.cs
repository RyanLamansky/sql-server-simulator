namespace SqlServerSimulator.Storage;

/// <summary>
/// SQL Server's <c>uniqueidentifier</c>: 16-byte fixed-length GUID. The on-disk
/// byte layout — first three groups stored little-endian, last group as raw
/// bytes — is exactly what <see cref="Guid.TryWriteBytes(Span{byte})"/> emits
/// and <see cref="Guid(ReadOnlySpan{byte})"/> consumes, so the encoder/decoder
/// can hand the span straight to the BCL.
/// </summary>
/// <remarks>
/// Comparison and ordering use SQL Server's own byte permutation — last 6
/// bytes most significant, then bytes 8-9, 6-7, 4-5, 0-3, with the first byte
/// in each group being the most significant within the group. Routing through
/// <see cref="System.Data.SqlTypes.SqlGuid"/> keeps that quirk in BCL code
/// rather than reimplementing it; the natural .NET <see cref="Guid.CompareTo(Guid)"/>
/// uses a different (and incompatible) order.
/// </remarks>
internal sealed class UniqueIdentifierSqlType : SqlType
{
    public override bool IsFixedLength => true;

    public override int FixedLength => 16;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        _ = value.AsGuid.TryWriteBytes(destination);
        return 16;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromGuid(new Guid(source));

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromGuid((Guid)raw);

    public override string ToString() => "uniqueidentifier";
}
