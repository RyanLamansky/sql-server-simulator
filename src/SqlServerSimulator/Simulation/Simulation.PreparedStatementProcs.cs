using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sp_prepare / sp_execute / sp_prepexec / sp_unprepare written as EXEC text:
// the prepared-statement procedures a TDS client sends as RPC requests, over
// the same per-session handles (SimulatedDbConnection.PreparedStatements), so
// a handle one form prepares the other runs (probed 2026-10-15 against SQL
// Server 2025).
partial class Simulation
{
    private static readonly string[] PrepareParameterNames = ["handle", "params", "stmt", "options"];

    /// <summary>
    /// <c>sp_prepare @handle OUTPUT, @params, @stmt [, @options]</c>: compiles
    /// the statement against its declarations and, if it compiles, stores it
    /// under the session's next handle and sends its result's columns as an
    /// empty result set, the statement run under <c>SET FMTONLY ON</c>. A
    /// statement that doesn't compile sends its errors then Msg 8180 and
    /// leaves the handle unset; a handle argument not marked <c>OUTPUT</c> is
    /// Msg 591, which ends the batch, and one of another type than <c>int</c>
    /// Msg 214.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpPrepare(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        for (var i = 0; i < 3; i++)
        {
            if (ArgumentFor(arguments, i, PrepareParameterNames[i]) is not { IsDefault: false })
                throw SimulatedSqlException.ProcedureExpectsParameter("sp_prepare", PrepareParameterNames[i], state: 11);
        }
        RequireSystemProcedureShape("sp_prepare", arguments, PrepareParameterNames);
        var handle = PreparedHandleSlot(arguments, "sp_prepare");
        var declaration = TextArgument(arguments, 1, "params");
        var statement = TextArgument(arguments, 2, "stmt") ?? "";

        var connection = batch.Connection;
        List<SpExecuteSqlParam> parameters;
        try
        {
            parameters = ParseSpExecuteSqlParamDefinitions(declaration ?? "", connection);
        }
        catch (SimulatedSqlException declarationError)
        {
            throw NotPrepared(declarationError);
        }
        var variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        var tableVariables = new Dictionary<string, HeapTable>(BatchContext.VariableNameComparer);
        foreach (var parameter in parameters)
        {
            if (parameter.TableType is { } tableType)
                tableVariables[parameter.Name] = CloneTableValuedArgument(tableType, parameter.Name, batch, supplied: null);
            else
                variables[parameter.Name] = new VariableSlot(parameter.Type, parameter.DeclaredMaxLength, SqlValue.Null(parameter.Type), parameter: null);
        }
        using (var command = new SimulatedDbCommand(this, connection))
        {
#pragma warning disable CA2100 // the prepared statement is the application's input; it is compiled, never run
            command.CommandText = statement.Length == 0 ? " " : statement;
#pragma warning restore CA2100
            var statementBatch = new BatchContext(command, variables, new ProcFrame("<dynamic-sql>", isDynamicSql: true), tableVariables);
            if (this.CompileBatch(CompileContextFor(statementBatch, command), key: null, out _) is { } compileError)
                throw NotPrepared(compileError);
        }

        handle.Value = SqlValue.FromInt32(connection.Prepare(statement, declaration ?? "")).CoerceTo(handle.DeclaredType);

