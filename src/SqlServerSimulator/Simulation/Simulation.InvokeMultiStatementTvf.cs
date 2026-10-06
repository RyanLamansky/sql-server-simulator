using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Executes a multi-statement TVF body and yields its row bytes. Allocates
    /// a child <see cref="BatchContext"/> wrapping a synthesized
    /// <see cref="SimulatedDbCommand"/> whose <c>CommandText</c> is the
    /// function's stored body. Parameters are seeded as typed variables in
    /// the child batch; the function's declared return-table variable is
    /// constructed fresh per call and pre-seeded in
    /// <see cref="BatchContext.TableVariables"/> so the body's
    /// <c>INSERT INTO @r ...</c> / <c>SELECT FROM @r</c> route through the
    /// existing <c>@t TABLE</c> plumbing. After the body dispatches (bare
    /// <c>RETURN;</c> signals via <see cref="BatchContext.ReturnSignaled"/>,
    /// fall-through is also legal), the accumulated rows in the return-table
    /// HeapTable are streamed back to the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counts toward <see cref="SimulatedDbConnection.NestingLevel"/>; a
    /// call that would exceed
    /// <see cref="SimulatedDbConnection.MaxNestingLevel"/> raises Msg 217
    /// (shared cap across UDF / proc / trigger / view recursion).
    /// </para>
    /// <para>
    /// Neither <see cref="BatchContext.UdfFrame"/> nor
    /// <see cref="BatchContext.ProcFrame"/> is set on the child batch — so
    /// value-form <c>RETURN N</c> in the body naturally falls into the
    /// existing Msg 178 path in
    /// <see cref="ParseReturnStatement"/>. Bare <c>RETURN;</c> sets
    /// <see cref="BatchContext.ReturnSignaled"/> and the dispatch loop bails
    /// — same path procedures use.
    /// </para>
    /// </remarks>
    internal IEnumerable<byte[]> InvokeMultiStatementTvf(
        BatchContext outerBatch,
        Func<MultiPartName, SqlValue>? outerResolver,
        MultiStatementTableValuedFunction function,
        Expression?[] arguments)
    {
        var connection = outerBatch.Connection;
        if (connection.NestingLevel >= SimulatedDbConnection.MaxNestingLevel)
            throw SimulatedSqlException.MaximumNestingLevelExceeded();

        var outerRuntime = new RuntimeContext(
            outerResolver ?? (name => throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString())),
            outerBatch);
        var (argValues, isDefault, tableArguments) = EvaluateFunctionArguments(function, arguments, outerRuntime);

        return InvokeMultiStatementTvfCore(outerBatch, function, argValues, isDefault, tableArguments);
    }

    private IEnumerable<byte[]> InvokeMultiStatementTvfCore(
        BatchContext outerBatch,
        MultiStatementTableValuedFunction function,
        SqlValue[] argValues,
        bool[] isDefault,
        HeapTable?[]? tableArguments)
    {
        var connection = outerBatch.Connection;
        using var bodyCommand = new SimulatedDbCommand(this, connection);
#pragma warning disable CA2100 // function.BodyText is the function's pre-validated stored body, not external input
        bodyCommand.CommandText = function.BodyText;
#pragma warning restore CA2100

        var variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        for (var i = 0; i < function.Parameters.Length; i++)
        {
            var param = function.Parameters[i];
            if (param.TableType is not null)
                continue;
            var value = isDefault[i] && param.Default is { } defaultExpr
                ? defaultExpr.Run(new RuntimeContext(_ => throw SimulatedSqlException.MustDeclareScalarVariable(""), outerBatch))
                    .CoerceTo(param.Type)
                : argValues[i];
            // An argument past the parameter's width is cut to it, as a
            // variable assignment is (probed 2026-09-27 against SQL Server
            // 2025).
            value = Parser.Expressions.Cast.ApplyCoercion(value, param.Type, param.DeclaredMaxLength);
            variables[param.Name] = new VariableSlot(param.Type, declaredMaxLength: param.DeclaredMaxLength, value, parameter: null) { SpelledNumeric = param.SpelledNumeric };
        }

        // Construct a fresh return-table HeapTable for this call. Constraint
        // instances are shared across calls (immutable; row-level enforcement
        // reads kind + ordinals only). Each call gets its own object id +
        // create date so a recursive call doesn't collide on identity.
        // An identity column numbers each call's rows afresh.
        var returnColumns = function.OutputColumns;
        if (Array.Exists(returnColumns, static column => column.Identity is not null))
            returnColumns = Array.ConvertAll(returnColumns, static column => column.WithFreshIdentity());
        var returnTable = new HeapTable(
            "@" + function.ReturnVariableName,
            returnColumns,
            outerBatch.CurrentDatabase.AllocateObjectId(),
            schemaId: Database.DboSchemaId,
            createDate: outerBatch.CurrentStatement.UtcNow,
            keyConstraints: function.KeyConstraints,
            checkConstraints: function.CheckConstraints,
            isTableVariable: true)
        {
            ReturnTableOf = function,
        };

        // The body binds and runs in the function's own database; it has run
        // to completion before the first row is handed back.
        var moduleScope = ModuleDatabaseScope.Enter(connection, function.Schema.Database);
        // The body parses under the QUOTED_IDENTIFIER captured at CREATE, not
        // the caller's. Swapping the session flag (rather than seeding the
        // child parser) is what carries it to everything else that reads the
        // connection — dynamic SQL, the plan-cache key, the Msg 1934 gates.
        // Restored in the finally below; see docs/claude/grammar.md.
        var savedQuotedIdentifiers = connection.QuotedIdentifiers;
        connection.QuotedIdentifiers = function.UsesQuotedIdentifier;
        var savedAnsiNulls = connection.AnsiNulls;
        connection.AnsiNulls = function.UsesAnsiNulls;
        // MS-TVF body batches have no UdfFrame / ProcFrame — see Msg 178 note
        // on the dedicated BatchContext constructor's remarks.
        // Body errors attribute to the outer invoking statement (probe-
        // confirmed: even a multi-statement TVF's mid-body error surfaces the
        // referencing SELECT's line, no procedure), so this frame leaves the
        // exception unresolved for the enclosing statement to stamp.
        var innerBatch = new BatchContext(bodyCommand, variables)
        {
            SuppressDiagnosticsResolution = true,
            CalledFunctionBody = true,
            ModuleObjectId = function.ObjectId,
            ModuleDefinitionText = function.DefinitionText,
            ModuleSchema = function.Schema,
            CapturesQueryStore = true,
            MultiStatementTvfBody = true,
            OwnershipChainOwnerId = Ownership.EffectiveOwnerId(function.Schema.Database, function),
        };
        SeedTableValuedParameters(innerBatch, outerBatch, function.Parameters, tableArguments);
        innerBatch.InheritCallerTriggerFrame(outerBatch);
        innerBatch.TableVariables[function.ReturnVariableName] = returnTable;
        connection.NestingLevel++;
        var identityScope = IdentityScope.Enter(connection);
        // WITH EXECUTE AS runs the body as the principal it names, as a
        // scalar function's does (probed 2026-10-04 against SQL Server 2025).
        var savedImpersonationDepth = connection.Security.ImpersonationDepth;
        // What the body reads real doesn't report under STATISTICS IO; the
        // caller's statement reports its scan of the return table instead,
        // under a table variable's name (probed 2026-09-28 against SQL Server
        // 2025).
        var callerIo = connection.StatementIo;
        connection.StatementIo = null;
        try
        {
            PushModuleExecuteAsFrame(connection, function.ExecuteAsClause, function.ExecuteAsPrincipalId, function.Schema.Database, innerBatch.OwnershipChainOwnerId ?? Database.DboPrincipalId);
            var parser = innerBatch.Parser;
            parser.MoveNextOptional();
            foreach (var _ in DispatchStatementsUntil(innerBatch, endKeyword: null))
            {
                // Drain — MS-TVF bodies don't yield result sets to the caller.
                // The return-table's rows are projected below; intermediate
                // SELECTs inside the body (if any) are discarded, same as
                // scalar UDF bodies.
            }
        }
        finally
        {
            connection.Security.RevertTo(savedImpersonationDepth);
            connection.StatementIo = callerIo;
            connection.NestingLevel--;
            connection.QuotedIdentifiers = savedQuotedIdentifiers;
            connection.AnsiNulls = savedAnsiNulls;
            identityScope.Exit(IdentityScopeKind.Function);
            moduleScope.Exit();
        }

        // Yield the accumulated @r rows, re-encoded in the selection's own
        // shape: the heap stores no non-persisted computed column, and may
        // have pushed a long value off-row into a chain only the return
        // table's heap can resolve.
        if (callerIo is null)
        {
            foreach (var rowBytes in returnTable.Rows)
                yield return ReturnTableRowAsSelected(returnTable, rowBytes, outerBatch);
            yield break;
        }
        returnTable.InternalName = connection.Simulation.AllocateTableVariableIdentity().InternalName;
        var counts = callerIo.Touch(returnTable);
        _ = counts?.ScanCount += 1;
        var lastPage = -1;
        foreach (var (page, _, rowBytes) in returnTable.Heap.EnumerateRowsWithAddress())
        {
            counts?.Enter(page, ref lastPage);
            yield return ReturnTableRowAsSelected(returnTable, rowBytes, outerBatch);
        }
    }

    /// <summary>
    /// One return-table row as <see cref="Selection.ForMultiStatementTvf"/>
    /// declares it: every output column, a non-persisted computed one
    /// evaluated as it is read (so its error reaches the caller, as on real),
    /// and every value inline.
    /// </summary>
    private static byte[] ReturnTableRowAsSelected(HeapTable returnTable, byte[] rowBytes, BatchContext batch)
    {
        var values = DecodeFullRow(returnTable, rowBytes);
        for (var i = 0; i < returnTable.Columns.Length; i++)
        {
            if (returnTable.Columns[i] is { Computed: not null, IsPersisted: false })
                values[i] = EvaluateComputedColumn(returnTable, values, i, batch);
        }
        var schema = new SqlType[values.Length];
        for (var i = 0; i < schema.Length; i++)
            schema[i] = returnTable.Columns[i].Type;
        return RowEncoder.EncodeRow(schema, values);
    }
}
