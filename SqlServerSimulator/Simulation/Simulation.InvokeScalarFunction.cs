using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Executes a scalar UDF body and returns the function's result. Allocates
    /// a child <see cref="BatchContext"/> via the UDF-body constructor, seeds
    /// the parameter variables from <paramref name="argValues"/> (with any
    /// <paramref name="isDefault"/> slots evaluated from the parameter's
    /// stored default expression), bumps the connection's
    /// <see cref="SimulatedDbConnection.NestingLevel"/>, and dispatches the
    /// body through the existing statement loop. The body's value-form
    /// <c>RETURN &lt;expr&gt;</c> lands its result in
    /// <see cref="BatchContext.UdfFrame"/>'s
    /// <see cref="UdfFrame.ReturnedValue"/>, which this method reads after
    /// dispatch completes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Probe-confirmed semantics:
    /// </para>
    /// <list type="bullet">
    /// <item><c>WITH RETURNS NULL ON NULL INPUT</c>: any NULL arg
    /// short-circuits before the body dispatches; returns typed NULL.</item>
    /// <item>Recursion cap: 32 levels. Msg 217 fires when entering the body
    /// would exceed it.</item>
    /// <item>Body falling through without RETURN: simulator returns typed
    /// NULL. Real SQL Server raises an error in this case; the fidelity gap
    /// arises because the simulator defers Msg 455 validation from CREATE
    /// time (where real SQL Server checks the last statement) to runtime.
    /// Apps that never write a no-RETURN body don't notice.</item>
    /// </list>
    /// </remarks>
    internal SqlValue InvokeScalarFunction(
        BatchContext outerBatch,
        ScalarFunction function,
        SqlValue[] argValues,
        bool[] isDefault)
    {
        var connection = outerBatch.Connection;
        if (connection.NestingLevel >= SimulatedDbConnection.MaxNestingLevel)
            throw SimulatedSqlException.MaximumNestingLevelExceeded();

        // RETURNS NULL ON NULL INPUT short-circuit. Slots flagged as DEFAULT
        // will materialize from the stored default expression in the child
        // batch — they don't trigger the short-circuit on their own.
        if (function.ReturnsNullOnNullInput)
        {
            for (var i = 0; i < argValues.Length; i++)
            {
                if (!isDefault[i] && argValues[i].IsNull)
                    return SqlValue.Null(function.ReturnType);
            }
        }

        // Synthesize a command for the body. The connection is the caller's;
        // database / transaction state is shared. CommandText is the function's
        // own stored body (set at CREATE FUNCTION time from the user's own
        // command text) — never external input, so the CA2100 injection
        // concern doesn't apply here.
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

        var udfFrame = new UdfFrame(function.ReturnType);
        var innerBatch = new BatchContext(bodyCommand, variables, udfFrame);
        connection.NestingLevel++;
        try
        {
            var parser = innerBatch.Parser;
            parser.MoveNextOptional();
            foreach (var _ in DispatchStatementsUntil(innerBatch, endKeyword: null))
            {
                // Drain — UDF bodies don't yield result sets to the caller
                // (Msg 444 territory). Assignment-form SELECTs yield a
                // SimulatedNonQuery which we discard here.
            }
        }
        finally
        {
            connection.NestingLevel--;
        }

        return udfFrame.ReturnedValue;
    }
}
