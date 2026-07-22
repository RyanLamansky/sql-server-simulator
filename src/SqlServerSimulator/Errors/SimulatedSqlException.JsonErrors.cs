namespace SqlServerSimulator;

partial class SimulatedSqlException
{
    /// <summary>
    /// Msg 13607: SQL Server's "JSON path is not properly formatted" error
    /// for a malformed second-arg path in JSON_VALUE / JSON_QUERY /
    /// JSON_MODIFY (e.g. missing leading <c>$</c>, unterminated quoted
    /// property, non-numeric array index).
    /// </summary>
    internal static SimulatedSqlException JsonInvalidPath(string path) =>
        new($"JSON path is not properly formatted. Unexpected character at position 0 in path '{path}'.", 13607, 16, 1);

    /// <summary>
    /// Msg 13608: SQL Server raises this when a <c>strict</c>-mode JSON
    /// path resolves through a missing property / out-of-bounds index /
    /// non-object-or-array intermediate. Lax mode silently returns NULL
    /// instead. The State byte is context-dependent: JSON_VALUE reports
    /// state 2, an OPENJSON … WITH column reports state 6, and the
    /// JSON_QUERY / JSON_MODIFY default is state 1.
    /// </summary>
    internal static SimulatedSqlException JsonStrictPathNotFound(byte state = 1) =>
        new("Property cannot be found on the specified JSON path.", 13608, 16, state);

    /// <summary>
    /// Msg 13624: a <c>strict</c>-mode JSON path under JSON_QUERY (or an
    /// <c>OPENJSON … WITH (col … AS JSON)</c> column) resolved to a value
    /// that is present but is neither an object nor an array. Lax mode
    /// returns NULL instead; a JSON <c>null</c> value returns NULL in
    /// both modes.
    /// </summary>
    internal static SimulatedSqlException JsonObjectOrArrayNotFound() =>
        new("Object or array cannot be found in the specified JSON path.", 13624, 16, 1);

    /// <summary>
    /// Msg 13609: invalid JSON text passed to JSON_VALUE / JSON_QUERY /
    /// JSON_MODIFY in strict mode. Lax mode silently returns NULL.
    /// </summary>
    internal static SimulatedSqlException JsonInvalidText() =>
        new("JSON text is not properly formatted. Unexpected character is found.", 13609, 16, 1);

    /// <summary>
    /// Msg 13638: <c>JSON_OBJECT</c> rejects a NULL key at runtime
    /// regardless of the active null clause. Probe-confirmed wording
    /// against SQL Server 2025 (2026-05-23).
    /// </summary>
    internal static SimulatedSqlException JsonObjectNullKey() =>
        new("User error : Name parameter value in 'json_object' cannot be null", 13638, 16, 1);

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
}
