using System.Buffers.Binary;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Fabricates and reads the 16-byte text pointer that <see cref="TextPointer"/>
/// emits, <see cref="TextValid"/> checks, and the <c>READTEXT</c> /
/// <c>WRITETEXT</c> / <c>UPDATETEXT</c> statements resolve back to a row.
/// </summary>
/// <remarks>
/// Real SQL Server's pointer is an opaque handle into the LOB allocation
/// structure naming a specific column and row. The simulator has no such
/// structure, so it derives the 16 bytes from what identifies the cell it was
/// read from: a 4-byte signature marking it as a simulator pointer, a 4-byte
/// FNV-1a-32 hash of the case-folded column name, and an 8-byte FNV-1a-64 hash
/// of the cell's own value. The encoding is deterministic — reading
/// <c>TEXTPTR</c> twice off an unchanged cell yields the same bytes, as on real
/// — and the value half is what tells two rows of one column apart.
/// <para>
/// A write through a pointer changes the value its bytes were derived from, so
/// the resolution keeps a per-table cache from (column, value hash) to the row
/// address it settled on: the chunked idiom, where one pointer drives a
/// <c>WRITETEXT</c> and then a run of appending <c>UPDATETEXT</c>s, resolves
/// through the cache after the first use. Two rows of one column holding the
/// same value share a pointer and resolve to the first of them — real tells
/// them apart, and that is the encoding's one divergence.
/// </para>
/// </remarks>
internal static class LegacyTextPointer
{
    /// <summary>The pointer width real declares: <c>binary(16)</c>.</summary>
    public const int Width = 16;

    private static ReadOnlySpan<byte> Signature => "SSTP"u8;

    private const ulong Fnv64Offset = 14695981039346656037;
    private const ulong Fnv64Prime = 1099511628211;

    public static uint ColumnHash(string columnName)
    {
        var h = 2166136261u;
        foreach (var ch in columnName)
        {
            var u = char.ToUpperInvariant(ch);
            h = (h ^ (byte)u) * 16777619u;
            h = (h ^ (byte)(u >> 8)) * 16777619u;
        }
        return h;
    }

    /// <summary>
    /// The value half of the pointer: FNV-1a-64 over the cell's own bytes for
    /// a binary column and over its UTF-16 code units for a character one.
    /// </summary>
    public static ulong ValueHash(SqlValue value)
    {
        var h = Fnv64Offset;
        if (value.IsNull)
            return h;
        if (SqlType.IsStringCategory(value.Type))
        {
            foreach (var ch in value.AsString)
            {
                h = (h ^ (byte)ch) * Fnv64Prime;
                h = (h ^ (byte)(ch >> 8)) * Fnv64Prime;
            }
            return h;
        }

        foreach (var b in value.AsBytes)
            h = (h ^ b) * Fnv64Prime;
        return h;
    }

    public static byte[] Fabricate(string columnName, SqlValue value)
    {
        var bytes = new byte[Width];
        Signature.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), ColumnHash(columnName));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), ValueHash(value));
        return bytes;
    }

    /// <summary>
    /// Reads a candidate pointer's two identity halves, or answers
    /// <see langword="false"/> when the bytes carry no simulator signature —
    /// the arbitrary-bytes case both <c>TEXTVALID</c> and the statements refuse.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> pointer, out uint columnHash, out ulong valueHash)
    {
        (columnHash, valueHash) = (0, 0);
        if (pointer.Length < Width || !pointer[..4].SequenceEqual(Signature))
            return false;
        columnHash = BinaryPrimitives.ReadUInt32LittleEndian(pointer[4..]);
        valueHash = BinaryPrimitives.ReadUInt64LittleEndian(pointer[8..]);
        return true;
    }

    public static bool Matches(ReadOnlySpan<byte> pointer, string columnName) =>
        TryRead(pointer, out var columnHash, out _) && columnHash == ColumnHash(columnName);
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
            ? SqlValue.Null(VarbinarySqlType.Get(LegacyTextPointer.Width))
            : SqlValue.FromVarbinary(VarbinarySqlType.Get(LegacyTextPointer.Width), LegacyTextPointer.Fabricate(this.columnName, value));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var operandType = this.column.GetSqlType(batch, resolveColumnType);
        return operandType is TextSqlType or NTextSqlType or ImageSqlType
            ? VarbinarySqlType.Get(LegacyTextPointer.Width)
            : throw SimulatedSqlException.InvalidArgumentDataType(operandType.SqlServerName, argumentIndex: 1, "textptr");
    }

    internal override string DebugDisplay() => $"TEXTPTR({this.column.DebugDisplay()})";

    internal override void VisitColumnReferencesCore(ColumnReferenceVisitor visit) => this.column.VisitColumnReferences(visit);
}
