using SqlServerSimulator.Schemas;

namespace SqlServerSimulator.Storage;

/// <summary>
/// A CLR user-defined type (<c>CREATE TYPE … EXTERNAL NAME</c>): one instance
/// per registered type, so reference equality is type identity as it is for
/// every other <see cref="SqlType"/>. A value is the type's serialized bytes —
/// what <c>CAST(x AS varbinary)</c> and <c>DATALENGTH</c> read — and every
/// member access deserializes them into the registered class.
/// </summary>
/// <remarks>
/// It shares <c>hierarchyid</c>'s row and column of the type-pair grids, which
/// is what real showed for a user type (probed 2026-09-28 against SQL Server
/// 2025): a string converts to it implicitly, a binary only explicitly, and
/// arithmetic is Msg 403. Two different CLR types, or one against
/// <c>hierarchyid</c>, clash (Msg 206), and a type not marked
/// <c>IsByteOrdered</c> can't be compared, sorted, grouped or keyed at all —
/// the gates <see cref="SqlType.IsIncomparable"/> drives.
/// </remarks>
internal sealed class ClrUdtSqlType(ClrUserDefinedType udt) : SqlType(SqlTypeCategory.Other, TypePairClass.HierarchyId)
{
    public readonly ClrUserDefinedType Udt = udt;

    /// <summary>A user-defined type outranks every system type.</summary>
    public override int Precedence => 31;

    public override Type ClrType => typeof(byte[]);

    /// <summary>
    /// Stored as a variable-length value whatever the format — a
    /// <c>Format.Native</c> value's bytes are the same width every time, but
    /// the catalog's <c>is_fixed_length</c> is the only place that shows.
    /// </summary>
    public override bool IsFixedLength => false;

    public override int GetVariableByteCount(SqlValue value) => value.AsClrUdtBytes.Length;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        var bytes = value.AsClrUdtBytes;
        bytes.CopyTo(destination);
        return bytes.Length;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromClrUdt(this, source.ToArray());

    /// <summary>
    /// A parameter carries the serialized bytes, or text for the type's
    /// <c>Parse</c>.
    /// </summary>
    public override SqlValue ConvertParameter(object raw) => raw switch
    {
        byte[] bytes => SqlValue.FromClrUdt(this, bytes),
        string text => this.Udt.ParseText(text),
        _ => throw new NotSupportedException($"No conversion from {raw.GetType()} to {this.Udt.Name}."),
    };

    /// <summary>The type's own name, as real's operator and conversion messages spell it.</summary>
    public override string ToString() => this.Udt.Name;
}
