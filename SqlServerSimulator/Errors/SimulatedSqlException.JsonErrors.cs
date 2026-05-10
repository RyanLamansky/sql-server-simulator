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
    /// instead.
    /// </summary>
    internal static SimulatedSqlException JsonStrictPathNotFound() =>
        new("Property cannot be found on the specified JSON path.", 13608, 16, 1);

    /// <summary>
    /// Msg 13609: invalid JSON text passed to JSON_VALUE / JSON_QUERY /
    /// JSON_MODIFY in strict mode. Lax mode silently returns NULL.
    /// </summary>
    internal static SimulatedSqlException JsonInvalidText() =>
        new("JSON text is not properly formatted. Unexpected character is found.", 13609, 16, 1);
}
