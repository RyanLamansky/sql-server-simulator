using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The API server cursor procedures written as EXEC text — sp_cursoropen,
// sp_cursorfetch, sp_cursor, sp_cursoroption, sp_cursorclose and the prepared
// family — over the per-session handles their TDS RPC form uses, through the
// operations both share (Simulation.ApiCursors.cs). Each checks its arguments
// as real's does, each refusal Msg 16902 or 16903 at a state of its own
// (probed 2026-10-16 against SQL Server 2025).
partial class Simulation
{
    /// <summary>
    /// Passes an API server cursor procedure's outcomes through, marking an
    /// error it raises as ending only the call: the caller's batch goes on —
    /// a statement the cursor's open names that doesn't exist included — and
    /// its <c>@@ROWCOUNT</c> stays where the statement before left it (probed
    /// 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> ApiCursorCall(IEnumerable<SimulatedStatementOutcome> outcomes)
    {
        using var enumerator = outcomes.GetEnumerator();
        while (true)
        {
            try
            {
                if (!enumerator.MoveNext())
                    yield break;
            }
            catch (SimulatedSqlException error)
            {
                error.EndedCalledBatch = true;
                throw;
            }
            yield return enumerator.Current;
        }
    }

    /// <summary>
    /// <c>sp_cursoropen @cursor OUTPUT, @stmt [, @scrollopt [, @ccopt [, @rowcount
    /// [, @params, value …]]]]</c>: opens the statement as an API server cursor
    /// and sends its columns, with the hidden ROWSTAT, as an empty result set.
    /// The parameter definitions bind the values that follow only under
    /// PARAMETERIZED_STMT (0x1000).
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpCursorOpen(BatchContext batch)
    {
        const string procedure = "sp_cursoropen";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var cursorSlot = HandleSlot(arguments, 0, "cursor");
        if (arguments.Count < 2)
        {
            _ = cursorSlot?.Value = SqlValue.FromInt32(0);
            throw AtProcedureLine(SimulatedSqlException.ApiCursorWrongParameterCount(procedure, 29), procedure, 1);
        }
        if (cursorSlot is null)
            throw AtProcedureLine(SimulatedSqlException.ApiCursorParameterInvalid(procedure, "cursor", 16), procedure, 1);
        cursorSlot.Value = SqlValue.FromInt32(0);
        var statement = StatementArgument(arguments, 1, procedure);
        var options = CursorOptionArguments(arguments, 2, procedure);
        // Without PARAMETERIZED_STMT the definitions and values are ignored,
        // so a statement naming a parameter fails to declare it (probed
        // 2026-10-16 against SQL Server 2025).
        var parameterized = (options.Scroll & 0x1000) != 0;
        var definition = parameterized ? TextArgument(arguments, 5, "params") ?? "" : "";
        foreach (var outcome in this.OpenCursorFromText(batch, procedure, statement, definition, arguments, valueStart: parameterized ? 6 : arguments.Count, cursorSlot, options, parameterizedWithoutDefinition: arguments.Count <= 5, definitionWithoutParameterized: false, prepared: null))
            yield return outcome;
    }

