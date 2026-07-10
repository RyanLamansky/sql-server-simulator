namespace SqlServerSimulator.Parser;

/// <summary>
/// Per-enumeration memo for <c>Selection.FindSourceColumn</c>. The name →
/// (source, column) binding is fixed for a given sources array, but the
/// per-row resolvers re-derive it once per column reference per row —
/// profiled at ~45% of total CPU on a four-way-join aggregate report over
/// 230k rows, the dominant cost of every scan-bound join / aggregate /
/// window query. Lookup is a short linear scan comparing the name's
/// component <b>string references</b>: a parsed <c>Reference</c> node passes
/// the same <see cref="MultiPartName"/> (hence the same string instances)
/// every row, so reference identity is a stable, near-free key. A miss falls
/// through to one full resolution and appends (copy-on-write, so an
/// accidentally-shared memo degrades to extra appends rather than torn
/// reads).
/// </summary>
/// <remarks>
/// One instance is created per enumerating method activation, beside the
/// closure that captures the sources array — execution-scoped, so a
/// plan-cached <c>Selection</c>'s concurrent executions never share one
/// (the shared-plan contract in <c>plan-cache.md</c>). The entry list stops
/// growing at <see cref="CapacityCap"/> — distinct reference names per query
/// are few, so the cap only guards against a hypothetical caller minting
/// fresh name strings per row, which would otherwise grow the memo without
/// ever hitting it.
/// </remarks>
internal sealed class SourceColumnMemo
{
    private const int CapacityCap = 64;

    private (string Leaf, string? Qualifier, int SourceIndex, int ColumnIndex)[] entries = [];

    /// <summary>
    /// Memoized <c>Selection.FindSourceColumn</c>: the (source, column)
    /// location of <paramref name="name"/> across <paramref name="sources"/>,
    /// or <c>(-1, -1)</c> for the outer-scope fallthrough. Msg 209 ambiguity
    /// still raises from the underlying resolution (never cached — it throws
    /// before the append).
    /// </summary>
    public (int SourceIndex, int ColumnIndex) Find(FromSource[] sources, MultiPartName name)
    {
        var snapshot = this.entries;
        foreach (var (leaf, qualifier, memoSource, memoColumn) in snapshot)
        {
            if (ReferenceEquals(leaf, name.Leaf) && ReferenceEquals(qualifier, name.ImmediateQualifier))
                return (memoSource, memoColumn);
        }

        var (sourceIndex, columnIndex) = Selection.FindSourceColumn(sources, name);
        if (snapshot.Length < CapacityCap)
        {
            var grown = new (string, string?, int, int)[snapshot.Length + 1];
            Array.Copy(snapshot, grown, snapshot.Length);
            grown[snapshot.Length] = (name.Leaf, name.ImmediateQualifier, sourceIndex, columnIndex);
            this.entries = grown;
        }

        return (sourceIndex, columnIndex);
    }
}
