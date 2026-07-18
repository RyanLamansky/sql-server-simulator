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

    /// <summary>
    /// Builds a simple-form CASE programmatically — <c>CASE input WHEN
    /// compareValues[i] THEN thens[i] ... [ELSE elseBranch] END</c> — bypassing
    /// the token parser. Used by PIVOT desugaring, where each pivot column is
    /// <c>&lt;agg&gt;(CASE forCol WHEN value THEN argCol END)</c>; the
    /// simple-form <c>=</c> comparison aligns the value's type to the FOR
    /// column's via <see cref="BooleanExpression.CompareValuesPromoted"/>.
    /// </summary>
    internal static CaseExpression CreateSimple(Expression input, Expression[] compareValues, Expression[] thens, Expression? elseBranch) =>
        new(input, searchedWhens: null, compareValues, thens, elseBranch);

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

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        SqlType? t = null;
        foreach (var then in this.thens)
            t = CombineArmType(t, then, batch, resolveColumnType);
        if (this.elseBranch is not null)
            t = CombineArmType(t, this.elseBranch, batch, resolveColumnType);
        // Every branch is a bare NULL literal (or the CASE has an implicit
        // ELSE NULL only) — fall back to the untyped-NULL placeholder type.
        this.cachedResultType = t ?? SqlType.Int32;
        return this.cachedResultType;
    }

    // A bare NULL literal is typeless in SQL Server's CASE result-type
    // resolution: it yields to the typed branches rather than forcing the
    // literal's placeholder int type onto the whole expression. So
    // `CASE WHEN … THEN 'x' ELSE NULL END` is nvarchar, not int. Only when
    // every branch is an untyped NULL does the placeholder type stand.
    private static SqlType? CombineArmType(SqlType? acc, Expression arm, BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        if (arm is Value { Constant.IsNull: true })
            return acc;
        var armType = arm.GetSqlType(batch, resolveColumnType);
        return acc is null ? armType : SqlType.Promote(acc, armType);
    }

    internal override string DebugDisplay() => "CASE ...";

    // CASE result is non-null only when every THEN branch is non-null AND
    // either there's a non-null ELSE or every WHEN covers every possible
    // input. Since proving exhaustive WHEN coverage is intractable, the
    // simulator follows real SQL Server's documented projection rule:
    // non-null iff every THEN AND the ELSE (or implicit ELSE NULL → null)
    // is non-null.
    internal override bool ResultIsNullable(Func<MultiPartName, bool> resolveColumnNullable)
    {
        for (var i = 0; i < this.thens.Length; i++)
        {
            if (this.thens[i].ResultIsNullable(resolveColumnNullable))
                return true;
        }
        // Missing ELSE = implicit NULL = nullable; explicit ELSE delegates.
        return this.elseBranch is null || this.elseBranch.ResultIsNullable(resolveColumnNullable);
    }

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
        // SQL Server caps CASE / IIF lexical nesting at ten levels (Msg 125,
        // State 4 for CASE). The count wraps the entire CASE parse so nesting
        // in a WHEN condition counts the same as in a THEN / ELSE result, and
        // it persists across a scalar-subquery boundary (both probe-confirmed).
        if (++context.CaseDepth > ParserContext.MaxCaseNestingDepth)
            throw SimulatedSqlException.CaseExpressionsNestedTooDeeply(4);
        try
        {
            return ParseCaseBody(context);
        }
        finally
        {
            context.CaseDepth--;
        }
    }

    private static CaseExpression ParseCaseBody(ParserContext context)
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

        // Real SQL Server fires Msg 8133 at compile time when every result
        // expression — every THEN body, plus the explicit ELSE if present
        // (absent ELSE = implicit NULL) — is a bare NULL literal. A typed
        // NULL (e.g. `CAST(NULL AS int)`) satisfies the rule because its
        // type isn't ambiguous. Probe-confirmed against SQL Server 2025.
        var anyTypedBranch = elseBranch is not null && !IsBareNullLiteral(elseBranch);
        for (var i = 0; !anyTypedBranch && i < thens.Count; i++)
            anyTypedBranch = !IsBareNullLiteral(thens[i]);

        return context.Token is not ReservedKeyword { Keyword: Keyword.End }
            ? throw SimulatedSqlException.SyntaxErrorNear(context)
            : !anyTypedBranch
                ? throw SimulatedSqlException.AllResultsInCaseAreNull()
                : new CaseExpression(
                    input,
                    input is null ? [.. searchedWhensList!] : null,
                    input is not null ? [.. compareValuesList!] : null,
                    [.. thens],
                    elseBranch);
    }
}
