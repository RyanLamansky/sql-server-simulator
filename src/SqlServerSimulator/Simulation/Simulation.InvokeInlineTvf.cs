using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Executes an inline TVF body and yields its row bytes. Allocates a
    /// child <see cref="BatchContext"/> wrapping a synthesized
    /// <see cref="SimulatedDbCommand"/> whose <c>CommandText</c> is the
    /// function's stored body. Parameters are seeded as typed variables in
    /// the child batch from <paramref name="arguments"/> (evaluated in the
    /// outer scope via <paramref name="outerResolver"/>). <c>DEFAULT</c>
    /// arg slots (null <see cref="Expression"/> entries) materialize from
    /// the parameter's stored default expression — same path scalar UDFs
    /// take.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counts toward <see cref="SimulatedDbConnection.NestingLevel"/>; a
    /// call that would exceed <see cref="SimulatedDbConnection.MaxNestingLevel"/>
    /// raises Msg 217 (same factory scalar UDFs use, since the cap is
    /// shared across "stored procedure, function, trigger, or view"
    /// recursion).
    /// </para>
    /// <para>
    /// The body is re-parsed on every call. The freshly parsed Selection
    /// captures the child batch's <see cref="VariableSlot"/> references, so
    /// per-call argument values flow through cleanly. The outer resolver
    /// passed to the body's Execute is null — TVF bodies in real SQL Server
    /// can't reach back into the caller's column scope; only the
    /// parameters carry outer values in.
    /// </para>
    /// </remarks>
    internal IEnumerable<byte[]> InvokeInlineTvf(
        BatchContext outerBatch,
        Func<MultiPartName, SqlValue>? outerResolver,
        InlineTableValuedFunction function,
        Expression?[] arguments)
    {
        var connection = outerBatch.Connection;
        if (connection.NestingLevel >= SimulatedDbConnection.MaxNestingLevel)
            throw SimulatedSqlException.MaximumNestingLevelExceeded();

        // Evaluate argument expressions in the caller's row scope. DEFAULT
        // slots stay flagged so the child batch evaluates the stored
        // default expression after seeding the rest of the parameters.
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

        return InvokeInlineTvfCore(outerBatch, function, argValues, isDefault);
    }

    private IEnumerable<byte[]> InvokeInlineTvfCore(
        BatchContext outerBatch,
        InlineTableValuedFunction function,
        SqlValue[] argValues,
        bool[] isDefault)
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
            var value = isDefault[i] && param.Default is { } defaultExpr
                ? defaultExpr.Run(new RuntimeContext(_ => throw SimulatedSqlException.MustDeclareScalarVariable(""), outerBatch))
                    .CoerceTo(param.Type)
                : argValues[i];
            variables[param.Name] = new VariableSlot(param.Type, declaredMaxLength: null, value, parameter: null);
        }

        // The UdfFrame here is a placeholder — inline TVF bodies don't use
        // value-form RETURN (the body is a single SELECT, not a
        // statement block ending in RETURN <expr>). Sharing the
        // batch constructor with scalar UDF invocation keeps the per-call
        // setup uniform across kinds.
        var dummyFrame = new UdfFrame(SqlType.Int32);
        // Body errors attribute to the outer invoking statement (probe-
        // confirmed: real reports the referencing SELECT's line, no procedure).
        var innerBatch = new BatchContext(bodyCommand, variables, dummyFrame) { SuppressDiagnosticsResolution = true };
        connection.NestingLevel++;
        try
        {
            var parser = innerBatch.Parser;
            parser.MoveNextRequired();
            var bodySelection = Selection.Parse(parser, depth: 0);
            var resultSet = bodySelection.Execute(innerBatch, outerResolver: null);
            foreach (var rowBytes in resultSet.RowBytes)
                yield return rowBytes;
        }
        finally
        {
            connection.NestingLevel--;
        }
    }
}