    /// <summary>
    /// <c>sp_cursorexecute @prepared_handle, @cursor OUTPUT [, @scrollopt [, @ccopt
    /// [, @rowcount [, value …]]]]</c>: opens a statement <c>sp_cursorprepare</c>
    /// prepared, its declared parameters bound from the values; a handle the
    /// session doesn't hold is Msg 8179 state 4.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpCursorExecute(BatchContext batch)
    {
        const string procedure = "sp_cursorexecute";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        if (arguments.Count < 2)
            throw AtProcedureLine(SimulatedSqlException.ApiCursorWrongParameterCount(procedure, 5), procedure, 1);
        if (ArgumentFor(arguments, 0, "prepared_handle") is not { Value: { IsNull: false } handleValue } || handleValue.Type != SqlType.Int32)
            throw AtProcedureLine(SimulatedSqlException.ApiCursorParameterInvalid(procedure, "prepared handle", 16), procedure, 1);
        var cursorSlot = HandleSlot(arguments, 1, "cursor")
            ?? throw AtProcedureLine(SimulatedSqlException.ApiCursorParameterInvalid(procedure, "cursor", 16), procedure, 1);
        cursorSlot.Value = SqlValue.FromInt32(0);
        if (!batch.Connection.PreparedApiCursors.TryGetValue(handleValue.AsInt32, out var prepared))
        {
            var missing = AtProcedureLine(SimulatedSqlException.CouldNotFindPreparedStatement(handleValue.AsInt32, state: 4), procedure, 1);
            missing.SystemProcedureReturnCode = 8179;
            throw missing;
        }
        var options = CursorOptionArguments(arguments, 2, procedure);
        foreach (var outcome in this.OpenCursorFromText(batch, procedure, prepared.Statement, prepared.Declaration, arguments, valueStart: 5, cursorSlot, options, parameterizedWithoutDefinition: false, definitionWithoutParameterized: false, prepared: null))
            yield return outcome;
    }

    /// <summary>
    /// <c>sp_cursorprepexec @prepared_handle OUTPUT, @cursor OUTPUT, @params, @stmt
    /// [, @scrollopt [, @ccopt [, @rowcount [, value …]]]]</c>: prepares the
    /// statement and opens it in one call. Parameter definitions without
    /// PARAMETERIZED_STMT are Msg 16902 state 50, and a statement that fails
    /// is its errors then Msg 8180, the handle set NULL and nothing prepared.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpCursorPrepExec(BatchContext batch)
    {
        const string procedure = "sp_cursorprepexec";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        if (arguments.Count < 4)
            throw AtProcedureLine(SimulatedSqlException.ApiCursorWrongParameterCount(procedure, 29), procedure, 1);
        var handleSlot = HandleSlot(arguments, 0, "prepared_handle")
            ?? throw AtProcedureLine(SimulatedSqlException.ApiCursorParameterInvalid(procedure, "prepared handle", 16), procedure, 1);
        var cursorSlot = HandleSlot(arguments, 1, "cursor")
            ?? throw AtProcedureLine(SimulatedSqlException.ApiCursorParameterInvalid(procedure, "cursor", 16), procedure, 1);
        cursorSlot.Value = SqlValue.FromInt32(0);
        var definition = TextArgument(arguments, 2, "params") ?? "";
        var statement = StatementArgument(arguments, 3, procedure);
        var options = CursorOptionArguments(arguments, 4, procedure);
        foreach (var outcome in this.OpenCursorFromText(batch, procedure, statement, definition, arguments, valueStart: 7, cursorSlot, options, parameterizedWithoutDefinition: false, definitionWithoutParameterized: definition.Length > 0, prepared: (handleSlot, new PreparedApiCursor(statement, definition))))
            yield return outcome;
    }

    /// <summary>The scroll and concurrency options an open's arguments carry, and the variables each reports back through.</summary>
    private sealed class CursorOptionArgs
    {
        public int Scroll, Cc, RowCountSent;
        public VariableSlot? ScrollSlot, CcSlot, RowCountSlot;
    }

