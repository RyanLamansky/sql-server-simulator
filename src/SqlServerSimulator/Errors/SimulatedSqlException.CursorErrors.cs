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
    /// Msg 16925: <c>FETCH ABSOLUTE</c> against a dynamic-sensitivity cursor,
    /// which can't position by ordinal. Probe-confirmed verbatim:
    /// <c>"The fetch type Absolute cannot be used with dynamic cursors."</c> —
    /// the direction name is title-cased, and real reports this ahead of the
    /// forward-only check, so a bare <c>FORWARD_ONLY</c> cursor (which defaults
    /// to dynamic sensitivity) gets this rather than
    /// <see cref="CursorFetchTypeForwardOnly"/>.
    /// </summary>
    internal static SimulatedSqlException CursorFetchTypeNotAllowed(string fetchType) =>
        new($"The fetch type {fetchType} cannot be used with dynamic cursors.", 16925, 16, 1);

    /// <summary>
    /// Msg 16911: a scrolling <c>FETCH</c> direction was issued against a
    /// cursor that isn't scrollable. Probe-confirmed verbatim, including the
    /// <c>fetch: </c> prefix and the <em>lower-cased</em> direction name —
    /// which is where this differs from
    /// <see cref="CursorFetchTypeNotAllowed"/>'s title case.
    /// </summary>
    internal static SimulatedSqlException CursorFetchTypeForwardOnly(string lowercaseFetchType) =>
        new($"fetch: The fetch type {lowercaseFetchType} cannot be used with forward only cursors.", 16911, 16, 1);

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

    /// <summary>
    /// Msg 16933: a positioned <c>UPDATE</c> / <c>DELETE … WHERE CURRENT OF</c>
    /// names a table the cursor's SELECT doesn't read — an unrelated table, or
    /// the base table behind a view the cursor reads (real binds the view, not
    /// what's under it). Also raised when the cursor's <c>FOR UPDATE OF (…)</c>
    /// list names no column of the target table, which narrows the cursor's
    /// updatable tables. Probe-confirmed verbatim against SQL Server 2025
    /// (class 16, state 1).
    /// </summary>
    internal static SimulatedSqlException CursorTableNotIncluded() =>
        new("The cursor does not include the table being modified or the table is not updatable through the cursor.", 16933, 16, 1);

    /// <summary>
    /// A positioned <c>UPDATE</c> / <c>DELETE … WHERE CURRENT OF</c> found no
    /// row to mutate for the named table even though the cursor is positioned —
    /// the table's slot is the NULL-extended side of an outer join. Real
    /// SQL Server raises <b>Msg 16947</b> (class 16, state 1) plus the standard
    /// <b>Msg 3621</b> statement-terminated companion; unlike
    /// <see cref="CursorOptimisticConflict"/> there is no descriptive Msg 16934,
    /// since nothing was modified out-of-band (probe-confirmed).
    /// </summary>
    internal static SimulatedSqlException CursorNoRowsAffected() =>
        new(
            "No rows were updated or deleted.\nThe statement has been terminated.",
            new SimulatedError(@class: 16, lineNumber: 0, "No rows were updated or deleted.", 16947, procedure: "", server: "", source: "Core Microsoft SqlClient Data Provider", state: 1),
            new SimulatedError(@class: 0, lineNumber: 0, "The statement has been terminated.", 3621, procedure: "", server: "", source: "Core Microsoft SqlClient Data Provider", state: 0));

    /// <summary>
    /// Msg 16932: a positioned <c>UPDATE … WHERE CURRENT OF</c> assigns a
    /// column that isn't in the cursor's <c>FOR UPDATE OF (…)</c> list.
    /// Probe-confirmed verbatim against SQL Server 2025 (state 1).
    /// </summary>
    internal static SimulatedSqlException CursorColumnNotInForUpdateList() =>
        new("The cursor has a FOR UPDATE list and the requested column to be updated is not in this list.", 16932, 16, 1);

    /// <summary>
    /// A positioned <c>UPDATE</c> / <c>DELETE WHERE CURRENT OF</c> on an
    /// <c>OPTIMISTIC</c> cursor found the current row modified (or deleted)
    /// out-of-band since it was fetched. Real SQL Server surfaces a three-error
    /// chain (probe-confirmed against SQL Server 2025): the terminating
    /// <b>Msg 16947</b> (class 16, state 1, <c>"No rows were updated or
    /// deleted."</c>) — the number a SqlClient consumer catches — followed by
    /// the descriptive class-0 <b>Msg 16934</b> and the standard <b>Msg
    /// 3621</b> statement-terminated companion. The full chain is reproduced so
    /// <c>SqlException.Errors</c> and <c>.Message</c> match.
    /// </summary>
    internal static SimulatedSqlException CursorOptimisticConflict() =>
        new(
            "No rows were updated or deleted.\nOptimistic concurrency check failed. The row was modified outside of this cursor.\nThe statement has been terminated.",
            new SimulatedError(@class: 16, lineNumber: 0, "No rows were updated or deleted.", 16947, procedure: "", server: "", source: "Core Microsoft SqlClient Data Provider", state: 1),
            new SimulatedError(@class: 0, lineNumber: 0, "Optimistic concurrency check failed. The row was modified outside of this cursor.", 16934, procedure: "", server: "", source: "Core Microsoft SqlClient Data Provider", state: 1),
            new SimulatedError(@class: 0, lineNumber: 0, "The statement has been terminated.", 3621, procedure: "", server: "", source: "Core Microsoft SqlClient Data Provider", state: 0));

    /// <summary>
    /// Msg 16950: a <c>FETCH</c> (or other cursor operation) named a cursor
    /// <em>variable</em> that has no cursor allocated to it — declared with
    /// <c>DECLARE @c CURSOR</c> but never <c>SET</c>, or referencing a
    /// deallocated cursor. Probe-confirmed verbatim against SQL Server 2025
    /// (class 16, state 2).
    /// </summary>
    internal static SimulatedSqlException CursorVariableNotAllocated(string variableName) =>
        new($"The variable '@{variableName}' does not currently have a cursor allocated to it.", 16950, 16, 2);
}
