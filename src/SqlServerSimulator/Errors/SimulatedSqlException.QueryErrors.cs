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
    /// Mimics SQL Server's Msg 130 — an aggregate whose argument expression
    /// contains another aggregate or a subquery at any depth
    /// (<c>SUM(MAX(a))</c>, <c>MAX(CASE WHEN EXISTS(…) THEN a END)</c>).
    /// A <em>windowed</em> aggregate over an aggregate is legal and must not
    /// reach this (<c>SUM(SUM(b)) OVER ()</c> returns a value on real).
    /// Class 15 — a bind-time error, unlike the class-16 Msg 8120 family.
    /// </summary>
    internal static SimulatedSqlException AggregateOnAggregateOrSubquery() =>
        new("Cannot perform an aggregate function on an expression containing an aggregate or a subquery.", 130, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 144 — an aggregate or subquery appears in a
    /// GROUP BY list expression (<c>GROUP BY MAX(a)</c>,
    /// <c>GROUP BY (SELECT …)</c>). Takes precedence over
    /// <see cref="GroupByExpressionHasNoLocalColumn"/>: a correlated subquery
    /// grouping item reports this even though it does reference a local column.
    /// </summary>
    internal static SimulatedSqlException AggregateOrSubqueryInGroupBy() =>
        new("Cannot use an aggregate or a subquery in an expression used for the group by list of a GROUP BY clause.", 144, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 164 — a GROUP BY item that references no column
    /// of the current query's own sources (<c>GROUP BY 1</c>,
    /// <c>GROUP BY GETDATE()</c>, <c>GROUP BY @v</c>, or an item naming only an
    /// outer reference). The rule is purely about column presence, not
    /// determinism: <c>GROUP BY a + DATEPART(year, GETDATE())</c> is legal
    /// because it contains <c>a</c>. Checked per item, so one offending
    /// expression fails the statement even beside a valid one.
    /// </summary>
    internal static SimulatedSqlException GroupByExpressionHasNoLocalColumn() =>
        new("Each GROUP BY expression must contain at least one column that is not an outer reference.", 164, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 8120 — a column in the SELECT list of an
    /// aggregate query (any aggregate, GROUP BY, or HAVING is present) that is
    /// neither inside an aggregate nor a GROUP BY item. The name is
    /// source-qualified (<c>t.b</c>), matching the two-part <c>%.*ls.%.*ls</c>
    /// template. SQL Server is strict here — no functional-dependency
    /// relaxation — and binds it at parse time, before any row is read.
    /// </summary>
    internal static SimulatedSqlException ColumnNotInGroupByForSelect(string qualifiedColumn) =>
        new($"Column '{qualifiedColumn}' is invalid in the select list because it is not contained in either an aggregate function or the GROUP BY clause.", 8120, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 8121 — the HAVING-clause counterpart to
    /// <see cref="ColumnNotInGroupByForSelect"/>.
    /// </summary>
    internal static SimulatedSqlException ColumnNotInGroupByForHaving(string qualifiedColumn) =>
        new($"Column '{qualifiedColumn}' is invalid in the HAVING clause because it is not contained in either an aggregate function or the GROUP BY clause.", 8121, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 8127 — the ORDER BY counterpart to
    /// <see cref="ColumnNotInGroupByForSelect"/>. This message double-quotes the
    /// column name where the SELECT / HAVING forms single-quote it.
    /// </summary>
    internal static SimulatedSqlException ColumnNotInGroupByForOrderBy(string qualifiedColumn) =>
        new($"Column \"{qualifiedColumn}\" is invalid in the ORDER BY clause because it is not contained in either an aggregate function or the GROUP BY clause.", 8127, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 8161 — raised when an argument to
    /// <c>GROUPING()</c> or <c>GROUPING_ID()</c> doesn't match any
    /// expression in the surrounding query's GROUP BY clause (or when the
    /// function is used outside a GROUP BY context entirely). The function
    /// name appears verbatim in the message (probe-confirmed: <c>GROUPING</c>
    /// uses uppercase, <c>GROUPING_ID</c> uses the underscored form).
    /// </summary>
    internal static SimulatedSqlException GroupingArgumentNotInGroupBy(int argumentIndex, string functionName = "GROUPING") =>
        new($"Argument {argumentIndex} of the {functionName} function does not match any of the expressions in the GROUP BY clause.", 8161, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 8156 — the same output column name given twice
    /// for one table source. Raised by a PIVOT whose <c>IN</c> list repeats a
    /// value (each becomes an output column) and by a derived table whose
    /// column-alias list repeats a name. <paramref name="alias"/> is the alias
    /// given to that source.
    /// </summary>
    internal static SimulatedSqlException ColumnSpecifiedMultipleTimes(string column, string alias) =>
        new($"The column '{column}' was specified multiple times for '{alias}'.", 8156, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 8167 — raised when the columns in an
    /// UNPIVOT's <c>IN</c> list don't all share one type (UNPIVOT folds them
    /// into a single value column, so their types must match). The
    /// conflicting column name is double-quoted in the message.
    /// </summary>
    internal static SimulatedSqlException UnpivotColumnTypeConflict(string column) =>
        new($"The type of column \"{column}\" conflicts with the type of other columns specified in the UNPIVOT list.", 8167, 16, 1);

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
    /// Mimics SQL Server's Msg 127 — a <c>TOP (n)</c> / <c>FETCH</c> row-count
    /// value resolved to a negative number. Reached by DML <c>TOP</c> (the
    /// simulator's SELECT <c>TOP</c> path doesn't range-check). Wording verbatim.
    /// </summary>
    internal static SimulatedSqlException TopRowCountMustNotBeNegative() =>
        new("A TOP N or FETCH rowcount value may not be negative.", 127, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 1031 — a <c>TOP (n) PERCENT</c> value fell
    /// outside the 0–100 range. Wording verbatim.
    /// </summary>
    internal static SimulatedSqlException TopPercentOutOfRange() =>
        new("Percent values must be between 0 and 100.", 1031, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 1014 — a <c>TOP</c> / <c>FETCH</c> clause
    /// resolved to an invalid value (NULL for the <c>PERCENT</c> form).
    /// Wording verbatim.
    /// </summary>
    internal static SimulatedSqlException TopClauseInvalidValue() =>
        new("A TOP or FETCH clause contains an invalid value.", 1014, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 1062 — <c>TOP N WITH TIES</c> used without a
    /// corresponding ORDER BY clause. Wording verbatim.
    /// </summary>
    internal static SimulatedSqlException TopWithTiesRequiresOrderBy() =>
        new("The TOP N WITH TIES clause is not allowed without a corresponding ORDER BY clause.", 1062, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 5362 — a bare <c>OVER w</c> reference named a
    /// window with no matching <c>WINDOW w AS (…)</c> definition. Wording
    /// verbatim (SQL Server 2022+), Level 15 State 3.
    /// </summary>
    internal static SimulatedSqlException WindowIsUndefined(string windowName) =>
        new($"Window '{windowName}' is undefined.", 5362, 15, 3);

    /// <summary>
    /// Mimics SQL Server's Msg 4123 — an <c>OVER (w …)</c> refinement supplied
    /// an element (<c>PARTITION BY</c> / <c>ORDER BY</c> / frame) the named
    /// window it refines already carries. Wording verbatim, including the
    /// two-word "can not". Probe-confirmed against SQL Server 2025
    /// (2026-08-01): Class 15, and the state tracks the *referenced* window
    /// rather than the conflicting element — State 2 when that window carries
    /// a frame, State 3 when it doesn't.
    /// </summary>
    internal static SimulatedSqlException WindowElementAlreadySpecified(bool referencedWindowHasFrame) =>
        new("Window element in OVER clause can not also be specified in WINDOW clause.", 4123, 15, referencedWindowHasFrame ? (byte)2 : (byte)3);

    /// <summary>
    /// Mimics SQL Server's Msg 4106 — a frame-rejecting window function
    /// referenced a named window that carries a frame. Distinct from
    /// <see cref="FunctionMayNotHaveWindowFrame"/> (Msg 10752, the inline
    /// <c>OVER (… ROWS …)</c> and <c>OVER (w ROWS …)</c> refinement form)
    /// despite the identical wording. Probe-confirmed against SQL Server 2025
    /// (2026-08-01): Class 15, State 2 for the ranking / distribution family
    /// and State 1 for <c>lag</c> / <c>lead</c> / the percentile pair.
    /// </summary>
    internal static SimulatedSqlException NamedWindowMayNotHaveWindowFrame(string functionLowerName, bool isRankingFamily) =>
        new($"The function '{functionLowerName}' may not have a window frame.", 4106, 15, isRankingFamily ? (byte)2 : (byte)1);

    /// <summary>
    /// Mimics SQL Server's Msg 5366 — an ORDER-BY-requiring window function
    /// referenced a named window whose definition has no <c>ORDER BY</c>.
    /// Counterpart of Msg 4112 (the inline <c>OVER (…)</c> form), with its own
    /// wording. Probe-confirmed against SQL Server 2025 (2026-08-01): Class 15,
    /// State 3 for the ranking / distribution family and State 2 for the offset
    /// (<c>lag</c> / <c>lead</c>) and value (<c>first_value</c> /
    /// <c>last_value</c>) families.
    /// </summary>
    internal static SimulatedSqlException FunctionMustHaveWindowWithOrderBy(string functionLowerName, bool isRankingFamily) =>
        new($"The function '{functionLowerName}' must have an OVER clause or a WINDOW with ORDER BY.", 5366, 15, isRankingFamily ? (byte)3 : (byte)2);

    /// <summary>
    /// Mimics SQL Server's Msg 5363 — an ordered-set analytic function
    /// (<c>percentile_cont</c> / <c>percentile_disc</c>) referenced a named
    /// window whose definition carries an <c>ORDER BY</c>. Counterpart of
    /// Msg 10758 (the inline form), with its own wording. Probe-confirmed
    /// against SQL Server 2025 (2026-08-01): Class 15, State 1.
    /// </summary>
    internal static SimulatedSqlException FunctionMayNotHaveOrderByInNamedWindow(string functionLowerName) =>
        new($"The function '{functionLowerName}' may not have ORDER BY in OVER or WINDOW clause.", 5363, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 5364 — the frame a named-window reference
    /// resolved to has no <c>ORDER BY</c> to frame against. Shares its wording
    /// with Msg 10756 (the inline form) but carries its own number.
    /// Probe-confirmed against SQL Server 2025 (2026-08-01): Class 15, State 1.
    /// </summary>
    internal static SimulatedSqlException NamedWindowFrameRequiresOrderBy() =>
        new("Window frame with ROWS or RANGE must have an ORDER BY clause.", 5364, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 5365 — named-window definitions reference each
    /// other in a loop (<c>WINDOW a AS (b …), b AS (a …)</c>). A window
    /// referencing *itself* is not this error: real reports it as Msg 5362
    /// (the name isn't in its own scope). Wording verbatim. Probe-confirmed
    /// against SQL Server 2025 (2026-08-01): Class 15, State 1.
    /// </summary>
    internal static SimulatedSqlException CyclicWindowReferences() =>
        new("Cyclic window references are not permitted.", 5365, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 16211 — one <c>WINDOW</c> clause defined the
    /// same name twice. Wording verbatim. Probe-confirmed against SQL Server
    /// 2025 (2026-08-01): Class 15, State 1.
    /// </summary>
    internal static SimulatedSqlException DuplicateWindowName() =>
        new("Cannot repeat window name in the WINDOW clause.", 16211, 15, 1);

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
    /// Mimics SQL Server's Msg 174 — fired when a function is called with the
    /// wrong number of arguments (e.g. <c>ISNULL(x)</c> or
    /// <c>ISNULL(a, b, c)</c>). Callers pass the spelling real reports:
    /// built-ins render lowercase regardless of source casing
    /// (probe-confirmed), while the <c>geography::</c> / <c>geometry::</c>
    /// constructors keep the caller's casing.
    /// </summary>
    internal static SimulatedSqlException FunctionRequiresNArguments(string functionName, int argumentCount) =>
        new($"The {functionName} function requires {argumentCount} argument(s).", 174, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 280 — <c>TEXTPTR</c> was applied to something
    /// other than a base-table <c>text</c> / <c>ntext</c> / <c>image</c> column
    /// (a literal, a CAST, or any computed expression). Probe-confirmed against
    /// SQL Server 2025 (2026-07-20).
    /// </summary>
    internal static SimulatedSqlException OnlyBaseTableColumnsInTextPtr() =>
        new("Only base table columns are allowed in the TEXTPTR function.", 280, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6607: the password-encryption machinery
    /// rejected an input — fired by <c>PWDENCRYPT</c> for a clear text over
    /// 128 characters (probe-confirmed at exactly the 128/129 boundary, same
    /// shape for varchar and nvarchar input). Also raised for a
    /// <c>CREATE/ALTER LOGIN</c> password over the same documented 128-char
    /// cap, where real's rejection shape is unverifiable from the reference
    /// instance (its login hits the Msg 15247 permission wall before password
    /// validation) — an approximation flagged in
    /// <c>docs/claude/permissions.md</c>.
    /// </summary>
    internal static SimulatedSqlException PasswordEncryptionInvalidValue() =>
        new("Password Encryption: The value supplied for parameter number 1 is invalid.", 6607, 16, 5);

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
    /// Mimics SQL Server error 104: a top-level <c>ORDER BY</c> over a
    /// UNION / INTERSECT / EXCEPT chain names something other than a projected
    /// column — a source column the select list left out, or any expression
    /// over one. The combined stream carries only the projected columns, so
    /// there is nothing else to sort by. Distinct from the Msg 207 a name that
    /// binds nowhere in the first branch's FROM scope raises: real emits that
    /// binding failure first and this second, so the first-error contract
    /// makes the two mutually exclusive here.
    /// </summary>
    internal static SimulatedSqlException OrderByItemNotInSelectListWithSetOperator() =>
        new("ORDER BY items must appear in the select list if the statement contains a UNION, INTERSECT or EXCEPT operator.", 104, 16, 1);

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
    /// Mimics SQL Server error 110: an <c>INSERT … VALUES</c> whose tuples are
    /// wider than the statement's own column list. The VALUES counterpart of
    /// Msg 121; the column-list-less form reports Msg 213 instead.
    /// </summary>
    internal static SimulatedSqlException FewerInsertColumnsThanValues() =>
        new("There are fewer columns in the INSERT statement than values specified in the VALUES clause. The number of values in the VALUES clause must match the number of columns specified in the INSERT statement.", 110, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 109: an <c>INSERT … VALUES</c> whose tuples are
    /// narrower than the statement's own column list. The VALUES counterpart of
    /// Msg 120; the column-list-less form reports Msg 213 instead.
    /// </summary>
    internal static SimulatedSqlException MoreInsertColumnsThanValues() =>
        new("There are more columns in the INSERT statement than values specified in the VALUES clause. The number of values in the VALUES clause must match the number of columns specified in the INSERT statement.", 109, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 239: a <c>WITH</c> prefix declares two or more
    /// CTEs with the same name in one statement.
    /// </summary>
    internal static SimulatedSqlException DuplicateCteName(string name) =>
        new($"Duplicate common table expression name '{name}' was specified.", 239, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8158: a column-rename list has fewer names
    /// than the rowset it renames produces — a CTE / view body projection or
    /// a table-value-constructor derived table with more columns than its
    /// alias column list. Counterpart to Msg 8159.
    /// </summary>
    internal static SimulatedSqlException HasMoreColumnsThanColumnList(string name) =>
        new($"'{name}' has more columns than were specified in the column list.", 8158, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8159: a column-rename list has more names than
    /// the rowset it renames produces — a CTE / view body projection or a
    /// table-value-constructor derived table with fewer columns than its
    /// alias column list. Counterpart to Msg 8158.
    /// </summary>
    internal static SimulatedSqlException HasFewerColumnsThanColumnList(string name) =>
        new($"'{name}' has fewer columns than were specified in the column list.", 8159, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8155: a table-value-constructor derived table
    /// (<c>(VALUES …) alias</c>) carries no column-alias list, so column
    /// <paramref name="columnPosition"/> of <paramref name="alias"/> is
    /// unnamed. Real SQL Server requires the list on a VALUES-in-FROM source.
    /// </summary>
    internal static SimulatedSqlException NoColumnNameSpecified(int columnPosition, string alias) =>
        new($"No column name was specified for column {columnPosition} of '{alias}'.", 8155, 16, 2);

    /// <summary>
    /// Msg 8155 for a derived table with several unnamed columns and no
    /// column-alias list. Real reports one error per unnamed column in a
    /// single exception rather than stopping at the first (probe-confirmed:
    /// <c>(SELECT 1, 2) s</c> reports columns 1 and 2), so the whole run is
    /// reproduced for <c>Errors</c> and <c>Message</c> parity.
    /// </summary>
    internal static SimulatedSqlException NoColumnNamesSpecified(IReadOnlyList<int> columnPositions, string alias)
    {
        var errors = new SimulatedError[columnPositions.Count];
        var lines = new string[columnPositions.Count];
        for (var i = 0; i < columnPositions.Count; i++)
        {
            lines[i] = $"No column name was specified for column {columnPositions[i]} of '{alias}'.";
            errors[i] = new SimulatedError(@class: 16, lineNumber: 0, lines[i], 8155, procedure: "", server: SimulatedDbConnection.DataSourceName, source: SourceName, state: 2);
        }
        return new(string.Join('\n', lines), errors);
    }

    /// <summary>
    /// Mimics SQL Server error 10709: the rows of a table value constructor
    /// (<c>VALUES (…), (…)</c>) don't all have the same number of columns.
    /// </summary>
    internal static SimulatedSqlException TableValueConstructorRowArityMismatch() =>
        new("The number of columns for each row in a table value constructor must be the same.", 10709, 16, 1);

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
    /// Mimics SQL Server error 460: the recursive member used SELECT DISTINCT.
    /// Probe-confirmed verbatim 2026-07-31.
    /// </summary>
    internal static SimulatedSqlException RecursiveCteDistinctNotAllowed(string cteName) =>
        new($"DISTINCT operator is not allowed in the recursive part of a recursive common table expression '{cteName}'.", 460, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 462: the recursive member used an outer join.
    /// Probe-confirmed verbatim 2026-07-31.
    /// </summary>
    internal static SimulatedSqlException RecursiveCteOuterJoinNotAllowed(string cteName) =>
        new($"Outer join is not allowed in the recursive part of a recursive common table expression '{cteName}'.", 462, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 467: the recursive member used GROUP BY, HAVING
    /// or an aggregate. Probe-confirmed verbatim 2026-07-31.
    /// </summary>
    internal static SimulatedSqlException RecursiveCteGroupingNotAllowed(string cteName) =>
        new($"GROUP BY, HAVING, or aggregate functions are not allowed in the recursive part of a recursive common table expression '{cteName}'.", 467, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 461: the recursive member of a recursive CTE
    /// used TOP, OFFSET or FETCH, which would make the recursion's row set
    /// depend on an unordered limit. Probe-confirmed verbatim 2026-07-31.
    /// </summary>
    internal static SimulatedSqlException RecursiveCteTopNotAllowed(string cteName) =>
        new($"The TOP or OFFSET operator is not allowed in the recursive part of a recursive common table expression '{cteName}'.", 461, 16, 1);

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
    /// Mimics SQL Server error 408: an <c>ORDER BY</c> term folds to a constant
    /// (<c>'x'</c>, <c>1 + 0</c>, <c>CAST(1 AS int)</c>, <c>NULL</c>).
    /// <paramref name="position"/> is the term's 1-based index in the ORDER BY
    /// list. A signed integer literal is the ordinal form and lands on Msg 108
    /// instead; a variable, a subquery, a UDF call and any server- or
    /// session-state function all sort fine (probe-confirmed).
    /// </summary>
    internal static SimulatedSqlException ConstantExpressionInOrderBy(int position) =>
        new($"A constant expression was encountered in the ORDER BY list, position {position}.", 408, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1008: an <c>ORDER BY</c> term is a bare variable,
    /// which real reads as a variable column position rather than a sort
    /// expression. A variable inside a larger expression (<c>@v + 1</c>) is
    /// accepted (probe-confirmed). <paramref name="position"/> is the term's
    /// 1-based index in the ORDER BY list.
    /// </summary>
    internal static SimulatedSqlException VariableInOrderByPosition(int position) =>
        new($"The SELECT item identified by the ORDER BY number {position} contains a variable as part of the expression identifying a column position. Variables are only allowed when ordering by an expression referencing a column name.", 1008, 16, 1);

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
    /// Mimics SQL Server's Msg 10752 — an explicit <c>ROWS</c> / <c>RANGE</c>
    /// frame specification was supplied for a function that doesn't accept
    /// one (ranking functions: <c>row_number</c> / <c>rank</c> /
    /// <c>dense_rank</c> / <c>ntile</c>; offset functions: <c>lag</c> /
    /// <c>lead</c>). Probe-confirmed against SQL Server 2025 (2026-05-12):
    /// Class 15, State 1 for LAG/LEAD and State 3 for ranking; the simulator
    /// uses State 1 uniformly (matching the LAG/LEAD probe; State 3 vs 1
    /// isn't routed through any caller behavior).
    /// </summary>
    internal static SimulatedSqlException FunctionMayNotHaveWindowFrame(string functionLowerName) =>
        new($"The function '{functionLowerName}' may not have a window frame.", 10752, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 10756 — an explicit <c>ROWS</c> or <c>RANGE</c>
    /// frame was supplied without an <c>ORDER BY</c> clause inside the same
    /// <c>OVER</c>. Probe-confirmed against SQL Server 2025 (2026-05-12):
    /// Class 15, State 1.
    /// </summary>
    internal static SimulatedSqlException WindowFrameRequiresOrderBy() =>
        new("Window frame with ROWS or RANGE must have an ORDER BY clause.", 10756, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 4193 — the frame's start bound is
    /// <c>N FOLLOWING</c> and the end bound is <c>N PRECEDING</c> or
    /// <c>CURRENT ROW</c>, which is semantically invalid (end before start).
    /// Probe-confirmed against SQL Server 2025 (2026-05-12): Class 16,
    /// State 1.
    /// </summary>
    internal static SimulatedSqlException FrameBetweenFollowingAndPreceding() =>
        new("'BETWEEN ... FOLLOWING AND ... PRECEDING' is not a valid window frame and cannot be used with the OVER clause.", 4193, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 4194 — a <c>RANGE</c> frame used a numeric-
    /// offset bound (<c>N PRECEDING</c> / <c>N FOLLOWING</c>). Real SQL
    /// Server restricts <c>RANGE</c> to <c>UNBOUNDED PRECEDING</c> /
    /// <c>UNBOUNDED FOLLOWING</c> / <c>CURRENT ROW</c> (the value-based
    /// offset form requires a separately licensed feature surface).
    /// Probe-confirmed against SQL Server 2025 (2026-05-12): Class 16,
    /// State 1.
    /// </summary>
    internal static SimulatedSqlException RangeFrameOnlySupportsUnboundedAndCurrentRow() =>
        new("RANGE is only supported with UNBOUNDED and CURRENT ROW window frame delimiters.", 4194, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 10757 — a non-ordered-set aggregate (anything
    /// other than <c>STRING_AGG</c> in this simulator's surface) was given a
    /// <c>WITHIN GROUP (ORDER BY ...)</c> clause. Function name is
    /// SQL-lowercase (<c>max</c>, <c>sum</c>, etc.).
    /// </summary>
    internal static SimulatedSqlException FunctionMayNotHaveWithinGroup(string functionLowerName) =>
        new($"The function '{functionLowerName}' may not have a WITHIN GROUP clause.", 10757, 15, 9);

    /// <summary>
    /// Mimics SQL Server's Msg 10753 — an ordered-set analytic function
    /// (<c>percentile_cont</c> / <c>percentile_disc</c>) was used without the
    /// required <c>OVER</c> clause. Probe-confirmed against SQL Server 2025
    /// (2026-05-27): Class 15, State 3.
    /// </summary>
    internal static SimulatedSqlException FunctionMustHaveOverClause(string functionLowerName) =>
        new($"The function '{functionLowerName}' must have an OVER clause.", 10753, 15, 3);

    /// <summary>
    /// Mimics SQL Server's Msg 10758 — an ordered-set analytic function
    /// (<c>percentile_cont</c> / <c>percentile_disc</c>) supplied an
    /// <c>ORDER BY</c> inside its <c>OVER</c> clause; the ordering must come
    /// from <c>WITHIN GROUP</c> instead. Probe-confirmed against SQL Server
    /// 2025 (2026-05-27): Class 15, State 1.
    /// </summary>
    internal static SimulatedSqlException FunctionMayNotHaveOrderByInOver(string functionLowerName) =>
        new($"The function '{functionLowerName}' may not have ORDER BY in OVER clause.", 10758, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 8727 — the percentile argument to
    /// <c>PERCENTILE_CONT</c> / <c>PERCENTILE_DISC</c> evaluated outside the
    /// closed interval <c>[0, 1]</c> (or was NULL). Raised at runtime once the
    /// argument value is known. Probe-confirmed against SQL Server 2025
    /// (2026-05-27): Class 16, State 1.
    /// </summary>
    internal static SimulatedSqlException PercentileInputOutOfRange() =>
        new("Input parameter of percentile function is outside of range [0, 1].", 8727, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 5308 — an <c>OVER (ORDER BY …)</c> or
    /// <c>WITHIN GROUP (ORDER BY …)</c> term folds to an <c>int</c> constant
    /// that could pass for a column index (<c>1</c>, <c>1 + 1</c>,
    /// <c>ABS(-1)</c>, <c>LEN('abc')</c>). The projection-level ORDER BY
    /// accepts ordinals; these inner ORDER BY positions carry no ordinal
    /// semantics, so real has nothing to bind the number to. A folded
    /// constant of any other shape lands on
    /// <see cref="ConstantNotAllowedInOrderedAggregate"/> instead.
    /// </summary>
    internal static SimulatedSqlException IntegerIndexNotAllowedInOrderedAggregate() =>
        new("Windowed functions, aggregates and NEXT VALUE FOR functions do not support integer indices as ORDER BY clause expressions.", 5308, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 5309 — an <c>OVER (ORDER BY …)</c> or
    /// <c>WITHIN GROUP (ORDER BY …)</c> term folds to a constant that isn't
    /// index-shaped: a string, a <c>NULL</c>, a non-<c>int</c> number, or an
    /// <c>int</c> below 1 (<c>'x'</c>, <c>1.5</c>, <c>0</c>, <c>-1</c>,
    /// <c>CAST(1 AS bigint)</c>). Probe-confirmed against SQL Server 2025:
    /// Class 15, State 1, and it reaches the named-window (<c>WINDOW w AS</c>)
    /// and <c>NEXT VALUE FOR … OVER</c> forms alike.
    /// </summary>
    internal static SimulatedSqlException ConstantNotAllowedInOrderedAggregate() =>
        new("Windowed functions, aggregates and NEXT VALUE FOR functions do not support constants as ORDER BY clause expressions.", 5309, 15, 1);

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

    /// <summary>
    /// Mimics SQL Server's Msg 321 — fired when a <c>WITH (...)</c> clause on
    /// a table / view / table-variable source (or after an UPDATE / DELETE
    /// target) names something that isn't a recognized table-hint keyword.
    /// Wording is verbatim from the probe (2026-05-14 against SQL Server
    /// 2025), including the surrounding double-quotes on the offending name.
    /// </summary>
    internal static SimulatedSqlException UnrecognizedTableHint(ReadOnlySpan<char> hintName) =>
        new($"\"{new string(hintName)}\" is not a recognized table hints option.", 321, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 10715 — fired when an <c>OPTION (USE HINT(...))</c>
    /// clause names a string that isn't in <c>sys.dm_exec_valid_use_hints</c>.
    /// Wording verbatim from the probe (2026-07-16 against SQL Server 2025),
    /// including the single quotes on the offending name. Contrast the generic
    /// OPTION-clause Msg 102 the rest of the hint grammar raises: <c>USE HINT</c>
    /// is the one OPTION hint whose argument SQL Server validates by name.
    /// </summary>
    internal static SimulatedSqlException InvalidUseHint(string hintName) =>
        new($"'{hintName}' is not a valid hint.", 10715, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 8748: the third <c>enable_ordinal</c> argument
    /// to <c>STRING_SPLIT</c> isn't a parse-time constant (a variable or
    /// column reference was used). Wording probe-confirmed against SQL Server
    /// 2025.
    /// </summary>
    internal static SimulatedSqlException StringSplitEnableOrdinalMustBeConstant() =>
        new($"The enable_ordinal argument for string_split only supports constant values (not variables or columns).", 8748, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 5373 — fired by <c>GENERATE_SERIES</c> when the
    /// three argument types don't all match. Wording probe-confirmed against
    /// SQL Server 2025: the suffix lists the supported types verbatim
    /// (<c>tinyint, smallint, int, bigint, decimal and numeric</c>) and the
    /// State / Class are 1 / 16.
    /// </summary>
    internal static SimulatedSqlException GenerateSeriesArgsMustShareType() =>
        new("All the input parameters should be of the same type. Supported types are tinyint, smallint, int, bigint, decimal and numeric.", 5373, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 4199 — fired by <c>GENERATE_SERIES</c> when the
    /// optional third argument (<c>step</c>) is zero. Same Msg-number as
    /// <see cref="StringSplitInvalidEnableOrdinal"/>; only the function name in
    /// the message differs (<c>generate_series</c> here).
    /// </summary>
    internal static SimulatedSqlException GenerateSeriesStepZero() =>
        new("Argument value 0 is invalid for argument 3 of generate_series function.", 4199, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13618: an <c>AS JSON</c> modifier in an
    /// <c>OPENJSON ... WITH</c> column declaration appears on a column whose
    /// declared type isn't <c>nvarchar(max)</c>. Wording probe-confirmed
    /// against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException OpenJsonAsJsonRequiresNVarcharMax() =>
        new($"AS JSON option can be specified only for column of nvarchar(max) type in WITH clause.", 13618, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13544: <c>FOR SYSTEM_TIME</c> clause targeted a
    /// table that isn't system-versioned. Wording probe-confirmed against SQL
    /// Server 2025 — the qualified table name appears between single quotes,
    /// and real SQL Server pads temp-table names out to their internal
    /// suffix-extended form.
    /// </summary>
    internal static SimulatedSqlException ForSystemTimeRequiresVersionedTable(string qualifiedTableName) =>
        new($"Temporal FOR SYSTEM_TIME clause can only be used with system-versioned tables. '{qualifiedTableName}' is not a system-versioned table.", 13544, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 213 as raised by <c>INSERT … EXEC</c> when a
    /// result set produced by the executed procedure / dynamic batch has a
    /// column count that doesn't match the INSERT target's column list.
    /// State 7 probe-confirmed against SQL Server 2025 (distinct from the
    /// OUTPUT INTO variant's State 1).
    /// </summary>
    internal static SimulatedSqlException InsertExecColumnCountMismatch() =>
        new("Column name or number of supplied values does not match table definition.", 213, 16, 7);

    /// <summary>
    /// Mimics SQL Server error 8164: an <c>INSERT … EXEC</c> executed a
    /// procedure / dynamic batch that itself contains an <c>INSERT … EXEC</c>.
    /// Wording and State 1 probe-confirmed against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException InsertExecCannotBeNested() =>
        new("An INSERT EXEC statement cannot be nested.", 8164, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 483: an <c>OUTPUT</c> clause combined with an
    /// <c>INSERT … EXEC</c> source. Wording and State 2 probe-confirmed
    /// against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException OutputClauseNotAllowedInInsertExec() =>
        new("The OUTPUT clause cannot be used in an INSERT...EXEC statement.", 483, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 9829: a <c>STRING_AGG</c> whose operand is a
    /// bounded (non-MAX) string type produced a concatenation exceeding 8000
    /// bytes. Real SQL Server raises this rather than truncating; a MAX-typed
    /// operand streams unbounded and never trips it. Wording and Level 16
    /// probe-confirmed against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException StringAggResultExceededLimit() =>
        new("STRING_AGG aggregation result exceeded the limit of 8000 bytes. Use LOB types to avoid result truncation.", 9829, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 11535: the executed module sent more result
    /// sets than its <c>WITH RESULT SETS</c> clause declared — including the
    /// <c>RESULT SETS NONE</c> form, which declares zero and so reports
    /// <c>"specified 0 result set(s)"</c>. Wording and State 1 probe-confirmed
    /// against SQL Server 2025; the sets that did match are streamed to the
    /// client before this fires.
    /// </summary>
    internal static SimulatedSqlException ResultSetsTooManySent(int declaredCount) =>
        new($"EXECUTE statement failed because its WITH RESULT SETS clause specified {declaredCount} result set(s), and the statement tried to send more result sets than this.", 11535, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 11536: the executed module sent fewer result
    /// sets than its <c>WITH RESULT SETS</c> clause declared. Unlike the other
    /// result-set-contract errors this one is attributed to the <c>EXECUTE</c>
    /// statement itself rather than the module's producing statement
    /// (probe-confirmed: <c>ERROR_PROCEDURE()</c> reads NULL).
    /// </summary>
    internal static SimulatedSqlException ResultSetsTooFewSent(int declaredCount, int sentCount) =>
        new($"EXECUTE statement failed because its WITH RESULT SETS clause specified {declaredCount} result set(s), but the statement only sent {sentCount} result set(s) at run time.", 11536, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 11537: a result set's column count doesn't
    /// match the count its <c>WITH RESULT SETS</c> definition declared. Note
    /// the wording asymmetry real uses — <c>"result set number N"</c> here
    /// versus <c>"result set #N"</c> in Msg 11538 / 11553.
    /// </summary>
    internal static SimulatedSqlException ResultSetsColumnCountMismatch(int declaredCount, int setNumber, int sentCount) =>
        new($"EXECUTE statement failed because its WITH RESULT SETS clause specified {declaredCount} column(s) for result set number {setNumber}, but the statement sent {sentCount} column(s) at run time.", 11537, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 11538: a <c>WITH RESULT SETS</c> column
    /// declared a type the run-time column can't reach by implicit
    /// conversion. Both type names render bare (no length / precision
    /// decoration) — probe-confirmed: a <c>decimal(5,2)</c> declaration
    /// reports <c>'decimal'</c> and a <c>time(3)</c> run-time column reports
    /// <c>'time'</c>.
    /// </summary>
    internal static SimulatedSqlException ResultSetsNoConversion(string declaredTypeName, int columnNumber, int setNumber, string runtimeTypeName) =>
        new($"EXECUTE statement failed because its WITH RESULT SETS clause specified type '{declaredTypeName}' for column #{columnNumber} in result set #{setNumber}, and the corresponding type sent at run time was '{runtimeTypeName}'; there is no conversion between the two types.", 11538, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 11553: a <c>WITH RESULT SETS</c> column
    /// declared <c>NOT NULL</c> received a NULL at run time. Raised per row as
    /// the result set streams, so rows preceding the offending one reach the
    /// client.
    /// </summary>
    internal static SimulatedSqlException ResultSetsNullInNonNullableColumn(int columnNumber, int setNumber) =>
        new($"EXECUTE statement failed because its WITH RESULT SETS clause specified a non-nullable type for column #{columnNumber} in result set #{setNumber}, and the corresponding value sent at run time was null.", 11553, 16, 1);
}
