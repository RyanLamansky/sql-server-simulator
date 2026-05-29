using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Handles <c>sp_set_session_context @key, @value [, @read_only]</c>,
    /// writing into the connection's <see cref="SimulatedDbConnection.SessionContext"/>.
    /// Accepts both named and positional argument forms (probe-confirmed both
    /// work). A NULL / missing <c>@key</c> raises Msg 225; setting a key that
    /// was previously stored with <c>@read_only = 1</c> raises Msg 15664. The
    /// stored value is type-preserved (<c>SESSION_CONTEXT</c> surfaces it as
    /// nvarchar). Read by <see cref="Parser.Expressions.SessionContext"/>.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpSetSessionContext(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        string? key = null;
        var value = SqlValue.Null(SqlType.NVarchar);
        var readOnly = false;
        var positional = 0;
        foreach (var arg in arguments)
        {
            if (arg.Name is null)
            {
                switch (positional++)
                {
                    case 0: key = ExpectSessionContextKey(arg.Value); break;
                    case 1: value = arg.Value; break;
                    case 2: readOnly = SessionContextBit(arg.Value); break;
                    default: throw SimulatedSqlException.InvalidProcedureParameters("sp_set_session_context");
                }
                continue;
            }

            switch (arg.Name)
            {
                case var n when BuiltInToken.Equals(n, "key"): key = ExpectSessionContextKey(arg.Value); break;
                case var n when BuiltInToken.Equals(n, "value"): value = arg.Value; break;
                case var n when BuiltInToken.Equals(n, "read_only"): readOnly = SessionContextBit(arg.Value); break;
                default: throw SimulatedSqlException.InvalidProcedureParameters("sp_set_session_context");
            }
        }

        if (key is null)
            throw SimulatedSqlException.InvalidProcedureParameters("sp_set_session_context");

        var store = batch.Connection.SessionContext;
        if (store.TryGetValue(key, out var existing) && existing.ReadOnly)
            throw SimulatedSqlException.SessionContextKeyIsReadOnly(key);
        store[key] = (value, readOnly);
        yield break;
    }

    private static string ExpectSessionContextKey(SqlValue keyValue) =>
        keyValue.IsNull
            ? throw SimulatedSqlException.InvalidProcedureParameters("sp_set_session_context")
            : keyValue.CoerceTo(SqlType.NVarchar).AsString;

    private static bool SessionContextBit(SqlValue value) =>
        !value.IsNull && value.CoerceTo(SqlType.Bit).AsBoolean;
}
