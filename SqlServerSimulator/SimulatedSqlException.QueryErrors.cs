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
    /// Mimics SQL Server error 155: the first argument to <c>DATEPART</c> /
    /// <c>DATEADD</c> / <c>DATEDIFF</c> / etc. wasn't a recognized datepart
    /// keyword (year / month / day / hour / minute / second / etc.).
    /// </summary>
    internal static SimulatedSqlException NotARecognizedDatepartOption(string keyword) =>
        new($"'{keyword}' is not a recognized datepart option.", 155, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 9810: a datepart keyword is incompatible with
    /// the date-family argument's data type (e.g. <c>DATEPART(hour, dateCol)</c>
    /// against a <c>date</c> column has no time component to extract).
    /// </summary>
    internal static SimulatedSqlException DatepartNotSupportedForType(string datepart, string function, string typeName) =>
        new($"The datepart {datepart} is not supported by date function {function} for data type {typeName}.", 9810, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 517: <c>DATEADD</c>'s output value falls
    /// outside the date/time type's representable range. The type-name slot
    /// is the *input* column's type (e.g. <c>'date'</c>), not the abstract
    /// SQL-server type family — verified by probe.
    /// </summary>
    internal static SimulatedSqlException DateAddOverflow(string typeName) =>
        new($"Adding a value to a '{typeName}' column caused an overflow.", 517, 16, 3);

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
    /// Mimics SQL Server error 108: a positional <c>ORDER BY</c> ordinal
    /// (e.g. <c>order by 0</c>, <c>order by 5</c> with only 3 columns) is
    /// outside the projection's column count. The validation is 1-based.
    /// </summary>
    internal static SimulatedSqlException OrderByPositionOutOfRange(int position) =>
        new($"The ORDER BY position number {position} is out of range of the number of items in the select list.", 108, 16, 1);
}
