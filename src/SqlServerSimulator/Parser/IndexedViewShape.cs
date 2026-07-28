using SqlServerSimulator.Parser.Expressions;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The structural facts about a view body that decide whether an index may be
/// created on it. Populated during a validation parse of the view's stored
/// text — <see cref="ParserContext.IndexedViewShapeCollector"/> is non-null
/// only for that parse, so the recording sites are no-ops on the hot path.
/// <para>Real SQL Server runs this battery at <c>CREATE INDEX</c>, not at
/// <c>CREATE VIEW</c> (probe-confirmed: every rejected shape below creates as
/// a view without complaint and fails only when indexed), which is why the
/// shape is gathered on demand rather than stamped onto the stored view.</para>
/// </summary>
internal sealed class IndexedViewShape
{
    /// <summary>`SELECT DISTINCT` anywhere in the body → Msg 10100.</summary>
    public bool HasDistinct;

    /// <summary>`TOP` / `OFFSET` / `FETCH` → Msg 10101.</summary>
    public bool HasTopOrOffset;

    /// <summary>A LEFT / RIGHT / FULL join → Msg 10113.</summary>
    public bool HasOuterJoin;

    /// <summary>A UNION / INTERSECT / EXCEPT chain → Msg 10116.</summary>
    public bool HasSetOperation;

    /// <summary>A GROUP BY, which makes <c>COUNT_BIG(*)</c> mandatory → Msg 10138.</summary>
    public bool HasGroupBy;

    /// <summary>A subquery at any depth → Msg 10127.</summary>
    public bool HasSubquery;

    /// <summary>
    /// A table the body joins to itself → Msg 1947, which embeds its qualified
    /// name. The table is kept rather than a formatted name because qualifying
    /// it needs the database, which only the gate site has.
    /// </summary>
    public Storage.HeapTable? SelfJoinedTable;

    /// <summary>
    /// The lower-cased name of the first nondeterministic built-in reached →
    /// Msg 1949, which embeds it (real reports <c>'getdate'</c> lower-cased
    /// regardless of how the call was written).
    /// </summary>
    public string? NondeterministicFunction;

    /// <summary>
    /// The aggregates the body projects, in parse order. Msg 10125 names the
    /// first disallowed one; <c>COUNT</c> takes its own Msg 10136; the
    /// presence of <c>COUNT_BIG</c> satisfies Msg 10138.
    /// </summary>
    public readonly List<AggregateKind> Aggregates = [];

    /// <summary>
    /// True when a <c>SUM</c> aggregates an expression that can produce NULL →
    /// Msg 8662. Real phrases this as the view "referencing an unknown value".
    /// </summary>
    public bool SumsNullableExpression;
}
