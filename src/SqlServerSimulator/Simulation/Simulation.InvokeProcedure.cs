using System.Runtime.ExceptionServices;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Invokes a stored procedure: binds <paramref name="arguments"/> to the
    /// procedure's declared parameters, allocates a child
    /// <see cref="BatchContext"/> seeded with the bound values, dispatches
    /// the body (yielding its result sets to the caller's iterator), and on
    /// exit writes back to OUTPUT-marked caller variables and the optional
    /// return-code variable. Mirrors the <see cref="InvokeScalarFunction"/>
    /// structure with three differences: result sets propagate up
    /// (UDF bodies discard); a return-code slot replaces the typed return
    /// value; OUTPUT parameters write back to caller variable slots.
    /// </summary>
    /// <remarks>
    /// Probe-confirmed argument-binding semantics (SQL Server 2025,
    /// 2026-05-12):
    /// <list type="bullet">
    /// <item>Positional args bind by index; named args bind by lookup.</item>
    /// <item>Once any positional bind happens, named args may follow (mixed
    /// is fine going positional → named); the reverse fires Msg 119 at
    /// parse.</item>
    /// <item>The count is checked before any name: an argument to a
    /// procedure declaring no parameters fires Msg 8146, more arguments than
    /// it declares Msg 8144 (probed 2026-09-25).</item>
    /// <item>Missing required parameter (no default) fires Msg 201, and only
    /// then an unknown parameter name Msg 8145.</item>
    /// <item>Each argument then binds in the order written: a second one
    /// for a parameter is Msg 8143, an OUTPUT the parameter doesn't declare
    /// Msg 8162, a value that won't convert Msg 8114 (probed 2026-10-06).</item>
    /// <item>Recursion past 32 fires Msg 217.</item>
    /// </list>
    /// <para>
    /// <c>attributionName</c> is what an error raised in the body reports as
    /// its <c>Procedure</c> and <c>ERROR_PROCEDURE()</c> reads: the
    /// invocation's own spelling, brackets dropped and case kept, so
    /// <c>exec p</c> reports <c>p</c> and <c>exec DBO.P</c> reports
    /// <c>DBO.P</c> (probe-confirmed against SQL Server 2025, 2026-09-23).
    /// </para>
    /// <para>
    /// A call whose outcomes reach the client as it produces them
    /// (<paramref name="streams"/>) sends what a T-SQL body produces as each
    /// statement ends, so the request can pause inside the body as it does
    /// between its batch's statements; otherwise the body's outcomes gather,
    /// and go out once it has ended. Either way the session's state reverts
    /// as the body ends, whether it completes, fails or the client abandons
    /// the batch partway through it.
    /// </para>
    /// </remarks>
    internal IEnumerable<SimulatedStatementOutcome> InvokeProcedure(
        BatchContext outerBatch,
        Procedure procedure,
        List<ProcArgument> arguments,
        string? returnCodeVariableName,
        string attributionName,
        Synonym? viaSynonym = null,
        bool framesScope = false,
        bool recompile = false,
        bool streams = false)
    {
        var connection = outerBatch.Connection;
        if (connection.NestingLevel >= SimulatedDbConnection.MaxNestingLevel)
            throw SimulatedSqlException.MaximumNestingLevelExceeded();

        // EXECUTE permission is checked at the call site against the caller's
        // principal; the error's Procedure attribution names the proc (probe-
        // confirmed). Ownership chaining suppresses the check when the caller
        // is itself a module body (EnforcesPermissions is false there).
        // A call written through a synonym is checked against the synonym
        // instead — an EXECUTE grant on the base proc does not admit it, and the
        // denial names the synonym and carries no Procedure attribution, since
        // the module was never entered (probe-confirmed).
        if (viaSynonym is not null)
        {
            PermissionEnforcement.CheckSchemaObject(outerBatch, "EXECUTE", viaSynonym);
        }
        else
        {
            // Attributed to the procedure as the call spells it, at its line 1
            // (probed 2026-09-27 against SQL Server 2025).
            try
            {
                PermissionEnforcement.CheckObject(outerBatch, procedure.Schema.Database, "EXECUTE", procedure.ObjectId, procedure.SchemaId,
                    procedure.Name, procedure.Schema.Name, procedure: attributionName, securable: procedure);
            }
            // A login with no user in the procedure's database is refused at
            // the calling statement instead, unattributed (probed 2026-09-28).
            catch (SimulatedSqlException denied) when (denied.Number != 916)
            {
                denied.PreserveDiagnostics(1, attributionName);
                throw;
            }
        }

        // Bind arguments to parameters. Positional args fill from the left;
        // named args do per-name lookup. Track which parameters are bound
        // so we can apply defaults / raise Msg 201 for unbound required.
        var boundValues = new SqlValue?[procedure.Parameters.Length];
        var boundOutputSlots = new VariableSlot?[procedure.Parameters.Length];
        var boundIsDefault = new bool[procedure.Parameters.Length];
        var boundTableValues = new HeapTable?[procedure.Parameters.Length];
        var boundCursorArgNames = new string?[procedure.Parameters.Length];
        // A binding error reports line 0 and names the procedure as the EXEC
        // spelled it (probed 2026-09-23).
        SimulatedSqlException BindingError(SimulatedSqlException error)
        {
            error.PreserveDiagnostics(0, attributionName);
            return error;
        }

        if (arguments.Count > 0 && procedure.Parameters.Length == 0)
            throw BindingError(SimulatedSqlException.ArgumentsSuppliedToParameterlessRoutine(procedure.Name, state: 2));
        if (arguments.Count > procedure.Parameters.Length)
            throw BindingError(SimulatedSqlException.TooManyArgumentsToFunction(procedure.Name));

        // Each argument's parameter, -1 for a name matching none. A parameter
        // counts as supplied for Msg 201 however many arguments name it.
        var argumentParameters = new int[arguments.Count];
        var positionalIndex = 0;
        for (var a = 0; a < arguments.Count; a++)
        {
            var arg = arguments[a];
            var paramIndex = -1;
            if (arg.Name is null)
            {
                paramIndex = positionalIndex++;
            }
            else
            {
                for (var i = 0; i < procedure.Parameters.Length; i++)
                {
                    if (outerBatch.CurrentDatabase.Collation.Equals(procedure.Parameters[i].Name, arg.Name))
                    {
                        paramIndex = i;
                        break;
                    }
                }
            }
            argumentParameters[a] = paramIndex;
            if (paramIndex < 0 || boundValues[paramIndex] is not null)
                continue;
            boundValues[paramIndex] = arg.Value;
            boundOutputSlots[paramIndex] = arg.OutputSlot;
            boundIsDefault[paramIndex] = arg.IsDefault;
            boundTableValues[paramIndex] = arg.TableValue;
            boundCursorArgNames[paramIndex] = arg.CursorVariableName;
        }

        // Apply defaults for unbound parameters; raise Msg 201 for any
        // still-unbound parameter without a default. TVP parameters have a
        // distinct path: an unbound TVP parameter materializes as an empty
        // table-variable clone (probe-confirmed: <c>EXEC p</c> with the TVP
        // arg omitted is legal and the body sees an empty <c>@rows</c>),
        // while a TVP parameter passed a scalar argument raises Msg 206.
        for (var i = 0; i < procedure.Parameters.Length; i++)
        {
            var param = procedure.Parameters[i];
            // Cursor parameters carry no scalar value / default — the body
            // assigns the cursor, and it binds back to the caller at exit. A
            // scalar variable passed OUTPUT there is Msg 206, and a cursor
            // variable already holding a cursor Msg 16951, either one before
            // the body runs (probed 2026-09-29 against SQL Server 2025).
            if (param.IsCursor)
            {
                if (boundOutputSlots[i] is { } scalarSlot)
                    throw BindingError(SimulatedSqlException.OperandTypeClash(SimulatedSqlException.FamilyRootName(scalarSlot.DeclaredType), "cursor"));
                if (boundCursorArgNames[i] is { } callerName && outerBatch.CursorVariables.TryGetValue(callerName, out var held) && held is not null)
                    throw SimulatedSqlException.CursorOutputArgumentAllocated(callerName);
                continue;
            }
            if (param.TableType is { } tvpType)
            {
                if (boundValues[i] is not null && boundTableValues[i] is null && !boundIsDefault[i])
                    throw SimulatedSqlException.OperandTypeClashScalarVsTableType(boundValues[i]!.Value.Type, tvpType.Name);
                continue;
            }
            if (boundValues[i] is not null && !boundIsDefault[i])
                continue;
            if (param.Default is null)
                throw BindingError(SimulatedSqlException.ProcedureExpectsParameter(procedure.Name, param.Name));
            // Defaults are re-evaluated per call in the outer batch's
            // expression-evaluation context (mirrors scalar-UDF behavior).
            // Column refs inside a default would be invalid here; the
            // resolver throws Msg 137 for an unbound name.
            var defaultValue = param.Default.Run(
                new RuntimeContext(_ => throw SimulatedSqlException.MustDeclareScalarVariable(""), outerBatch));
            // A default that fails to convert is the call's binding error, at
            // line 0 under the procedure, as an argument's is (probed
            // 2026-09-30 against SQL Server 2025).
            try
            {
                boundValues[i] = defaultValue.CoerceTo(param.Type);
            }
            catch (SimulatedSqlException conversion)
            {
                throw BindingError(conversion);
            }
        }

        // Then each argument in the order written: a second one for its
        // parameter is Msg 8143 — unless an unknown name came first, which
        // stops the duplicate check and leaves Msg 8145 to the end — an OUTPUT
        // the parameter doesn't declare Msg 8162, and a value that won't
        // assign or convert Msg 206 / 8114, so `@a = 1, @a = 'x'` is Msg 8143
        // where `@a = 'x', @a = 1` is Msg 8114 (probed 2026-10-06 against SQL
        // Server 2025).
        var coercedValues = new SqlValue?[procedure.Parameters.Length];
        var firstSpellings = new string?[procedure.Parameters.Length];
        string? unknownArgument = null;
        for (var a = 0; a < arguments.Count; a++)
        {
            var arg = arguments[a];
            var i = argumentParameters[a];
            if (i < 0)
            {
                unknownArgument ??= arg.Name;
                continue;
            }
            var param = procedure.Parameters[i];
            if (firstSpellings[i] is { } firstSpelling)
            {
                if (unknownArgument is not null)
                    continue;
                throw BindingError(SimulatedSqlException.ParameterSuppliedMultipleTimes(firstSpelling));
            }
            firstSpellings[i] = arg.Name ?? param.Name;
            if (arg.OutputSlot is not null && !param.IsOutput && !param.IsCursor)
                throw BindingError(SimulatedSqlException.ParameterNotDeclaredOutput(param.Name));
            if (param.IsCursor || param.TableType is not null || arg.IsDefault)
                continue;
            // A supplied value meets the parameter's one-way assignment rule
            // as the call runs, reported at line 0 under the procedure
            // (probed 2026-10-06 against SQL Server 2025).
            if (!arg.IsUntypedNull)
            {
                try
                {
                    AssignmentRules.RequireAssignable(arg.Value.Type, param.Type);
                }
                catch (SimulatedSqlException clash)
                {
                    throw BindingError(clash);
                }
            }
            // A CLR procedure's conversion failure carries state 1 (probed
            // 2026-09-28 against SQL Server 2025).
            coercedValues[i] = BindParameterValue(arg.Value, param.Type, param.DeclaredMaxLength, attributionName, procedure.ClrEntry is null ? (byte)5 : (byte)1,
                sourceName: arg.IsNumericLiteral ? "numeric" : null);
        }
        if (unknownArgument is not null)
            throw BindingError(SimulatedSqlException.NotAParameterForProcedure(unknownArgument, procedure.Name));

        // Seed the child batch's variable dictionary with the bound values,
        // coerced to each parameter's declared type. TVP parameters land in
        // a parallel table-variables seed (registered on the child batch
        // post-construction).
        var variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        var tableVariables = new Dictionary<string, HeapTable>(BatchContext.VariableNameComparer);
        for (var i = 0; i < procedure.Parameters.Length; i++)
        {
            var param = procedure.Parameters[i];
            if (param.IsCursor)
                continue; // seeded into the child's CursorVariables below
            if (param.TableType is { } tvpType)
            {
                // The caller may have supplied an existing table variable
                // (which we pass through as-is, but flagged read-only) or
                // omitted the arg entirely (we clone an empty table from
                // the type template).
                tableVariables[param.Name] = CloneTableValuedArgument(tvpType, param.Name, outerBatch, boundTableValues[i]);
                continue;
            }
            var coerced = coercedValues[i]
                ?? BindParameterValue(boundValues[i]!.Value, param.Type, param.DeclaredMaxLength, attributionName, procedure.ClrEntry is null ? (byte)5 : (byte)1);
            var slot = new VariableSlot(param.Type, declaredMaxLength: param.DeclaredMaxLength, SqlValue.Null(param.Type), parameter: null)
            {
                SpelledNumeric = param.SpelledNumeric,
                XmlSchemaCollection = param.XmlSchemaCollection,
                XmlDocument = param.XmlDocument,
            };
            try
            {
                slot.Assign(coerced);
            }
            catch (SimulatedSqlException invalid) when (param.XmlSchemaCollection is not null)
            {
                // A typed parameter validates its argument as the body's
                // first act, so the error names the procedure at line 0
                // (probed 2026-10-06 against SQL Server 2025).
                invalid.PreserveDiagnostics(0, attributionName);
                throw;
            }
            variables[param.Name] = slot;
        }

        // Synthesize a command wrapping the proc body and a child batch.
        // The connection is the caller's, so database / transaction state
        // is shared. Result sets yielded by the body's dispatch propagate
        // through this iterator to the outer caller.
        //
        // Empty body short-circuit: `CREATE PROC p AS` (with nothing after
        // AS) is legal in real SQL Server. ParserContext rejects an empty
        // CommandText, so we skip the dispatch entirely — the proc behaves
        // as if a no-op body ran (default RETURN code 0, no result sets,
        // no output-param mutations).
        var procFrame = new ProcFrame(procedure.Name);
        List<SimulatedStatementOutcome> outcomes = [];
        SimulatedSqlException? bodyError = null;
        var enteredTranCount = 0;
        // A body ending under SET IMPLICIT_TRANSACTIONS ON — the caller's or
        // its own — raises no Msg 266 for the transaction count it changed
        // (probed 2026-09-28 against SQL Server 2025).
        var endedUnderImplicitTransactions = connection.ImplicitTransactions;
        BatchContext? innerBatch = null;
        // What the body's run changed on the session, given back as it ends.
        var bodyEntered = false;
        var savedImpersonationDepth = 0;
        var savedQuotedIdentifiers = false;
        var savedAnsiNulls = false;
        var savedTextSize = 0;
        var savedNoCount = false;
        SimulatedDbConnection.SessionOptionScope savedOptions = default;
        var compiles = false;
        IEnumerator<SimulatedStatementOutcome>? streamed = null;
        SimulatedDbCommand? bodyCommand = null;
        // The body binds and runs in the procedure's own database, which is
        // the session's unless the call named it with a three-part name.
        var moduleScope = ModuleDatabaseScope.Enter(connection, procedure.Schema.Database);
        var identityScope = IdentityScope.Enter(connection);
        try
        {
            // Module WITH EXECUTE AS: push the impersonation frame around the body
            // (OWNER / SELF → dbo, CALLER → no-op, a named user → that principal,
            // Msg 15517 here if missing). The frame is active while the body
            // runs so its scalars observe the impersonated identity; it unwinds
            // on body exit — the empty-body branch below and the finally each
            // revert to this depth.
            savedImpersonationDepth = connection.Security.ImpersonationDepth;
            PushProcedureExecuteAsFrame(connection, procedure, procedure.Schema.Database);
            if (procedure.ClrEntry is { } clrEntry)
            {
                connection.Security.RevertTo(savedImpersonationDepth);
                enteredTranCount = connection.CurrentTransaction?.TranCount ?? 0;
                bodyError = RunClrProcedure(outerBatch, procedure, clrEntry, variables, procFrame, outcomes, attributionName);
            }
            else if (string.IsNullOrEmpty(procedure.BodyText))
            {
                connection.Security.RevertTo(savedImpersonationDepth);
            }
            else
            {
#pragma warning disable CA2100 // procedure.BodyText is the simulator's own captured body span
                bodyCommand = new SimulatedDbCommand(this, connection) { CommandText = procedure.BodyText };
#pragma warning restore CA2100

                // The body parses under the QUOTED_IDENTIFIER captured at CREATE, not
                // the caller's. Swapping the session flag (rather than seeding the
                // child parser) is what carries it to everything else that reads the
                // connection — dynamic SQL, the plan-cache key, the Msg 1934 gates.
                // Restored in the finally below; see docs/claude/grammar.md.
                savedQuotedIdentifiers = connection.QuotedIdentifiers;
                connection.QuotedIdentifiers = procedure.UsesQuotedIdentifier;
                savedAnsiNulls = connection.AnsiNulls;
                connection.AnsiNulls = procedure.UsesAnsiNulls;
                innerBatch = new BatchContext(bodyCommand, variables, procFrame, tableVariables)
                {
                    // Body errors report a line relative to the whole CREATE
                    // statement (probe-confirmed) and the invocation's spelling of
                    // the procedure's name.
                    LineOffset = procedure.BodyLineOffset,
                    ErrorProcedureName = attributionName,
                    ContinueOnError = ContinuesCalledBatch(outerBatch),
                    OwnershipChainOwnerId = Ownership.EffectiveOwnerId(procedure.Schema.Database, procedure),
                    ModuleObjectId = procedure.ObjectId,
                    ModuleDefinitionText = procedure.DefinitionText,
                    ModuleSchema = procedure.Schema,
                };
                // Seed cursor parameters as unallocated cursor variables in the
                // child frame; the body SETs and OPENs a cursor on each.
                foreach (var param in procedure.Parameters)
                {
                    if (param.IsCursor)
                        innerBatch.CursorVariables[param.Name] = null;
                }
                bodyEntered = true;
                connection.NestingLevel++;
                // SET TEXTSIZE issued inside a proc body reverts at proc exit
                // (probe-confirmed 2026-07-19), like the standard SET options;
                // the body's result sets keep their production-time cap via the
                // dispatch loop's per-statement ClientTextSize stamp.
                savedTextSize = connection.TextSize;
                // SET NOCOUNT reverts at proc exit the same way (probe-confirmed);
                // the counts the body's own statements reported were already
                // stamped as it produced them.
                savedNoCount = connection.NoCount;
                // XACT_ABORT / ROWCOUNT / DATEFIRST revert the same way, and unlike
                // the six ANSI toggles the body's own SET does take effect while it
                // runs (probe-confirmed for all three).
                savedOptions = new SimulatedDbConnection.SessionOptionScope(connection);
                enteredTranCount = connection.CurrentTransaction?.TranCount ?? 0;
                // STATISTICS TIME reports the body's compile on every call,
                // ahead of what the body sends, at its last statement's line
                // (probed 2026-09-28 against SQL Server 2025), so a body it
                // reports gathers its outcomes.
                compiles = connection.StatisticsTime && ReportsStatistics(outerBatch);
                innerBatch.CallerStreams = streams && !compiles;
                innerBatch.YieldsBetweenStatements = outerBatch.YieldsBetweenStatements && innerBatch.CallerStreams;
                // An error that ends the body keeps what the body sent before
                // it, which reaches the caller ahead of the error.
                try
                {
                    // The body compiles as the call is about to run it, unless
                    // its plan stands; what it couldn't inline goes out first,
                    // and an error compiling it is the EXEC's own, as dynamic
                    // SQL's is.
                    var recompiles = recompile || procedure.RecompilesEveryCall;
                    if (this.CompileModuleBody(innerBatch, procedure.Schema.Database, ref procedure.CompiledPlan, parent: null, recompiles, keepsPlan: !recompiles, out var compileError) is { } failures)
                        outcomes.AddRange(CompileFailuresSent(innerBatch, failures));
                    if (compileError is not null)
                    {
                        compileError.EndedCalledBatch = true;
                        compileError.EndedCalledBatchIn = innerBatch;
                        throw compileError;
                    }
                    var parser = innerBatch.Parser;
                    parser.MoveNextOptional();
                    if (innerBatch.CallerStreams)
                    {
                        streamed = DispatchStatementsUntil(innerBatch, endKeyword: null).GetEnumerator();
                    }
                    else
                    {
                        foreach (var outcome in DispatchStatementsUntil(innerBatch, endKeyword: null))
                            outcomes.Add(outcome);
                    }
                }
                catch (SimulatedSqlException ex)
                {
                    bodyError = ex;
                }
            }

            if (streamed is not null)
            {
                if (framesScope)
                    yield return new SimulatedProcScopeBoundary(isEnter: true);
                foreach (var outcome in outcomes)
                    yield return outcome;
                outcomes.Clear();
                while (NextBodyOutcome(streamed, ref bodyError))
                    yield return streamed.Current;
            }
        }
        finally
        {
            streamed?.Dispose();
            if (bodyEntered)
            {
                if (compiles)
                    outcomes.Insert(0, new SimulatedInfoOutcome(CompileTime(innerBatch!, clock: null, innerBatch!.LastTopLevelStatementLine + innerBatch.LineOffset, attributionName)));
                connection.NestingLevel--;
                endedUnderImplicitTransactions = connection.ImplicitTransactions;
                connection.QuotedIdentifiers = savedQuotedIdentifiers;
                connection.AnsiNulls = savedAnsiNulls;
                connection.TextSize = savedTextSize;
                connection.NoCount = savedNoCount;
                savedOptions.Restore(connection);
                // Local temp tables the body created are dropped at proc exit
                // (SQL Server's module-scoped lifetime — so a re-entrant call
                // re-creates them without a Msg 2714 collision).
                innerBatch!.DropScopedTempTables();
                // Unwind the module's EXECUTE AS frame on body exit (including
                // a body error), before control and the OUTPUT / return-code
                // writeback return to the caller's security context.
                connection.Security.RevertTo(savedImpersonationDepth);
            }
            bodyCommand?.Dispose();
            identityScope.Exit(IdentityScopeKind.Procedure);
            moduleScope.Exit();
        }

        // Writeback: any OUTPUT-marked argument copies the child batch's
        // final parameter value back to the caller's slot. Param.IsOutput
        // gating means a non-OUTPUT param doesn't write back even if the
        // caller passed OUTPUT — and an OUTPUT-declared param doesn't write
        // back unless the caller actually passed OUTPUT (probe-confirmed:
        // the caller's var retains its original value if OUTPUT keyword
        // was omitted on the call site).
        // An error that ended the body leaves the caller's variables as they
        // were: no OUTPUT writeback and no return status.
        var bodyCompleted = bodyError is null;
        SimulatedSqlException? writebackError = null;
        for (var i = 0; bodyCompleted && i < procedure.Parameters.Length; i++)
        {
            var param = procedure.Parameters[i];
            // Cursor OUTPUT parameter: bind the cursor the body assigned to the
            // parameter into the caller's cursor variable (refcounting it so it
            // survives the child frame's teardown). Must run before the child
            // frame is torn down (which drops the param's own reference).
            if (param.IsCursor)
            {
                // Only an open cursor reaches the caller; one the body left
                // closed leaves the variable unallocated (probed 2026-09-29
                // against SQL Server 2025).
                if (boundCursorArgNames[i] is { } callerCursorName && innerBatch is not null
                    && innerBatch.CursorVariables.TryGetValue(param.Name, out var producedCursor)
                    && producedCursor is { IsOpen: true })
                {
                    RebindCursorVariable(outerBatch, callerCursorName, producedCursor);
                }
                continue;
            }
            if (param.IsOutput && boundOutputSlots[i] is { } callerSlot)
            {
                // Written back as SET assigns the caller's variable, so a
                // string wider than it is cut to its width (probed 2026-09-28
                // against SQL Server 2025), and one that doesn't convert is
                // Msg 8114 state 2 at line 0, the variable kept, and the
                // batch ended (probed 2026-10-02).
                var finalValue = variables[param.Name].Value;
                try
                {
                    callerSlot.Value = BindParameterValue(finalValue, callerSlot.DeclaredType, callerSlot.DeclaredMaxLength, attributionName);
                }
                catch (SimulatedSqlException failure) when (failure.Number == 8114)
                {
                    var refused = SimulatedSqlException.OutputParameterConversionFailed(finalValue.Type, callerSlot.DeclaredType);
                    refused.PreserveDiagnostics(0, attributionName);
                    writebackError ??= refused;
                }
            }
        }

        // Frame-exit teardown of the proc body's LOCAL cursors + cursor
        // variables (releasing their SCROLL_LOCKS locks). Cursors handed out
        // through an OUTPUT parameter above already have the caller's reference,
        // so the teardown's decrement leaves them alive.
        if (innerBatch is not null)
            TeardownFrameCursors(innerBatch);

        // Return status: the body's RETURN value, or the status its own
        // errors earned it.
        if (bodyCompleted && returnCodeVariableName is not null)
        {
            var rcSlot = outerBatch.GetVariableSlot(returnCodeVariableName);
            var rc = procFrame.ReturnCode ?? procFrame.StatusWithoutReturnValue;
            // A status the variable can't hold is Msg 8114 state 3, the
            // variable kept (probed 2026-10-02 against SQL Server 2025).
            try
            {
                rcSlot.Value = BindParameterValue(SqlValue.FromInt32(rc), rcSlot.DeclaredType, rcSlot.DeclaredMaxLength, attributionName, conversionState: 3);
            }
            catch (SimulatedSqlException failure)
            {
                writebackError ??= failure;
            }
        }

        // A call its caller frames brackets the body in scope markers, the
        // exit carrying the return status (probed 2026-09-28 against SQL
        // Server 2025); one an error ended is closed by the calling statement.
        if (framesScope && streamed is null)
            yield return new SimulatedProcScopeBoundary(isEnter: true);
        foreach (var outcome in outcomes)
            yield return outcome;
        if (bodyError is not null)
            ExceptionDispatchInfo.Throw(bodyError);
        if (writebackError is not null)
            throw writebackError;
        if ((connection.CurrentTransaction?.TranCount ?? 0) is var exitTranCount && exitTranCount != enteredTranCount && !endedUnderImplicitTransactions)
        {
            if (framesScope)
                yield return new SimulatedReturnStatus(procFrame.ReturnCode ?? procFrame.StatusWithoutReturnValue) { InModule = outerBatch.ProcFrame is not null || outerBatch.TriggerFrame is not null };
            throw SimulatedSqlException.TransactionCountMismatch(enteredTranCount, exitTranCount, attributionName);
        }
        if (framesScope)
            yield return ScopeExit(outerBatch, procFrame.ReturnCode ?? procFrame.StatusWithoutReturnValue);
    }

    /// <summary>
    /// Moves a streaming body to its next outcome, an error that ends the
    /// body landing on <paramref name="bodyError"/> — caught around the move,
    /// since the iterator sending each outcome can't catch around its
    /// <c>yield</c>.
    /// </summary>
    private static bool NextBodyOutcome(IEnumerator<SimulatedStatementOutcome> body, ref SimulatedSqlException? bodyError)
    {
        try
        {
            return body.MoveNext();
        }
        catch (SimulatedSqlException ex)
        {
            bodyError = ex;
            return false;
        }
    }

    /// <summary>
    /// Whether a procedure, trigger or dynamic-SQL body called from
    /// <paramref name="caller"/> runs on past a statement-terminating error the
    /// way its caller would, sending the error among its outcomes (probed
    /// 2026-09-24 against SQL Server 2025). A trigger body starts under
    /// <c>XACT_ABORT ON</c>, so only an error that option exempts continues
    /// there. It does when the caller itself
    /// continues and no <c>TRY</c> in it is open, since an open one catches
    /// the body's first error and abandons the rest. An <c>INSERT … EXEC</c>
    /// body runs on the same way, inserting what its later statements return
    /// (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    private static bool ContinuesCalledBatch(BatchContext caller)
        => caller.ContinueOnError && caller.TryFrameDepth == 0;

    /// <summary>
    /// Converts an argument to its parameter's declared type the way real
    /// binds a procedure or <c>sp_executesql</c> parameter, probed 2026-09-23
    /// against SQL Server 2025: every conversion failure becomes Msg 8114
    /// state 5 naming the two types (<c>'x'</c> for an <c>int</c> is not the
    /// Msg 245 a CAST raises, and <c>300</c> for a <c>tinyint</c> not Msg
    /// 220), while a malformed <c>xml</c> value keeps its own parse error —
    /// both reported at line 0 and attributed to <paramref name="procedure"/>
    /// (empty for <c>sp_executesql</c>).
    /// </summary>
    internal static SqlValue BindParameterValue(SqlValue value, SqlType target, int? declaredMaxLength, string procedure, byte conversionState = 5, string? sourceName = null)
    {
        try
        {
            // An argument is assigned to its parameter as SET assigns a
            // variable, so a string past the declared width is cut to it
            // (probed 2026-09-25 against SQL Server 2025: `@a nvarchar(2)`
            // bound `N'abcd'` reads `ab`).
            return Parser.Expressions.Cast.ApplyCoercion(value, target, declaredMaxLength);
        }
        catch (SimulatedSqlException ex) when (ex.Number is 6359 or (>= 9400 and <= 9465))
        {
            ex.PreserveDiagnostics(0, procedure);
            throw;
        }
        catch (Exception ex) when (ex is OverflowException || (ex is SimulatedSqlException sql && Parser.Expressions.Cast.IsConversionFailure(sql.Number)))
        {
            var converting = sourceName is not null
                ? SimulatedSqlException.ConvertingDataTypeError(sourceName, SimulatedSqlException.FamilyRootName(target), conversionState)
                : SimulatedSqlException.ConvertingDataTypeError(value.Type, SimulatedSqlException.FamilyRootName(target), conversionState);
            converting.PreserveDiagnostics(0, procedure);
            throw converting;
        }
    }
}
