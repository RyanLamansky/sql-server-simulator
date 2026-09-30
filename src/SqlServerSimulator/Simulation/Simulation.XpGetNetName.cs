using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    private static readonly SqlType[] XpGetNetNameSchema = [SqlType.NVarchar];

    private static readonly string[] XpGetNetNameColumnNames = ["Server Net Name"];

    /// <summary>
    /// Handles <c>EXEC xp_getnetname [@netname OUTPUT [, 1 | 2]]</c>, which SMO
    /// reads for the server's fully qualified name: the machine name
    /// <c>SERVERPROPERTY('MachineName')</c> reports, with the <c>DOMAIN</c>
    /// placeholder a host outside a domain carries for form 1 and alone for
    /// form 2. Without an OUTPUT variable it answers one <c>Server Net Name</c>
    /// row, and a variable too narrow for the name reads NULL (probed
    /// 2026-09-30 against SQL Server 2025 on Linux).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeXpGetNetName(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var form = arguments.Count >= 2 && !arguments[1].Value.IsNull ? ScalarArguments.CoerceToInt(arguments[1].Value) : 0;
        var name = form switch
        {
            1 => "SIMULATED.DOMAIN",
            2 => "DOMAIN",
            _ => "SIMULATED",
        };
        var outputSlot = arguments.Count >= 1 ? arguments[0].OutputSlot : null;
        if (outputSlot is null)
        {
            yield return new SimulatedSqlResultSet(XpGetNetNameSchema, XpGetNetNameColumnNames, [[SqlValue.FromNVarchar(name)]]);
            yield break;
        }
        // A variable too narrow for the name reads NULL rather than a
        // truncated name.
        var width = outputSlot.DeclaredType switch
        {
            VarcharSqlType { length: > 0 } varchar => varchar.length,
            NVarcharSqlType { length: > 0 } nvarchar => nvarchar.length,
            CharSqlType fixedChar => fixedChar.length,
            NCharSqlType fixedNChar => fixedNChar.length,
            _ => int.MaxValue,
        };
        outputSlot.Value = width < name.Length ? SqlValue.Null(outputSlot.DeclaredType) : SqlValue.FromNVarchar(name).CoerceTo(outputSlot.DeclaredType);
    }
}
