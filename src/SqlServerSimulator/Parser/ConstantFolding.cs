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
                // unreachable rather than merely unused.
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
    /// A fold that raises leaves the term standing: real sorts
    /// <c>OVER (ORDER BY 1/0)</c>, <c>OVER (ORDER BY CAST('a' AS int))</c> and
    /// <c>OVER (ORDER BY POWER(CAST(2 AS int), 40))</c> rather than reporting
    /// the folding error (probe-confirmed — the statement-level ORDER BY does
    /// the opposite and reports Msg 8134 / 245 / 232).
    /// </para>
    /// </remarks>
    internal static void RejectConstantWindowOrderByTerm(Expression term, ParserContext context)
    {
        if (!term.IsWrittenConstant)
            return;

        SqlValue folded;
        try
        {
            // A written constant reaches no column, so the resolver is
            // unreachable rather than merely unused.
            folded = term.Run(new RuntimeContext(static _ => throw new NotSupportedException(), context.Batch));
        }
        catch (SimulatedSqlException)
        {
            return;
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
}
