using SqlServerSimulator.Parser;

namespace SqlServerSimulator.ExtendedEvents;

/// <summary>
/// The statement and module events: <c>sql_statement_starting</c> /
/// <c>_completed</c> around a batch's statements, <c>sp_statement_starting</c>
/// / <c>_completed</c> around a module body's, and <c>module_start</c> /
/// <c>module_end</c> around a procedure's body, with SQL Server 2025's fields
/// (probed 2026-10-16): the statement's line, its byte offsets — into the
/// batch, or a module's definition — and its text, a completed event adding
/// its duration and row counts.
/// </summary>
internal static class XeStatementEvents
{
    /// <summary>
    /// Whether a running session takes any statement event — one array read
    /// for each of the four, which is all a statement pays when none does.
    /// </summary>
    public static bool AnyTaken(Simulation simulation) =>
        (simulation.ExtendedEventsIfMade is { } server && AnyStatementEvent(server.Taken))
        || (simulation.TraceTaken is { } traced && AnyStatementEvent(traced));

    private static bool AnyStatementEvent(bool[] taken) =>
        taken[(int)XeEventKind.SqlStatementStarting] || taken[(int)XeEventKind.SqlStatementCompleted]
        || taken[(int)XeEventKind.SpStatementStarting] || taken[(int)XeEventKind.SpStatementCompleted];

