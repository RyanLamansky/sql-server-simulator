using System.Runtime.ExceptionServices;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Holds <c>EXEC ( … )</c>'s operand to real's grammar — string literals
    /// and variables joined by <c>+</c>, nothing else: a function call, a
    /// parenthesis, a binary literal, <c>NULL</c> or <c>COLLATE</c> is a syntax
    /// error at that token (probed 2026-09-26 against SQL Server 2025). The
    /// cursor is left where it was, for the expression parse that follows.
    /// </summary>
    private static void RejectNonStringExecOperands(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        while (true)
        {
            if (context.Token is not (AtPrefixedString or Literal { Value.Type.Category: SqlTypeCategory.String }))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            if (context.Token is not Operator { Character: '+' })
                break;
            context.MoveNextRequired();
        }
        if (context.Token is not Operator { Character: ')' or ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.RestoreCheckpoint(checkpoint);
    }

    /// <summary>
    /// Parses <c>EXEC ( &lt;string-expression&gt; )</c> — dynamic SQL via
    /// EXEC. The string operand is evaluated in the caller's batch (so
    /// concatenation works: <c>EXEC ('SELECT ' + @col + ' FROM t')</c>),
    /// then the resulting string is re-tokenized and dispatched as a fresh
    /// batch inside its own <see cref="BatchContext"/>. Outer variables
    /// are NOT visible inside the dynamic batch (probe-confirmed: real SQL
    /// Server raises Msg 137 if the dynamic SQL references an outer
    /// <c>@var</c>). Result sets from the dynamic batch propagate to the
    /// outer caller.
    /// </summary>
    /// <remarks>
    /// Cursor on entry: the opening <c>(</c> after EXEC. Cursor on exit:
    /// the token after the closing <c>)</c>. Skip-mode evaluates the
    /// expression (cursor advance) but suppresses the dispatch.
    /// </remarks>
    private IEnumerable<SimulatedStatementOutcome> ParseExecDynamicSql(BatchContext batch, bool insertExecSource = false)
    {
        var context = batch.Parser;
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        RejectNonStringExecOperands(context);
        // Each operand is a literal or a variable; EXEC joins their texts itself
        // rather than through `+`, so a non-string variable converts on its own
        // and a NULL one adds nothing (probed 2026-10-02 against SQL Server
        // 2025: `EXEC ('SELECT ' + @int)` runs, `'SELECT 1' + @null` too).
        var operands = new List<(SqlValue Literal, VariableSlot? Slot)>();
        while (true)
        {
            operands.Add(context.Token is AtPrefixedString variable
                ? (default, batch.GetVariableSlot(variable.Value))
                : (((Literal)context.Token).Value, null));
            context.MoveNextRequired();
            if (context.Token is not Operator { Character: '+' })
                break;
            context.MoveNextRequired();
        }

        // The arguments `?` placeholders bind, which only a linked server
        // takes (`EXEC ('…', 1, @v OUTPUT) AT server`).
        var arguments = new List<ProcArgument>();
        while (context.Token is Operator { Character: ',' })
        {
            context.MoveNextRequired();
            var argument = ParseExecArgument(context, batch, name: null);
            if (argument.OutputSlot is null && context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output or ContextualKeyword.Out })
                throw SimulatedSqlException.ConstantPassedAsOutput();
            arguments.Add(argument);
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        // AS { LOGIN | USER } = 'name' runs the batch in that security
        // context, reverting when it returns (probed 2026-10-02 against SQL
        // Server 2025).
        (bool IsLogin, string Name)? runAs = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.As })
        {
            var isLogin = context.GetNextRequired() switch
            {
                ReservedKeyword { Keyword: Keyword.User } => false,
                UnquotedString { ContextualKeyword: ContextualKeyword.Login } => true,
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
            if (context.GetNextRequired() is not Operator { Character: '=' }
                || context.GetNextRequired() is not Literal { Value: { IsNull: false } principal } || !SqlType.IsStringCategory(principal.Type))
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            runAs = (isLogin, principal.AsString);
            context.MoveNextOptional();
        }
        string? linkedServerName = null;
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.At })
        {
            if (context.GetNextRequired() is not Name serverName)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            linkedServerName = serverName.Value;
            context.MoveNextOptional();
        }
        else if (arguments.Count > 0)
        {
            throw SimulatedSqlException.ExecuteArgumentsWithoutServer();
        }
        var resultSets = ParseExecuteOptions(batch, insertExecSource, characterStringForm: true);

        if (batch.IsSkipping)
            yield break;

        var connection = batch.Connection;
        if (connection.NestingLevel >= SimulatedDbConnection.MaxNestingLevel)
            throw SimulatedSqlException.MaximumNestingLevelExceeded();

        var text = new System.Text.StringBuilder();
        var maxText = VarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault);
        foreach (var (literal, slot) in operands)
        {
            var part = slot?.Value ?? literal;
            if (part.Type is XmlSqlType)
                throw SimulatedSqlException.ImplicitConversionNotAllowed("xml", "nvarchar");
            if (!part.IsNull)
                _ = text.Append(SqlType.IsStringCategory(part.Type) ? part.AsString : part.CoerceTo(maxText).AsString);
        }
        // Dynamic SQL of NULL or nothing is a no-op (matches real SQL Server's
        // lenient handling); a linked server's provider refuses it.
        if (text.Length == 0 && linkedServerName is null)
            yield break;

        var sqlText = text.ToString();
        var impersonationDepth = connection.Security.ImpersonationDepth;
        if (runAs is var (asLogin, asName))
            ApplyExecuteAs(connection, connection.CurrentDatabase, asLogin, asName, ModuleGuard);
        try
        {
            var dynamicBatch = linkedServerName is null
                ? ExecuteDynamicBatch(batch, sqlText, preDeclaredVariables: null, streams: batch.SendsAsStatementsEnd && resultSets is null && !insertExecSource, declarations: "EXEC")
                : ExecuteAtLinkedServer(batch, linkedServerName, sqlText, arguments, insertExecSource);
            foreach (var outcome in resultSets is null ? dynamicBatch : ApplyResultSetsContract(dynamicBatch, resultSets))
                yield return outcome;
        }
        finally
        {
            connection.Security.RevertTo(impersonationDepth);
        }
        batch.CurrentStatement.SuppressErrorReset = true;
    }

    /// <summary>
    /// Parses an <c>EXEC sp_executesql N'sql', N'@p1 type [OUTPUT], ...',
    /// @p1 = arg1, ...</c> call. The first argument is the SQL text; the
    /// second (optional) is a parameter-declaration string that pre-declares
    /// the dynamic batch's <c>@</c>-variables; remaining args bind values
    /// (with optional <c>OUTPUT</c> for writeback). Outer <c>@</c>-variables
    /// are NOT visible inside the dynamic batch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cursor on entry: first token after the parsed <c>sp_executesql</c>
    /// procedure name (a literal, an <c>@</c>-variable, or end-of-args).
    /// Cursor on exit: the trailing statement boundary.
    /// </para>
    /// <para>
    /// The parameter-declaration string is mini-parsed: comma-separated
    /// entries each shaped like <c>@name type [OUTPUT]</c>. Values for each
    /// declared parameter come from the trailing arg list (positional or
    /// named); OUTPUT parameters write back to the caller's variable slot
    /// at exit. Probe-confirmed against SQL Server 2025.
    /// </para>
    /// </remarks>
    private IEnumerable<SimulatedStatementOutcome> ParseSpExecuteSql(BatchContext batch, string? returnCodeVar, bool insertExecSource = false, string? calledInDatabase = null)
    {
        var context = batch.Parser;

        // A call with no statement is the procedure's own Msg 201, naming it
        // (probed 2026-10-02 against SQL Server 2025).
        if (IsStatementBoundary(context.Token))
        {
            if (batch.IsSkipping)
                yield break;
            throw SimulatedSqlException.ProcedureExpectsParameter("sp_executesql", "statement", state: 10);
        }

        // Argument 1: SQL text (literal or @-variable, coerced to string).
        // A leading `@name =` is accepted and the name discarded: real binds
        // sp_executesql's first two arguments purely by position and does not
        // check what they were called, so `@stmt =`, `@statement =`, `@sql =`
        // and even `@nonsense =` all run the same statement (probe-confirmed).
        // Writing the second parameter's name first doesn't reorder them
        // either — real takes `@params = N'@x int', @stmt = N'…'` as
        // statement-then-declarations and tries to run `@x int` as the batch.
        // This is what the SSMS / DacFx "create the stub if absent" idiom
        // emits, so it is the common spelling rather than an exotic one.
        // Whether an argument was named matters even though its name is
        // discarded: real's Msg 119 counts the whole list, so naming the
        // first argument obliges every later one to be named too.
        var sawNamedArgument = TryConsumeSpExecuteSqlArgumentName(context) is not null;
        var (sqlRaw, _) = ParseSpExecuteSqlValueArg(context, batch);
        var sqlValue = sqlRaw.CoerceTo(NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault));
        var hasMoreArgs = context.Token is Operator { Character: ',' };

        // Argument 2 (optional): parameter-declaration string.
        List<SpExecuteSqlParam>? declaredParams = null;
        // Kept verbatim: Msg 8178 quotes the two argument strings exactly as
        // written, spacing included.
        var paramDefsText = "";
        var declaresParameters = false;
        SqlType? paramDefsType = null;
        if (hasMoreArgs)
        {
            context.MoveNextRequired();
            if (TryConsumeSpExecuteSqlArgumentName(context) is not null)
                sawNamedArgument = true;
            else if (sawNamedArgument)
                throw SimulatedSqlException.MustPassParameterAsNamed(2);
            var (paramDefsRaw, _) = ParseSpExecuteSqlValueArg(context, batch);
            paramDefsType = paramDefsRaw.Type;
            var paramDefs = paramDefsRaw.CoerceTo(NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault));
            // A declaration argument of another type is refused when the call
            // runs (Msg 214), so its text is never parsed as declarations.
            if (!paramDefs.IsNull && SqlType.IsNationalStringCategory(paramDefsType))
            {
                paramDefsText = paramDefs.AsString;
                declaresParameters = true;
            }
            hasMoreArgs = context.Token is Operator { Character: ',' };
        }

        // Remaining args: positional/named values bound to declared params.
        var argumentValues = new List<(string? Name, SqlValue Value, VariableSlot? OutputSlot, bool IsUntypedNull, HeapTable? Table, bool IsDefault)>();
        while (hasMoreArgs)
        {
            context.MoveNextRequired();
            // Unlike the first two, a trailing value argument's name is
            // load-bearing: real matches it against the declaration list, so
            // `@y = 2, @x = 1` binds by name rather than by position
            // (probe-confirmed), and an OUTPUT writeback needs it.
            var argName = TryConsumeSpExecuteSqlArgumentName(context);
            if (argName is not null)
                sawNamedArgument = true;
            else if (sawNamedArgument)
                throw SimulatedSqlException.MustPassParameterAsNamed(3 + argumentValues.Count);
            var isUntypedNull = context.Token is ReservedKeyword { Keyword: Keyword.Null };
            var (argValue, argOutputSlot) = ParseSpExecuteSqlValueArg(context, batch, out var argTable, out var argIsDefault);
            argumentValues.Add((argName, argValue, argOutputSlot, isUntypedNull, argTable, argIsDefault));
            hasMoreArgs = context.Token is Operator { Character: ',' };
        }

        var resultSets = ParseExecuteOptions(batch, insertExecSource);

        if (batch.IsSkipping)
            yield break;

        if (!SqlType.IsNationalStringCategory(sqlRaw.Type))
            throw SimulatedSqlException.SpExecuteSqlArgumentNotUnicode("@statement", 2).PinLine(1);
        if (paramDefsType is not null && !SqlType.IsNationalStringCategory(paramDefsType))
            throw SimulatedSqlException.SpExecuteSqlArgumentNotUnicode("@params", 3).PinLine(1);

        // The declarations, and the arguments bound to them below, belong to
        // the call: an error in either is the call's, and the caller's batch
        // goes on past it (probed 2026-10-04 against SQL Server 2025).
        if (declaresParameters)
        {
            var committed = new List<SpExecuteSqlParam>();
            try
            {
                declaredParams = ParseSpExecuteSqlParamDefinitions(paramDefsText, batch.Connection, committed);
            }
            catch (SimulatedSqlException declarationError)
            {
                var reported = declarationError.Number is 102 or 156 && !sqlValue.IsNull
                    ? this.WithStatementCompileErrors(batch, sqlValue.AsString, committed, declarationError)
                    : declarationError;
                reported.EndedCalledBatch = true;
                WriteFailedStatus(batch, returnCodeVar, reported);
                throw reported;
            }
        }

        if (sqlValue.IsNull)
            yield break;

        var sqlText = sqlValue.AsString;
        var connection = batch.Connection;
        if (connection.NestingLevel >= SimulatedDbConnection.MaxNestingLevel)
            throw SimulatedSqlException.MaximumNestingLevelExceeded();

        // Bind declared params: positional fill first, then named lookup. An
        // error binding the arguments reports line 0, as a procedure's does —
        // but a declared parameter missing from a call that supplied none is
        // line 1 wherever the EXEC sits (probed 2026-09-29 against SQL Server 2025).
        SimulatedSqlException ArgumentBindingError(SimulatedSqlException error) =>
            error.PinLine(argumentValues.Count > 0 ? 0 : 1);
        var preDeclared = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        Dictionary<string, HeapTable>? tableArguments = null;
        var outputBindings = new List<(SpExecuteSqlParam Param, VariableSlot CallerSlot)>();
        try
        {
            if (declaredParams is not null)
            {
                var positional = 0;
                var bound = new SqlValue?[declaredParams.Count];
                var boundOutputSlots = new VariableSlot?[declaredParams.Count];
                var boundIsUntypedNull = new bool[declaredParams.Count];
                var boundTables = new HeapTable?[declaredParams.Count];
                // Real checks the declarations for completeness *before* it
                // complains about a name it doesn't recognize, so an unknown name
                // alongside a missing declared one reports the missing one
                // (probe-confirmed) — hence the flag rather than an immediate
                // throw.
                var sawUnknownName = false;
                if (declaredParams.Count == 0 && argumentValues.Count > 0)
                    throw SimulatedSqlException.ArgumentsSuppliedToParameterlessRoutine("").PinLine(0);
                foreach (var (name, value, outputSlot, isUntypedNull, table, isDefault) in argumentValues)
                {
                    int idx;
                    if (name is null)
                    {
                        idx = positional++;
                        if (idx >= declaredParams.Count)
                            throw SimulatedSqlException.TooManyArgumentsToFunction("").PinLine(0);
                    }
                    else
                    {
                        idx = -1;
                        for (var i = 0; i < declaredParams.Count; i++)
                        {
                            if (context.Batch.CurrentDatabase.Collation.Equals(declaredParams[i].Name, name))
                            {
                                idx = i;
                                break;
                            }
                        }
                        if (idx < 0)
                        {
                            sawUnknownName = true;
                            continue;
                        }
                    }
                    // A second value for one parameter is a surplus argument.
                    if (bound[idx] is not null || boundTables[idx] is not null)
                        throw SimulatedSqlException.TooManyArgumentsToFunction("").PinLine(0);
                    if (isDefault)
                        continue;
                    bound[idx] = table is null ? value : null;
                    boundTables[idx] = table;
                    boundOutputSlots[idx] = outputSlot;
                    boundIsUntypedNull[idx] = isUntypedNull;
                }
                for (var i = 0; i < declaredParams.Count; i++)
                {
                    var param = declaredParams[i];
                    // A table-valued parameter takes a table variable of its own
                    // type, and an unsupplied one is an empty table; anything else
                    // is Msg 206 against the type as the call binds (probed
                    // 2026-10-04 against SQL Server 2025).
                    if (param.TableType is { } tableType)
                    {
                        if (boundTables[i] is { } suppliedTable && !ReferenceEquals(suppliedTable.DeclaredTableType, tableType))
                            throw SimulatedSqlException.OperandTypeClash(suppliedTable.DeclaredTableType?.Name ?? "table", tableType.Name).PinLine(0);
                        if (bound[i] is { } scalar)
                            throw SimulatedSqlException.OperandTypeClash(boundIsUntypedNull[i] ? "NULL" : SimulatedSqlException.FamilyRootName(scalar.Type), tableType.Name).PinLine(0);
                        (tableArguments ??= new Dictionary<string, HeapTable>(BatchContext.VariableNameComparer))[param.Name] =
                            CloneTableValuedArgument(tableType, param.Name, batch, boundTables[i]);
                        continue;
                    }
                    if (boundTables[i] is { } misplacedTable)
                        throw SimulatedSqlException.OperandTypeClash(misplacedTable.DeclaredTableType?.Name ?? "table", SimulatedSqlException.FamilyRootName(param.Type)).PinLine(0);
                    // Every declared parameter without a default has to be
                    // supplied — an explicit NULL counts, an omission does not,
                    // and OUTPUT parameters are no exception. Where several are missing real names the first
                    // declared one, which is what this loop's order gives.
                    // The stored name is unprefixed (it keys a variable slot); the
                    // message spells it the way the declaration did.
                    if (bound[i] is null)
                    {
                        bound[i] = param.Default ?? throw ArgumentBindingError(SimulatedSqlException.ParameterizedQueryExpectsParameter(paramDefsText, sqlText, "@" + param.Name));
                        boundIsUntypedNull[i] = bound[i]!.Value.IsNull;
                    }
                    if (boundOutputSlots[i] is not null && !param.IsOutput)
                        throw SimulatedSqlException.ParameterNotDeclaredOutput(param.Name).PinLine(0);
                    if (!boundIsUntypedNull[i])
                    {
                        try
                        {
                            AssignmentRules.RequireAssignable(bound[i]!.Value.Type, param.Type);
                        }
                        catch (SimulatedSqlException refused)
                        {
                            // At line 0, as every other binding error.
                            throw refused.PinLine(0);
                        }
                    }
                    var initialValue = BindParameterValue(bound[i]!.Value, param.Type, param.DeclaredMaxLength, procedure: "");
                    var slot = new VariableSlot(param.Type, param.DeclaredMaxLength, initialValue, parameter: null);
                    preDeclared[param.Name] = slot;
                    if (param.IsOutput && boundOutputSlots[i] is { } caller)
                        outputBindings.Add((param, caller));
                }

                // Only once every declaration is satisfied does an unrecognized
                // argument name become the complaint — real reports the missing
                // declaration first when both are wrong. The name is empty, which
                // is why real's message carries a double space.
                if (sawUnknownName)
                    throw SimulatedSqlException.TooManyArgumentsToFunction("").PinLine(0);
            }
        }
        catch (SimulatedSqlException bindingError)
        {
            bindingError.EndedCalledBatch = true;
            WriteFailedStatus(batch, returnCodeVar, bindingError);
            throw;
        }

        // The status sp_executesql returns is @@ERROR as its batch left it,
        // including when an error of the batch's own ended it, so an error is
        // held until the status is written (probed 2026-09-24 against
        // SQL Server 2025), and what the batch sent before it still goes first.
        // A three-part name calls the procedure in that database, where the
        // batch runs — `EXEC other.sys.sp_executesql` is the idiom for running
        // dynamic SQL in another database (probed 2026-10-02 against SQL
        // Server 2025).
        // A call whose outcomes reach the client as they are produced sends
        // the batch's as it runs; the status and the OUTPUT writeback follow.
        var streams = batch.SendsAsStatementsEnd && resultSets is null && !insertExecSource;
        var dynamicBatch = ExecuteDynamicBatch(batch, sqlText, preDeclared, viaSystemProcedure: true, tableVariables: tableArguments,
            runsIn: calledInDatabase is null ? null
                : this.Databases.TryGetValue(calledInDatabase, out var calledIn) ? calledIn
                : throw SimulatedSqlException.DatabaseDoesNotExist(calledInDatabase),
            streams: streams,
            declarations: "sp_executesql " + paramDefsText);
        List<SimulatedStatementOutcome> outcomes = [];
        SimulatedSqlException? failure = null;
        using (var sent = (resultSets is null ? dynamicBatch : ApplyResultSetsContract(dynamicBatch, resultSets)).GetEnumerator())
        {
            while (NextBodyOutcome(sent, ref failure))
            {
                if (streams)
                    yield return sent.Current;
                else
                    outcomes.Add(sent.Current);
            }
        }

        // Writeback: sp_executesql's OUTPUT params copy the dynamic batch's
        // final variable values back to the caller's slots.
        // A value is assigned as SET assigns the variable, cut to its width,
        // and one that doesn't convert is Msg 8114 state 2 at line 0, the
        // variable kept (probed 2026-10-02 against SQL Server 2025).
        SimulatedSqlException? writebackError = null;
        if (failure is null)
        {
            foreach (var (param, callerSlot) in outputBindings)
            {
                if (!preDeclared.TryGetValue(param.Name, out var slot))
                    continue;
                try
                {
                    callerSlot.Value = BindParameterValue(slot.Value, callerSlot.DeclaredType, callerSlot.DeclaredMaxLength, procedure: "", conversionState: 2);
                }
                catch (SimulatedSqlException refused)
                {
                    writebackError ??= refused;
                }
            }
        }

        if (returnCodeVar is not null && failure is null or { EndedCalledBatch: true })
        {
            var rcSlot = batch.GetVariableSlot(returnCodeVar);
            rcSlot.Value = SqlValue.FromInt32(failure?.Number ?? batch.Connection.LastErrorNumber).CoerceTo(rcSlot.DeclaredType);
        }

        foreach (var outcome in outcomes)
            yield return outcome;
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
        if (writebackError is not null)
            throw writebackError;
        batch.CurrentStatement.SuppressErrorReset = true;
    }

    /// <summary>
    /// An error in the declarations or the arguments bound to them is the
    /// status <c>EXEC @rc = sp_executesql</c> returns, as one the batch raises
    /// is (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    private static void WriteFailedStatus(BatchContext batch, string? returnCodeVar, SimulatedSqlException error)
    {
        if (returnCodeVar is null)
            return;
        var slot = batch.GetVariableSlot(returnCodeVar);
        slot.Value = SqlValue.FromInt32(error.AtAtErrorNumber).CoerceTo(slot.DeclaredType);
    }

    /// <summary>
    /// A syntax error in the declarations still compiles the statement, against
    /// the parameters a comma completed before it, and reports what that
    /// compile raises after it — so <c>N'a int'</c> is Msg 102 then Msg 137
    /// for the statement's <c>@a</c>, while <c>N'@a int, b int'</c> declares
    /// <c>@a</c> and <c>N'@a int +'</c> doesn't (probed 2026-10-06 against SQL
    /// Server 2025). The statement never runs.
    /// </summary>
    private SimulatedSqlException WithStatementCompileErrors(BatchContext outerBatch, string sqlText, List<SpExecuteSqlParam> committed, SimulatedSqlException declarationError)
    {
        using var command = new SimulatedDbCommand(this, outerBatch.Connection);
#pragma warning disable CA2100 // dynamic SQL is the application's input; it is compiled, never run
        command.CommandText = sqlText.Length == 0 ? " " : sqlText;
#pragma warning restore CA2100
        var variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        var tableVariables = new Dictionary<string, HeapTable>(BatchContext.VariableNameComparer);
        foreach (var parameter in committed)
        {
            if (parameter.TableType is { } tableType)
                tableVariables[parameter.Name] = CloneTableValuedArgument(tableType, parameter.Name, outerBatch, supplied: null);
            else
                variables[parameter.Name] = new VariableSlot(parameter.Type, parameter.DeclaredMaxLength, SqlValue.Null(parameter.Type), parameter: null);
        }
        var statementBatch = new BatchContext(command, variables, new ProcFrame("<dynamic-sql>", isDynamicSql: true), tableVariables);
        if (this.CompileBatch(CompileContextFor(statementBatch, command), key: null, out _) is not { } compileError)
            return declarationError;
        // A CATCH reads the declaration's error and @@ERROR the last.
        var report = SimulatedSqlException.Aggregate([declarationError, compileError]).ReportingLastToAtAtError();
        report.CatchReadsFirstEntry = true;
        return report;
    }

    /// <summary>
    /// Consumes an <c>@name =</c> prefix and returns the name, leaving the
    /// cursor on the value. Returns null with the cursor unmoved when the next
    /// token isn't one — an <c>@</c>-variable holding the argument's value
    /// looks identical until the following token settles it, which is why this
    /// runs off a checkpoint rather than a single-token peek.
    /// </summary>
    private static string? TryConsumeSpExecuteSqlArgumentName(ParserContext context)
    {
        if (context.Token is not AtPrefixedString candidate)
            return null;
        var checkpoint = context.SaveCheckpoint();
        context.MoveNextOptional();
        if (context.Token is Operator { Character: '=' })
        {
            context.MoveNextRequired();
            return candidate.Value;
        }
        context.RestoreCheckpoint(checkpoint);
        return null;
    }

    /// <summary>
    /// Parses one sp_executesql positional / named-value argument. Accepts
    /// the same shapes as a regular EXEC argument
    /// (<see cref="ParseExecArgument"/>) but doesn't enforce the no-mixed-
    /// position rule — sp_executesql's grammar is more permissive.
    /// </summary>
    private static (SqlValue Value, VariableSlot? OutputSlot) ParseSpExecuteSqlValueArg(ParserContext context, BatchContext batch) =>
        ParseSpExecuteSqlValueArg(context, batch, out _, out _);

    /// <summary>
    /// <see cref="ParseSpExecuteSqlValueArg(ParserContext, BatchContext)"/>
    /// for a trailing value argument, which may also be a table variable — a
    /// table-valued parameter's value, reported through
    /// <paramref name="table"/> — or <c>DEFAULT</c>, which leaves the
    /// parameter unsupplied (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    private static (SqlValue Value, VariableSlot? OutputSlot) ParseSpExecuteSqlValueArg(ParserContext context, BatchContext batch, out HeapTable? table, out bool isDefault)
    {
        table = null;
        isDefault = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Default })
        {
            isDefault = true;
            context.MoveNextOptional();
            return (SqlValue.Null(SqlType.Int32), null);
        }
        if (context.Token is AtPrefixedString tableRef
            && batch.TryGetTableVariable(tableRef.Value.TrimStart('@'), out var tableVariable))
        {
            table = tableVariable;
            context.MoveNextOptional();
            return (SqlValue.Null(SqlType.Int32), null);
        }
        if (context.Token is AtPrefixedString varRef)
        {
            var slot = batch.GetVariableSlot(varRef.Value);
            context.MoveNextOptional();
            VariableSlot? outputSlot = null;
            if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output or ContextualKeyword.Out })
            {
                outputSlot = slot;
                context.MoveNextOptional();
            }
            return (slot.Value, outputSlot);
        }
        var value = context.Token switch
        {
            Literal lit => lit.Value,
            Numeric num => num.Value,
            ReservedKeyword { Keyword: Keyword.Null } => SqlValue.Null(SqlType.Int32),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
        context.MoveNextOptional();
        return context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output or ContextualKeyword.Out }
            ? throw SimulatedSqlException.ConstantPassedAsOutput()
            : (value, null);
    }

    /// <summary>
    /// The declarations' table-valued parameters as empty read-only table
    /// variables, for the describing procedures that compile a batch without
    /// arguments; null when there are none.
    /// </summary>
    private static Dictionary<string, HeapTable>? EmptyTableValuedParameters(List<SpExecuteSqlParam> parameters, BatchContext batch)
    {
        Dictionary<string, HeapTable>? tables = null;
        foreach (var parameter in parameters)
        {
            if (parameter.TableType is { } tableType)
                (tables ??= new Dictionary<string, HeapTable>(BatchContext.VariableNameComparer))[parameter.Name] = CloneTableValuedArgument(tableType, parameter.Name, batch, supplied: null);
        }
        return tables;
    }

    /// <summary>
    /// Mini-parser for sp_executesql's parameter-declaration string. Splits
    /// on commas (outside parens), then parses each segment as
    /// <c>@name type [OUTPUT]</c>. Returns the ordered parameter list used
    /// to seed the dynamic batch.
    /// </summary>
    private static List<SpExecuteSqlParam> ParseSpExecuteSqlParamDefinitions(string source, SimulatedDbConnection connection, List<SpExecuteSqlParam>? committed = null)
    {
        if (string.IsNullOrWhiteSpace(source))
            return [];

        // Real reads the declarations at line 1 of its own text, wherever the
        // call sits (probed 2026-09-29 against SQL Server 2025).
        try
        {
            return ParseSpExecuteSqlParamDefinitionsCore(source, connection, committed);
        }
        catch (SimulatedSqlException error)
        {
            // A READONLY parameter's Msg 346 carries its own line.
            throw error.PinLine(1, keepStamped: error.Number == 346);
        }
    }

    private static List<SpExecuteSqlParam> ParseSpExecuteSqlParamDefinitionsCore(string source, SimulatedDbConnection connection, List<SpExecuteSqlParam>? committed)
    {
        // Real parses the definitions as the parenthesized list it prints in
        // Msg 8178 — `(@p int)` — so a list that ends early is a syntax error
        // near that closing `)`, and text after it is Msg 4124. A synthetic
        // SimulatedDbCommand lets the tokenizer walk it; nothing dispatches.
        using var defCommand = new SimulatedDbCommand(connection.Simulation, connection);
#pragma warning disable CA2100
        defCommand.CommandText = "(" + source + ")";
#pragma warning restore CA2100
        var defBatch = new BatchContext(defCommand);
        var defContext = defBatch.Parser;
        defContext.MoveNextOptional();
        var open = defContext.SaveCheckpoint();
        defContext.MoveNextRequired();

        // A string that opens with a query is one real reads as a complete
        // parenthesized query, reporting the statement text after it as Msg
        // 4124; a nested `(` that isn't followed by one is a syntax error at
        // the token after the parentheses (probed 2026-09-29 against SQL
        // Server 2025). The parse runs at the declarations' own line, 1.
        var nested = 0;
        while (defContext.Token is Operator { Character: '(' })
        {
            nested++;
            defContext.MoveNextRequired();
        }
        if (nested > 0 || defContext.Token is ReservedKeyword { Keyword: Keyword.Select })
        {
            if (defContext.Token is not ReservedKeyword { Keyword: Keyword.Select })
                throw SimulatedSqlException.SyntaxErrorNear(defContext);
            defContext.RestoreCheckpoint(open);
            defContext.MoveNextRequired();
            if (nested == 0 && defContext.GetNextRequired() is Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(defContext);
            // The query is only read, never bound or run: a missing table is no
            // error here and a sequence draw takes no value. Its grammar takes
            // no ORDER BY, OPTION or `;` of its own.
            defBatch.SkipModeFlag = true;
            if (nested == 0)
                RejectDeclarationQueryClauses(defContext);
            defContext.RestoreCheckpoint(open);
            defContext.MoveNextRequired();
            _ = ParseBodyQuery(defContext, position: QueryPosition.ParenthesizedModuleBody);
            if (defContext.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(defContext);
            // Text after the query's own closing parenthesis is the statement
            // text real appends: a further statement reads, anything else is a
            // syntax error at it.
            if (defContext.GetNextOptional() is { } after && after is not ReservedKeyword { Keyword: Keyword.Select })
                throw SimulatedSqlException.SyntaxErrorNear(defContext);
            throw SimulatedSqlException.BatchParametersNotValid();
        }
        defContext.RestoreCheckpoint(open);
        defContext.MoveNextRequired();

        var parameters = new List<SpExecuteSqlParam>();
        // Every declaration's Msg 346 and Msg 2715 is reported, in order, once
        // the list has parsed (probed 2026-09-30 against SQL Server 2025).
        var declarationErrors = new List<SimulatedSqlException>();
        while (true)
        {
            if (defContext.Token is not AtPrefixedString name)
                throw SimulatedSqlException.SyntaxErrorNear(defContext);
            defContext.MoveNextRequired();

            // A user-defined table type declares a table-valued parameter,
            // whose grammar takes READONLY (Msg 352 without it) and nothing
            // else — a default or OUTPUT is a syntax error at it (probed
            // 2026-10-04 against SQL Server 2025).
            if (TryResolveTableTypeParameter(defContext, out _) is { } tableType)
            {
                if (defContext.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.ReadOnly })
                {
                    throw defContext.Token is Operator { Character: '=' } or UnquotedString { ContextualKeyword: ContextualKeyword.Output or ContextualKeyword.Out }
                        ? SimulatedSqlException.SyntaxErrorNear(defContext)
                        : SimulatedSqlException.TableValuedParameterMustBeReadOnly("@" + name.Value);
                }
                defContext.MoveNextRequired();
                parameters.Add(new SpExecuteSqlParam(name.Value, SqlType.Int32, null, isOutput: false, defaultValue: null, tableType));
                if (defContext.Token is Operator { Character: ',' })
                {
                    committed?.Add(parameters[^1]);
                    defContext.MoveNextRequired();
                    continue;
                }
                if (defContext.Token is not Operator { Character: ')' })
                    throw SimulatedSqlException.SyntaxErrorNear(defContext);
                if (defContext.GetNextOptional() is not null)
                    throw SimulatedSqlException.BatchParametersNotValid();
                return declarationErrors.Count == 0 ? parameters : throw SimulatedSqlException.Aggregate(declarationErrors);
            }

            // Type parsing reuses the procedure-parameter type grammar.
            SqlType type;
            int? declaredMaxLength;
            try
            {
                (type, declaredMaxLength) = ParseSpExecuteSqlParamType(defContext, parameters.Count + 1, "@" + name.Value);
            }
            catch (SimulatedSqlException error) when (error.Number == 2715)
            {
                declarationErrors.Add(error);
                (type, declaredMaxLength) = (SqlType.Int32, null);
            }

            // A default comes before OUTPUT and is a constant, as a procedure
            // parameter's is: `@p int = 5 OUTPUT` (probed 2026-09-25 against
            // SQL Server 2025).
            SqlValue? defaultValue = null;
            if (defContext.Token is Operator { Character: '=' })
            {
                defaultValue = ParseSpExecuteSqlParamDefault(defContext);
                defContext.MoveNextRequired();
            }
            if (defContext.Token is ReservedKeyword { Keyword: Keyword.Not } && defContext.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Null })
            {
                declarationErrors.Add(SimulatedSqlException.NotNullParameterNotSupported("@" + name.Value));
                defContext.MoveNextRequired();
            }
            var readOnly = NoteReadOnlyScalarParameter(defContext, name, declarationErrors);

            var isOutput = false;
            if (!readOnly && defContext.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output or ContextualKeyword.Out })
            {
                isOutput = true;
                defContext.MoveNextOptional();
            }

            // A name declared twice is Msg 134 (probed 2026-10-04 against SQL
            // Server 2025).
            if (parameters.Exists(declared => BatchContext.VariableNameComparer.Equals(declared.Name, name.Value)))
                throw SimulatedSqlException.VariableAlreadyDeclared(name.Value);
            parameters.Add(new SpExecuteSqlParam(name.Value, type, declaredMaxLength, isOutput, defaultValue));

            if (defContext.Token is Operator { Character: ',' })
            {
                committed?.Add(parameters[^1]);
                defContext.MoveNextRequired();
                continue;
            }
            if (defContext.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(defContext);
            if (defContext.GetNextOptional() is not null)
                throw SimulatedSqlException.BatchParametersNotValid();
            return declarationErrors.Count == 0 ? parameters : throw SimulatedSqlException.Aggregate(declarationErrors);
        }
    }

    /// <summary>
    /// A declaration string read as a parenthesized query takes no clause of
    /// its own past the query specification: <c>ORDER BY</c> and <c>OPTION</c>
    /// are Msg 156 on the keyword and <c>;</c> Msg 102 (probed 2026-09-29
    /// against SQL Server 2025). Entered on the token after the opening
    /// parenthesis; leaves the cursor wherever the scan stopped.
    /// </summary>
    private static void RejectDeclarationQueryClauses(ParserContext context)
    {
        var depth = 1;
        while (context.Token is { } token)
        {
            switch (token)
            {
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' }:
                    depth--;
                    break;
                case ReservedKeyword { Keyword: Keyword.Order or Keyword.Option } keyword when depth == 1:
                    throw SimulatedSqlException.SyntaxErrorNearKeyword(keyword);
                case Operator { Character: ';' } when depth == 1:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            if (depth == 0 || !context.MoveNext())
                return;
        }
    }

    /// <summary>
    /// Reads a parameter declaration's <c>= constant</c> default, entered on the
    /// <c>=</c> and leaving the cursor on the constant: a literal, a signed
    /// number, <c>NULL</c>, or <c>DEFAULT</c>, which reads as NULL. A
    /// variable is Msg 137 and anything else a syntax error at it.
    /// </summary>
    private static SqlValue ParseSpExecuteSqlParamDefault(ParserContext context)
    {
        var negate = false;
        if (context.GetNextRequired() is Operator { Character: '-' or '+' } sign)
        {
            negate = sign.Character == '-';
            context.MoveNextRequired();
            if (context.Token is not Numeric)
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        return context.Token switch
        {
            Numeric number => negate
                ? Parser.Expressions.Negate.Of(new Parser.Expressions.Value(number.Value, number.IntegerLiteralDigitCount)).Run(new RuntimeContext(static name => throw SimulatedSqlException.InvalidColumnName(name), context.Batch))
                : number.Value,
            Literal { Value: var literal } => literal,
            ReservedKeyword { Keyword: Keyword.Null or Keyword.Default } => SqlValue.Null(SqlType.Int32),
            AtPrefixedString variable => throw SimulatedSqlException.MustDeclareScalarVariable(variable.Value.TrimStart('@')),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
    }

    /// <summary>
    /// Parses a parameter type inside an sp_executesql parameter-declaration
    /// string. Shape mirrors <see cref="ParseProcedureParameterType"/> but
    /// without the optional default expression.
    /// </summary>
    private static (SqlType Type, int? DeclaredMaxLength) ParseSpExecuteSqlParamType(ParserContext context, int ordinal, string parameterName)
    {
        var (qualifiedTypeName, typeName) = TypeNameSynonyms.ReadTypeName(context);
        context.MoveNextOptional();

        int? declaredMaxLength = null;
        int? declaredScale = null;
        if (context.Token is Operator { Character: '(' })
        {
            var lengthToken = context.GetNextRequired();
            declaredMaxLength = lengthToken is Numeric { Value: { IsNull: false } numericValue }
                ? numericValue.AsInt32
                : context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Max }
                    ? SqlType.MaxLengthSentinel
                    : throw SimulatedSqlException.SyntaxErrorNear(context);
            switch (context.GetNextRequired())
            {
                case Operator { Character: ',' }:
                    _ = context.GetNextRequired();
                    declaredScale = TypeNameSynonyms.ReadSecondTypeArgument(context, typeName);
                    if (context.GetNextRequired() is not Operator { Character: ')' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    break;
                case Operator { Character: ')' }:
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextOptional();
        }

        var (resolvedType, resolvedMaxLength, _, _) = ResolveTypeReference(
            context.Batch, qualifiedTypeName, typeName, declaredMaxLength, declaredScale,
            index: ordinal, TypeSpecSite.Scalar, columnName: parameterName);
        return (resolvedType, resolvedMaxLength);
    }

    /// <summary>
    /// Dispatches a string of SQL as a fresh child batch. The child batch
    /// shares the outer connection (so transaction / temp-table / catalog
    /// state are shared) but has its own variable scope — outer
    /// <c>@</c>-variables are invisible to the dynamic SQL. The batch is one
    /// nesting level below its caller, and <c>sp_executesql</c>'s two, the
    /// procedure counting as one (<paramref name="viaSystemProcedure"/>;
    /// probed 2026-10-02 against SQL Server 2025: <c>@@NESTLEVEL</c> reads 2
    /// in an <c>EXEC('…')</c> a level-1 procedure runs, 3 in its
    /// <c>sp_executesql</c>). A call whose outcomes reach the client as it
    /// produces them (<paramref name="streams"/>) sends the batch's as each of
    /// its statements ends, as <c>InvokeProcedure</c> does a body's. A batch
    /// that names its <paramref name="declarations"/> has its compile
    /// remembered (<see cref="DynamicBatchKey"/>), so the same call again skips
    /// it until a schema change, as a repeated top-level batch does.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> ExecuteDynamicBatch(
        BatchContext outerBatch,
        string sqlText,
        Dictionary<string, VariableSlot>? preDeclaredVariables,
        bool viaSystemProcedure = false,
        Database? runsIn = null,
        Dictionary<string, HeapTable>? tableVariables = null,
        bool streams = false,
        string? declarations = null)
    {
        var nestingLevels = viaSystemProcedure ? 2 : 1;
        var connection = outerBatch.Connection;
        using var dynCommand = new SimulatedDbCommand(this, connection);
#pragma warning disable CA2100 // dynamic SQL is the application's input; the caller is responsible for sanitization
        // An empty string is a batch of nothing, which the command refuses to hold.
        dynCommand.CommandText = sqlText.Length == 0 ? " " : sqlText;
#pragma warning restore CA2100

        // Seed an empty variable dict (so outer @vars don't leak in) plus
        // any pre-declared sp_executesql parameters. Use the proc-body
        // BatchContext ctor with a sentinel ProcFrame so RETURN N parses
        // without raising Msg 178 — dynamic-SQL batches inherit the proc-
        // body's RETURN semantics (RETURN value is captured but unused).
        var variables = preDeclaredVariables is null
            ? new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer)
            : new Dictionary<string, VariableSlot>(preDeclaredVariables, BatchContext.VariableNameComparer);
        var procFrame = new ProcFrame("<dynamic-sql>", isDynamicSql: true);
        var innerBatch = new BatchContext(dynCommand, variables, procFrame, tableVariables) { ContinueOnError = ContinuesCalledBatch(outerBatch) };

        connection.NestingLevel += nestingLevels;
        var enteredDatabase = connection.CurrentDatabase;
        if (runsIn is not null)
            connection.CurrentDatabase = runsIn;
        connection.EnterNestedDatabase(enteredDatabase);
        // SET NOCOUNT inside the dynamic batch binds for that batch only, the
        // same module scope USE and temp tables get (probe-confirmed for both
        // EXEC('…') and sp_executesql).
        var enteredNoCount = connection.NoCount;
        // XACT_ABORT / ROWCOUNT / DATEFIRST bind for the dynamic batch only,
        // the same module scope (probe-confirmed: `EXEC('SET XACT_ABORT ON …')`
        // leaves the caller's @@OPTIONS bit clear).
        var enteredOptions = new SimulatedDbConnection.SessionOptionScope(connection);
        var enteredTranCount = connection.CurrentTransaction?.TranCount ?? 0;
        // As for a procedure, a batch ending under SET IMPLICIT_TRANSACTIONS ON
        // raises no Msg 266.
        var endedUnderImplicitTransactions = connection.ImplicitTransactions;
        // A batch of its own is a scope of its own for SCOPE_IDENTITY.
        var identityScope = IdentityScope.Enter(connection);
        List<SimulatedStatementOutcome> outcomes = [];
        SimulatedSqlException? batchError = null;
        var compiled = false;
        IEnumerator<SimulatedStatementOutcome>? streamed = null;
        try
        {
            try
            {
                // Dynamic SQL is a batch of its own and compiles as one; an error
                // compiling it is the EXEC's own, and the caller carries on.
                StatementClock? compileClock = connection.StatisticsTime && ReportsStatistics(outerBatch) ? StatementClock.Start(connection) : null;
                var dynamicKey = declarations is not null && tableVariables is null ? DynamicBatchKey(connection, dynCommand.CommandText, declarations) : null;
                BatchContext? compileContext = null;
                List<SimulatedSqlException>? inliningFailures = null;
                if (!this.CompiledBefore(dynamicKey, Volatile.Read(ref this.SchemaVersion)))
                {
                    compileContext = CompileContextFor(innerBatch, dynCommand);
                    if (this.CompileBatch(compileContext, key: null, out inliningFailures, dynamicKey: dynamicKey) is { } compileError)
                    {
                        compileError.EndedCalledBatch = !compileError.EndsCompileSilently;
                        throw compileError;
                    }
                    innerBatch.StatementsCompiledOnRun = compileContext.StatementsCompiledOnRun;
                    innerBatch.JoinOrderWarnedStatements = compileContext.JoinOrderWarnedStatements;
                }
                compiled = true;
                this.AttachStatementPlans(innerBatch, dynamicKey);
                outcomes.AddRange(CompileFailuresSent(innerBatch, inliningFailures));
                if (compileClock is not null)
                    outcomes.Add(new SimulatedInfoOutcome(CompileTime(innerBatch, compileClock, compileContext?.LastTopLevelStatementLine ?? 0, innerBatch.ErrorProcedureName)));

                // An error that ends the batch keeps what the batch sent before
                // it, which reaches the caller ahead of the error.
                var parser = innerBatch.Parser;
                parser.MoveNextOptional();
                innerBatch.CallerStreams = streams;
                innerBatch.YieldsBetweenStatements = outerBatch.YieldsBetweenStatements && streams;
                innerBatch.StreamsResultRows = outerBatch.StreamsResultRows && streams;
                if (streams)
                {
                    streamed = DispatchStatementsUntil(innerBatch, endKeyword: null).GetEnumerator();
                }
                else
                {
                    foreach (var outcome in DispatchStatementsUntil(innerBatch, endKeyword: null))
                        outcomes.Add(outcome);
                }
            }
            catch (SimulatedSqlException ex) when (compiled || ex.EndsCompileSilently)
            {
                batchError = ex;
            }

            if (streamed is not null)
            {
                yield return new SimulatedProcScopeBoundary(isEnter: true);
                foreach (var outcome in outcomes)
                    yield return outcome;
                outcomes.Clear();
                while (NextBodyOutcome(streamed, ref batchError))
                    yield return streamed.Current;
            }
        }
        finally
        {
            streamed?.Dispose();
            connection.NestingLevel -= nestingLevels;
            // A USE inside the dynamic batch binds for that batch only — the
            // caller resumes on the database it was on (probe-confirmed for
            // both EXEC('…') and sp_executesql). This is what makes
            // sp_MSforeachdb's `USE [?]` idiom run each command against its
            // own database without leaving the session there.
            connection.CurrentDatabase = enteredDatabase;
            connection.LeaveNestedDatabase();
            identityScope.Exit(IdentityScopeKind.Procedure);
            connection.NoCount = enteredNoCount;
            endedUnderImplicitTransactions = connection.ImplicitTransactions;
            enteredOptions.Restore(connection);
            // A temp table created by the dynamic batch is dropped when it
            // returns (SQL Server's module-scoped lifetime — so re-running the
            // same `create table #t` through sp_executesql, as tedious does,
            // doesn't collide with Msg 2714).
            innerBatch.DropScopedTempTables();
        }

        // Copy any pre-declared variable's final value back to the caller's
        // slot (sp_executesql OUTPUT writeback path).
        if (batchError is null && preDeclaredVariables is not null)
        {
            foreach (var (name, _) in preDeclaredVariables)
                preDeclaredVariables[name] = variables[name];
        }

        // Bracket the dynamic batch's outcomes with proc-scope markers so the
        // TDS endpoint renders them with DONEINPROC and closes the scope with
        // RETURNSTATUS + DONEPROC — matching real SQL Server, which runs an
        // EXEC('…') / sp_executesql body as a nested procedure scope. In-process
        // consumers ignore the markers.
        // The scope returns the status a procedure without a RETURN value
        // would (probed 2026-09-28 against SQL Server 2025); one an error ended
        // is closed by the calling statement, with no status.
        if (streamed is null)
            yield return new SimulatedProcScopeBoundary(isEnter: true);
        foreach (var outcome in outcomes)
            yield return outcome;
        if (batchError is { EndsCompileSilently: true })
        {
            // sp_executesql still returns @@ERROR, which nothing changed,
            // unless the caller's batch ends too (probed 2026-10-08 against
            // SQL Server 2025).
            var callerEnded = CallerEndedBySilentCompile(connection);
            if (callerEnded is null)
                yield return ScopeExit(outerBatch, viaSystemProcedure ? connection.LastErrorNumber : null);
            else if (ClosesSilentScope(callerEnded, outerBatch))
                yield return ScopeExit(outerBatch, returnStatus: null);
            if (callerEnded is not null)
                throw callerEnded;
            yield break;
        }
        if (batchError is not null)
        {
            batchError.LeavingScopeInto(outerBatch);
            ExceptionDispatchInfo.Throw(batchError);
        }
        // sp_executesql returns its last statement's @@ERROR instead (probed
        // 2026-10-06 against SQL Server 2025: 8134 after `SELECT 1/0`, 0 when
        // a statement after it succeeded, 50000 after a RAISERROR).
        yield return ScopeExit(outerBatch, viaSystemProcedure ? connection.LastErrorNumber : procFrame.StatusWithoutReturnValue);
        if ((connection.CurrentTransaction?.TranCount ?? 0) is var exitTranCount && exitTranCount != enteredTranCount && !endedUnderImplicitTransactions)
            throw SimulatedSqlException.TransactionCountMismatch(enteredTranCount, exitTranCount, procedure: "");
    }

    /// <summary>
    /// One parameter declared in an sp_executesql parameter-declaration
    /// string. Distinct from <see cref="ProcedureParameter"/> because
    /// sp_executesql params have no defaults — every declared param must be
    /// bound by a positional/named arg.
    /// </summary>
    private readonly struct SpExecuteSqlParam(string name, SqlType type, int? declaredMaxLength, bool isOutput, SqlValue? defaultValue, TableType? tableType = null)
    {
        public readonly string Name = name;
        public readonly SqlType Type = type;

        /// <summary>
        /// The table type of a table-valued parameter (declared
        /// <c>READONLY</c>), which the batch reads as a read-only table
        /// variable; null for a scalar one.
        /// </summary>
        public readonly TableType? TableType = tableType;

        /// <summary>The declared width a bound value is cut to, as a procedure parameter's is.</summary>
        public readonly int? DeclaredMaxLength = declaredMaxLength;
        public readonly bool IsOutput = isOutput;

        /// <summary>The declaration's constant default, which an unsupplied parameter takes in place of Msg 8178.</summary>
        public readonly SqlValue? Default = defaultValue;
    }
}
