using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Handles <c>sp_set_session_context @key, @value [, @read_only]</c>,
    /// writing into the connection's <see cref="SimulatedDbConnection.SessionContext"/>,
    /// with real's refusals (probed 2026-10-04 against SQL Server 2025): fewer
    /// than two arguments is Msg 16903 and more than three Msg 16914, a name
    /// that is no parameter being ignored; a key that is NULL or no string is
    /// Msg 225, and one empty or over 256 bytes Msg 15666; a MAX-typed or xml
    /// value, and a NULL or non-numeric <c>@read_only</c>, Msg 15600; and a key
    /// set <c>@read_only = 1</c> refuses another write with Msg 15664. The
    /// stored value is type-preserved. Read by <see cref="Parser.Expressions.SessionContext"/>.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpSetSessionContext(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        if (arguments.Count < 2)
            throw SimulatedSqlException.ConnectionContextParameterCount(tooMany: false);
        if (arguments.Count > 3)
            throw SimulatedSqlException.ConnectionContextParameterCount(tooMany: true);

        SqlValue? key = null;
        var value = SqlValue.Null(SqlType.NVarchar);
        SqlValue? readOnly = null;
        var positional = 0;
        foreach (var arg in arguments)
        {
            var slot = arg.Name is null ? positional++
                : BuiltInToken.Equals(arg.Name, "key") ? 0
                : BuiltInToken.Equals(arg.Name, "value") ? 1
                : BuiltInToken.Equals(arg.Name, "read_only") ? 2
                : -1;
            switch (slot)
            {
                case 0: key = arg.Value; break;
                case 1: value = arg.Value; break;
                case 2: readOnly = arg.Value; break;
                default: break;
            }
        }

        if (key is not { IsNull: false } keyValue || !SqlType.IsStringCategory(keyValue.Type) || keyValue.Type is XmlSqlType)
            throw SimulatedSqlException.InvalidProcedureParameters("sp_set_session_context");
        var keyText = keyValue.CoerceTo(SqlType.NVarchar).AsString;
        if (keyText.Length is 0 or > 128)
            throw SimulatedSqlException.SessionContextKeyTooLong(keyText);
        if (SqlType.IsMaxLength(value.Type) || value.Type is XmlSqlType || value.Type == SqlType.Text || value.Type == SqlType.NText || value.Type == SqlType.Image)
            throw SimulatedSqlException.InvalidConnectionContextOption();
        var isReadOnly = readOnly switch
        {
            null => false,
            { IsNull: true } => throw SimulatedSqlException.InvalidConnectionContextOption(),
            { Type: var type } when type.Category is not (SqlTypeCategory.Integer or SqlTypeCategory.Decimal or SqlTypeCategory.Money or SqlTypeCategory.Approximate) && type != SqlType.Bit => throw SimulatedSqlException.InvalidConnectionContextOption(),
            { } flag => flag.CoerceTo(SqlType.Bit).AsBoolean,
        };

        var store = batch.Connection.SessionContext;
        if (store.TryGetValue(keyText, out var existing) && existing.ReadOnly)
            throw SimulatedSqlException.SessionContextKeyIsReadOnly(keyText);
        store[keyText] = (value, isReadOnly);
    }
}
