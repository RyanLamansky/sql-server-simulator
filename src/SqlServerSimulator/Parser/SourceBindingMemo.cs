namespace SqlServerSimulator.Parser;

/// <summary>
/// Parse-time memo for <c>Selection.FindSourceColumn</c> while a query's
/// <c>ON</c>, <c>WHERE</c> or projection binds. Binding a projection resolves
/// each column reference once per pass that reads it — typing, nullability,
/// output keys, identity and pass-through columns, the read recorder — each
/// comparing the name against every column of every source; measured in
/// <c>plan-cache.md</c>'s one-off profile.
/// </summary>
/// <remarks>
/// <para>
/// A resolution depends only on the <see cref="FromSource"/> instances it
/// searches and on the name, so the key is the sources' <b>identities</b>, in
/// order, rather than the array holding them: the planner's helpers pass
/// copies (<c>[.. sources]</c>), and the identities survive the copy where the
/// array's does not. A call over any other sources — an outer scope's, a
/// nested query's — misses the key and resolves as before. Names compare by
/// their parts' string references, which a parsed <c>Reference</c> keeps
/// across every pass (as <see cref="SourceColumnMemo"/> does per row).
/// </para>
/// <para>
/// The memo is installed per thread for the extent of <see cref="Enter"/>'s
/// guard, nested guards restoring the outer one, so it neither outlives the
/// binding that filled it nor holds the sources past it; the thread keeps
/// released memos for reuse, as <see cref="ExpressionNode.Walk{TState}(ref TState, NodeVisitor{TState})"/>
/// keeps its stacks. An ambiguous name raises Msg 209 on every resolution,
/// never from the memo.
/// </para>
/// </remarks>
internal sealed class SourceBindingMemo
{
    private const int CapacityCap = 64;

    [ThreadStatic]
    private static SourceBindingMemo? active;

    [ThreadStatic]
    private static SourceBindingMemo? idle;

    private FromSource[] sources = [];
    private int sourceCount;
    private (MultiPartName Name, int SourceIndex, int ColumnIndex)[] entries = new (MultiPartName, int, int)[16];
    private int count;

    /// <summary>The memo installed when this one was, or the next idle one while this one is idle.</summary>
    private SourceBindingMemo? next;

    /// <summary>
    /// Memoizes resolutions over <paramref name="sources"/>' current members
    /// until the returned guard is disposed.
    /// </summary>
    public static Scope Enter(FromSource[] sources)
    {
        var memo = idle ?? new SourceBindingMemo();
        idle = memo.next;
        memo.Rekey(sources);
        memo.next = active;
        active = memo;
        return new(memo);
    }

    /// <summary>
    /// The installed memo when <paramref name="sources"/> holds exactly the
    /// sources it was keyed on, else <see langword="null"/>.
    /// </summary>
    public static SourceBindingMemo? For(FromSource[] sources)
    {
        if (active is not { } memo || memo.sourceCount != sources.Length)
            return null;
        var keyed = memo.sources;
        for (var i = 0; i < sources.Length; i++)
        {
            if (!ReferenceEquals(keyed[i], sources[i]))
                return null;
        }
        return memo;
    }

    public bool TryGet(MultiPartName name, out (int SourceIndex, int ColumnIndex) found)
    {
        var entries = this.entries;
        for (var i = 0; i < this.count; i++)
        {
            if (entries[i].Name.IsSameInstanceAs(name))
            {
                found = (entries[i].SourceIndex, entries[i].ColumnIndex);
                return true;
            }
        }
        found = default;
        return false;
    }

    public void Add(MultiPartName name, (int SourceIndex, int ColumnIndex) found)
    {
        if (this.count == CapacityCap)
            return;
        if (this.count == this.entries.Length)
            Array.Resize(ref this.entries, this.count * 2);
        this.entries[this.count++] = (name, found.SourceIndex, found.ColumnIndex);
    }

    /// <summary>
    /// Keys the memo on <paramref name="sources"/>' current members,
    /// forgetting what it held — as the planner does after replacing one.
    /// </summary>
    public void Rekey(FromSource[] sources)
    {
        Array.Clear(this.entries, 0, this.count);
        this.count = 0;
        if (this.sources.Length < sources.Length)
            this.sources = new FromSource[Math.Max(sources.Length, 4)];
        else
            Array.Clear(this.sources, sources.Length, this.sourceCount > sources.Length ? this.sourceCount - sources.Length : 0);
        sources.CopyTo(this.sources, 0);
        this.sourceCount = sources.Length;
    }

    /// <summary>
    /// The guard <see cref="Enter"/> returns; disposing it restores the memo
    /// installed before and keeps this one idle, holding no source or name.
    /// </summary>
    internal readonly ref struct Scope(SourceBindingMemo memo)
    {
        public readonly SourceBindingMemo Memo = memo;

        public void Dispose()
        {
            var memo = this.Memo;
            active = memo.next;
            Array.Clear(memo.entries, 0, memo.count);
            memo.count = 0;
            Array.Clear(memo.sources, 0, memo.sourceCount);
            memo.sourceCount = 0;
            memo.next = idle;
            idle = memo;
        }
    }
}