    /// <summary>
    /// The <c>@scrollopt</c>, <c>@ccopt</c> and <c>@rowcount</c> arguments
    /// from <paramref name="start"/> on: each an <c>int</c>, an output one an
    /// <c>int</c> variable — else Msg 16902, state 20 for the first and 25 for
    /// the other two.
    /// </summary>
    private static CursorOptionArgs CursorOptionArguments(List<ProcArgument> arguments, int start, string procedure, bool opens = true)
    {
        var options = new CursorOptionArgs();
        string[] names = ["scrollopt", "ccopt", "rowcount"];
        for (var i = 0; i < names.Length; i++)
        {
            if (ArgumentFor(arguments, start + i, names[i]) is not { IsDefault: false } argument)
                continue;
            var typed = argument.OutputSlot is { } slot ? slot.DeclaredType == SqlType.Int32 : argument.Value.Type == SqlType.Int32;
            if (!typed || (i < 2 && argument.Value.IsNull))
                throw AtProcedureLine(SimulatedSqlException.ApiCursorParameterInvalid(procedure, names[i], i == 0 ? (byte)20 : (byte)25), procedure, 1);
            var value = argument.Value.IsNull ? 0 : argument.Value.AsInt32;
            switch (i)
            {
                case 0:
                    (options.Scroll, options.ScrollSlot) = (value, argument.OutputSlot);
                    break;
                case 1:
                    (options.Cc, options.CcSlot) = (value, argument.OutputSlot);
                    break;
                default:
                    (options.RowCountSent, options.RowCountSlot) = (value, argument.OutputSlot);
                    break;
            }
        }

        // An open takes one scroll type and one concurrency, with the flags
        // past them it knows: scrollopt's state 24, ccopt's 26 (probed
        // 2026-10-16 against SQL Server 2025).
        if (opens && ArgumentFor(arguments, start, "scrollopt") is { IsDefault: false }
            && ((options.Scroll & 0x1F) is not (1 or 2 or 4 or 8 or 16) || (options.Scroll & 0x4FE0) != 0))
        {
            throw AtProcedureLine(SimulatedSqlException.ApiCursorParameterInvalid(procedure, "scrollopt", 24), procedure, 1);
        }
        if (opens && ArgumentFor(arguments, start + 1, "ccopt") is { IsDefault: false }
            && ((options.Cc & 0xF) is not (1 or 2 or 4 or 8) || (options.Cc & 0xBFF0) != 0))
        {
            throw AtProcedureLine(SimulatedSqlException.ApiCursorParameterInvalid(procedure, "ccopt", 26), procedure, 1);
        }
        return options;
    }

    /// <summary>The statement argument: a non-empty string, else Msg 16902 state 18.</summary>
    private static string StatementArgument(List<ProcArgument> arguments, int position, string procedure) =>
        ArgumentFor(arguments, position, "stmt") is { IsDefault: false, Value: { IsNull: false } value } && SqlType.IsStringCategory(value.Type)
            && value.CoerceTo(SqlType.NVarchar).AsString is { Length: > 0 } text
            ? text
            : throw AtProcedureLine(SimulatedSqlException.ApiCursorParameterInvalid(procedure, "stmt", 18), procedure, 1);

    /// <summary>The <c>int</c> variable a handle argument marked <c>OUTPUT</c> names, or null for any other argument.</summary>
    private static VariableSlot? HandleSlot(List<ProcArgument> arguments, int position, string name) =>
        ArgumentFor(arguments, position, name) is { OutputSlot: { } slot } && slot.DeclaredType == SqlType.Int32 ? slot : null;

