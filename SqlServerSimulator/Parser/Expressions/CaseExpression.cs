using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL Server's <c>CASE</c> expression in both forms: searched
/// (<c>CASE WHEN cond THEN r ... [ELSE r] END</c>) and simple
/// (<c>CASE input WHEN val THEN r ... [ELSE r] END</c>). Branches are
/// evaluated in source order; the first true predicate wins. UNKNOWN is
/// treated as exclude (same as WHERE) — no match at all and no ELSE
/// produces NULL of the result type.
/// </summary>
/// <remarks>
/// <para>
/// The simple form's <c>WHEN</c> compares <see cref="input"/> against each
/// branch's compare value through <see cref="BooleanExpression.CompareValuesPromoted"/>
/// using <c>=</c> semantics — so a NULL input vs a NULL compare value
/// yields UNKNOWN, not a match (matching SQL Server: <c>CASE NULL WHEN NULL ...</c>
/// falls through to ELSE).
/// </para>
/// <para>
/// Branch values are coerced to a single common type computed by
/// <see cref="SqlType.Promote"/> across all THEN / ELSE branches — matching
/// SQL Server's "all CASE branches share one result type" rule. The common
/// type is cached on the first <see cref="GetSqlType"/> call (which the
/// projection planner always makes) and used by <see cref="Run"/> to coerce
/// each matched branch's runtime value, so the projection schema and the
/// per-row output stay consistent. When <see cref="GetSqlType"/> hasn't been
/// called (e.g. CASE used purely as a comparison operand in WHERE),
/// <see cref="Run"/> returns the matched branch's runtime value uncoerced —
/// the comparison's <c>Promote</c> path handles type alignment from there.
/// </para>
/// <para>
/// When no branch matches and no ELSE is present, returns NULL of the
/// cached result type (or <see cref="SqlType.Int32"/> when the type cache
/// is unset). <see cref="RowEncoder"/>'s schema check passes through NULL
/// values regardless of the column's declared type.
/// </para>
/// </remarks>
internal sealed class CaseExpression : Expression
{
    private readonly Expression? input;
    private readonly BooleanExpression[]? searchedWhens;
    private readonly Expression[]? compareValues;
    private readonly Expression[] thens;
    private readonly Expression? elseBranch;
    private SqlType? cachedResultType;

    private CaseExpression(Expression? input, BooleanExpression[]? searchedWhens, Expression[]? compareValues, Expression[] thens, Expression? elseBranch)
    {
        this.input = input;
        this.searchedWhens = searchedWhens;
        this.compareValues = compareValues;
        this.thens = thens;
        this.elseBranch = elseBranch;
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var raw = this.input is null ? FindSearchedMatch(runtime) : FindSimpleMatch(runtime);
        return this.cachedResultType is { } target && !raw.IsNull ? raw.CoerceTo(target) : raw;
    }

    private SqlValue FindSearchedMatch(RuntimeContext runtime)
    {
        for (var i = 0; i < this.searchedWhens!.Length; i++)
        {
            if (this.searchedWhens[i].Run(runtime) == true)
                return this.thens[i].Run(runtime);
        }
        return this.elseBranch is null ? SqlValue.Null(this.cachedResultType ?? SqlType.Int32) : this.elseBranch.Run(runtime);
    }

    private SqlValue FindSimpleMatch(RuntimeContext runtime)
    {
        var inputValue = this.input!.Run(runtime);
        for (var i = 0; i < this.compareValues!.Length; i++)
        {
            var compareValue = this.compareValues[i].Run(runtime);
            if (BooleanExpression.CompareValuesPromoted(inputValue, compareValue, "equal to", static (l, r) => l.Equals(r)) == true)
                return this.thens[i].Run(runtime);
        }
        return this.elseBranch is null ? SqlValue.Null(this.cachedResultType ?? SqlType.Int32) : this.elseBranch.Run(runtime);
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType)
    {
        var t = this.thens[0].GetSqlType(resolveColumnType);
        for (var i = 1; i < this.thens.Length; i++)
            t = SqlType.Promote(t, this.thens[i].GetSqlType(resolveColumnType));
        if (this.elseBranch is not null)
            t = SqlType.Promote(t, this.elseBranch.GetSqlType(resolveColumnType));
        this.cachedResultType = t;
        return t;
    }

    internal override string DebugDisplay() => "CASE ...";

    /// <summary>
    /// Parses a CASE expression. Entered with
    /// <see cref="ParserContext.Token"/> on the <c>CASE</c> keyword;
    /// dispatches to searched form (when the token after <c>CASE</c> is
    /// <c>WHEN</c>) or simple form (otherwise — parses the input expression
    /// first). Both forms require at least one WHEN clause and a closing
    /// <c>END</c>; the cursor is left on <c>END</c> per the lookahead
    /// contract that <see cref="Expression.Parse"/>'s binary loop expects.
    /// </summary>
    public static CaseExpression ParseCase(ParserContext context)
    {
        context.MoveNextRequired();

        Expression? input = null;
        if (context.Token is not ReservedKeyword { Keyword: Keyword.When })
            input = Expression.Parse(context);

        if (context.Token is not ReservedKeyword { Keyword: Keyword.When })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var thens = new List<Expression>();
        var searchedWhensList = input is null ? new List<BooleanExpression>() : null;
        var compareValuesList = input is not null ? new List<Expression>() : null;

        while (context.Token is ReservedKeyword { Keyword: Keyword.When })
        {
            context.MoveNextRequired();

            if (input is null)
                searchedWhensList!.Add(BooleanExpression.Parse(context));
            else
                compareValuesList!.Add(Expression.Parse(context));

            if (context.Token is not ReservedKeyword { Keyword: Keyword.Then })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();

            thens.Add(Expression.Parse(context));
        }

        Expression? elseBranch = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Else })
        {
            context.MoveNextRequired();
            elseBranch = Expression.Parse(context);
        }

        return context.Token is not ReservedKeyword { Keyword: Keyword.End }
            ? throw SimulatedSqlException.SyntaxErrorNear(context)
            : new CaseExpression(
                input,
                input is null ? [.. searchedWhensList!] : null,
                input is not null ? [.. compareValuesList!] : null,
                [.. thens],
                elseBranch);
    }
}
