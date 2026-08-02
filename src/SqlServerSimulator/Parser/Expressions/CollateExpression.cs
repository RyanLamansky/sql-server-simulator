using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL Server's postfix <c>expr COLLATE collation_name</c> operator. Wraps
/// a string-valued expression with an explicit collation override; the only
/// site that currently consults the override is <c>LIKE</c> (which reads
/// <see cref="Collation.CaseSensitive"/> via <see cref="ResolvedCollation"/>
/// to decide whether to flip <c>RegexOptions.IgnoreCase</c>). Other string
/// operators continue to route through <see cref="Collation.Baseline"/>,
/// matching the documented "COLLATE clause" caveat in
/// <c>docs/claude/database-options.md</c>.
/// </summary>
/// <remarks>
/// <para>Precedence: tighter than <c>+</c> and the binary comparison
/// operators, looser than primary expressions. <c>'a' + 'b' COLLATE X</c>
/// parses as <c>'a' + ('b' COLLATE X)</c>; <c>'A' = 'a' COLLATE X</c>
/// parses as <c>'A' = ('a' COLLATE X)</c>. Probe-confirmed against
/// SQL Server 2025.</para>
/// <para>Chained COLLATE (<c>expr COLLATE A COLLATE B</c>) is a syntax
/// error in real SQL Server (Msg 156); the simulator rejects with the same
/// message number via the <c>SyntaxErrorNearKeyword</c> path.</para>
/// <para>Type validation is deferred to <see cref="Run"/>: a non-string
/// inner raises Msg 447 at runtime rather than at parse time. The probed
/// real-server behavior raises Msg 447 at compile / bind time, but the
/// simulator's lazy plan has no separate bind phase and the inner's
/// <see cref="SqlType"/> isn't statically known for unresolved column refs;
/// runtime enforcement gives the same end state for the common shapes.</para>
/// </remarks>
internal sealed class CollateExpression(Expression inner, Collation collation) : Expression
{
    /// <summary>The wrapped expression. Exposed so consumers (notably <see cref="BooleanExpression"/>'s LIKE handler) can peer through to enforce binding rules.</summary>
    public readonly Expression Inner = inner;

    /// <summary>The collation named by the postfix. <c>LIKE</c> reads <see cref="Collation.CaseSensitive"/> off this to choose the regex's case-folding behavior.</summary>
    public readonly Collation ResolvedCollation = collation;

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        Rewrap(this.Inner.GetSqlType(batch, resolveColumnType), this.ResolvedCollation);

    /// <summary>
    /// Stamps the postfix collation onto <paramref name="type"/> at
    /// <see cref="Coercibility.Explicit"/> rank. A postfix settles an
    /// unresolved collation outright for the Unicode string family, but a
    /// <c>varchar</c> whose collation never resolved has bytes in no known
    /// code page and can't be re-collated at all — real reports that as
    /// <b>Msg 446 State 6</b> (probe-confirmed against SQL Server 2025:
    /// <c>concat(&lt;nvarchar pair&gt;) COLLATE X</c> returns rows where the
    /// <c>varchar</c> spelling of the same expression raises).
    /// </summary>
    private static SqlType Rewrap(SqlType type, Collation collation)
    {
        return !SqlType.IsNationalStringCategory(type) && UnresolvedCollation.On(type) is { } conflict
            ? throw SimulatedSqlException.UnresolvedCollationInOperation(
                conflict.RightName, conflict.LeftName, conflict.OperatorName, "COLLATE", 6)
            : type.WithCollation(collation, Coercibility.Explicit);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.Inner.Run(runtime);
        var rewrapped = Rewrap(value.Type, this.ResolvedCollation);
        if (value.IsNull)
            return SqlValue.Null(rewrapped);
        if (value.Type.Category != SqlTypeCategory.String)
            throw SimulatedSqlException.CollateClauseRequiresString(value.Type);
        // When the postfix swaps to a collation with a different storage
        // encoding (e.g. CP1252 → UTF-8 on the *_UTF8 collations), a fixed-
        // length char(N) value carries a .NET string sized for the inner
        // collation's byte budget but the outer encoder's fixed N-byte slot
        // would overflow. Re-route through FromString so the new type's
        // <see cref="SqlValue.NormalizeFixedLengthStringToByteCount"/> re-
        // pads / re-truncates the .NET string for the new storage encoding.
        // Variable-length varchar values size their destination buffer
        // dynamically via GetVariableByteCount, so they don't need the same
        // dance; their per-collation byte semantics fall under the broader
        // CAST + postfix-COLLATE composition gap (see collations.md).
        return rewrapped is CharSqlType
            && value.Type.Collation!.StorageEncoding != rewrapped.Collation!.StorageEncoding
                ? SqlValue.FromString(rewrapped, value.AsString)
                : value.WithType(rewrapped);
    }

