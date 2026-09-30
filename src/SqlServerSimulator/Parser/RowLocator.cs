using System.Globalization;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Reads the base row a FROM source's current row came from — its stable
/// <c>(page, slot)</c> heap address — from inside an expression, which only
/// ever sees a row through its column resolver.
/// </summary>
/// <remarks>
/// <para>
/// A locator is asked for by name: a column reference whose leaf carries a
/// marker character no identifier can spell. <see cref="TextPointerMarker"/>
/// in front of a column's leaf asks for that column's <c>TEXTPTR</c>, which
/// needs the row's address and the cell together;
/// <see cref="AddressMarker"/> followed by a source index asks for the
/// address alone, packed into a <c>bigint</c>. The name travels through the
/// ordinary resolver chain, so a locator correlates to an enclosing query's
/// source exactly as a column does, and the per-enumeration
/// <see cref="SourceColumnMemo"/> binds it once, on its miss path, as
/// <c>(-2 - source, column)</c>. The tuple resolver already branches on a
/// negative source for the outer-scope fallthrough, so an ordinary column
/// resolution pays nothing for the locator's existence.
/// </para>
/// <para>
/// The address itself comes from <see cref="RowAddressMap"/>: a heap source
/// hands out a fresh <c>byte[]</c> per row, and every producer of those
/// arrays (the scan, the clustered-order walk, the index-seek and snapshot
/// materializations) records the array it yields against the address it read
/// it from, but only while the executing statement has installed a map —
/// which a plan whose query reads a locator does as it starts, and a consumer
/// that wants the addresses of a body's rows does around its run. With no map
/// installed each producer tests one hoisted local per row.
/// </para>
/// <para>
/// Because the locator is a value, it rides through every stage that carries
/// projected values — <c>TOP</c>, a sort, a window function — which is what
/// lets a write through a row-limited or windowed body (see
/// <see cref="Selection.ExecuteWithRowAddresses"/>) find the base row behind
/// each row the body yields.
/// </para>
/// </remarks>
internal static class RowLocator
{
    public const char TextPointerMarker = '\u001E';
    public const char AddressMarker = '\u001F';

    /// <summary>The name <c>TEXTPTR(<paramref name="column"/>)</c> resolves.</summary>
    public static MultiPartName TextPointerName(MultiPartName column) =>
        column.WithLeaf(TextPointerMarker + column.Leaf);

    /// <summary>The name the address of FROM source <paramref name="sourceIndex"/>'s current row resolves as.</summary>
    public static MultiPartName AddressName(int sourceIndex) =>
        new(AddressMarker + sourceIndex.ToString(CultureInfo.InvariantCulture));

    public static bool IsLocatorName(MultiPartName name) =>
        name.Leaf is [TextPointerMarker or AddressMarker, ..];

    /// <summary>
    /// Binds a locator name across <paramref name="sources"/>: <c>(-2 - s, c)</c>
    /// for a text pointer of source <c>s</c>'s column <c>c</c>, <c>(-2 - s, -1)</c>
    /// for source <c>s</c>'s address, or <c>(-1, -1)</c> when the column is
    /// the enclosing scope's.
    /// </summary>
    public static (int SourceIndex, int ColumnIndex) Find(FromSource[] sources, MultiPartName name)
    {
        var leaf = name.Leaf;
        if (leaf[0] == AddressMarker)
        {
            var index = int.Parse(leaf.AsSpan(1), CultureInfo.InvariantCulture);
            return index < sources.Length ? (-2 - index, -1) : (-1, -1);
        }
        var (s, c) = Selection.FindSourceColumn(sources, name.WithLeaf(leaf[1..]));
        return s < 0 ? (-1, -1) : (-2 - s, c);
    }

    /// <summary>
    /// The value a locator bound by <see cref="Find"/> reads off the current
    /// row: NULL for a NULL-extended outer-join side.
    /// </summary>
    public static SqlValue Resolve(FromSource[] sources, byte[]?[] tuple, int boundSource, int column, BatchContext batch)
    {
        var s = -2 - boundSource;
        var bytes = tuple[s];
        if (column < 0)
        {
            return bytes is not null && batch.CurrentStatement.RowAddresses is { } map && map.TryGet(bytes, out var address)
                ? SqlValue.FromInt64(Pack(address))
                : SqlValue.Null(SqlType.BigInt);
        }

        var source = sources[s];
        if (source.BackingTable is not { } table || source.LateralPlan is not null)
            throw SimulatedSqlException.OnlyBaseTableColumnsInTextPtr();
        if (bytes is null)
            return SqlValue.Null(VarbinarySqlType.Get(LegacyTextPointer.Width));
        if (batch.CurrentStatement.RowAddresses is not { } addresses || !addresses.TryGet(bytes, out var rowAddress))
            throw new NotSupportedException($"TEXTPTR over '{source.Qualifier}' isn't modeled for this row source: its rows don't carry the address a text pointer names.");
        return LegacyTextPointer.For(table, column, rowAddress, Selection.DecodeOrCompute(source, column, bytes, batch));
    }

    public static long Pack((int Page, int Slot) address) => ((long)address.Page << 32) | (uint)address.Slot;

    public static (int Page, int Slot) Unpack(long packed) => ((int)(packed >> 32), (int)(uint)packed);
}

/// <summary>
/// The addresses of the heap rows a statement has read, keyed by the
/// <c>byte[]</c> instance each producer yielded (reference identity — heap
/// producers copy every row out of its page, so an instance names one read of
/// one row). Installed on <see cref="StatementContext.RowAddresses"/> only
/// while something will ask; see <see cref="RowLocator"/>.
/// </summary>
internal sealed class RowAddressMap
{
    private readonly Dictionary<byte[], (int Page, int Slot)> addresses = new(ReferenceEqualityComparer.Instance);

    public void Record(byte[] row, int page, int slot) => this.addresses[row] = (page, slot);

    public bool TryGet(byte[] row, out (int Page, int Slot) address) => this.addresses.TryGetValue(row, out address);
}

/// <summary>
/// The address of FROM source <c>sourceIndex</c>'s current row, as a
/// <c>bigint</c> <see cref="RowLocator.Pack"/> reads back — the column
/// <see cref="Selection.ExecuteWithRowAddresses"/> appends to a body's
/// projection. Never parsed from text.
/// </summary>
internal sealed class RowAddress(int sourceIndex) : Expression
{
    private readonly MultiPartName name = RowLocator.AddressName(sourceIndex);

    public override SqlValue Run(RuntimeContext runtime) => runtime.ResolveColumn(this.name);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.BigInt;

    internal override string DebugDisplay() => $"ROW_ADDRESS({this.name.Leaf[1..]})";

    internal override void Describe(NodeShape shape) => shape.Local(this.name.Leaf);
}
