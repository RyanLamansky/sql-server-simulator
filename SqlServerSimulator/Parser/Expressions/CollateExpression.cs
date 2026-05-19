using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL Server's postfix <c>expr COLLATE collation_name</c> operator. Wraps
/// a string-valued expression with an explicit collation override; the only
/// site that currently consults the override is <c>LIKE</c> (which reads
/// <see cref="Collation.CaseSensitive"/> via <see cref="ResolvedCollation"/>
/// to decide whether to flip <c>RegexOptions.IgnoreCase</c>). Other string
/// operators continue to route through <see cref="Collation.Default"/>,
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

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => this.Inner.GetSqlType(resolveColumnType);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.Inner.Run(runtime);
        return !value.IsNull && value.Type.Category != SqlTypeCategory.String
            ? throw SimulatedSqlException.CollateClauseRequiresString(value.Type)
            : value;
    }

    internal override string DebugDisplay() => $"{this.Inner.DebugDisplay()} COLLATE {this.ResolvedCollation.Name}";

    internal override void VisitColumnReferences(Action<MultiPartName> visit) => this.Inner.VisitColumnReferences(visit);

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
        return !Collation.ByName.TryGetValue(collationName, out var collation)
            ? throw SimulatedSqlException.InvalidCollation(collationName)
            : new CollateExpression(source, collation);
    }
}
