using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// <c>sp_validatelogins</c>: the Windows logins whose accounts the server's
    /// lookup can't find, as <c>(SID, NT Login)</c> in name order — on the
    /// Linux server <c>BUILTIN\Administrators</c>, which no name lookup
    /// finds (see <see cref="WellKnownWindowsAccounts"/>). Any argument is Msg 8146 and a caller
    /// outside <c>sysadmin</c> and <c>securityadmin</c> Msg 15247 at the
    /// procedure's line 6 (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpValidateLogins(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        if (arguments.Count > 0)
        {
            var refusal = SimulatedSqlException.ArgumentsSuppliedToParameterlessRoutine("sp_validatelogins", state: 1);
            refusal.PreserveDiagnostics(0, calledAs);
            refusal.SystemProcedureBindingError = true;
            throw refusal;
        }
        var simulation = batch.Connection.Simulation;
        var effective = batch.Connection.Security.Effective;
        if (effective.IsDatabaseScoped
            || !(simulation.IsLoginSysadmin(effective.LoginName) || simulation.IsLoginInServerRole(effective.LoginName, SecurityAdminRoleId)))
        {
            throw SimulatedSqlException.UserDoesNotHavePermission().AtSystemProcedureLine(6);
        }
        var orphaned = new List<ServerLogin>();
        foreach (var (_, login) in simulation.Logins)
        {
            if (login.IsWindows && !TryResolveWindowsAccount(login.Name, out _, out _))
                orphaned.Add(login);
        }
        orphaned.Sort((a, b) => simulation.ServerCollation.Compare(a.Name, b.Name));
        yield return new SimulatedSqlResultSet([VarbinarySqlType.Get(85), SqlType.SystemName], ["SID", "NT Login"],
            orphaned.ConvertAll(static login => new[] { SqlValue.FromVarbinary(VarbinarySqlType.Get(85), login.Sid), SqlValue.FromSystemName(login.Name) }))
        {
            ColumnNullability = [true, false],
        };
    }

    private static readonly SystemProcedureParameter[] SpGetAlertInfoParameters =
    [
        new("includeaddresses", SqlType.Bit, 0, SqlValue.FromBoolean(false)),
    ];

    /// <summary>
    /// <c>sp_MSgetalertinfo [@includeaddresses]</c>: SQL Server Agent's alert
    /// settings, as an instance that never configured them holds them — no
    /// fail-safe operator, forwarding at severity 131 — and with
    /// <c>@includeaddresses = 1</c> the fail-safe operator's three empty
    /// addresses. A caller outside <c>sysadmin</c> is refused <c>EXECUTE</c>
    /// on it (Msg 229, in <c>mssqlsystemresource</c>) (probed 2026-10-16
    /// against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpGetAlertInfo(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments("sp_MSgetalertinfo", calledAs, arguments, SpGetAlertInfoParameters);
        if (!batch.Connection.Security.EffectiveIsDbo && !IsSysadminSession(batch))
            throw SimulatedSqlException.PermissionDenied("EXECUTE", "sp_MSgetalertinfo", "mssqlsystemresource", "sys");
        var text = NVarcharSqlType.Get(255, Collation.Baseline, Coercibility.Implicit);
        var nullText = SqlValue.Null(text);
        var zero = SqlValue.FromInt32(0);
        yield return new SimulatedSqlResultSet(
            [text, SqlType.Int32, text, SqlType.Int32, text, text, text, SqlType.Int32, SqlType.Int32],
            ["AlertFailSafeOperator", "AlertNotificationMethod", "AlertForwardingServer", "AlertForwardingSeverity", "AlertPagerToTemplate",
                "AlertPagerCCTemplate", "AlertPagerSubjectTemplate", "AlertPagerSendSubjectOnly", "AlertForwardAlways"],
            [[nullText, zero, nullText, SqlValue.FromInt32(131), nullText, nullText, nullText, zero, zero]])
        {
            ColumnNullability = [true, true, true, true, true, true, true, true, false],
        };
        if (!values[0].IsNull && values[0].CoerceTo(SqlType.Bit).AsBoolean)
        {
            yield return new SimulatedSqlResultSet([text, text, text], ["AlertFailSafeEmailAddress", "AlertFailSafePagerAddress", "AlertFailSafeNetSendAddress"],
                [[nullText, nullText, nullText]]);
        }
    }
}
