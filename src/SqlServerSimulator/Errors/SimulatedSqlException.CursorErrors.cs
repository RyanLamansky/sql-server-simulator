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
    internal static SimulatedSqlException CursorDoesNotExist(string name, byte state = 1) =>
        new($"A cursor with the name '{name}' does not exist.", 16916, 16, state);

    /// <summary>
    /// Msg 16902: <c>CURSOR_STATUS</c> was handed an argument it can't use —
    /// a NULL source (state 40), a source other than <c>global</c> /
    /// <c>local</c> / <c>variable</c> (state 42), or a NULL or empty cursor
    /// name (state 43). Probe-confirmed verbatim against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException CursorStatusInvalidParameter(string parameterName, byte state) =>
        new($"cursor_status: The value of the parameter '{parameterName}' is invalid.", 16902, 16, state);

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
    /// Msg 16943: a FETCH from a KEYSET, DYNAMIC or FAST_FORWARD cursor whose
    /// table a definition change — an <c>ALTER TABLE</c>, an index created —
    /// reached since the cursor opened (probed 2026-10-03 against SQL Server
    /// 2025, state 4; a STATIC cursor reads on).
    /// </summary>
    internal static SimulatedSqlException CursorTableSchemaChanged() =>
        new("Could not complete cursor operation because the table schema changed after the cursor was declared.", 16943, 16, 4);

    /// <summary>
    /// Msg 16924: a <c>FETCH … INTO</c> list has a different cardinality than
    /// the cursor's projected columns. Probe-confirmed verbatim against SQL
    /// Server 2025 (note the <c>"Cursorfetch:"</c> prefix, no space).
    /// </summary>
    internal static SimulatedSqlException CursorFetchVariableCountMismatch() =>
        new("Cursorfetch: The number of variables declared in the INTO list must match that of selected columns.", 16924, 16, 1);

    /// <summary>Msg 154 state 3: a cursor's query carrying <c>SELECT … INTO</c>.</summary>
    internal static SimulatedSqlException IntoNotAllowedInCursorDeclaration() =>
        new("an INTO clause is not allowed in a cursor declaration.", 154, 15, 3);

    /// <summary>
    /// Mimics SQL Server error 154 for a <c>USE</c> inside a procedure,
    /// function or trigger body, raised as the body parses (probed 2026-10-04
    /// against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException UseNotAllowedInModule() =>
        new("a USE database statement is not allowed in a procedure, function or trigger.", 154, 15, 1);

    /// <summary>
    /// Msg 16949: a cursor variable read or assigned as a scalar —
    /// <c>SELECT @c = 1</c> (probed 2026-10-02 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException CursorVariableUsedAsScalar(string name) =>
        new($"The variable '@{name}' is a cursor variable, but it is used in a place where a cursor variable is not valid.", 16949, 16, 1);

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
    /// SQL Server raises <b>Msg 16947</b> (class 16, state 1), which the
    /// dispatch loop follows with the standard Msg 3621; unlike
    /// <see cref="CursorOptimisticConflict"/> there is no descriptive Msg 16934,
    /// since nothing was modified out-of-band (probe-confirmed).
    /// </summary>
    internal static SimulatedSqlException CursorNoRowsAffected() =>
        new("No rows were updated or deleted.", 16947, 16, 1);

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
    /// the descriptive class-0 <b>Msg 16934</b>, and then the standard Msg 3621
    /// the dispatch loop adds.
    /// </summary>
    internal static SimulatedSqlException CursorOptimisticConflict() =>
        new(
            "No rows were updated or deleted.\nOptimistic concurrency check failed. The row was modified outside of this cursor.",
            new SimulatedError(@class: 16, lineNumber: 0, "No rows were updated or deleted.", 16947, procedure: "", server: "", source: "Core Microsoft SqlClient Data Provider", state: 1),
            new SimulatedError(@class: 0, lineNumber: 0, "Optimistic concurrency check failed. The row was modified outside of this cursor.", 16934, procedure: "", server: "", source: "Core Microsoft SqlClient Data Provider", state: 1));

    /// <summary>
    /// Msg 16950: a <c>FETCH</c> (or other cursor operation) named a cursor
    /// <em>variable</em> that has no cursor allocated to it — declared with
    /// <c>DECLARE @c CURSOR</c> but never <c>SET</c>, or referencing a
    /// deallocated cursor. Probe-confirmed verbatim against SQL Server 2025
    /// (class 16, state 2).
    /// </summary>
    internal static SimulatedSqlException CursorVariableNotAllocated(string variableName) =>
        new($"The variable '@{variableName}' does not currently have a cursor allocated to it.", 16950, 16, 2);

    /// <summary>
    /// Msg 1048: a cursor declaration names two options that contradict each
    /// other, named in the order real names that pair. Raised while the batch
    /// compiles. Probed 2026-09-29 against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ConflictingCursorOptions(string first, string second) =>
        new($"Conflicting cursor options {first} and {second}.", 1048, 15, 1);

    /// <summary>
    /// Msg 1049: an SQL-92 declaration (<c>INSENSITIVE</c> / <c>SCROLL</c>
    /// before <c>CURSOR</c>) also names a T-SQL option after it. Probed
    /// 2026-09-29 against SQL Server 2025, whose line for it is 0 in most
    /// placements.
    /// </summary>
    internal static SimulatedSqlException CursorSyntaxMixed() =>
        new("Mixing old and new syntax to specify cursor options is not allowed.", 1049, 15, 1);

    /// <summary>
    /// Msg 1058: <c>READ_ONLY</c> and <c>FOR READ ONLY</c> on one cursor
    /// declaration. Probed 2026-09-29 against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException CursorReadOnlyTwice() =>
        new("Cannot specify both READ_ONLY and FOR READ ONLY on a cursor declaration.", 1058, 15, 1);

    /// <summary>
    /// Msg 153: <c>INSENSITIVE</c> written after <c>CURSOR</c>, where only
    /// the SQL-92 form's position before it takes the word — reported
    /// lower-cased whatever the declaration wrote, and for a <c>SET @c =
    /// CURSOR</c> too. Probed 2026-09-29 against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException InvalidCursorOption(string option) =>
        new($"Invalid usage of the option {option} in the DECLARE CURSOR statement.", 153, 15, 1);

    /// <summary>
    /// Msg 412: a cursor's <c>FOR UPDATE OF</c> list names a column only a
    /// constructed rowset such as <c>VALUES</c> supplies. Probed 2026-09-29
    /// against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ColumnDerivedOrConstant(string column) =>
        new($"The column \"{column}\" is not updatable because it is derived or constant.", 412, 16, 1);

    /// <summary>
    /// Msg 1051: a procedure's cursor parameter lacks <c>VARYING</c> or
    /// <c>OUTPUT</c>, or writes them the other way round. Probed 2026-09-29
    /// against SQL Server 2025, which goes on to report the body's uses of the
    /// parameter as undeclared variables.
    /// </summary>
    internal static SimulatedSqlException CursorParameterNeedsVaryingOutput() =>
        new("Cursor parameters in a stored procedure must be declared with OUTPUT and VARYING options, and they must be specified in the order CURSOR VARYING OUTPUT.", 1051, 15, 2);

    /// <summary>
    /// Msg 16951: <c>EXEC</c> passes a cursor variable that already holds a
    /// cursor to a procedure's cursor OUTPUT parameter; the procedure doesn't
    /// run. Probed 2026-09-29 against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException CursorOutputArgumentAllocated(string variable) =>
        new($"The variable '@{variable}' cannot be used as a parameter because a CURSOR OUTPUT parameter must not have a cursor allocated to it before execution of the procedure.", 16951, 16, 1);

    /// <summary>
    /// Msg 137 as <c>sp_describe_cursor</c> and its siblings raise it for a
    /// <c>variable</c>-sourced identity naming no declared cursor variable —
    /// class 16 state 100, unlike the binder's. Probed 2026-09-29 against SQL
    /// Server 2025.
    /// </summary>
    internal static SimulatedSqlException CursorProcedureUndeclaredVariable(string variable) =>
        new($"Must declare the scalar variable \"@{variable}\".", 137, 16, 100);

    /// <summary>
    /// Msg 16966: <c>sp_cursoropen</c> asked for a STATIC or FAST_FORWARD
    /// cursor with a concurrency other than READ_ONLY — SCROLL_LOCKS (2) or
    /// either OPTIMISTIC (4, 8). Probed 2026-09-29 against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ApiCursorConcurrencyIncompatible(int ccopt) =>
        new($"sp_cursoropen: Specified concurrency control option {ccopt} ({(ccopt == 2 ? "SCROLL_LOCKS" : "OPTIMISTIC")}) is incompatible with static or fast forward only cursors. Only read-only is compatible with static or fast forward only cursors.", 16966, 16, 1);
}
