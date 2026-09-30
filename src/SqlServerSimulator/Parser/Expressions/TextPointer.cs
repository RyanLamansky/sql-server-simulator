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
/// structure naming a specific column and row. The simulator names the same
/// two things directly: the table's object id, a 4-byte FNV-1a-32 hash of the
/// case-folded column name, and the row's stable heap address — page, then
/// slot. So, as on real, two rows holding the same value get distinct
/// pointers, an ordinary <c>UPDATE</c> (even one moving the row's key) leaves a
/// pointer read before it valid and reading the new value, and a deleted row's
/// pointer stops resolving (Msg 7123), all probed 2026-09-30 against SQL
/// Server 2025. The row's address reaches <c>TEXTPTR</c> through a
/// <see cref="RowLocator"/>.
/// </remarks>
internal static class LegacyTextPointer
{
    /// <summary>The pointer width real declares: <c>binary(16)</c>.</summary>
    public const int Width = 16;

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
    /// <c>TEXTPTR</c> of column <paramref name="columnIndex"/> of the row at
    /// <paramref name="address"/>, whose value is <paramref name="cell"/>: NULL
    /// for a cell that has never held a value, and a pointer otherwise — a cell
    /// a write set NULL keeps its LOB root on real, and with it a pointer
    /// (<see cref="Heap.RootedNullLobCells"/>).
    /// </summary>
    public static SqlValue For(HeapTable table, int columnIndex, (int Page, int Slot) address, SqlValue cell) =>
        !IsRooted(table, columnIndex, address, cell)
            ? SqlValue.Null(VarbinarySqlType.Get(Width))
            : SqlValue.FromVarbinary(VarbinarySqlType.Get(Width), Fabricate(table, columnIndex, address));

    /// <summary>Whether the cell holds a LOB root: a value, or a NULL a write left one behind.</summary>
    public static bool IsRooted(HeapTable table, int columnIndex, (int Page, int Slot) address, SqlValue cell) =>
        !cell.IsNull || table.Heap.IsRootedNullLob(address, table.StorageOrdinals[columnIndex]);

    private static byte[] Fabricate(HeapTable table, int columnIndex, (int Page, int Slot) address)
    {
        var bytes = new byte[Width];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, table.ObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), ColumnHash(table.Columns[columnIndex].Name));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), address.Page);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), address.Slot);
        return bytes;
    }

    /// <summary>
    /// The row a pointer names in <paramref name="table"/>'s column
    /// <paramref name="columnIndex"/>, when it names a live row there whose
    /// cell holds a LOB root; false for bytes naming another table or column, a
    /// deleted row, or anything else — the cases real refuses with Msg 7123
    /// and <c>TEXTVALID</c> answers 0 to.
    /// </summary>
    public static bool TryResolve(ReadOnlySpan<byte> pointer, HeapTable table, int columnIndex, out (int Page, int Slot) address)
    {
        address = default;
        if (pointer.Length < Width
            || BinaryPrimitives.ReadInt32LittleEndian(pointer) != table.ObjectId
            || BinaryPrimitives.ReadUInt32LittleEndian(pointer[4..]) != ColumnHash(table.Columns[columnIndex].Name))
        {
            return false;
        }
        address = (BinaryPrimitives.ReadInt32LittleEndian(pointer[8..]), BinaryPrimitives.ReadInt32LittleEndian(pointer[12..]));
        var heap = table.Heap;
        if (address.Page < 0 || address.Page >= heap.Pages.Count || address.Slot < 0
            || heap.IsSlotTombstoned(address.Page, address.Slot)
            || heap.ReadSlotBytes(address.Page, address.Slot) is not { } row)
        {
            return false;
        }
        var cell = RowDecoder.DecodeColumn(table.StoredColumns, row, table.StorageOrdinals[columnIndex], heap);
        return IsRooted(table, columnIndex, address, cell);
    }
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

    /// <summary>The <see cref="RowLocator"/> name the pointer resolves as.</summary>
    private readonly MultiPartName pointerName;

    public TextPointer(ParserContext context)
    {
        if (Parse(context) is not Reference reference)
            throw SimulatedSqlException.OnlyBaseTableColumnsInTextPtr();
        this.column = reference;
        this.pointerName = RowLocator.TextPointerName(reference.ReferencedName);
        context.ReadsRowLocators = true;
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime) => runtime.ResolveColumn(this.pointerName);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var operandType = this.column.GetSqlType(batch, resolveColumnType);
        return operandType is TextSqlType or NTextSqlType or ImageSqlType
            ? VarbinarySqlType.Get(LegacyTextPointer.Width)
            : throw SimulatedSqlException.InvalidArgumentDataType(operandType.SqlServerName, argumentIndex: 1, "textptr");
    }

    internal override string DebugDisplay() => $"TEXTPTR({this.column.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.column);
}
