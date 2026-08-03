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

    /// <summary>
    /// Whether the simple form carries a NULL constant on one side of <em>every</em>
    /// <c>WHEN</c> comparison it stands for — the input folds to NULL, or every
    /// compare value does — which makes all of them UNKNOWN and leaves no arm
    /// reachable, so real runs the ELSE alone and drops the rest of the CASE
    /// with the comparisons. Probe-confirmed in both directions
    /// (<c>CASE CAST(NULL AS int) WHEN 7 / 0 THEN …</c> and
    /// <c>CASE 7 / 0 WHEN CAST(NULL AS int) THEN … WHEN NULL THEN …</c> both
    /// answer where the dropped operand alone raises), and so are its fences: a
    /// single non-NULL compare value beside the NULL one leaves the input
    /// standing (<c>CASE 7 / 0 WHEN CAST(NULL AS int) THEN 1 WHEN 5 THEN 2 …</c>
    /// is Msg 8134).
    /// <para>
    /// Settled while parsing because that is when real settles it: a NULL the
    /// <em>row</em> supplies doesn't skip the arms — <c>CASE nullcol WHEN 7 /
    /// zerocol …</c> is Msg 8134 on real, where
    /// <c>CASE CAST(NULL AS int) WHEN 7 / zerocol …</c> answers the ELSE.
    /// </para>
    /// </summary>
    private readonly bool noArmReachable;

    private SqlType? cachedResultType;

    private CaseExpression(
        Expression? input,
        BooleanExpression[]? searchedWhens,
        Expression[]? compareValues,
        Expression[] thens,
        Expression? elseBranch,
        bool noArmReachable)
    {
        this.input = input;
        this.searchedWhens = searchedWhens;
        this.compareValues = compareValues;
        this.thens = thens;
        this.elseBranch = elseBranch;
        this.noArmReachable = noArmReachable;
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
        new(input, searchedWhens: null, compareValues, thens, elseBranch, noArmReachable: false);

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
        // Every comparison settled UNKNOWN while compiling, so real drops the
        // input and the compare values with them and goes straight to the ELSE.
        if (!this.noArmReachable)
        {
            var inputValue = this.input!.Run(runtime);
            for (var i = 0; i < this.compareValues!.Length; i++)
            {
                var compareValue = this.compareValues[i].Run(runtime);
                if (BooleanExpression.CompareValuesPromoted(inputValue, compareValue, "equal to", static (l, r) => l.Equals(r)) == true)
                    return this.thens[i].Run(runtime);
            }
        }
        return this.elseBranch is null ? SqlValue.Null(this.cachedResultType ?? SqlType.Int32) : this.elseBranch.Run(runtime);
    }

    // THEN / ELSE branches share one result type. An untyped NULL branch is
    // typeless in SQL Server's resolution — it yields to the typed branches
    // rather than forcing its placeholder int type onto the whole expression
    // (`CASE WHEN … THEN 'x' ELSE NULL END` is nvarchar, not int) — and an
    // integer-literal branch sizes by digit count against a decimal sibling
    // (`CASE … 1 … 2.5` → numeric(2, 1)). Both rules live in PromoteValueArms.
    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        // The WHEN side binds too: a searched CASE carries whole predicates and
        // a simple one an implicit `=` per compare value, and real reports a
        // cross-collation conflict (or a legacy-LOB argument, or an unknown
        // column) in either while compiling.
        if (this.searchedWhens is { } whens)
        {
            foreach (var when in whens)
                when.Bind(batch, resolveColumnType);
        }
        else
        {
            var inputType = this.input!.GetSqlType(batch, resolveColumnType);
            foreach (var compareValue in this.compareValues!)
                BooleanExpression.RequireResolvableCollation(inputType, compareValue.GetSqlType(batch, resolveColumnType), "equal to");
        }

        var arms = this.elseBranch is null ? this.thens : [.. this.thens, this.elseBranch];
        this.cachedResultType = PromoteValueArms(arms, batch, resolveColumnType);
        return this.cachedResultType;
    }

    internal override string DebugDisplay() => "CASE ...";

    // Non-null iff every surviving THEN and the ELSE (or the implicit
    // ELSE NULL) is non-null — proving exhaustive WHEN coverage is
    // intractable, so real's projection rule is the OR over arms.
    // "Surviving" is the constant fold real runs first: a branch whose
    // condition folds to a constant either becomes the whole answer (TRUE) or
    // drops out (FALSE / UNKNOWN), which is what makes
    // `CASE WHEN 1 = 1 THEN <not null col> END` NOT NULL despite carrying no
    // ELSE, and `CASE WHEN 1 = 2 THEN 5 END` nullable despite carrying no
    // nullable arm.
    // A surviving arm also answers for the conversion the branch unification
    // put on it (Expression.ArmConversionIsNullable).
    internal override bool ResultIsNullable(NullabilityContext context)
    {
        var promoted = context.TypeOf(this);
        for (var i = 0; i < this.thens.Length; i++)
        {
            if (TryFoldWhen(context, i, out var branchTaken))
            {
                if (branchTaken)
                    return this.thens[i].ResultIsNullable(context) || ArmConversionIsNullable(this.thens[i], promoted, context);
                continue;
            }

            if (this.thens[i].ResultIsNullable(context) || ArmConversionIsNullable(this.thens[i], promoted, context))
                return true;
        }
        // Missing ELSE = implicit NULL = nullable; explicit ELSE delegates.
        return this.elseBranch is null
            || this.elseBranch.ResultIsNullable(context)
            || ArmConversionIsNullable(this.elseBranch, promoted, context);
    }

    /// <summary>
    /// Folds branch <paramref name="index"/>'s condition when real would have:
    /// a searched form's whole predicate, or a simple form's implicit
    /// <c>input = compareValues[index]</c> over two constants.
    /// </summary>
    private bool TryFoldWhen(NullabilityContext context, int index, out bool branchTaken)
    {
        if (this.searchedWhens is { } whens)
            return context.TryFoldCondition(whens[index], out branchTaken);

        branchTaken = false;
        if (!context.TryFold(this.input!, out var inputValue)
            || !context.TryFold(this.compareValues![index], out var compareValue))
        {
            return false;
        }

        try
        {
            branchTaken = BooleanExpression.CompareValuesPromoted(
                inputValue, compareValue, "equal to", static (l, r) => l.Equals(r)) == true;
            return true;
        }
        catch (Exception e) when (e is SimulatedSqlException or NotSupportedException)
        {
            // An incomparable pair leaves the branch unfolded; the statement's
            // own evaluation is what reports it.
            return false;
        }
    }

    // Numeric-named if any value arm (a THEN or the ELSE) is — the same arm
    // set the result-type promotion walks; the WHEN conditions don't produce
    // the result value.
    internal override bool ResultReportsNumeric
    {
        get
        {
            foreach (var then in this.thens)
            {
                if (then.ResultReportsNumeric)
                    return true;
            }
            return this.elseBranch is not null && this.elseBranch.ResultReportsNumeric;
        }
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

        // Real folds a CASE whose conditions and arms are all constant, so
        // `ORDER BY CASE WHEN 1 = 1 THEN 1 ELSE 2 END` is Msg 408 while any
        // column, variable or subquery inside it sorts (probe-confirmed).
        // IIF reaches the same treatment through the built-in dispatcher.
        var savedFoldableArguments = context.FoldableArguments;
        context.FoldableArguments = true;
        try
        {
            var parsed = ParseCaseBody(context);
            if (context.FoldableArguments)
                parsed.FoldedOverConstantArguments = true;
            return parsed;
        }
        finally
        {
            context.FoldableArguments = savedFoldableArguments;
            context.CaseDepth--;
        }
    }

    /// <summary>
    /// Settles which arms real can see are unreachable while compiling, the
    /// step every rule below rides on. An arm is unreachable when its own
    /// condition folds to something other than TRUE, or when an earlier arm's
    /// folds to TRUE — and once any arm's does, every later arm and the ELSE go
    /// with it, whatever the arms before it did.
    /// <para>
    /// Returns whether the walk settled <em>every</em> condition it passed, so
    /// the caller can tell "the ELSE is the answer" (nothing decided TRUE, all
    /// decided) from "nothing decided yet". <paramref name="takenArm"/> is the
    /// arm real knows wins, or -1; <paramref name="elseDropped"/> is set once
    /// any arm decided TRUE.
    /// </para>
    /// </summary>
    private static bool DecideArms(
        Expression? input,
        List<BooleanExpression>? searchedWhens,
        List<Expression>? compareValues,
        ParserContext context,
        bool[] armDropped,
        out int takenArm,
        out bool elseDropped)
    {
        takenArm = -1;
        elseDropped = false;
        var inputIsConstant = false;
        SqlValue inputValue = default;
        if (input is not null)
            inputIsConstant = ConstantFolding.TryFold(input, context, out inputValue);

        var allDecided = true;
        for (var i = 0; i < armDropped.Length; i++)
        {
            if (elseDropped)
            {
                armDropped[i] = true;
                continue;
            }

            var condition = input is null
                ? ConstantFolding.TryFoldPredicate(searchedWhens![i], context, out var folded) ? folded == true : null
                : FoldSimpleCondition(inputIsConstant, inputValue, compareValues![i], context);
            if (condition is null)
            {
                allDecided = false;
            }
            else if (condition == true)
            {
                elseDropped = true;
                if (allDecided)
                    takenArm = i;
            }
            else
            {
                armDropped[i] = true;
            }
        }
        return allDecided;
    }

    /// <summary>
    /// Folds one simple-form <c>WHEN</c>'s implicit <c>input = compareValue</c>.
    /// A NULL constant on either side settles it UNKNOWN without the other side
    /// folding at all, which is how real settles
    /// <c>CASE &lt;bad&gt; WHEN CAST(NULL AS int) THEN … WHEN NULL THEN …</c>;
    /// one live compare value beside the NULL one leaves the input standing.
    /// </summary>
    private static bool? FoldSimpleCondition(bool inputIsConstant, SqlValue inputValue, Expression compareValue, ParserContext context)
    {
        if (inputIsConstant && inputValue.IsNull)
            return false;
        if (!ConstantFolding.TryFold(compareValue, context, out var candidate))
            return null;
        if (candidate.IsNull)
            return false;
        if (!inputIsConstant)
            return null;
        try
        {
            return BooleanExpression.CompareValuesPromoted(
                inputValue, candidate, "equal to", static (l, r) => l.Equals(r)) == true;
        }
        catch (Exception e) when (e is SimulatedSqlException or NotSupportedException)
        {
            // An incomparable pair leaves the arm standing; the statement's own
            // evaluation is what reports it.
            return null;
        }
    }

    /// <summary>
    /// Stops the aggregate pass evaluating an unreachable arm's aggregates.
    /// Real picks the arm while compiling and everything the arms it dropped
    /// carried goes with them — including an aggregate, which is otherwise
    /// evaluated per row whether or not the arm holding it can be reached
    /// (<c>SELECT CASE 23 WHEN -38 THEN COUNT(7 / 0) ELSE 2 END</c> answers 2
    /// on real, and does so even where the argument reads a column). The
    /// aggregate stays registered — see
    /// <see cref="AggregateExpression.OperandUnreachable"/> for why. The fence
    /// is that the arm's condition has to be settled while compiling: an arm
    /// real decides per row keeps its aggregates, so
    /// <c>CASE WHEN col = 1 THEN SUM(7 / 0) ELSE 2 END</c> raises there as it
    /// does here.
    /// </summary>
    /// <param name="collector">The enclosing query's aggregate list, or null outside one.</param>
    /// <param name="bounds">
    /// Collector counts sampled before each arm and before the ELSE, plus the
    /// count after the whole CASE — so entry <c>i</c> bounds arm <c>i</c>'s own
    /// registrations.
    /// </param>
    /// <param name="unreachable">Which of the <paramref name="bounds"/> intervals real settled as unreachable.</param>
    private static void MarkUnreachableAggregates(List<AggregateExpression>? collector, int[] bounds, bool[] unreachable)
    {
        if (collector is null)
            return;
        for (var i = 0; i < unreachable.Length; i++)
        {
            if (!unreachable[i])
                continue;
            for (var j = bounds[i]; j < bounds[i + 1]; j++)
                collector[j].OperandUnreachable = true;
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
        // Where each arm's — then the ELSE's — aggregate registrations start,
        // so the unreachable ones can be withdrawn once the arm walk settles.
        var aggregateBounds = new List<int>();

        while (context.Token is ReservedKeyword { Keyword: Keyword.When })
        {
            aggregateBounds.Add(context.AggregateCollector?.Count ?? 0);
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

        aggregateBounds.Add(context.AggregateCollector?.Count ?? 0);
        Expression? elseBranch = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Else })
        {
            context.MoveNextRequired();
            elseBranch = Expression.Parse(context);
        }
        aggregateBounds.Add(context.AggregateCollector?.Count ?? 0);

        // Real SQL Server fires Msg 8133 at compile time when every result
        // expression — every THEN body, plus the explicit ELSE if present
        // (absent ELSE = implicit NULL) — is a bare NULL literal. A typed
        // NULL (e.g. `CAST(NULL AS int)`) satisfies the rule because its
        // type isn't ambiguous. Probe-confirmed against SQL Server 2025.
        var anyTypedBranch = elseBranch is not null && !IsBareNullLiteral(elseBranch);
        for (var i = 0; !anyTypedBranch && i < thens.Count; i++)
            anyTypedBranch = !IsBareNullLiteral(thens[i]);

        if (context.Token is not ReservedKeyword { Keyword: Keyword.End })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (!anyTypedBranch)
            throw SimulatedSqlException.AllResultsInCaseAreNull();

        var armDropped = new bool[thens.Count];
        var allDecided = DecideArms(input, searchedWhensList, compareValuesList, context, armDropped, out var takenArm, out var elseDropped);
        bool[] unreachable = [.. armDropped, elseDropped];
        MarkUnreachableAggregates(context.AggregateCollector, [.. aggregateBounds], unreachable);

        var parsed = new CaseExpression(
            input,
            input is null ? [.. searchedWhensList!] : null,
            input is not null ? [.. compareValuesList!] : null,
            [.. thens],
            elseBranch,
            // Real drops the input and the compare values with the arms when it
            // can see none of them matches, and runs the ELSE alone.
            noArmReachable: input is not null && allDecided && takenArm < 0);
        // Real folds the whole CASE to a constant whenever the arm it settled
        // on is one — even where an arm it dropped reads a column or an
        // aggregate, which is what makes `ORDER BY CASE 1 WHEN 1 THEN 5 ELSE
        // col END` Msg 408 while `ORDER BY CASE 1 WHEN 1 THEN col ELSE 5 END`
        // sorts (both probe-confirmed).
        if (allDecided
            && (takenArm >= 0
                ? thens[takenArm].IsWrittenConstant
                : elseBranch is null || elseBranch.IsWrittenConstant))
        {
            parsed.FoldedOverConstantArguments = true;
        }
        return parsed;
    }
}