        // The statement's columns, as FMTONLY answers them: nothing runs.
        var describedVariables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        foreach (var (name, slot) in variables)
            describedVariables[name] = new VariableSlot(slot.DeclaredType, slot.DeclaredMaxLength, slot.Value, parameter: null);
        var savedFmtOnly = connection.FmtOnly;
        connection.FmtOnly = true;
        try
        {
            foreach (var outcome in this.ExecuteDynamicBatch(batch, statement, describedVariables, viaSystemProcedure: true, tableVariables: tableVariables))
                yield return outcome;
        }
        finally
        {
            connection.FmtOnly = savedFmtOnly;
        }
    }

    /// <summary>A preparation's refusal: what compiling raised, then Msg 8180 from the procedure's line 1.</summary>
    private static SimulatedSqlException NotPrepared(SimulatedSqlException compileError)
    {
        var notPrepared = SimulatedSqlException.StatementsCouldNotBePrepared();
        notPrepared.PreserveDiagnostics(1, "sp_prepare");
        var report = SimulatedSqlException.Aggregate([compileError, notPrepared]).ReportingLastToAtAtError();
        report.EndedCalledBatch = true;
        return report;
    }

    /// <summary>
    /// <c>sp_execute @handle [, value …]</c>: runs a prepared statement with
    /// its arguments bound as <c>sp_executesql</c> binds them; a handle the
    /// session doesn't hold is Msg 8179.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpExecute(BatchContext batch, string? returnCodeVar, bool insertExecSource)
    {
        var context = batch.Parser;
        if (IsStatementBoundary(context.Token))
        {
            if (batch.IsSkipping)
                return [];
            throw AtProcedureLine(SimulatedSqlException.ProcedureExpectsParameter("sp_execute", "handle", state: 12), "sp_execute", 1);
        }
        _ = TryConsumeSpExecuteSqlArgumentName(context);
        var (handle, _) = ParseSpExecuteSqlValueArg(context, batch);
        return ParseSpExecuteSql(batch, returnCodeVar, insertExecSource, prepared: () => FindPrepared(batch, handle, "sp_execute", state: 4));
    }

    /// <summary>
    /// <c>sp_prepexec @handle OUTPUT, @params, @stmt [, value …]</c>: prepares
    /// the statement under the session's next handle, then runs it as
    /// <c>sp_execute</c> does.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpPrepExec(BatchContext batch, string? returnCodeVar, bool insertExecSource)
    {
        var context = batch.Parser;
        if (IsStatementBoundary(context.Token))
        {
            if (batch.IsSkipping)
                return [];
            throw AtProcedureLine(SimulatedSqlException.ProcedureExpectsParameter("sp_prepexec", "handle", state: 11), "sp_prepexec", 1);
        }
        _ = TryConsumeSpExecuteSqlArgumentName(context);
        var (_, handleSlot) = ParseSpExecuteSqlValueArg(context, batch);
        var texts = new SqlValue[2];
        for (var i = 0; i < texts.Length; i++)
        {
            if (context.Token is not Operator { Character: ',' })
            {
                if (batch.IsSkipping)
                    return [];
                throw AtProcedureLine(SimulatedSqlException.ProcedureExpectsParameter("sp_prepexec", i == 0 ? "params" : "stmt", state: 11), "sp_prepexec", 1);
            }
            context.MoveNextRequired();
            _ = TryConsumeSpExecuteSqlArgumentName(context);
            (texts[i], _) = ParseSpExecuteSqlValueArg(context, batch);
        }
        return ParseSpExecuteSql(batch, returnCodeVar, insertExecSource, prepared: () =>
        {
            if (handleSlot is null)
                throw HandleNotOutput("sp_prepexec");
            var declaration = texts[0].IsNull ? "" : texts[0].CoerceTo(SqlType.NVarchar).AsString;
            var statement = texts[1].IsNull ? "" : texts[1].CoerceTo(SqlType.NVarchar).AsString;
            var handle = batch.Connection.Prepare(statement, declaration);
            handleSlot.Value = SqlValue.FromInt32(handle).CoerceTo(handleSlot.DeclaredType);
            return batch.Connection.PreparedStatements[handle];
        });
    }

    /// <summary><c>sp_unprepare @handle</c>: releases a prepared statement; a handle the session doesn't hold is Msg 8179.</summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpUnprepare(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        // Real's message names the cursor procedure that shares the parameter.
        if (ArgumentFor(arguments, 0, "handle") is not { IsDefault: false })
            throw SimulatedSqlException.ProcedureExpectsParameter("sp_unprepare/sp_cursorunprepare", "handle", state: 13);
        RequireSystemProcedureShape("sp_unprepare", arguments, ["handle"]);
        var handle = arguments[0].Value;
        var number = handle.IsNull ? 0 : handle.CoerceTo(SqlType.Int32).AsInt32;
        if (!Unprepare(batch.Connection, number))
            throw AtProcedureLine(SimulatedSqlException.CouldNotFindPreparedStatement(number, state: 8), "sp_unprepare", 1);
    }

    /// <summary>The prepared statement <paramref name="handle"/> names, or Msg 8179 at the procedure's line 1.</summary>
    private static PreparedStatement FindPrepared(BatchContext batch, SqlValue handle, string procedure, byte state)
    {
        var number = handle.IsNull ? 0 : handle.CoerceTo(SqlType.Int32).AsInt32;
        return batch.Connection.PreparedStatements.TryGetValue(number, out var prepared)
            ? prepared
            : throw AtProcedureLine(SimulatedSqlException.CouldNotFindPreparedStatement(number, state), procedure, 1);
    }

    /// <summary>
    /// The variable an <c>@handle OUTPUT</c> argument names: Msg 591 when it
    /// isn't marked <c>OUTPUT</c>, and Msg 214 for a variable of another type
    /// than <c>int</c>, which the batch goes on past.
    /// </summary>
    private static VariableSlot PreparedHandleSlot(List<ProcArgument> arguments, string procedure)
    {
        var argument = ArgumentFor(arguments, 0, "handle");
        if (argument?.OutputSlot is not { } slot)
            throw HandleNotOutput(procedure);
        if (slot.DeclaredType != SqlType.Int32)
            throw AtProcedureLine(SimulatedSqlException.ProcedureExpectsParameterOfType("@handle", "int"), procedure, 1);
        return slot;
    }

    /// <summary>Msg 591 for a handle argument not marked <c>OUTPUT</c>, which ends the batch.</summary>
    private static SimulatedSqlException HandleNotOutput(string procedure) =>
        AtProcedureLine(SimulatedSqlException.FormalParameterNotOutput(procedure, "@handle", state: 52), procedure, 1);

    /// <summary>The argument bound to the parameter at <paramref name="position"/>, positionally or by <paramref name="name"/>.</summary>
    private static ProcArgument? ArgumentFor(List<ProcArgument> arguments, int position, string name)
    {
        foreach (var argument in arguments)
        {
            if (argument.Name is { } written && BuiltInToken.Equals(written, name))
                return argument;
        }
        return position < arguments.Count && arguments[position].Name is null ? arguments[position] : null;
    }

    /// <summary>A string argument's text, or null for NULL, <c>DEFAULT</c> or a missing one.</summary>
    private static string? TextArgument(List<ProcArgument> arguments, int position, string name) =>
        ArgumentFor(arguments, position, name) is { IsDefault: false, Value: { IsNull: false } value } ? value.CoerceTo(SqlType.NVarchar).AsString : null;
}
