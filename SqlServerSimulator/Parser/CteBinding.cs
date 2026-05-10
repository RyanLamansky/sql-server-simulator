using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// One named binding from a <c>WITH</c> prefix. Lifetime is exactly one
/// following statement: <see cref="Simulation.CreateResultSetsForCommand"/>
/// populates <see cref="ParserContext.CteBindings"/> via <c>ParseCteBindings</c>
/// before dispatching to SELECT / INSERT / UPDATE / DELETE / MERGE, and
/// clears the slot at the start of the next iteration.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Plan"/> is null while the body is mid-parse — a sentinel so
/// <c>ParseSingleFromSource</c> can detect self-references. For non-recursive
/// CTEs the sentinel is replaced by the body's plan after parse. For
/// recursive CTEs the sentinel transitions into <see cref="IsRecursivePartParse"/>
/// mode after the anchor parses; subsequent self-references resolve into a
/// FromSource backed by <see cref="CurrentIterationRows"/>.
/// </para>
/// <para>
/// The fields beyond <see cref="Name"/> / <see cref="ColumnNames"/> are
/// mutable and used as scratch space during the body parse and during
/// recursive iteration; they're never observed concurrently because the
/// simulator is single-threaded per Simulation.
/// </para>
/// </remarks>
internal sealed class CteBinding(string name, string[] columnNames)
{
    public readonly string Name = name;

    /// <summary>
    /// The body's projected column names (or the rename list when present).
    /// Mutable so the parser can install the rename-list names after the
    /// body parse without needing to re-create the binding — the recursive
    /// Selection's closure captures this binding by reference and the
    /// runtime <see cref="CurrentIterationRows"/> / <see cref="MaxRecursion"/>
    /// slots must stay reachable through the same instance.
    /// </summary>
    public string[] ColumnNames = columnNames;

    /// <summary>
    /// The body's parsed plan, or <see langword="null"/> while parsing is
    /// in flight. Set once when the body's <c>Selection</c> resolves.
    /// </summary>
    public Selection? Plan;

    /// <summary>
    /// Per-column types of the body, captured from the anchor branch in a
    /// recursive CTE so subsequent recursive branches can self-reference
    /// with a known schema. Null until the anchor parse completes.
    /// </summary>
    public SqlType[]? Schema;

    /// <summary>
    /// True while a recursive CTE is parsing its recursive branches —
    /// signals <c>ParseSingleFromSource</c> to resolve self-references via
    /// <see cref="CurrentIterationRows"/> instead of raising. Set by the
    /// recursive-CTE body parser after the anchor branch completes.
    /// </summary>
    public bool IsRecursivePartParse;

    /// <summary>
    /// Set by <c>ParseSingleFromSource</c> when the recursive branch parses
    /// hit a self-reference. The recursive-CTE body parser reads this to
    /// classify each branch as anchor or recursive, and to enforce
    /// "exactly one self-reference per recursive branch" (Msg 253).
    /// </summary>
    public int SelfReferenceCountInCurrentBranch;

    /// <summary>
    /// Runtime slot read by the recursive branch's self-reference
    /// FromSource. The recursive-CTE Selection rebinds this between
    /// iterations: anchor rows for the first iteration, then the previous
    /// iteration's output rows for each subsequent iteration. Empty
    /// terminates iteration.
    /// </summary>
    public IEnumerable<byte[]>? CurrentIterationRows;

    /// <summary>
    /// MAXRECURSION cap for the recursive iteration. Default 100 matches
    /// SQL Server; <c>OPTION (MAXRECURSION N)</c> on the surrounding
    /// statement overrides; <c>0</c> disables the check. The recursive
    /// Selection reads this value at execute time so the OPTION clause
    /// (parsed after the CTE binding) takes effect.
    /// </summary>
    public int MaxRecursion = 100;
}
