using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

partial class SimulatedSqlException
{
    /// <summary>
    /// Mimics the SqlException that occurs then when a TOP/OFFSET/FETCH clause has an inappropriate column reference.
    /// </summary>
    /// <param name="name">The name of the column.</param>
    /// <returns>The exception.</returns>
    internal static SimulatedSqlException ColumnReferenceNotAllowed(MultiPartName name)
        => new($"The reference to column \"{name}\" is not allowed in an argument to a TOP, OFFSET, or FETCH clause. Only references to columns at an outer scope or standalone expressions and subqueries are allowed here.", 4115, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 205 — fired when the branches of a UNION /
    /// INTERSECT / EXCEPT chain have different column counts.
    /// </summary>
    internal static SimulatedSqlException SetOpUnequalColumnCount() =>
        new("All queries combined using a UNION, INTERSECT or EXCEPT operator must have an equal number of expressions in their target lists.", 205, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 156 specifically for the per-branch
    /// ORDER BY rejection in set-op chains. The keyword in the message
    /// is the set operator that follows the offending ORDER BY.
    /// </summary>
    internal static SimulatedSqlException PerBranchOrderByRejected(string setOpKeyword) =>
        new($"Incorrect syntax near the keyword '{setOpKeyword}'.", 156, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 116 — fired when an IN(SELECT) subquery
    /// projects more than one column. EXISTS doesn't trip this; only
    /// constructs that need a single value per row do.
    /// </summary>
    internal static SimulatedSqlException SubqueryNotIntroducedWithExists() =>
        new("Only one expression can be specified in the select list when the subquery is not introduced with EXISTS.", 116, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 512 — fired when a scalar subquery (or one
    /// behind a comparison operator) returns more than one row. Verbatim
    /// text from the probed real SQL Server, including the literal extra
    /// space in the <c>&lt;= , &gt;</c> sequence.
    /// </summary>
    internal static SimulatedSqlException SubqueryReturnedMoreThanOneValue() =>
        new("Subquery returned more than 1 value. This is not permitted when the subquery follows =, !=, <, <= , >, >= or when the subquery is used as an expression.", 512, 16, 1);

    /// <summary>
    /// Mimics the SqlException that occurs then when a TOP or FETCH clause returns something other than an integer.
    /// </summary>
    /// <returns>The exception.</returns>
    internal static SimulatedSqlException TopFetchRequiresInteger() => new("The number of rows provided for a TOP or FETCH clauses row count parameter must be an integer.", 1060, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 153 — fired when a FETCH clause appears
    /// without a preceding OFFSET (FETCH alone is invalid; OFFSET must
    /// always come first). Wording verbatim from the probed server.
    /// </summary>
    internal static SimulatedSqlException FetchInvalidUsageWithoutOffset() =>
        new("Invalid usage of the option next in the FETCH statement.", 153, 15, 2);

    /// <summary>
    /// Mimics SQL Server's Msg 10741 — fired when both <c>TOP</c> and
    /// <c>OFFSET</c> appear on the same SELECT (or subquery). Wording
    /// verbatim, including the "can not" two-word form and the
    /// non-grammatical "a OFFSET" article.
    /// </summary>
    internal static SimulatedSqlException TopAndOffsetMutuallyExclusive() =>
        new("A TOP can not be used in the same query or sub-query as a OFFSET.", 10741, 15, 2);

    /// <summary>
    /// Mimics SQL Server's Msg 10742 — fired when an <c>OFFSET</c> clause
    /// resolves to a negative value at execution time. Wording verbatim,
    /// including the non-grammatical "a OFFSET" article.
    /// </summary>
    internal static SimulatedSqlException OffsetMustNotBeNegative() =>
        new("The offset specified in a OFFSET clause may not be negative.", 10742, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 10744 — fired when a <c>FETCH</c> clause
    /// resolves to zero or a negative value at execution time. Wording
    /// verbatim, including the literal "greater then zero" typo from the
    /// real server's error catalog.
    /// </summary>
    internal static SimulatedSqlException FetchMustBeGreaterThanZero() =>
        new("The number of rows provided for a FETCH clause must be greater then zero.", 10744, 15, 1);

    internal static SimulatedSqlException UnrecognizedBuiltInFunction(string name) => new($"'{name}' is not a recognized built-in function name.", 195, 15, 10);

    /// <summary>
    /// Mimics SQL Server's Msg 174 — fired when a built-in function is called
    /// with the wrong number of arguments (e.g. <c>ISNULL(x)</c> or
    /// <c>ISNULL(a, b, c)</c>). The function name is rendered lowercase in
    /// the message regardless of source casing — probe-confirmed.
    /// </summary>
    internal static SimulatedSqlException FunctionRequiresNArguments(string functionLowerName, int argumentCount) =>
        new($"The {functionLowerName} function requires {argumentCount} argument(s).", 174, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 155: the first argument to <c>DATEPART</c> /
    /// <c>DATEADD</c> / <c>DATEDIFF</c> / etc. wasn't a recognized datepart
    /// keyword (year / month / day / hour / minute / second / etc.). The
    /// message embeds the calling function's name verbatim
    /// (<c>"... is not a recognized datepart option."</c> for <c>DATEPART</c>,
    /// <c>"... datediff_big option."</c> for <c>DATEDIFF_BIG</c>, etc.) —
    /// probed against SQL Server 2025 (2026-05-08).
    /// </summary>
    internal static SimulatedSqlException NotARecognizedDatepartOption(string keyword, string functionLowerName) =>
        new($"'{keyword}' is not a recognized {functionLowerName} option.", 155, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 9810: a datepart keyword is incompatible with
    /// the date-family argument's data type (e.g. <c>DATEPART(hour, dateCol)</c>
    /// against a <c>date</c> column has no time component to extract).
    /// </summary>
    internal static SimulatedSqlException DatepartNotSupportedForType(string datepart, string function, string typeName) =>
        new($"The datepart {datepart} is not supported by date function {function} for data type {typeName}.", 9810, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 9806: a datepart keyword is unconditionally
    /// rejected by a function regardless of operand type. Probe-confirmed
    /// against SQL Server 2025 (2026-05-08): <c>DATEDIFF</c> / <c>DATEDIFF_BIG</c>
    /// reject <c>tzoffset</c> and <c>iso_week</c> with this message, with no
    /// trailing "for data type X" clause.
    /// </summary>
    internal static SimulatedSqlException DatepartNotSupportedForFunction(string datepart, string functionLowerName) =>
        new($"The datepart {datepart} is not supported by date function {functionLowerName}.", 9806, 16, 0);

    /// <summary>
    /// Mimics SQL Server error 517: <c>DATEADD</c>'s output value falls
    /// outside the date/time type's representable range. The type-name slot
    /// is the *input* column's type (e.g. <c>'date'</c>), not the abstract
    /// SQL-server type family — verified by probe.
    /// </summary>
    internal static SimulatedSqlException DateAddOverflow(string typeName) =>
        new($"Adding a value to a '{typeName}' column caused an overflow.", 517, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 535: <c>DATEDIFF</c> / <c>DATEDIFF_BIG</c>
    /// produced a value outside the result type's range (int for DATEDIFF,
    /// bigint for DATEDIFF_BIG). The function name appears twice in the
    /// message — first naming the function that overflowed, then in the
    /// "Try to use {fn} with a less precise datepart" remediation hint.
    /// Probe-confirmed against SQL Server 2025 (2026-05-08): Class 16, State 0.
    /// </summary>
    internal static SimulatedSqlException DateDiffOverflow(string functionLowerName) =>
        new($"The {functionLowerName} function resulted in an overflow. The number of dateparts separating two date/time instances is too large. Try to use {functionLowerName} with a less precise datepart.", 535, 16, 0);

    /// <summary>
    /// Mimics SQL Server error 506: the <c>ESCAPE</c> clause of a <c>LIKE</c>
    /// predicate received a value that wasn't exactly one character (empty,
    /// multi-char). The displayed value is whatever the expression evaluated
    /// to; SQL Server quotes it with double quotes in the message.
    /// </summary>
    internal static SimulatedSqlException InvalidEscapeCharacter(string value) =>
        new($"The invalid escape character \"{value}\" was specified in a LIKE predicate.", 506, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 145: an <c>ORDER BY</c> item references a
    /// column or expression that isn't in the SELECT list when
    /// <c>SELECT DISTINCT</c> is specified. The post-DISTINCT row stream no
    /// longer carries the source column, so the reference would be
    /// ambiguous; SQL Server rejects at parse time with this fixed text.
    /// </summary>
    internal static SimulatedSqlException OrderByItemNotInSelectListWithDistinct() =>
        new("ORDER BY items must appear in the select list if SELECT DISTINCT is specified.", 145, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 306: a <c>text</c>, <c>ntext</c>, or
    /// <c>image</c> column appeared in a context that requires comparison or
    /// sorting (ORDER BY, GROUP BY, DISTINCT, or as an operand the simulator
    /// would otherwise route to <see cref="IComparable"/>). <c>LIKE</c> and
    /// <c>IS NULL</c>/<c>IS NOT NULL</c> are exempt and dispatch through
    /// their dedicated paths before this check fires.
    /// </summary>
    internal static SimulatedSqlException LobTypesCannotBeComparedOrSorted() =>
        new("The text, ntext, and image data types cannot be compared or sorted, except when using IS NULL or LIKE operator.", 306, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 120: <c>INSERT … SELECT</c> whose source
    /// projects fewer columns than the destination's column list (explicit
    /// or implied). Distinct from Msg 121 (the "more items" variant).
    /// </summary>
    internal static SimulatedSqlException InsertSelectListFewerThanInsertList() =>
        new("The select list for the INSERT statement contains fewer items than the insert list. The number of SELECT values must match the number of INSERT columns.", 120, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 121: <c>INSERT … SELECT</c> whose source
    /// projects more columns than the destination's column list (explicit
    /// or implied). Distinct from Msg 120 (the "fewer items" variant).
    /// </summary>
    internal static SimulatedSqlException InsertSelectListMoreThanInsertList() =>
        new("The select list for the INSERT statement contains more items than the insert list. The number of SELECT values must match the number of INSERT columns.", 121, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 239: a <c>WITH</c> prefix declares two or more
    /// CTEs with the same name in one statement.
    /// </summary>
    internal static SimulatedSqlException DuplicateCteName(string name) =>
        new($"Duplicate common table expression name '{name}' was specified.", 239, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8158: a CTE's column-rename list has fewer
    /// names than the body's projection produces. Counterpart to Msg 8159.
    /// </summary>
    internal static SimulatedSqlException CteHasMoreColumnsThanList(string name) =>
        new($"'{name}' has more columns than were specified in the column list.", 8158, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8159: a CTE's column-rename list has more
    /// names than the body's projection produces. Counterpart to Msg 8158.
    /// </summary>
    internal static SimulatedSqlException CteHasFewerColumnsThanList(string name) =>
        new($"'{name}' has fewer columns than were specified in the column list.", 8159, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 240: anchor and recursive parts of a recursive
    /// CTE produce different per-column types. Recursive CTEs require strict
    /// type equality (no Promote-style widening), unlike regular UNION ALL.
    /// </summary>
    internal static SimulatedSqlException RecursiveCteTypeMismatch(string columnName, string cteName) =>
        new($"Types don't match between the anchor and the recursive part in column \"{columnName}\" of recursive query \"{cteName}\".", 240, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 247: an anchor branch (no self-reference)
    /// appears after a recursive branch (self-reference) in a recursive CTE's
    /// UNION ALL chain. All anchors must precede all recursive branches.
    /// </summary>
    internal static SimulatedSqlException AnchorAfterRecursive(string cteName) =>
        new($"An anchor member was found in the recursive part of recursive query \"{cteName}\".", 247, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 252: a recursive CTE doesn't have a top-level
    /// <c>UNION ALL</c> operator. Fires when the body has a self-reference
    /// without UNION ALL splitting it from an anchor, or when a UNION (dedupe)
    /// is used instead of UNION ALL.
    /// </summary>
    internal static SimulatedSqlException RecursiveCteMissingUnionAll(string cteName) =>
        new($"Recursive common table expression '{cteName}' does not contain a top-level UNION ALL operator.", 252, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 253: a single recursive branch references the
    /// CTE more than once. Each recursive branch must contain exactly one
    /// self-reference.
    /// </summary>
    internal static SimulatedSqlException RecursiveCteMultipleReferences(string cteName) =>
        new($"Recursive member of a common table expression '{cteName}' has multiple recursive references.", 253, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 530: the recursive CTE's iteration count
    /// exceeded MAXRECURSION. The literal <paramref name="limit"/> appears
    /// in the message — overrides via <c>OPTION (MAXRECURSION N)</c> are
    /// reflected. <c>MAXRECURSION 0</c> disables the check.
    /// </summary>
    internal static SimulatedSqlException MaxRecursionExceeded(int limit) =>
        new($"The statement terminated. The maximum recursion {limit} has been exhausted before statement completion.", 530, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1033: <c>ORDER BY</c> appears inside a CTE
    /// body without an accompanying <c>TOP</c> / <c>OFFSET</c> / <c>FOR XML</c>.
    /// Real SQL Server's wording lists the broader set of contexts (views,
    /// inline functions, derived tables, subqueries, common table expressions);
    /// the simulator enforces the rule for CTE bodies only.
    /// </summary>
    internal static SimulatedSqlException OrderByInvalidInCte() =>
        new("The ORDER BY clause is invalid in views, inline functions, derived tables, subqueries, and common table expressions, unless TOP, OFFSET or FOR XML is also specified.", 1033, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 108: a positional <c>ORDER BY</c> ordinal
    /// (e.g. <c>order by 0</c>, <c>order by 5</c> with only 3 columns) is
    /// outside the projection's column count. The validation is 1-based.
    /// </summary>
    internal static SimulatedSqlException OrderByPositionOutOfRange(int position) =>
        new($"The ORDER BY position number {position} is out of range of the number of items in the select list.", 108, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 4108 — a windowed (OVER) function appeared
    /// somewhere other than the SELECT projection or ORDER BY: WHERE, HAVING,
    /// GROUP BY, ON, etc.
    /// </summary>
    internal static SimulatedSqlException WindowedFunctionInWrongClause() =>
        new("Windowed functions can only appear in the SELECT or ORDER BY clauses.", 4108, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 10759 — <c>DISTINCT</c> isn't allowed in
    /// combination with the <c>OVER</c> clause (e.g. <c>COUNT(DISTINCT x) OVER (...)</c>).
    /// </summary>
    internal static SimulatedSqlException DistinctNotAllowedInOver() =>
        new("Use of DISTINCT is not allowed with the OVER clause.", 10759, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 4113 — an aggregate that has no windowed form
    /// (currently <c>STRING_AGG</c>) was used with <c>OVER</c>.
    /// </summary>
    internal static SimulatedSqlException FunctionNotValidForOver(string functionLowerName) =>
        new($"The function '{functionLowerName}' is not a valid windowing function, and cannot be used with the OVER clause.", 4113, 15, 4);

    /// <summary>
    /// Mimics SQL Server's Msg 9819 — <c>NTILE(N)</c> requires <c>N</c> to be
    /// a positive number; raised at runtime when the bucket-count expression
    /// evaluates to zero or negative.
    /// </summary>
    internal static SimulatedSqlException NTileBucketCountMustBePositive() =>
        new("The function 'NTILE' must have a positive integer value.", 9819, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 10757 — a non-ordered-set aggregate (anything
    /// other than <c>STRING_AGG</c> in this simulator's surface) was given a
    /// <c>WITHIN GROUP (ORDER BY ...)</c> clause. Function name is
    /// SQL-lowercase (<c>max</c>, <c>sum</c>, etc.).
    /// </summary>
    internal static SimulatedSqlException FunctionMayNotHaveWithinGroup(string functionLowerName) =>
        new($"The function '{functionLowerName}' may not have a WITHIN GROUP clause.", 10757, 15, 9);

    /// <summary>
    /// Mimics SQL Server's Msg 5308 — windowed/aggregate ORDER BY rejects
    /// integer-ordinal expressions (e.g. <c>STRING_AGG(x, ',') WITHIN GROUP
    /// (ORDER BY 1)</c>). The projection-level ORDER BY accepts ordinals;
    /// these inner ORDER BY positions don't.
    /// </summary>
    internal static SimulatedSqlException IntegerIndexNotAllowedInOrderedAggregate() =>
        new("Windowed functions, aggregates and NEXT VALUE FOR functions do not support integer indices as ORDER BY clause expressions.", 5308, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 8133 — every result expression in a
    /// <c>CASE</c> specification is a bare <c>NULL</c> literal. SQL Server
    /// requires at least one branch (THEN body or explicit ELSE) to be a
    /// typed expression so the result type can be inferred. An absent ELSE
    /// is treated as implicit NULL — so a single <c>WHEN cond THEN NULL</c>
    /// with no ELSE also fires Msg 8133. Probe-confirmed against SQL Server
    /// 2025 (2026-05-11): Class 16, State 1, verbatim wording. Also fires
    /// for <c>IIF(cond, NULL, NULL)</c> (real SQL Server desugars IIF to
    /// CASE); <c>COALESCE(NULL, NULL)</c> takes a different path (Msg 4127)
    /// and <c>NULLIF</c> a third (Msg 4151).
    /// </summary>
    internal static SimulatedSqlException AllResultsInCaseAreNull() =>
        new("At least one of the result expressions in a CASE specification must be an expression other than the NULL constant.", 8133, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 148 — the string operand of <c>WAITFOR DELAY</c>
    /// (or <c>WAITFOR TIME</c>, not modeled) wasn't a valid time format. Probe-
    /// confirmed against SQL Server 2025 (2026-05-11): Class 15, State 1,
    /// verbatim wording. Valid format is <c>HH:MM:SS[.fff]</c> with hours
    /// 0-23 and no leading sign. Negative, day-component, and over-24h
    /// strings all surface this same error.
    /// </summary>
    internal static SimulatedSqlException IncorrectWaitForTimeSyntax(string value) =>
        new($"Incorrect time syntax in time string '{value}' used with WAITFOR.", 148, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 9815 — the operand of <c>WAITFOR DELAY</c>
    /// (or <c>WAITFOR TIME</c>) is a variable typed as <c>time</c>. Real
    /// SQL Server reserves the operand for char/varchar/nchar/nvarchar
    /// values; the <c>time</c> type is paradoxically rejected. Probe-
    /// confirmed against SQL Server 2025 (2026-05-11): Class 16, State 0,
    /// verbatim wording.
    /// </summary>
    internal static SimulatedSqlException WaitForCannotBeTimeType() =>
        new("Waitfor delay and waitfor time cannot be of type time.", 9815, 16, 0);

    /// <summary>
    /// Mimics SQL Server's Msg 8141 — an inline column-level CHECK constraint
    /// (i.e. one written next to a column definition rather than at the
    /// table level) references a column other than its owning column. Real
    /// SQL Server enforces a "one-column scope" rule on inline CHECKs:
    /// they may only reference the column they're attached to. Probe-confirmed
    /// against SQL Server 2025 (2026-05-11): Class 16, State 0, first-line
    /// wording verbatim (real SQL Server appends a second "Could not create
    /// constraint or index. See previous errors." sentence which the simulator
    /// doesn't model; apps that string-match the error read the first line).
    /// </summary>
    internal static SimulatedSqlException InlineCheckReferencesAnotherColumn(string owningColumn, string tableName) =>
        new($"Column CHECK constraint for column '{owningColumn}' references another column, table '{tableName}'.", 8141, 16, 0);

    /// <summary>
    /// Mimics SQL Server's Msg 4701 — <c>TRUNCATE TABLE</c> against a name
    /// that doesn't resolve to a table. Distinct from <c>DROP TABLE</c>'s
    /// Msg 3701 and from generic INSERT/UPDATE/DELETE's Msg 208: TRUNCATE
    /// has its own error path. Probe-confirmed against SQL Server 2025
    /// (2026-05-11): Class 16, State 1, verbatim wording (the
    /// <c>"or you do not have permissions"</c> suffix is part of the
    /// real-server message and is preserved here).
    /// </summary>
    internal static SimulatedSqlException CannotTruncateObjectDoesNotExist(string name) =>
        new($"Cannot find the object \"{name}\" because it does not exist or you do not have permissions.", 4701, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 214 — fired by <c>STRING_SPLIT</c> when the
    /// separator argument is NULL, empty, or multi-character (probe-confirmed
    /// against SQL Server 2025: class 16 state 11, verbatim wording referring
    /// to the parameter as <c>'separator'</c> of type <c>nchar(1)/nvarchar(1)</c>).
    /// </summary>
    internal static SimulatedSqlException StringSplitSeparatorMustBeSingleChar() =>
        new("Procedure expects parameter 'separator' of type 'nchar(1)/nvarchar(1)'.", 214, 16, 11);

    /// <summary>
    /// Mimics SQL Server's Msg 4199 — fired by <c>STRING_SPLIT</c> when the
    /// optional third argument (<c>enable_ordinal</c>) is something other than
    /// 0 / 1 / NULL. Wording is verbatim from the probe; the message echoes
    /// both the offending value and the argument index.
    /// </summary>
    internal static SimulatedSqlException StringSplitInvalidEnableOrdinal(long value) =>
        new($"Argument value {value.ToString(System.Globalization.CultureInfo.InvariantCulture)} is invalid for argument 3 of string_split function.", 4199, 16, 1);
}
