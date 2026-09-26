using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;
using System.Collections.Frozen;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The compile-time constant folding SQL Server applies to an <c>ORDER BY</c>
/// term, and the rejections that ride on it: Msg 408 on a statement's ORDER BY
/// (see <c>Selection.ParseOrderByItems</c>), Msg 5308 / 5309 inside an
/// <c>OVER (ORDER BY …)</c> or <c>WITHIN GROUP (ORDER BY …)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both rejections read the same predicate — <see cref="Expression.IsWrittenConstant"/> —
/// and probing confirms the two paths agree cell for cell on what counts as
/// constant: every term real answers with Msg 408 on the statement path it
/// answers with Msg 5308 / 5309 in the window position, and every term it
/// sorts on one it sorts on the other.
/// </para>
/// <para>
/// <b>The foldable catalog is not the deterministic catalog.</b> Real folds a
/// call only when the intrinsic is in its own foldable list, which cuts across
/// determinism in both directions (every entry below probe-confirmed against
/// SQL Server 2025): <c>DATENAME</c> folds although
/// <c>OBJECTPROPERTY(…, 'IsDeterministic')</c> calls it nondeterministic,
/// while <c>UPPER</c>, <c>LOWER</c>, <c>QUOTENAME</c>, <c>STRING_ESCAPE</c>,
/// <c>HASHBYTES</c>, <c>COMPRESS</c>, <c>DECOMPRESS</c>, <c>ISJSON</c>,
/// <c>CHOOSE</c>, <c>ISNULL</c>, <c>PARSENAME</c>, <c>JSON_MODIFY</c>,
/// <c>JSON_ARRAY</c>, <c>JSON_OBJECT</c>, <c>SQL_VARIANT_PROPERTY</c>,
/// <c>FORMATMESSAGE</c> and <c>TRY_PARSE</c> are deterministic yet sort fine
/// over literal arguments. So <see cref="FoldedBuiltIns"/> is its own probed
/// list rather than the complement of
/// <c>ModuleDeterminism</c>'s nondeterministic set — an entry added without a
/// probe row risks rejecting a term real accepts, the one direction that
/// breaks a working query.
/// </para>
/// </remarks>
internal static class ConstantFolding
{
    /// <summary>
    /// The built-in scalar functions real SQL Server folds to a constant when
    /// every argument is itself written-constant, so that the folded term
    /// lands on Msg 408 / 5308 / 5309 in an ORDER BY position. Probed one call
    /// per name over literal arguments, on the statement ORDER BY and the
    /// <c>OVER (ORDER BY …)</c> path alike.
    /// </summary>
    /// <remarks>
    /// <c>CAST</c> / <c>TRY_CAST</c> / <c>CONVERT</c> / <c>TRY_CONVERT</c> /
    /// <c>COALESCE</c> appear here for catalog completeness even though their
    /// expression classes also answer structurally — those classes are
    /// constructed outside the built-in dispatcher too. <c>IIF</c> rides this
    /// list; <c>CASE</c> is folded at its own parser (it has no call syntax to
    /// dispatch through) and a <c>COLLATE</c> postfix over a constant folds
    /// structurally.
    /// </remarks>
    private static readonly FrozenSet<string> FoldedBuiltIns = new[]
    {
        "ABS",
        "ACOS",
        "ASCII",
        "ASIN",
        "ATAN",
        "ATN2",
        "BINARY_CHECKSUM",
        "BIT_COUNT",
        "CAST",
        "CEILING",
        "CHAR",
        "CHARINDEX",
        "CHECKSUM",
        "COALESCE",
        "CONCAT",
        "CONCAT_WS",
        "CONVERT",
        "COS",
        "COT",
        "DATALENGTH",
        "DATEADD",
        "DATEDIFF",
        "DATEDIFF_BIG",
        "DATEFROMPARTS",
        "DATENAME",
        "DATEPART",
        "DATETIME2FROMPARTS",
        "DATETIMEFROMPARTS",
        "DATETIMEOFFSETFROMPARTS",
        "DATETRUNC",
        "DATE_BUCKET",
        "DAY",
        "DEGREES",
        "DIFFERENCE",
        "EOMONTH",
        "EXP",
        "FLOOR",
        "GET_BIT",
        "GREATEST",
        "IIF",
        "ISNUMERIC",
        "JSON_PATH_EXISTS",
        "JSON_QUERY",
        "JSON_VALUE",
        "LEAST",
        "LEFT",
        "LEFT_SHIFT",
        "LEN",
        "LOG",
        "LOG10",
        "LTRIM",
        "MONTH",
        "NCHAR",
        "NULLIF",
        "PATINDEX",
        "PI",
        "POWER",
        "RADIANS",
        "REGEXP_COUNT",
        "REGEXP_INSTR",
        "REGEXP_REPLACE",
        "REGEXP_SUBSTR",
        "REPLACE",
        "REPLICATE",
        "REVERSE",
        "RIGHT",
        "RIGHT_SHIFT",
        "ROUND",
        "RTRIM",
        "SET_BIT",
        "SID_BINARY",
        "SIGN",
        "SIN",
        "SMALLDATETIMEFROMPARTS",
        "SOUNDEX",
        "SPACE",
        "SQRT",
        "SQUARE",
        "STR",
        "STUFF",
        "SUBSTRING",
        "SWITCHOFFSET",
        "TAN",
        "TIMEFROMPARTS",
        "TODATETIMEOFFSET",
        "TRANSLATE",
        "TRIM",
        "TRY_CAST",
        "TRY_CONVERT",
        "UNICODE",
        "YEAR",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> FoldedBuiltInLookup =
        FoldedBuiltIns.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>
    /// Whether real folds a call to <paramref name="uppercaseName"/> whose
    /// arguments are all written constants.
    /// </summary>
    internal static bool IsFoldedBuiltIn(ReadOnlySpan<char> uppercaseName) =>
        FoldedBuiltInLookup.Contains(uppercaseName);

    /// <summary>
    /// Whether <paramref name="expression"/> is a written constant real folds to
    /// <b>NULL</b> while compiling. Read by the two shapes whose comparison
    /// against that value can then match nothing — a simple <c>CASE</c>'s input
    /// and <c>NULLIF</c>'s first argument — so their remaining operands leave
    /// the tree with the comparison (probe-confirmed: <c>CASE CAST(NULL AS int)
    /// WHEN &lt;bad&gt; THEN …</c>, <c>CASE CAST(NULL AS int) / 17 WHEN …</c>,
    /// <c>CASE NULLIF(1, 1) WHEN …</c> and <c>NULLIF(-CAST(NULL AS real),
    /// &lt;bad&gt;)</c> all answer on real where the operand alone raises).
    /// <para>
    /// This is a strictly wider reading than <see cref="Expression.IsNullConstant"/>,
    /// which stays syntactic because real's <em>comparison</em> fold does — a
    /// folded-NULL operand there still raises the other side's error
    /// (<c>WHERE CAST(NULL AS int) / 17 &gt; &lt;overflowing expression&gt;</c>
    /// is Msg 8115 on real). The two rules are probed apart and stay apart.
    /// </para>
    /// <para>
    /// A fold that raises answers <see langword="false"/>, leaving the shape to
    /// report the error at runtime the way real does.
    /// </para>
    /// </summary>
    internal static bool FoldsToNull(Expression expression, ParserContext context) =>
        TryFold(expression, context, out var value) && value.IsNull;

    /// <summary>
    /// Evaluates <paramref name="expression"/> at compile time when real folds
    /// it there — <see cref="Expression.IsWrittenConstant"/> decides which
    /// shapes qualify, and a fold that <em>raises</em> answers
    /// <see langword="false"/> so the shape reports the error at runtime the
    /// way real does.
    /// </summary>
    internal static bool TryFold(Expression expression, ParserContext context, out SqlValue value)
    {
        if (expression.IsWrittenConstant)
        {
            try
            {
                // A written constant reaches no column, so the resolver is
                // unreachable rather than merely unused. Typing it first lets
                // a compile-time refusal (a JSON function's Msg 8116) stand in
                // for a Run that would otherwise meet a value it never checks.
                _ = expression.GetSqlType(context.Batch, static _ => throw new NotSupportedException());
                value = expression.Run(new RuntimeContext(static _ => throw new NotSupportedException(), context.Batch));
                return true;
            }
            catch (Exception e) when (e is SimulatedSqlException or NotSupportedException)
            {
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// The <see cref="BooleanExpression"/> counterpart of
    /// <see cref="TryFold(Expression, ParserContext, out SqlValue)"/>: evaluates
    /// a predicate real settles while compiling, reporting its three-valued
    /// result through <paramref name="value"/>. A predicate that isn't written
    /// constant — and a fold that raises, which real leaves standing for
    /// runtime — answers <see langword="false"/>, which callers must keep
    /// distinct from a fold that answered UNKNOWN.
    /// </summary>
    internal static bool TryFoldPredicate(BooleanExpression predicate, ParserContext context, out bool? value)
    {
        if (predicate.IsWrittenConstant)
        {
            try
            {
                // A written constant reaches no column, so the resolver is
                // unreachable rather than merely unused.
                value = predicate.Run(new RuntimeContext(static _ => throw new NotSupportedException(), context.Batch));
                return true;
            }
            catch (Exception e) when (e is SimulatedSqlException or NotSupportedException)
            {
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Adds to <paramref name="sink"/> the subexpressions of
    /// <paramref name="root"/> that SQL Server evaluates once when the plan
    /// starts rather than per row, and that can raise there: a written
    /// constant whose fold raises (<c>1/0</c>, <c>CAST('x' AS int)</c>,
    /// <c>POWER(2, 40)</c>), and a computation over variables and literals
    /// alone (<c>@x / 0</c>). Real raises such an error before reading a row,
    /// so it surfaces over an empty table and under a WHERE that excludes
    /// every row alike — probed 2026-09-26 against SQL Server 2025.
    /// </summary>
    /// <remarks>
    /// A branch real may never take stays per-row: the walk doesn't enter
    /// <c>CASE</c>, <c>IIF</c>, <c>COALESCE</c>, <c>NULLIF</c> or
    /// <c>CHOOSE</c>, nor an aggregate's or window function's operands
    /// (<c>SUM(1/0)</c> over no rows is NULL there), nor a subquery, which
    /// starts its own plan. A fold that succeeds can't raise at startup, so it
    /// isn't collected; a variable computation is, since its value is only
    /// known per execution. Only nodes known free of side effects qualify
    /// (<see cref="Expression.ParallelSafe"/>), so <c>RAND()</c>,
    /// <c>NEXT VALUE FOR</c> and a UDF call never run an extra time.
    /// </remarks>
    internal static void CollectStartupConstants(ExpressionNode root, ParserContext context, List<Expression> sink) =>
        root.Walk((node, _) =>
        {
            switch (node)
            {
                case CaseExpression or Iif or Coalesce or NullIf or Choose or AggregateExpression or WindowExpression:
                    return false;
                case Expression expression when expression.IsWrittenConstant:
                    if (FoldRaises(expression, context))
                        sink.Add(expression);
                    return false;
                case Expression expression when IsVariableComputation(expression):
                    sink.Add(expression);
                    return false;
                default:
                    return true;
            }
        });

    /// <summary>
    /// Whether evaluating the written constant <paramref name="expression"/>
    /// raises — which real, attempting the same fold while compiling, takes
    /// as "not a constant" and leaves for the plan to raise at runtime.
    /// </summary>
    internal static bool FoldRaises(Expression expression, ParserContext context)
    {
        try
        {
            // A written constant reaches no column, so the resolver is
            // unreachable rather than merely unused.
            _ = expression.Run(new RuntimeContext(static _ => throw new NotSupportedException(), context.Batch));
            return false;
        }
        catch (SimulatedSqlException)
        {
            return true;
        }
    }

    /// <summary>
    /// Whether <paramref name="expression"/> has one value for the whole
    /// statement that real computes as its plan starts — a written constant,
    /// a variable or parameter, or a computation over those alone (see
    /// <see cref="IsVariableComputation"/>) — so a DML write converts it to
    /// its target column there, before any row.
    /// </summary>
    internal static bool IsStartupValue(Expression expression) =>
        expression.IsWrittenConstant || expression is VariableReference || IsVariableComputation(expression);

    /// <summary>
    /// Whether <paramref name="expression"/> computes over variables and
    /// literals alone through side-effect-free nodes, with no conditional
    /// branch — so evaluating it once up front runs exactly what real's plan
    /// start runs. A bare variable or literal is excluded: it can't raise.
    /// </summary>
    private static bool IsVariableComputation(Expression expression)
    {
        if (expression is VariableReference or Value || !expression.ParallelSafe)
            return false;

        var readsVariable = false;
        var qualifies = true;
        expression.Walk((node, shape) =>
        {
            switch (node)
            {
                case CaseExpression or Iif or Coalesce or NullIf or Choose:
                    qualifies = false;
                    break;
                case VariableReference:
                    readsVariable = true;
                    break;
                default:
                    if (shape.Column is not null)
                        qualifies = false;
                    break;
            }
            return qualifies;
        });
        return qualifies && readsVariable;
    }

    /// <summary>
    /// Applies real's Msg 5308 / 5309 gate to one ORDER BY term inside an
    /// <c>OVER (…)</c>, a named <c>WINDOW</c> definition or a
    /// <c>WITHIN GROUP (…)</c> — the positions that carry no ordinal
    /// semantics, so a folded constant has nothing to name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Which of the two messages fires is decided on the folded value, not on
    /// the written shape: an <c>int</c> that could pass for a column index
    /// (<c>1</c>, <c>1 + 1</c>, <c>ABS(-1)</c>, <c>LEN('abc')</c>, <c>300</c>)
    /// is Msg 5308, and everything else — a string, a <c>NULL</c>, a
    /// non-<c>int</c> numeric such as <c>1.5</c> / <c>CAST(1 AS bigint)</c> /
    /// <c>CAST(1 AS tinyint)</c>, or an <c>int</c> that isn't a plausible
    /// index (<c>0</c>, <c>-1</c>, <c>1 - 2</c>) — is Msg 5309. Real applies
    /// no range check against the select list: <c>OVER (ORDER BY 100)</c> over
    /// a one-column select is Msg 5308 all the same (probe-confirmed).
    /// </para>
    /// <para>
    /// A fold that raises is no rejection: real accepts
    /// <c>OVER (ORDER BY 1/0)</c>, <c>OVER (ORDER BY CAST('a' AS int))</c> and
    /// <c>OVER (ORDER BY POWER(CAST(2 AS int), 40))</c>, and in an <c>OVER</c>
    /// clause never evaluates the key at all — it returns the rows unraised,
    /// as it does for <c>PARTITION BY 1/0</c> (probed 2026-09-26) — so the
    /// returned term is a stand-in constant the sort reads instead. A
    /// <c>WITHIN GROUP</c> key is the aggregated value's order and real does
    /// evaluate it there, so that caller keeps its own term.
    /// </para>
    /// </remarks>
    internal static Expression RejectConstantWindowOrderByTerm(Expression term, ParserContext context)
    {
        if (!term.IsWrittenConstant)
            return term;

        SqlValue folded;
        try
        {
            // A written constant reaches no column, so the resolver is
            // unreachable rather than merely unused.
            folded = term.Run(new RuntimeContext(static _ => throw new NotSupportedException(), context.Batch));
        }
        catch (SimulatedSqlException)
        {
            return NeverEvaluatedKey();
        }

        // A NULL is never index-shaped here, matching real for a written
        // `NULL` and for `CAST(NULL AS int)`. Real does answer Msg 5308 for an
        // int-typed NULL that a TRY_ conversion produced — its index test is a
        // "not less than one" comparison, which NULL leaves UNKNOWN — but the
        // simulator can't tell those NULLs apart from the untyped one, so it
        // reports 5309 for the whole family (see docs/claude/query.md).
        throw !folded.IsNull && folded.Type == SqlType.Int32 && folded.AsInt32 >= 1
            ? SimulatedSqlException.IntegerIndexNotAllowedInOrderedAggregate()
            : SimulatedSqlException.ConstantNotAllowedInOrderedAggregate();
    }

    /// <summary>
    /// A window key standing in for one whose fold raises: a constant, so it
    /// leaves the order as it was, and evaluated with no error, as real
    /// leaves such a key unevaluated. Fresh per call, since a node belongs to
    /// one tree.
    /// </summary>
    internal static Expression NeverEvaluatedKey() => Value.NonLiteral(SqlValue.Null(SqlType.Int32));

    /// <summary>
    /// The window-clause <c>PARTITION BY</c> counterpart of
    /// <see cref="RejectConstantWindowOrderByTerm"/>: a constant partition key
    /// is legal, and one whose fold raises is left unevaluated as real leaves
    /// it (see <see cref="NeverEvaluatedKey"/>).
    /// </summary>
    internal static Expression SettleWindowPartitionTerm(Expression term, ParserContext context) =>
        term.IsWrittenConstant && FoldRaises(term, context) ? NeverEvaluatedKey() : term;
}