    /// <summary>
    /// The statement at <paramref name="start"/> of <paramref name="batch"/>'s
    /// text as the events place it, or null for one real fires none for: a
    /// block, <c>IF</c> / <c>WHILE</c> (whose condition real reports as a
    /// statement of its own, which the simulator doesn't split out), a
    /// <c>TRY</c> / <c>CATCH</c>, a label and a <c>DECLARE</c> initializing
    /// nothing (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    public static XeStatement? Place(BatchContext batch, int start)
    {
        var text = batch.Parser.Command.CommandText;
        if (start >= text.Length)
            return null;
        var simulation = batch.Connection.Simulation;
        var end = LockReports.StatementEnd(simulation, text, start);
        var through = end < 0 ? text.Length - 1 : end;
        var statement = text[start..(through + 1)];
        var word = FirstWord(statement);
        if (word.Equals("IF", StringComparison.OrdinalIgnoreCase) || word.Equals("WHILE", StringComparison.OrdinalIgnoreCase)
            || word.Equals("TRY", StringComparison.OrdinalIgnoreCase) || word.Equals("CATCH", StringComparison.OrdinalIgnoreCase)
            || statement.TrimEnd().EndsWith(':'))
        {
            return null;
        }
        if (word.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
        {
            var rest = FirstWord(statement.AsSpan(5).TrimStart());
            if (!(rest.StartsWith("TRAN", StringComparison.OrdinalIgnoreCase) || rest.Equals("DISTRIBUTED", StringComparison.OrdinalIgnoreCase)))
                return null;
        }
        if (word.Equals("DECLARE", StringComparison.OrdinalIgnoreCase) && !statement.Contains('=', StringComparison.Ordinal))
            return null;
        var module = batch.ModuleObjectId != 0 ? batch.ModuleSchema?.Database ?? batch.CurrentDatabase : null;
        var offset = module is not null && batch.ModuleDefinitionText is not null ? batch.ModuleBodyOffset() : 0;
        return new XeStatement(statement, batch.CurrentStatement.StartLine + batch.LineOffset, 2 * (offset + start), end < 0 && module is null ? -1 : 2 * (offset + through),
            batch.ModuleObjectId, module, Parser.Expressions.ObjectProperty.FindObject(module ?? batch.CurrentDatabase, batch.ModuleObjectId), CountsRows(word));
    }

    private static ReadOnlySpan<char> FirstWord(ReadOnlySpan<char> text)
    {
        var at = 0;
        while (at < text.Length && char.IsAsciiLetter(text[at]))
            at++;
        return text[..at];
    }

    /// <summary>
    /// Whether a statement sets the row count its completed event's
    /// <c>last_row_count</c> carries, where a session option, <c>PRINT</c>
    /// and their kind leave the last one standing and an <c>EXEC</c> the last
    /// its body's statements set.
    /// </summary>
    private static bool CountsRows(ReadOnlySpan<char> word) =>
        word.Equals("SELECT", StringComparison.OrdinalIgnoreCase) || word.Equals("INSERT", StringComparison.OrdinalIgnoreCase)
        || word.Equals("UPDATE", StringComparison.OrdinalIgnoreCase) || word.Equals("DELETE", StringComparison.OrdinalIgnoreCase)
        || word.Equals("MERGE", StringComparison.OrdinalIgnoreCase) || word.Equals("WITH", StringComparison.OrdinalIgnoreCase)
        || word.Equals("DECLARE", StringComparison.OrdinalIgnoreCase) || word.IsEmpty;

    public static void Starting(SimulatedDbConnection connection, XeStatement statement)
    {
        if (statement.Module is null)
        {
            XeEvents.Fire(connection, XeEventKind.SqlStatementStarting, @event => XeEvents.Data(@event,
                ("state", XeValue.FromMap(0, null)),
                ("line_number", XeValue.FromSigned(statement.Line)),
                ("offset", XeValue.FromSigned(statement.Offset)),
                ("offset_end", XeValue.FromSigned(statement.OffsetEnd)),
                ("statement", XeValue.FromUnicode(statement.Text))));
            return;
        }
        XeEvents.Fire(connection, XeEventKind.SpStatementStarting, @event => XeEvents.Data(@event,
            ("source_database_id", XeValue.FromUnsigned((ulong)statement.Module.Id)),
            ("object_id", XeValue.FromSigned(statement.ObjectId)),
            ("object_type", XeValue.FromMap(ObjectTypeKey(statement.Object), null)),
            ("state", XeValue.FromMap(0, null)),
            ("nest_level", XeValue.FromUnsigned((ulong)connection.NestingLevel)),
            ("line_number", XeValue.FromSigned(statement.Line)),
            ("offset", XeValue.FromSigned(statement.Offset)),
            ("offset_end", XeValue.FromSigned(statement.OffsetEnd)),
            ("object_name", XeValue.FromUnicode("")),
            ("statement", XeValue.FromUnicode(statement.Text))));
    }

    public static void Completed(SimulatedDbConnection connection, XeStatement statement, long durationMicroseconds)
    {
        // A procedure call counts every row its body's statements did.
        var called = connection.XeCallRows;
        connection.XeCallRows = -1;
        var rows = (ulong)Math.Max(0, called >= 0 ? called : connection.LastStatementRowCount);
        connection.XeModuleRows += (long)rows;
        if (statement.CountsRows)
            connection.XeLastRowCount = rows;
        var last = connection.XeLastRowCount;
        if (statement.Module is null)
        {
            XeEvents.Fire(connection, XeEventKind.SqlStatementCompleted, @event => XeEvents.Data(@event,
                ("duration", XeValue.FromSigned(durationMicroseconds)),
                ("row_count", XeValue.FromUnsigned(rows)),
                ("last_row_count", XeValue.FromUnsigned(last)),
                ("line_number", XeValue.FromSigned(statement.Line)),
                ("offset", XeValue.FromSigned(statement.Offset)),
                ("offset_end", XeValue.FromSigned(statement.OffsetEnd)),
                ("statement", XeValue.FromUnicode(statement.Text))));
            return;
        }
        XeEvents.Fire(connection, XeEventKind.SpStatementCompleted, @event => XeEvents.Data(@event,
            ("source_database_id", XeValue.FromUnsigned((ulong)statement.Module.Id)),
            ("object_id", XeValue.FromSigned(statement.ObjectId)),
            ("object_type", XeValue.FromMap(ObjectTypeKey(statement.Object), null)),
            ("duration", XeValue.FromSigned(durationMicroseconds)),
            ("row_count", XeValue.FromUnsigned(rows)),
            ("last_row_count", XeValue.FromUnsigned(last)),
            ("nest_level", XeValue.FromUnsigned((ulong)connection.NestingLevel)),
            ("line_number", XeValue.FromSigned(statement.Line)),
            ("offset", XeValue.FromSigned(statement.Offset)),
            ("offset_end", XeValue.FromSigned(statement.OffsetEnd)),
            ("object_name", XeValue.FromUnicode("")),
            ("statement", XeValue.FromUnicode(statement.Text))));
    }

    /// <summary>A module's key in the <c>object_type</c> map: <c>PROC</c>, <c>TRIGGER</c>, <c>FUNCTION</c>, <c>TABFUNC</c>.</summary>
    private static ulong ObjectTypeKey(Schemas.SchemaObject? module) => module switch
    {
        Schemas.Procedure => 8272,
        Schemas.Trigger => 21076,
        Schemas.MultiStatementTableValuedFunction => 18004,
        Schemas.ScalarFunction => 20038,
        _ => 0,
    };

    /// <summary>
    /// <c>module_start</c> as <paramref name="caller"/>'s statement calls
    /// <paramref name="procedure"/>: the module's database, id, type code and
    /// name, the calling statement's line and offsets, and an empty
    /// statement, as real reports them (probed 2026-10-16 against SQL Server
    /// 2025).
    /// </summary>
    /// <returns>The rows the caller's module had counted, which <see cref="ModuleEnd"/> adds the body's to.</returns>
    public static long ModuleStart(BatchContext caller, Schemas.Procedure procedure)
    {
        var connection = caller.Connection;
        var enclosing = connection.XeModuleRows;
        connection.XeModuleRows = 0;
        if (!XeEvents.Takes(connection.Simulation, XeEventKind.ModuleStart))
            return enclosing;
        var placed = Place(caller, caller.CurrentStatement.StartIndex);
        XeEvents.Fire(connection, XeEventKind.ModuleStart, @event => XeEvents.Data(@event,
            ("source_database_id", XeValue.FromUnsigned((ulong)procedure.Schema.Database.Id)),
            ("object_id", XeValue.FromSigned(procedure.ObjectId)),
            ("line_number", XeValue.FromSigned(placed?.Line ?? 0)),
            ("offset", XeValue.FromSigned(placed?.Offset ?? 0)),
            ("offset_end", XeValue.FromSigned(placed?.OffsetEnd ?? 0)),
            ("object_type", XeValue.FromAnsi("P ")),
            ("object_name", XeValue.FromUnicode(procedure.Name)),
            ("statement", XeValue.FromUnicode(""))), traceText: CallingText(caller, placed));
        return enclosing;
    }

    /// <summary>
    /// The statement calling a module as SQL Trace's <c>SP:Starting</c> and
    /// <c>SP:Completed</c> report it: the calling statement, or for a procedure
    /// called by RPC the statement real reconstructs for the call (probed
    /// 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static string? CallingText(BatchContext caller, XeStatement? placed) =>
        caller.Parser.Command is { CommandType: System.Data.CommandType.StoredProcedure } command && caller.ModuleObjectId == 0
            ? XeEvents.RpcStatement(command)
            : placed?.Text;

    /// <summary><c>module_end</c>: <see cref="ModuleStart"/>'s fields with the body's duration and the rows its statements counted.</summary>
    public static void ModuleEnd(BatchContext caller, Schemas.Procedure procedure, long durationMicroseconds, long enclosing)
    {
        var connection = caller.Connection;
        var rows = connection.XeModuleRows;
        connection.XeModuleRows = enclosing;
        connection.XeCallRows = rows;
        if (!XeEvents.Takes(connection.Simulation, XeEventKind.ModuleEnd))
            return;
        var placed = Place(caller, caller.CurrentStatement.StartIndex);
        XeEvents.Fire(connection, XeEventKind.ModuleEnd, @event => XeEvents.Data(@event,
            ("source_database_id", XeValue.FromUnsigned((ulong)procedure.Schema.Database.Id)),
            ("object_id", XeValue.FromSigned(procedure.ObjectId)),
            ("duration", XeValue.FromUnsigned((ulong)durationMicroseconds)),
            ("row_count", XeValue.FromUnsigned((ulong)Math.Max(0, rows))),
            ("line_number", XeValue.FromSigned(placed?.Line ?? 0)),
            ("offset", XeValue.FromSigned(placed?.Offset ?? 0)),
            ("offset_end", XeValue.FromSigned(placed?.OffsetEnd ?? 0)),
            ("object_type", XeValue.FromAnsi("P ")),
            ("object_name", XeValue.FromUnicode(procedure.Name)),
            ("statement", XeValue.FromUnicode(""))), traceText: CallingText(caller, placed));
    }
}

/// <summary>A statement as the statement events place it, and the module it runs in when it does.</summary>
internal sealed class XeStatement(string text, int line, int offset, int offsetEnd, int objectId, Database? module, Schemas.SchemaObject? @object, bool countsRows)
{
    public readonly string Text = text;
    public readonly int Line = line;
    public readonly int Offset = offset;
    public readonly int OffsetEnd = offsetEnd;
    public readonly int ObjectId = objectId;

    /// <summary>The database of the module the statement runs in; null for a batch's statement.</summary>
    public readonly Database? Module = module;

    public readonly Schemas.SchemaObject? Object = @object;
    public readonly bool CountsRows = countsRows;

    /// <summary>When the statement began, for its completed event's duration.</summary>
    public readonly long StartedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
}
