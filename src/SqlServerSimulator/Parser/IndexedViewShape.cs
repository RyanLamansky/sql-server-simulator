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
    /// <summary>
    /// The first CTE the body declares → Msg 10137, which embeds it. Declared
    /// order, not reference order: real names the first one even when the
    /// body's SELECT reads only a later one, and even when nothing reads it.
    /// </summary>
    public string? CteName;

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

    /// <summary>The alias of the first derived table the body reads → Msg 10109, which embeds it.</summary>
    public string? DerivedTableAlias;

    /// <summary>A <c>HAVING</c> clause → Msg 10121.</summary>
    public bool HasHaving;

    /// <summary>A <c>ROLLUP</c>, <c>CUBE</c> or <c>GROUPING SETS</c> → Msg 10119.</summary>
    public bool HasGroupingSets;

    /// <summary>A ranking or aggregate window function → Msg 10143.</summary>
    public bool HasWindow;

    /// <summary>A <c>CROSS</c> or <c>OUTER APPLY</c> → Msg 10142.</summary>
    public bool HasApply;

    /// <summary>A <c>PIVOT</c> or <c>UNPIVOT</c> → Msg 10114.</summary>
    public bool HasPivot;

    /// <summary>A table hint on any source → Msg 10140.</summary>
    public bool HasTableHint;

    /// <summary>A <c>CHECKSUM(*)</c> / <c>BINARY_CHECKSUM(*)</c> star → Msg 10117.</summary>
    public bool UsesStarOperator;

    /// <summary>The first view the body reads, as <c>schema.view</c> → Msg 1937, which embeds it.</summary>
    public string? ReferencedView;

    /// <summary>
    /// The first float or real column a <c>WHERE</c> or <c>GROUP BY</c> reads →
    /// Msg 1962, which embeds it.
    /// </summary>
    public string? ImpreciseFilterColumn;

    /// <summary>
    /// A <c>float</c> or <c>real</c> computation — a column, a literal, any
    /// expression of either type — in a <c>WHERE</c>, a join's <c>ON</c> or a
    /// <c>GROUP BY</c>, which makes the view imprecise to
    /// <c>OBJECTPROPERTY(…, 'IsPrecise')</c>; one only projected doesn't
    /// (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    public bool FiltersOrGroupsImprecisely;

    /// <summary>
    /// Of <see cref="FiltersOrGroupsImprecisely"/>, a <c>float</c> or
    /// <c>real</c> constant → Msg 1964.
    /// </summary>
    public bool ImpreciseFilterConstant;

    /// <summary>The first table-valued function the body reads, as <c>schema.name</c> → Msg 10129.</summary>
    public string? TableValuedFunction;

    /// <summary>An <c>OPENJSON</c> rowset → Msg 10148.</summary>
    public bool UsesOpenJson;

    /// <summary>An xml data type method → Msg 1985.</summary>
    public bool UsesXmlMethod;

    /// <summary>The user functions the body calls, whose creation-time SET options Msg 1935 judges.</summary>
    public readonly List<Schemas.UserDefinedFunction> CalledFunctions = [];

    /// <summary>A select-list expression over an aggregate's result → Msg 8668.</summary>
    public bool ExpressionOverAggregate;

    /// <summary>A <c>GROUP BY</c> expression the select list doesn't carry → Msg 8660.</summary>
    public bool GroupingExpressionNotProjected;

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
