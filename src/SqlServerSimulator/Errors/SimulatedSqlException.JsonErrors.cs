using System.Globalization;

namespace SqlServerSimulator;

// The path and document errors flagged AbortsAsUnderXactAbort below behave as
// errors do under SET XACT_ABORT ON whatever the option says: uncaught, they
// end the batch and roll the transaction back, and caught, they doom it
// (probed 2026-09-24 against SQL Server 2025 for each flagged number).
partial class SimulatedSqlException
{
    /// <summary>
    /// Msg 13607: SQL Server's "JSON path is not properly formatted" error
    /// for a malformed second-arg path in JSON_VALUE / JSON_QUERY /
    /// JSON_MODIFY / OPENJSON (e.g. missing leading <c>$</c>, unterminated
    /// quoted property, non-numeric array index).
    /// <paramref name="character"/> is the character the parser stopped on
    /// (<c>.</c> when it ran off the end of the path) and
    /// <paramref name="position"/> its zero-based index. The State byte names
    /// what the parser was reading when it stopped — see the state table on
    /// the path parser.
    /// </summary>
    internal static SimulatedSqlException JsonInvalidPath(char character, int position, byte state) =>
        new(
            $"JSON path is not properly formatted. Unexpected character '{character}' is found at position {position.ToString(CultureInfo.InvariantCulture)}.",
            13607,
            16,
            state)
        { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 13608: SQL Server raises this when a <c>strict</c>-mode JSON
    /// path resolves through a missing property / out-of-bounds index /
    /// non-object-or-array intermediate. Lax mode silently returns NULL
    /// instead. The State byte is context-dependent: JSON_MODIFY reports
    /// state 2, an OPENJSON … WITH column reports state 6, and the
    /// JSON_VALUE / JSON_QUERY default is state 1.
    /// </summary>
    internal static SimulatedSqlException JsonStrictPathNotFound(byte state = 1) =>
        new("Property cannot be found on the specified JSON path.", 13608, 16, state) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 13619: <c>JSON_MODIFY</c> refuses a path with no segments.
    /// <c>$</c> on its own names the whole document, which the function has
    /// no edit to make against; <c>append $</c> is the one segment-less form
    /// it takes.
    /// </summary>
    internal static SimulatedSqlException JsonUnsupportedModifyPath() =>
        new("Unsupported JSON path found in argument 2 of JSON_MODIFY.", 13619, 16, 1);

    /// <summary>
    /// Msg 13621: a <c>strict</c>-mode <c>append</c> path named a value that
    /// is present but isn't an array, so there is nothing to append onto.
    /// Lax mode leaves the document unchanged instead.
    /// </summary>
    internal static SimulatedSqlException JsonArrayNotFound() =>
        new("Array cannot be found in the specified JSON path.", 13621, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 13623: a <c>strict</c>-mode JSON path under JSON_VALUE resolved to
    /// an object or an array, which JSON_VALUE has no scalar to return for.
    /// Lax mode returns NULL instead. Probe-confirmed State 2 against
    /// SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException JsonScalarNotFound() =>
        new("Scalar value cannot be found in the specified JSON path.", 13623, 16, 2) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 13624: a <c>strict</c>-mode JSON path under JSON_QUERY (or an
    /// <c>OPENJSON … WITH (col … AS JSON)</c> column) resolved to a value
    /// that is present but is neither an object nor an array. Lax mode
    /// returns NULL instead; a JSON <c>null</c> value returns NULL in
    /// both modes. JSON_QUERY reports State 2, an OPENJSON column State 1.
    /// </summary>
    internal static SimulatedSqlException JsonObjectOrArrayNotFound(byte state) =>
        new("Object or array cannot be found in the specified JSON path.", 13624, 16, state) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 13611: a <c>strict</c> OPENJSON document path resolved to a value
    /// that isn't an object or array, JSON <c>null</c> included — state 1 for
    /// the default schema, 2 with a <c>WITH</c> clause (probed 2026-09-24
    /// against SQL Server 2025). Lax mode opens no rows instead.
    /// </summary>
    internal static SimulatedSqlException JsonNotObjectOrArrayForOpenJson(byte state) =>
        new("Value referenced by JSON path is not an array or object and cannot be opened with OPENJSON.", 13611, 16, state) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 13666: a JSON builder or aggregate given a CLR-typed value
    /// (<c>hierarchyid</c>, the spatial types, a CLR user-defined type).
    /// <paramref name="functions"/> is real's own lower-case naming —
    /// <c>json_object and json_objectagg</c> share State 2, <c>json_array</c>
    /// is State 3 and <c>json_arrayagg</c> State 1 (probed 2026-10-02 against
    /// SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException JsonBuilderClrType(string functions, byte state) =>
        new($"{functions} does not support CLR type as parameters", 13666, 16, state);

    /// <summary>
    /// Msg 13612: an <c>OPENJSON … WITH</c> binary column met a scalar that
    /// isn't base64.
    /// </summary>
    internal static SimulatedSqlException OpenJsonNotBase64() =>
        new("Cannot convert a string value found in the JSON text to binary value because it is not Base64 encoded.", 13612, 16, 5);

    /// <summary>
    /// Msg 13613: an <c>OPENJSON … WITH</c> binary column's base64 decodes to
    /// more bytes than the column holds — state 1, or 2 for a
    /// <c>rowversion</c> column.
    /// </summary>
    internal static SimulatedSqlException OpenJsonBase64Truncated(byte state) =>
        new("Base64 encoded string cannot be converted to binary value. Binary data would be truncated.", 13613, 16, state);

    /// <summary>Msg 13614: an <c>OPENJSON … WITH</c> column of a legacy LOB type or <c>sql_variant</c>.</summary>
    internal static SimulatedSqlException OpenJsonUnsupportedLobType() =>
        new("TEXT, NTEXT, SQL_VARIANT and IMAGE types cannot be used as column types in OPENJSON function with explicit schema. These types are not supported in WITH clause.", 13614, 16, 1);

    /// <summary>Msg 13616: an <c>OPENJSON … WITH</c> column of a CLR type.</summary>
    internal static SimulatedSqlException OpenJsonUnsupportedClrType() =>
        new("CLR types cannot be used as column types in OPENJSON function with explicit schema. CLR types are not supported in WITH clause.", 13616, 16, 1);

    /// <summary>
    /// Msg 13625: a strict <c>JSON_VALUE</c> over a MAX document matched a
    /// string longer than its <c>nvarchar(4000)</c> result.
    /// </summary>
    internal static SimulatedSqlException JsonValueTruncated() =>
        new("String value in the specified JSON path would be truncated.", 13625, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 13606: a JSON function met a value nested inside 129 containers
    /// (State 1), or a path of more than 128 steps (State 4).
    /// </summary>
    internal static SimulatedSqlException JsonTooDeep(byte state) =>
        new("JSON text/path that has more than 128 nesting levels cannot be parsed.", 13606, 16, state);

    /// <summary>
    /// Msg 13609: the document argument of JSON_VALUE / JSON_QUERY /
    /// JSON_MODIFY / OPENJSON isn't JSON text, whatever the path's lax or
    /// strict prefix says. <paramref name="character"/> is the character the
    /// reader stopped on (<c>.</c> when it ran off the end) and
    /// <paramref name="position"/> its zero-based index. The State byte names
    /// the caller: JSON_VALUE / JSON_QUERY report 1, OPENJSON 3 with a
    /// document path and 4 without, JSON_MODIFY 7.
    /// </summary>
    internal static SimulatedSqlException JsonInvalidText(char character, int position, byte state = 1) =>
        new(
            $"JSON text is not properly formatted. Unexpected character '{character}' is found at position {position.ToString(CultureInfo.InvariantCulture)}.",
            13609,
            16,
            state)
        { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 13638: <c>JSON_OBJECT</c> rejects a NULL key at runtime
    /// regardless of the active null clause. Probe-confirmed wording
    /// against SQL Server 2025 (2026-05-23).
    /// </summary>
    internal static SimulatedSqlException JsonObjectNullKey() =>
        new("User error : Name parameter value in 'json_object' cannot be null", 13638, 16, 1);

    /// <summary>Msg 13604: a <c>FOR JSON</c> column of a spatial or CLR user-defined type.</summary>
    internal static SimulatedSqlException ForJsonClrType() =>
        new("FOR JSON cannot serialize CLR objects. Cast CLR types explicitly into one of the supported types in FOR JSON queries.", 13604, 16, 1);

    /// <summary>
    /// Msg 13603: a <c>FOR JSON PATH</c> column alias with an empty step — one
    /// that starts or ends with <c>.</c> or holds <c>..</c>.
    /// </summary>
    internal static SimulatedSqlException ForJsonInvalidPropertyName(string property) =>
        new($"Property '{property}' cannot be generated in JSON output due to invalid character in the column name or alias. Column name or alias that contains '..', starts or ends with '.' is not allowed in query that has FOR JSON clause.", 13603, 16, 1);

    /// <summary>
    /// Msg 13601: two columns in a <c>FOR JSON PATH</c> projection resolve to
    /// the same JSON document path — a duplicate leaf, a leaf whose name is
    /// also used as an object prefix, or an object reopened after another
    /// object intervened (paths for one object must be contiguous). Probe-
    /// confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForJsonPropertyConflict(string property) =>
        new($"Property '{property}' cannot be generated in JSON output due to a conflict with another column name or alias. FOR JSON PATH requires that the column expressions are ordered based on the JSON document paths specified in the column aliases.", 13601, 16, 1);

    /// <summary>
    /// Msg 13600: <c>FOR JSON AUTO</c> on a SELECT with no FROM clause — AUTO
    /// keys every nesting level off a table, so it has nothing to key off.
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForJsonAutoRequiresTable() =>
        new("FOR JSON AUTO requires at least one table for generating JSON objects. Use FOR JSON PATH or add a FROM clause with a table name.", 13600, 16, 1);

    /// <summary>
    /// Msg 13605: a <c>FOR JSON</c> projection contains a column expression
    /// with no name or alias. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForJsonColumnWithoutName() =>
        new("Column expressions and data sources without names or aliases cannot be formatted as JSON text using FOR JSON clause. Add alias to the unnamed column or table.", 13605, 16, 1);

    /// <summary>
    /// Msg 13620: <c>FOR JSON</c> can't combine <c>ROOT</c> with
    /// <c>WITHOUT_ARRAY_WRAPPER</c>. Probe-confirmed wording against
    /// SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForJsonRootWithoutWrapperConflict() =>
        new("ROOT option and WITHOUT_ARRAY_WRAPPER option cannot be used together in FOR JSON. Remove one of these options.", 13620, 16, 1);

    /// <summary>
    /// Msg 13602: a <c>FOR JSON</c> clause sits on the SELECT an
    /// <c>INSERT … SELECT</c> or <c>SELECT … INTO</c> writes from
    /// (<paramref name="statementKind"/> is real's own word for the statement).
    /// The FOR XML counterpart is Msg 6819, which a variable-assigning SELECT
    /// raises for both clauses. Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForJsonNotAllowedIn(string statementKind) =>
        new($"The FOR JSON clause is not allowed in a {statementKind} statement.", 13602, 16, 1);
}
