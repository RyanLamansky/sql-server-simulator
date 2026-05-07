using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Per-source state captured during a SELECT's FROM clause parsing — one
/// instance per table (or derived table) that participates in the row
/// stream. Bundles the column metadata, the underlying byte stream, and
/// any qualifier (alias or table name) used for column resolution.
/// </summary>
/// <remarks>
/// <para>
/// Pre-JOIN, every Selection had exactly one source; multi-table FROM
/// (with <see cref="JoinSpec"/>) extends that to <c>FromSource[]</c>.
/// Column lookup walks the array in source order; a qualified reference
/// (<c>alias.col</c> / <c>tableName.col</c>) restricts to the matching
/// source, an unqualified reference searches all and raises Msg 209 when
/// the column name appears in more than one.
/// </para>
/// <para>
/// <see cref="StoredSchema"/> equals <see cref="Columns"/> for ordinary
/// heap rows but diverges when computed-column projections are stripped
/// from storage; <see cref="StorageOrdinals"/> maps logical (Columns)
/// indices to physical (StoredSchema) indices and is null for derived
/// tables that don't have a separate stored layout.
/// </para>
/// </remarks>
internal sealed class FromSource(
    string? qualifier,
    string[] columnNames,
    HeapColumn[] columns,
    HeapColumn[] storedSchema,
    int[]? storageOrdinals,
    Heap? lobStore,
    IEnumerable<byte[]> rows)
{
    public readonly string? Qualifier = qualifier;
    public readonly string[] ColumnNames = columnNames;
    public readonly HeapColumn[] Columns = columns;
    public readonly HeapColumn[] StoredSchema = storedSchema;
    public readonly int[]? StorageOrdinals = storageOrdinals;
    public readonly Heap? LobStore = lobStore;
    public readonly IEnumerable<byte[]> Rows = rows;
}

/// <summary>
/// The four set-operation variants the simulator parses. <c>Union</c>
/// dedupes; <c>UnionAll</c> preserves duplicates; <c>Intersect</c> keeps
/// rows present in both branches (dedupes); <c>Except</c> keeps left-side
/// rows not in the right (dedupes). NULLs are equal during dedup /
/// matching — opposite of the <c>=</c> operator's three-valued behavior,
/// matching SQL Server's documented set-op semantics.
/// </summary>
internal enum SetOpKind
{
    Union,
    UnionAll,
    Intersect,
    Except,
}

/// <summary>
/// The variants of JOIN the simulator parses. <c>Inner</c> includes the
/// bare <c>JOIN</c> keyword (which SQL Server treats as INNER) and the
/// explicit <c>INNER JOIN</c>. <c>Left</c> covers <c>LEFT [OUTER] JOIN</c>.
/// <c>Cross</c> is the unconditional Cartesian product (and rejects ON).
/// <c>RIGHT JOIN</c> and <c>FULL OUTER JOIN</c> aren't modeled — the
/// parser raises <see cref="NotSupportedException"/>; rewrite RIGHT as
/// LEFT with sources reversed.
/// </summary>
internal enum JoinKind
{
    Inner,
    Left,
    Cross,
}

/// <summary>
/// Describes how the next <see cref="FromSource"/> joins to the
/// accumulated row tuple. <see cref="OnPredicate"/> is null only for
/// <see cref="JoinKind.Cross"/>; the parser enforces the pairing.
/// </summary>
internal sealed class JoinSpec(JoinKind kind, BooleanExpression? onPredicate)
{
    public readonly JoinKind Kind = kind;
    public readonly BooleanExpression? OnPredicate = onPredicate;
}