    internal override string DebugDisplay() => $"{this.Inner.DebugDisplay()} COLLATE {this.ResolvedCollation.Name}";

    internal override void VisitColumnReferences(Action<MultiPartName> visit) => this.Inner.VisitColumnReferences(visit);

    internal override bool IsRowIndependent => this.Inner.IsRowIndependent;

    internal override bool ResultIsNullable(NullabilityContext context) => this.Inner.ResultIsNullable(context);

    // Real folds the postfix away when its operand is constant, so
    // `ORDER BY 'x' COLLATE Latin1_General_CI_AS` is Msg 408 (probe-confirmed).
    private protected override bool IsStructuralConstant => this.Inner.IsWrittenConstant;

    /// <summary>
    /// Consumes the <c>COLLATE collation_name</c> postfix when invoked from
    /// <see cref="Expression.Parse"/>'s binary-operator loop. The current
    /// token is the <c>COLLATE</c> reserved keyword; this method consumes
    /// the keyword and a single identifier (collation name), then leaves
    /// the cursor on the collation-name token so the caller's surrounding
    /// loop can advance via <c>GetNextOptional</c>. Rejects chained
    /// COLLATE (<c>expr COLLATE A COLLATE B</c>) with Msg 156 to match
    /// probed real-server behavior. Unknown names raise Msg 448.
    /// </summary>
    public static CollateExpression ParsePostfix(Expression source, ParserContext context)
    {
        if (source is CollateExpression)
            throw SimulatedSqlException.SyntaxErrorNearKeyword("collate");
        context.MoveNextRequired();
        var collationName = context.Token switch
        {
            UnquotedString us => us.Value,
            Name n => n.Value,
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
        var collation = Collation.TryGet(ResolvePseudoCollationName(collationName, context.Batch))
            ?? throw SimulatedSqlException.InvalidCollation(collationName);
        return new CollateExpression(source, collation);
    }

    /// <summary>
    /// Expands the pseudo-collation keywords <c>database_default</c> (the active
    /// database's collation) and <c>catalog_default</c> (the fixed metadata /
    /// catalog collation) to their concrete collation name; any other name
    /// passes through unchanged for the caller's own recognition check. The
    /// keyword match is case-insensitive. Shared by the expression-level postfix
    /// and the CREATE TABLE / ALTER COLUMN / table-variable / temp-table
    /// column-definition parse sites, all of which resolve at bind time against
    /// the session's active database (safe under the plan cache, which keys per
    /// database). Real SQL Server records the resolved name in
    /// <c>sys.columns.collation_name</c>; <c>catalog_default</c> resolves to the
    /// database collation in a non-contained database (containment isn't
    /// modeled) but is kept pinned to the catalog collation here to match the
    /// simulator's catalog-column comparison contract. Probe-confirmed
    /// (SQL Server 2025): <c>N'x' COLLATE database_default</c> reports the
    /// active database's collation from <c>SQL_VARIANT_PROPERTY</c>, including
    /// for <c>#temp</c> columns (the session database, not tempdb).
    /// </summary>
    internal static string ResolvePseudoCollationName(string collationName, BatchContext batch) =>
        string.Equals(collationName, "database_default", StringComparison.OrdinalIgnoreCase) ? batch.CurrentDatabase.Collation.Name
        : string.Equals(collationName, "catalog_default", StringComparison.OrdinalIgnoreCase) ? Collation.Catalog.Name
        : collationName;
}
