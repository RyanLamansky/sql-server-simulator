using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
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
        // EXECUTE-permission check at the invocation seam: once per statement,
        // covering the non-query (SET / IF operand) contexts the read-source
        // sink doesn't reach. Query-context invocations were pre-memoized by
        // CheckReadSources so they aren't re-checked per row.
        PermissionEnforcement.CheckScalarFunctionExecute(outerBatch, function);
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

        var udfFrame = new UdfFrame(function.ReturnType);
        // The body parses under the QUOTED_IDENTIFIER captured at CREATE, not
        // the caller's. Swapping the session flag (rather than seeding the
        // child parser) is what carries it to everything else that reads the
        // connection — dynamic SQL, the plan-cache key, the Msg 1934 gates.
        // Restored in the finally below; see docs/claude/grammar.md.
        var savedQuotedIdentifiers = connection.QuotedIdentifiers;
        connection.QuotedIdentifiers = function.UsesQuotedIdentifier;
        // Errors inside a scalar-UDF body attribute to the outer invoking
        // statement (probe-confirmed: real reports the SELECT's line, no
        // procedure) — so this frame leaves the exception unresolved.
        var innerBatch = new BatchContext(bodyCommand, variables, udfFrame) { SuppressDiagnosticsResolution = true };
        connection.NestingLevel++;
        // Module WITH EXECUTE AS: push the impersonation frame around the body
        // (OWNER / SELF → dbo, CALLER → no-op, a named user → that principal),
        // so the body's identity scalars observe the impersonated principal.
        var savedImpersonationDepth = connection.Security.ImpersonationDepth;
        PushModuleExecuteAsFrame(connection, function.ExecuteAsClause, outerBatch.CurrentDatabase);
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
            connection.QuotedIdentifiers = savedQuotedIdentifiers;
            connection.Security.RevertTo(savedImpersonationDepth);
        }

        return udfFrame.ReturnedValue;
    }
}
