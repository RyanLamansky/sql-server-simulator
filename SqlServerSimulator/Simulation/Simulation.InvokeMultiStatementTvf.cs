using SqlServerSimulator.Parser;
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
        var argCount = function.Parameters.Length;
        var argValues = new SqlValue[argCount];
        var isDefault = new bool[argCount];
        for (var i = 0; i < argCount; i++)
        {
            var argExpr = arguments[i];
            if (argExpr is null)
            {
                isDefault[i] = true;
                argValues[i] = SqlValue.Null(function.Parameters[i].Type);
            }
            else
            {
                argValues[i] = argExpr.Run(outerRuntime).CoerceTo(function.Parameters[i].Type);
            }
        }

        return InvokeMultiStatementTvfCore(outerBatch, function, argValues, isDefault);
    }

    private IEnumerable<byte[]> InvokeMultiStatementTvfCore(
        BatchContext outerBatch,
        MultiStatementTableValuedFunction function,
        SqlValue[] argValues,
        bool[] isDefault)
    {
        var connection = outerBatch.Connection;
        using var bodyCommand = new SimulatedDbCommand(this, connection);
#pragma warning disable CA2100 // function.BodyText is the function's pre-validated stored body, not external input
        bodyCommand.CommandText = function.BodyText;
#pragma warning restore CA2100

        var variables = new Dictionary<string, VariableSlot>(StringComparer.InvariantCultureIgnoreCase);
        for (var i = 0; i < function.Parameters.Length; i++)
        {
            var param = function.Parameters[i];
            var value = isDefault[i] && param.Default is { } defaultExpr
                ? defaultExpr.Run(new RuntimeContext(_ => throw SimulatedSqlException.MustDeclareScalarVariable(""), outerBatch))
                    .CoerceTo(param.Type)
                : argValues[i];
            variables[param.Name] = new VariableSlot(param.Type, declaredMaxLength: null, value, parameter: null);
        }

        // Construct a fresh return-table HeapTable for this call. Constraint
        // instances are shared across calls (immutable; row-level enforcement
        // reads kind + ordinals only). Each call gets its own object id +
        // create date so a recursive call doesn't collide on identity.
        var returnTable = new HeapTable(
            "@" + function.ReturnVariableName,
            function.OutputColumns,
            outerBatch.CurrentDatabase.AllocateObjectId(),
            schemaId: Database.DboSchemaId,
            createDate: outerBatch.CurrentStatement.UtcNow,
            keyConstraints: function.KeyConstraints,
            checkConstraints: function.CheckConstraints,
            isTableVariable: true);

        // MS-TVF body batches have no UdfFrame / ProcFrame — see Msg 178 note
        // on the dedicated BatchContext constructor's remarks.
        var innerBatch = new BatchContext(bodyCommand, variables);
        innerBatch.TableVariables[function.ReturnVariableName] = returnTable;
        connection.NestingLevel++;
        try
        {
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
            connection.NestingLevel--;
        }

        // Yield the accumulated @r rows. Iterating the table-variable's Heap
        // returns row bytes directly — same shape the inline TVF path yields.
        foreach (var rowBytes in returnTable.Rows)
            yield return rowBytes;
    }
}
