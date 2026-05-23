namespace SqlServerSimulator;

partial class SimulatedSqlException
{
    /// <summary>
    /// Msg 16915: a <c>DECLARE … CURSOR</c> reuses a cursor name already
    /// declared on the connection. Probe-confirmed verbatim against SQL
    /// Server 2025.
    /// </summary>
    internal static SimulatedSqlException CursorAlreadyExists(string name) =>
        new($"A cursor with the name '{name}' already exists.", 16915, 16, 1);

    /// <summary>
    /// Msg 16916: <c>OPEN</c> / <c>FETCH</c> / <c>CLOSE</c> / <c>DEALLOCATE</c>
    /// (or <c>WHERE CURRENT OF</c>) names a cursor that was never declared.
    /// Probe-confirmed verbatim against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException CursorDoesNotExist(string name) =>
        new($"A cursor with the name '{name}' does not exist.", 16916, 16, 1);

    /// <summary>
    /// Msg 16905: <c>OPEN</c> on a cursor that is already open. Probe-confirmed
    /// verbatim against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException CursorAlreadyOpen() =>
        new("The cursor is already open.", 16905, 16, 1);

    /// <summary>
    /// Msg 16917: an operation requiring an open cursor hit a closed one.
    /// Probe-confirmed: <c>CLOSE</c> on a not-open cursor reports state 1,
    /// <c>FETCH</c> on a not-open cursor reports state 2. Note the wording is
    /// <c>"Cursor is not open."</c> (no leading "The").
    /// </summary>
    internal static SimulatedSqlException CursorNotOpen(byte state) =>
        new("Cursor is not open.", 16917, 16, state);

    /// <summary>
    /// Msg 16924: a <c>FETCH … INTO</c> list has a different cardinality than
    /// the cursor's projected columns. Probe-confirmed verbatim against SQL
    /// Server 2025 (note the <c>"Cursorfetch:"</c> prefix, no space).
    /// </summary>
    internal static SimulatedSqlException CursorFetchVariableCountMismatch() =>
        new("Cursorfetch: The number of variables declared in the INTO list must match that of selected columns.", 16924, 16, 1);

    /// <summary>
    /// Msg 16925: a scrolling <c>FETCH</c> direction (anything but <c>NEXT</c>)
    /// was issued against a forward-only / dynamic cursor that doesn't support
    /// it. Probe-confirmed verbatim: <c>"The fetch type Absolute cannot be used
    /// with dynamic cursors."</c> — the direction name is title-cased.
    /// </summary>
    internal static SimulatedSqlException CursorFetchTypeNotAllowed(string fetchType) =>
        new($"The fetch type {fetchType} cannot be used with dynamic cursors.", 16925, 16, 1);

    /// <summary>
    /// Msg 16929: <c>UPDATE … WHERE CURRENT OF</c> / <c>DELETE … WHERE CURRENT
    /// OF</c> targeted a read-only cursor (STATIC / FAST_FORWARD / declared
    /// <c>FOR READ ONLY</c>). Probe-confirmed verbatim against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException CursorIsReadOnly() =>
        new("The cursor is READ ONLY.", 16929, 16, 1);

    /// <summary>
    /// Msg 16931: <c>WHERE CURRENT OF</c> on a cursor that isn't positioned on
    /// a live row (before the first <c>FETCH</c>, past the last row, or on a
    /// row that was deleted out from under a keyset cursor). Probe-confirmed
    /// verbatim against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException CursorNoCurrentRow() =>
        new("There are no rows in the current fetch buffer.", 16931, 16, 1);
}