    /// <summary>
    /// The open the three text-form opens share: binds the values from
    /// <paramref name="valueStart"/> on (and any named one) to the statement's
    /// <paramref name="definition"/>, opens the cursor, reports the handle and
    /// the options through their variables, and sends the announce — or, with
    /// <paramref name="prepared"/>, stores the preparation once the open
    /// succeeds.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> OpenCursorFromText(
        BatchContext batch,
        string procedure,
        string statement,
        string definition,
        List<ProcArgument> arguments,
        int valueStart,
        VariableSlot cursorSlot,
        CursorOptionArgs options,
        bool parameterizedWithoutDefinition,
        bool definitionWithoutParameterized,
        (VariableSlot Slot, PreparedApiCursor Statement)? prepared)
    {
        var connection = batch.Connection;
        var declared = definition.Length == 0 ? [] : ParseSpExecuteSqlParamDefinitions(definition, connection);
        var values = new List<ProcArgument>();
        for (var i = 0; i < arguments.Count; i++)
        {
            if (i >= valueStart || (arguments[i].Name is { } name && declared.Exists(parameter => BuiltInToken.Equals(parameter.Name, name.TrimStart('@')))))
                values.Add(arguments[i]);
        }
        if (declared.Count == 0 && values.Count > 0)
        {
            _ = options.RowCountSlot?.Value = SqlValue.FromInt32(0);
            throw SimulatedSqlException.ArgumentsSuppliedToParameterlessRoutine("").PinLine(0);
        }

        var bindings = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        var opening = OpenApiCursor(
            connection,
            procedure,
            statement,
            options.Scroll,
            options.Cc,
            options.RowCountSent,
            parameterizedWithoutDefinition,
            definitionWithoutParameterized,
            (name, declareOpen) =>
            {
                BindCursorValues(declared, values, definition, statement, bindings);
                foreach (var outcome in this.ExecuteDynamicBatch(batch, declareOpen, bindings, viaSystemProcedure: true, apiServerCursor: true))
                {
                    if (outcome is SimulatedErrorOutcome failed)
                        throw failed.Exception;
                }
                return connection.Cursors[name];
            },
            command => new BatchContext(command, CopyOfBindings(bindings)),
            preparing: prepared is not null);

        cursorSlot.Value = SqlValue.FromInt32(opening.Handle);
        _ = options.ScrollSlot?.Value = SqlValue.FromInt32(opening.Scroll);
        _ = options.CcSlot?.Value = SqlValue.FromInt32(opening.Cc);
        if (opening.RowCount is { } rowcount)
            _ = options.RowCountSlot?.Value = SqlValue.FromInt32(rowcount);
        if (prepared is { } preparation && !(definitionWithoutParameterized && opening.Error is not null))
        {
            preparation.Slot.Value = opening.Error is null
                ? SqlValue.FromInt32(connection.StorePreparedCursor(preparation.Statement))
                : SqlValue.Null(SqlType.Int32);
        }
        if (opening.Error is { } error)
        {
            error.SystemProcedureReturnCode = opening.ReturnStatus;
            throw error;
        }
        return [ApiCursorResult(opening.Cursor!.Cursor, opening.Fetched ?? [])];
    }

    /// <summary>
    /// Binds each declared parameter of a cursor's statement from
    /// <paramref name="values"/> — positionally, then by name — into
    /// <paramref name="bindings"/>; one left out is Msg 8178.
    /// </summary>
    private static void BindCursorValues(List<SpExecuteSqlParam> declared, List<ProcArgument> values, string definition, string statement, Dictionary<string, VariableSlot> bindings)
    {
        var positional = 0;
        var bound = new SqlValue?[declared.Count];
        foreach (var value in values)
        {
            var index = value.Name is { } name ? declared.FindIndex(parameter => BuiltInToken.Equals(parameter.Name, name.TrimStart('@'))) : positional++;
            if (index < 0 || index >= declared.Count)
                throw SimulatedSqlException.TooManyArgumentsToFunction("").PinLine(0);
            if (!value.IsDefault)
                bound[index] = value.Value;
        }
        for (var i = 0; i < declared.Count; i++)
        {
            var parameter = declared[i];
            var value = bound[i] ?? parameter.Default ?? throw SimulatedSqlException.ParameterizedQueryExpectsParameter(definition, statement, "@" + parameter.Name).PinLine(1);
            bindings[parameter.Name] = new VariableSlot(parameter.Type, parameter.DeclaredMaxLength, BindParameterValue(value, parameter.Type, parameter.DeclaredMaxLength, procedure: ""), parameter: null);
        }
    }

    /// <summary>A copy of <paramref name="bindings"/>, slot by slot, for one fetch's batch to read.</summary>
    private static Dictionary<string, VariableSlot> CopyOfBindings(Dictionary<string, VariableSlot> bindings)
    {
        var copy = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        foreach (var (name, slot) in bindings)
            copy[name] = new VariableSlot(slot.DeclaredType, slot.DeclaredMaxLength, slot.Value, parameter: null);
        return copy;
    }

    /// <summary>
    /// <c>sp_cursorfetch @cursor [, @fetchtype [, @rownum [, @nrows]]]</c>: sends
    /// up to <c>@nrows</c> rows (20 by default) of an API server cursor, NEXT
    /// unless the fetch type says otherwise; an INFO fetch (256) reports its
    /// position and row count through <c>@rownum</c> and <c>@nrows</c>, which
    /// it takes only as <c>OUTPUT</c> and no other fetch takes so.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpCursorFetch(BatchContext batch)
    {
        const string procedure = "sp_cursorfetch";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        if (arguments.Count == 0)
            throw AtProcedureLine(SimulatedSqlException.ApiCursorWrongParameterCount(procedure, 1), procedure, 1);
        var handle = IntArgument(arguments, 0, "cursor", procedure, "cursor", 1) ?? 0;
        var fetchType = ArgumentFor(arguments, 1, "fetchtype") is { IsDefault: false } ? IntArgument(arguments, 1, "fetchtype", procedure, "fetchtype", 3) ?? 0 : 2;
        var rownumArgument = ArgumentFor(arguments, 2, "rownum");
        var nrowsArgument = ArgumentFor(arguments, 3, "nrows");
        var info = fetchType == 0x100;
        if (info)
        {
            if (rownumArgument is null || nrowsArgument is null)
                throw AtProcedureLine(SimulatedSqlException.ApiCursorWrongParameterCount(procedure, 1), procedure, 1);
            if (rownumArgument.Value.OutputSlot is not { } rownumSlot || rownumSlot.DeclaredType != SqlType.Int32)
                throw AtProcedureLine(SimulatedSqlException.ApiCursorParameterInvalid(procedure, "rownum", 5), procedure, 1);
            if (nrowsArgument.Value.OutputSlot is not { } nrowsSlot || nrowsSlot.DeclaredType != SqlType.Int32)
                throw AtProcedureLine(SimulatedSqlException.ApiCursorParameterInvalid(procedure, "nrows", 6), procedure, 1);
            var reported = FetchApiCursor(batch.Connection, handle, fetchType, 0, 0);
            if (reported.Error is { } missing)
                throw missing;
            rownumSlot.Value = SqlValue.FromInt32(reported.InfoRowNumber);
            nrowsSlot.Value = SqlValue.FromInt32(reported.InfoRowCount);
            yield break;
        }
        if (fetchType is not (1 or 2 or 4 or 8 or 16 or 32))
            throw AtProcedureLine(SimulatedSqlException.ApiCursorParameterInvalid(procedure, "fetchtype", fetchType == 0x80 ? (byte)45 : (byte)4), procedure, 1);
        if (rownumArgument is { OutputSlot: not null })
            throw AtProcedureLine(SimulatedSqlException.ApiCursorParameterInvalid(procedure, "rownum", 5), procedure, 1);
        if (nrowsArgument is { OutputSlot: not null })
            throw AtProcedureLine(SimulatedSqlException.ApiCursorParameterInvalid(procedure, "nrows", 6), procedure, 1);
        var rownum = rownumArgument is { IsDefault: false } ? IntArgument(arguments, 2, "rownum", procedure, "rownum", 5) ?? 0 : 0;
        var nrows = nrowsArgument is { IsDefault: false } ? IntArgument(arguments, 3, "nrows", procedure, "nrows", 7) ?? 20 : 20;
        if (nrows < 0)
            throw AtProcedureLine(SimulatedSqlException.ApiCursorParameterInvalid(procedure, "nrows", 7), procedure, 1);

        var fetching = FetchApiCursor(batch.Connection, handle, fetchType, rownum, nrows);
        if (fetching.Error is { } error)
        {
            error.SystemProcedureReturnCode = fetching.ReturnStatus;
            throw error;
        }
        yield return ApiCursorResult(fetching.Cursor!.Cursor, fetching.Rows!);
    }

    /// <summary>
    /// An <c>int</c> argument's value — null for NULL — or Msg 16902 at
    /// <paramref name="state"/> for one of another type; a NULL cursor handle
    /// is refused the same way.
    /// </summary>
    private static int? IntArgument(List<ProcArgument> arguments, int position, string name, string procedure, string reported, byte state)
    {
        if (ArgumentFor(arguments, position, name) is not { IsDefault: false } argument)
            return null;
        if (argument.Value.Type != SqlType.Int32 || (argument.Value.IsNull && position == 0))
            throw AtProcedureLine(SimulatedSqlException.ApiCursorParameterInvalid(procedure, reported, state), procedure, 1);
        return argument.Value.IsNull ? null : argument.Value.AsInt32;
    }

    /// <summary><c>sp_cursorclose @cursor</c>: closes and releases an API server cursor.</summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpCursorClose(BatchContext batch)
    {
        const string procedure = "sp_cursorclose";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        if (arguments.Count == 0)
            throw AtProcedureLine(SimulatedSqlException.ApiCursorWrongParameterCount(procedure, 1), procedure, 1);
        if (CloseApiCursor(batch.Connection, IntArgument(arguments, 0, "cursor", procedure, "cursor", 1) ?? 0) is { } error)
            throw error;
    }

    /// <summary><c>sp_cursoroption @cursor, @code, @value</c>: sets one of an API server cursor's options.</summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpCursorOption(BatchContext batch)
    {
        const string procedure = "sp_cursoroption";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        if (arguments.Count < 3)
            throw AtProcedureLine(SimulatedSqlException.ApiCursorWrongParameterCount(procedure, 4), procedure, 1);
        var handle = IntArgument(arguments, 0, "cursor", procedure, "cursor", 1) ?? 0;
        var code = IntArgument(arguments, 1, "code", procedure, "code", 15) ?? 0;
        if (SetApiCursorOption(batch.Connection, handle, code, ArgumentFor(arguments, 2, "value")?.Value ?? SqlValue.Null(SqlType.Int32)) is { } error)
            throw error;
    }

    /// <summary>
    /// <c>sp_cursor @cursor, @optype, @rownum, @table [, @column = value …]</c>:
    /// a positioned UPDATE, DELETE or SETPOSITION on a fetch-buffer row, or an
    /// INSERT through the cursor's table, each named value a column; an empty
    /// table name means the cursor's own table.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpCursor(BatchContext batch)
    {
        const string procedure = "sp_cursor";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        if (arguments.Count < 3)
            throw AtProcedureLine(SimulatedSqlException.ApiCursorWrongParameterCount(procedure, 6), procedure, 1);
        var handle = IntArgument(arguments, 0, "cursor", procedure, "cursor", 1) ?? 0;
        var optype = IntArgument(arguments, 1, "optype", procedure, "optype", 33) ?? 0;
        // One operation, ABSOLUTE (0x40) aside.
        if ((optype & ~0x7F) != 0 || (optype & 0x3F) is not (1 or 2 or 4 or 8 or 16 or 32))
            throw AtProcedureLine(SimulatedSqlException.ApiCursorParameterInvalid(procedure, "optype", 33), procedure, 1);
        var rownum = IntArgument(arguments, 2, "rownum", procedure, "rownum", 34) ?? 0;
        var setPositionOnly = (optype & 0x7) == 0;
        if (setPositionOnly && arguments.Count > 3)
            throw AtProcedureLine(SimulatedSqlException.ApiCursorTooManyParameters(procedure), procedure, 1);
        if (!setPositionOnly && arguments.Count < 4)
            throw AtProcedureLine(SimulatedSqlException.ApiCursorWrongParameterCount(procedure, 7), procedure, 1);
        var table = "";
        if (!setPositionOnly)
        {
            table = ArgumentFor(arguments, 3, "table") is { IsDefault: false, Value: { IsNull: false } tableValue } && SqlType.IsStringCategory(tableValue.Type)
                ? tableValue.CoerceTo(SqlType.NVarchar).AsString
                : throw AtProcedureLine(SimulatedSqlException.ApiCursorParameterInvalid(procedure, "table", 36), procedure, 1);
        }
        var variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        var valueNames = new List<string>();
        for (var i = 4; i < arguments.Count; i++)
        {
            if (arguments[i].Name is not { } column)
                throw AtProcedureLine(SimulatedSqlException.ApiCursorValuesInvalid(), procedure, 1);
            var value = arguments[i].Value;
            var name = column.TrimStart('@');
            variables[name] = new VariableSlot(value.Type, null, value, parameter: null);
            valueNames.Add("@" + name);
        }

        var operating = OperateApiCursor(batch.Connection, handle, optype, rownum, table, valueNames, write =>
        {
            var affected = 0;
            foreach (var outcome in this.ExecuteDynamicBatch(batch, write, variables, viaSystemProcedure: true))
            {
                switch (outcome)
                {
                    case SimulatedErrorOutcome failed:
                        throw failed.Exception;
                    case SimulatedNonQuery { RecordsAffected: >= 0 } counted:
                        affected += counted.RecordsAffected;
                        break;
                }
            }
            return affected;
        });
        foreach (var count in operating.Counts)
            yield return new SimulatedNonQuery(count) { DoneKind = StatementDoneKind.CursorOperation };
        if (operating.Error is { } error)
        {
            error.SystemProcedureReturnCode = operating.ReturnStatus;
            throw error;
        }
    }

    /// <summary>
    /// <c>sp_cursorprepare @prepared_handle OUTPUT, @params, @stmt [, @options
    /// [, @scrollopt [, @ccopt]]]</c>: checks the statement opens as a cursor,
    /// sends its columns as an empty result set and stores it under the
    /// session's next cursor-preparation handle, echoing the two options when
    /// they are passed <c>OUTPUT</c>. <c>@options</c> takes only 1
    /// (RETURN_METADATA), else Msg 214; a statement that fails is its errors
    /// then Msg 8180.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpCursorPrepare(BatchContext batch)
    {
        const string procedure = "sp_cursorprepare";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        if (arguments.Count < 3)
            throw AtProcedureLine(SimulatedSqlException.ApiCursorWrongParameterCount(procedure, 5), procedure, 1);
        var handleSlot = HandleSlot(arguments, 0, "prepared_handle")
            ?? throw AtProcedureLine(SimulatedSqlException.ApiCursorParameterInvalid(procedure, "prepared handle", 16), procedure, 1);
        handleSlot.Value = SqlValue.Null(SqlType.Int32);
        if (ArgumentFor(arguments, 1, "params") is { IsDefault: false, Value: { IsNull: false } paramsValue } && !SqlType.IsNationalStringCategory(paramsValue.Type))
            throw TypedRefusal(SimulatedSqlException.ProcedureExpectsParameterOfType("params", "ntext/nchar/nvarchar", state: 3), procedure);
        var definition = TextArgument(arguments, 1, "params") ?? "";
        var statement = StatementArgument(arguments, 2, procedure);
        if (ArgumentFor(arguments, 3, "options") is { IsDefault: false } optionsArgument
            && (optionsArgument.Value.IsNull || optionsArgument.Value.Type != SqlType.Int32 || optionsArgument.Value.AsInt32 != 1))
        {
            throw TypedRefusal(SimulatedSqlException.ProcedureExpectsParameterOfType("@options", "int", state: 3), procedure);
        }
        var options = CursorOptionArguments(arguments, 4, procedure, opens: false);

        // The statement opens once, its parameters NULL, for its columns; the
        // check takes no cursor handle (probed 2026-10-16 against SQL Server
        // 2025: the session's next open still takes the first).
        var connection = batch.Connection;
        var bindings = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        foreach (var parameter in definition.Length == 0 ? [] : ParseSpExecuteSqlParamDefinitions(definition, connection))
            bindings[parameter.Name] = new VariableSlot(parameter.Type, parameter.DeclaredMaxLength, SqlValue.Null(parameter.Type), parameter: null);
        var handleBefore = connection.LastCursorHandle;
        var opening = OpenApiCursor(
            connection,
            procedure,
            statement,
            options.Scroll == 0 ? 1 : options.Scroll & 0x1F,
            1,
            0,
            parameterizedWithoutDefinition: false,
            definitionWithoutParameterized: false,
            (name, declareOpen) =>
            {
                foreach (var outcome in this.ExecuteDynamicBatch(batch, declareOpen, bindings, viaSystemProcedure: true, apiServerCursor: true))
                {
                    if (outcome is SimulatedErrorOutcome failed)
                        throw failed.Exception;
                }
                return connection.Cursors[name];
            },
            command => new BatchContext(command, CopyOfBindings(bindings)),
            preparing: true);
        if (opening.Cursor is { } described)
            _ = CloseApiCursor(connection, described.Handle);
        connection.LastCursorHandle = handleBefore;
        if (opening.Error is { } error)
        {
            error.SystemProcedureReturnCode = opening.ReturnStatus;
            throw error;
        }

        handleSlot.Value = SqlValue.FromInt32(connection.PrepareCursor(statement, definition));
        _ = options.ScrollSlot?.Value = SqlValue.FromInt32(options.Scroll);
        _ = options.CcSlot?.Value = SqlValue.FromInt32(options.Cc);
        yield return ApiCursorResult(opening.Cursor!.Cursor, []);
    }

    /// <summary>A Msg 214 type refusal from <paramref name="procedure"/>'s line 1, which returns its number.</summary>
    private static SimulatedSqlException TypedRefusal(SimulatedSqlException refusal, string procedure)
    {
        var error = AtProcedureLine(refusal, procedure, 1);
        error.SystemProcedureReturnCode = refusal.Number;
        return error;
    }

    /// <summary>
    /// <c>sp_cursorunprepare @prepared_handle</c>: releases a prepared cursor
    /// statement — or a prepared statement, as <c>sp_unprepare</c> releases a
    /// prepared cursor's; a handle the session doesn't hold is Msg 8179.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpCursorUnprepare(BatchContext batch)
    {
        const string procedure = "sp_cursorunprepare";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        if (ArgumentFor(arguments, 0, "prepared_handle") is not { IsDefault: false } argument)
        {
            var missing = AtProcedureLine(SimulatedSqlException.ProcedureExpectsParameter("sp_unprepare/sp_cursorunprepare", "handle", state: 13), procedure, 1);
            missing.SystemProcedureReturnCode = 201;
            throw missing;
        }
        if (argument.Value.IsNull || argument.Value.Type != SqlType.Int32)
            throw TypedRefusal(SimulatedSqlException.ProcedureExpectsParameterOfType("@handle", "int"), procedure);
        if (!Unprepare(batch.Connection, argument.Value.AsInt32))
        {
            var missing = AtProcedureLine(SimulatedSqlException.CouldNotFindPreparedStatement(argument.Value.AsInt32, state: 8), procedure, 1);
            missing.SystemProcedureReturnCode = 8179;
            throw missing;
        }
    }

    /// <summary>
    /// Releases prepared handle <paramref name="handle"/> from whichever set
    /// holds it: <c>sp_unprepare</c> releases a prepared cursor's handle too
    /// (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    internal static bool Unprepare(SimulatedDbConnection connection, int handle) =>
        connection.PreparedStatements.Remove(handle) || connection.PreparedApiCursors.Remove(handle);

    /// <summary>
    /// The result set an API server cursor sends its rows in, the hidden
    /// ROWSTAT last; a cursor that identifies its rows by key — keyset,
    /// dynamic or forward-only — marks the key's columns, which SQL Server's
    /// OLE DB provider reads a linked server's writable columns from (probed
    /// 2026-10-11 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlResultSet ApiCursorResult(Cursor cursor, IEnumerable<SqlValue[]> rows) =>
        cursor.FetchResult(rows);
}
