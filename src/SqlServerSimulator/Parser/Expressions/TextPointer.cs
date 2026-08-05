using System.Buffers.Binary;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Fabricates and validates the 16-byte in-row text pointer that
/// <see cref="TextPointer"/> emits and <see cref="TextValid"/> checks.
/// </summary>
/// <remarks>
/// Real SQL Server's text pointer is an opaque 16-byte handle into the LOB
/// allocation structure that encodes the specific column and row; the only
/// sanctioned consumers are the deprecated <c>READTEXT</c> / <c>WRITETEXT</c> /
/// <c>UPDATETEXT</c> statements, which the simulator does not model. The
/// simulator therefore fabricates a shape that carries just enough identity for
/// <c>TEXTVALID</c> to accept a pointer against the column it came from and
/// reject it against any other column or against arbitrary bytes: an 8-byte
/// signature marks it as a simulator pointer, and an 8-byte FNV-1a-64 hash of
/// the case-folded column name captures column identity. It intentionally does
/// <em>not</em> encode row identity, so two non-NULL cells of one column share
/// a pointer (real varies per row) — a divergence with no observable consumer
/// while <c>READTEXT</c> and friends stay unmodeled.
/// </remarks>
internal static class LegacyTextPointer
{
    private static ReadOnlySpan<byte> Signature => "SSSTXTPR"u8;

    private static ulong ColumnHash(string columnName)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var h = offset;
        foreach (var ch in columnName)
        {
            var u = char.ToUpperInvariant(ch);
            h = (h ^ (byte)u) * prime;
            h = (h ^ (byte)(u >> 8)) * prime;
        }
        return h;
    }

    public static byte[] Fabricate(string columnName)
    {
        var bytes = new byte[16];
        Signature.CopyTo(bytes);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), ColumnHash(columnName));
        return bytes;
    }

    public static bool Matches(ReadOnlySpan<byte> pointer, string columnName) =>
        pointer.Length == 16
        && pointer[..8].SequenceEqual(Signature)
        && BinaryPrimitives.ReadUInt64LittleEndian(pointer[8..]) == ColumnHash(columnName);
}

/// <summary>
/// SQL <c>TEXTPTR(column)</c>: returns the 16-byte <c>varbinary</c> text
/// pointer of a <c>text</c> / <c>ntext</c> / <c>image</c> base-table column,
/// or NULL when the cell is NULL. The argument must be a base-table column
/// reference — a literal, CAST, or computed expression raises Msg 280, and a
/// column of any other type raises Msg 8116 (both probe-confirmed against SQL
/// Server 2025). Reference:
/// https://learn.microsoft.com/en-us/sql/t-sql/functions/textptr-transact-sql
/// </summary>
internal sealed class TextPointer : Expression
{
    private readonly Reference column;
    private readonly string columnName;

    public TextPointer(ParserContext context)
    {
        if (Parse(context) is not Reference reference)
            throw SimulatedSqlException.OnlyBaseTableColumnsInTextPtr();
        this.column = reference;
        this.columnName = reference.ReferencedName.Leaf;
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.column.Run(runtime);
        return value.IsNull
            ? SqlValue.Null(VarbinarySqlType.Get(16))
            : SqlValue.FromVarbinary(VarbinarySqlType.Get(16), LegacyTextPointer.Fabricate(this.columnName));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var operandType = this.column.GetSqlType(batch, resolveColumnType);
        return operandType is TextSqlType or NTextSqlType or ImageSqlType
            ? VarbinarySqlType.Get(16)
            : throw SimulatedSqlException.InvalidArgumentDataType(operandType.SqlServerName, argumentIndex: 1, "textptr");
    }

    internal override string DebugDisplay() => $"TEXTPTR({this.column.DebugDisplay()})";

    internal override void VisitColumnReferencesCore(ColumnReferenceVisitor visit) => this.column.VisitColumnReferences(visit);
}
