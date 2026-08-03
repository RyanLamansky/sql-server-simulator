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
    /// <item>Unknown parameter name in a named-arg fires Msg 201 ("expects
    /// parameter '@X'").</item>
    /// <item>Missing required parameter (no default) fires Msg 201.</item>
    /// <item>Too many positional args fires Msg 8144.</item>
    /// <item>Duplicate named arg fires Msg 8143 (at parse, not here).</item>
    /// <item>Recursion past 32 fires Msg 217.</item>
    /// </list>
    /// </remarks>
    internal IEnumerable<SimulatedStatementOutcome> InvokeProcedure(
        BatchContext outerBatch,
        Procedure procedure,
        IReadOnlyList<ProcArgument> arguments,
        string? returnCodeVariableName,
        Synonym? viaSynonym = null)
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
            PermissionEnforcement.CheckObject(outerBatch, procedure.Schema.Database, "EXECUTE", procedure.ObjectId, procedure.SchemaId,
                procedure.Name, procedure.Schema.Name, procedure: $"{procedure.Schema.Name}.{procedure.Name}");
        }

        // Bind arguments to parameters. Positional args fill from the left;
        // named args do per-name lookup. Track which parameters are bound
        // so we can apply defaults / raise Msg 201 for unbound required.
        var boundValues = new SqlValue?[procedure.Parameters.Length];
        var boundOutputSlots = new VariableSlot?[procedure.Parameters.Length];
        var boundIsDefault = new bool[procedure.Parameters.Length];
        var boundTableValues = new HeapTable?[procedure.Parameters.Length];
        var boundCursorArgNames = new string?[procedure.Parameters.Length];
        var positionalIndex = 0;
        foreach (var arg in arguments)
        {
            int paramIndex;
            if (arg.Name is null)
            {
                paramIndex = positionalIndex++;
                if (paramIndex >= procedure.Parameters.Length)
                    throw SimulatedSqlException.TooManyArgumentsToFunction(procedure.Name);
            }
            else
            {
                paramIndex = -1;
                for (var i = 0; i < procedure.Parameters.Length; i++)
                {
                    if (outerBatch.CurrentDatabase.Collation.Equals(procedure.Parameters[i].Name, arg.Name))
                    {
                        paramIndex = i;
                        break;
                    }
                }
                if (paramIndex < 0)
                {
                    // Unknown named arg — Msg 201 names the first
                    // unsatisfied required parameter (real SQL Server's
                    // wording references the first missing one). Since we
                    // don't know which is unsatisfied yet, surface the
                    // procedure's first parameter as the placeholder.
                    throw SimulatedSqlException.ProcedureExpectsParameter(procedure.Name, procedure.Parameters[0].Name);
                }
            }
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
            // assigns the cursor, and it binds back to the caller at exit.
            if (param.IsCursor)
                continue;
            if (param.TableType is { } tvpType)
            {
                if (boundValues[i] is not null && boundTableValues[i] is null && !boundIsDefault[i])
                    throw SimulatedSqlException.OperandTypeClashScalarVsTableType(boundValues[i]!.Value.Type, tvpType.Name);
                continue;
            }
            if (boundValues[i] is not null && !boundIsDefault[i])
                continue;
            if (param.Default is null)
                throw SimulatedSqlException.ProcedureExpectsParameter(procedure.Name, param.Name);
            // Defaults are re-evaluated per call in the outer batch's
            // expression-evaluation context (mirrors scalar-UDF behavior).
            // Column refs inside a default would be invalid here; the
            // resolver throws Msg 137 for an unbound name.
            var defaultValue = param.Default.Run(
                new RuntimeContext(_ => throw SimulatedSqlException.MustDeclareScalarVariable(""), outerBatch));
            boundValues[i] = defaultValue.CoerceTo(param.Type);
        }

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
                var clone = tvpType.Clone("@" + param.Name, outerBatch, isTableValuedParameter: true);
                if (boundTableValues[i] is { } supplied)
                {
                    // Row bytes can point into the source heap's off-row
                    // pages (LOB chains, overflow-pushed var columns), so
                    // off-row-capable schemas decode and re-encode each row
                    // against the clone's heap; pointer-free schemas copy
                    // the bytes as-is.
                    var reencode = supplied.Heap.ReclaimColumns is not null;
                    foreach (var row in supplied.Heap.EnumerateRows())
                    {
                        _ = clone.Heap.Insert(reencode
                            ? RowEncoder.EncodeRow(supplied.StoredColumns, RowDecoder.DecodeRow(supplied.StoredColumns, row, supplied.Heap), clone.Heap)
                            : row);
                    }
                }
                tableVariables[param.Name] = clone;
                continue;
            }
            var coerced = boundValues[i]!.Value.CoerceTo(param.Type);
            variables[param.Name] = new VariableSlot(param.Type, declaredMaxLength: param.DeclaredMaxLength, coerced, parameter: null);
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
        // Module WITH EXECUTE AS: push the impersonation frame around the body
        // (OWNER / SELF → dbo, CALLER → no-op, a named user → that principal,
        // Msg 15517 here if missing). The frame is active while the body
        // materializes below (eager) so its scalars observe the impersonated
        // identity; it unwinds on body exit — the empty-body branch below and
        // the non-empty branch's finally each revert to this depth.
        var savedImpersonationDepth = connection.Security.ImpersonationDepth;
        PushProcedureExecuteAsFrame(connection, procedure, outerBatch.CurrentDatabase);

        var procFrame = new ProcFrame(procedure.Name);
        List<SimulatedStatementOutcome> outcomes;
        BatchContext? innerBatch = null;
        if (string.IsNullOrEmpty(procedure.BodyText))
        {
            outcomes = [];
            connection.Security.RevertTo(savedImpersonationDepth);
        }
        else
        {
            using var bodyCommand = new SimulatedDbCommand(this, connection);
#pragma warning disable CA2100 // procedure.BodyText is the simulator's own captured body span
            bodyCommand.CommandText = procedure.BodyText;
#pragma warning restore CA2100

            // The body parses under the QUOTED_IDENTIFIER captured at CREATE, not
            // the caller's. Swapping the session flag (rather than seeding the
            // child parser) is what carries it to everything else that reads the
            // connection — dynamic SQL, the plan-cache key, the Msg 1934 gates.
            // Restored in the finally below; see docs/claude/grammar.md.
            var savedQuotedIdentifiers = connection.QuotedIdentifiers;
            connection.QuotedIdentifiers = procedure.UsesQuotedIdentifier;
            innerBatch = new BatchContext(bodyCommand, variables, procFrame, tableVariables)
            {
                // Body errors report a line relative to the whole CREATE
                // statement and carry the schema-qualified procedure name,
                // matching real SqlClient (probe-confirmed).
                LineOffset = procedure.BodyLineOffset,
                ErrorProcedureName = $"{procedure.Schema.Name}.{procedure.Name}",
            };
            // Seed cursor parameters as unallocated cursor variables in the
            // child frame; the body SETs and OPENs a cursor on each.
            foreach (var param in procedure.Parameters)
            {
                if (param.IsCursor)
                    innerBatch.CursorVariables[param.Name] = null;
            }
            connection.NestingLevel++;
            // SET TEXTSIZE issued inside a proc body reverts at proc exit
            // (probe-confirmed 2026-07-19), like the standard SET options;
            // the body's result sets keep their production-time cap via the
            // dispatch loop's per-statement ClientTextSize stamp.
            var savedTextSize = connection.TextSize;
            // SET NOCOUNT reverts at proc exit the same way (probe-confirmed);
            // the counts the body's own statements reported were already
            // stamped as it produced them.
            var savedNoCount = connection.NoCount;
            // Materialize outcomes to a list so the try/finally cleanup
            // (NestingLevel decrement, OUTPUT param writeback, return-code
            // assignment) runs even when the iterator is partially consumed.
            try
            {
                var parser = innerBatch.Parser;
                parser.MoveNextOptional();
                outcomes = [.. DispatchStatementsUntil(innerBatch, endKeyword: null)];
            }
            finally
            {
                connection.NestingLevel--;
                connection.QuotedIdentifiers = savedQuotedIdentifiers;
                connection.TextSize = savedTextSize;
                connection.NoCount = savedNoCount;
                // Local temp tables the body created are dropped at proc exit
                // (SQL Server's module-scoped lifetime — so a re-entrant call
                // re-creates them without a Msg 2714 collision).
                innerBatch.DropScopedTempTables();
                // Unwind the module's EXECUTE AS frame on body exit (including
                // a body error), before control and the OUTPUT / return-code
                // writeback return to the caller's security context.
                connection.Security.RevertTo(savedImpersonationDepth);
                // The proc body's PRINT buffer belongs to the inner batch, so
                // the top-level flush in CreateResultSetsForCommand never sees
                // it; deliver it here (also on error, matching the real
                // server's flush-as-they-happen info tokens).
                innerBatch.FlushPrintMessages();
            }
        }

        // Writeback: any OUTPUT-marked argument copies the child batch's
        // final parameter value back to the caller's slot. Param.IsOutput
        // gating means a non-OUTPUT param doesn't write back even if the
        // caller passed OUTPUT — and an OUTPUT-declared param doesn't write
        // back unless the caller actually passed OUTPUT (probe-confirmed:
        // the caller's var retains its original value if OUTPUT keyword
        // was omitted on the call site).
        for (var i = 0; i < procedure.Parameters.Length; i++)
        {
            var param = procedure.Parameters[i];
            // Cursor OUTPUT parameter: bind the cursor the body assigned to the
            // parameter into the caller's cursor variable (refcounting it so it
            // survives the child frame's teardown). Must run before the child
            // frame is torn down (which drops the param's own reference).
            if (param.IsCursor)
            {
                if (boundCursorArgNames[i] is { } callerCursorName && innerBatch is not null
                    && innerBatch.CursorVariables.TryGetValue(param.Name, out var producedCursor))
                {
                    RebindCursorVariable(outerBatch, callerCursorName, producedCursor);
                }
                continue;
            }
            if (param.IsOutput && boundOutputSlots[i] is { } callerSlot)
            {
                var finalValue = variables[param.Name].Value;
                callerSlot.Value = finalValue.CoerceTo(callerSlot.DeclaredType);
            }
        }

        // Frame-exit teardown of the proc body's LOCAL cursors + cursor
        // variables (releasing their SCROLL_LOCKS locks). Cursors handed out
        // through an OUTPUT parameter above already have the caller's reference,
        // so the teardown's decrement leaves them alive.
        if (innerBatch is not null)
            TeardownFrameCursors(innerBatch);

        // Return code: coerce the proc's RETURN value (or default 0) to
        // int and store into the caller's `@rc` slot. Probe-confirmed:
        // RETURN NULL coerces to 0 (NULL doesn't propagate to the return
        // code), so the CoerceTo handles the NULL→0 fall-through via the
        // standard coercion path… except CoerceTo turns NULL-of-X into
        // NULL-of-int. Explicit fallback to 0 keeps that fidelity.
        if (returnCodeVariableName is not null)
        {
            var rcSlot = outerBatch.GetVariableSlot(returnCodeVariableName);
            var rc = procFrame.ReturnCode.IsNull
                ? SqlValue.FromInt32(0)
                : procFrame.ReturnCode.CoerceTo(SqlType.Int32);
            rcSlot.Value = rc.CoerceTo(rcSlot.DeclaredType);
        }

        foreach (var outcome in outcomes)
            yield return outcome;
    }
}
