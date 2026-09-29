using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    private static readonly SqlType[] SpHelpSrvRoleMemberSchema =
        [SqlType.SystemName, SqlType.SystemName, VarbinarySqlType.Get(85)];

    private static readonly string[] SpHelpSrvRoleMemberColumnNames = ["ServerRole", "MemberName", "MemberSID"];

    // master.dbo.spt_values' name column, which both of sp_helpsrvrole's
    // columns read.
    private static readonly SqlType[] SpHelpSrvRoleSchema =
    [
        NVarcharSqlType.Get(35, Collation.Baseline, Coercibility.Implicit),
        NVarcharSqlType.Get(35, Collation.Baseline, Coercibility.Implicit),
    ];

    private static readonly string[] SpHelpSrvRoleColumnNames = ["ServerRole", "Description"];

    /// <summary>The <c>Description</c> <c>sp_helpsrvrole</c> reports for each fixed server role, in <see cref="FixedServerRoles"/>' order.</summary>
    private static readonly string[] FixedServerRoleDescriptions =
    [
        "System Administrators", "Security Administrators", "Server Administrators", "Setup Administrators",
        "Process Administrators", "Disk Administrators", "Database Creators", "Bulk Insert Administrators",
        "Server State Readers", "Server State Managers", "Definition Readers", "Database Connectors",
        "Database Managers", "Login Managers", "Security Definition Readers", "Performance Definition Readers",
        "Server Security State Readers", "Server Performance State Readers",
    ];

    /// <summary>
    /// <c>EXEC sp_helpsrvrolemember [@srvrolename]</c>: each fixed server
    /// role's members — or one fixed role's — as <c>ServerRole</c> /
    /// <c>MemberName</c> / <c>MemberSID</c>, read through
    /// <c>sys.server_role_members</c> and <c>sys.server_principals</c> as real's
    /// body reads them, so a restricted session sees only what those views
    /// show it. A name that is no fixed role, a custom one included, is
    /// Msg 15412 (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpSrvRoleMember(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var (name, _) = ParseHelpArgs(arguments, "sp_helpsrvrolemember", firstName: "srvrolename");
        int? roleId = null;
        if (name is not null)
        {
            if (!FixedServerRoleIds.TryGetValue(name, out var fixedId))
                throw SimulatedSqlException.NotAKnownFixedRole(name);
            roleId = fixedId;
        }
        var rows = new List<SqlValue[]>();
        foreach (var (role, member, sid) in BuiltInResources.ServerRoleMemberRows(batch, roleId))
            rows.Add([SqlValue.FromSystemName(role), SqlValue.FromSystemName(member), SqlValue.FromVarbinary(sid)]);
        yield return new SimulatedSqlResultSet(SpHelpSrvRoleMemberSchema, SpHelpSrvRoleMemberColumnNames, rows);
    }

    /// <summary>
    /// <c>EXEC sp_helpsrvrole [@srvrolename]</c>: the fixed server roles with
    /// their descriptions, or one of them; any other name is Msg 15412 (probed
    /// 2026-09-29 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpSrvRole(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var (name, _) = ParseHelpArgs(arguments, "sp_helpsrvrole", firstName: "srvrolename");
        if (name is not null && !FixedServerRoleIds.ContainsKey(name))
            throw SimulatedSqlException.NotAKnownFixedRole(name);
        var rows = new List<SqlValue[]>();
        for (var i = 0; i < FixedServerRoles.Length; i++)
        {
            var roleName = FixedServerRoles[i].Name;
            if (name is null || BuiltInToken.Comparer.Equals(roleName, name))
                rows.Add([SqlValue.FromNVarchar((NVarcharSqlType)SpHelpSrvRoleSchema[0], roleName), SqlValue.FromNVarchar((NVarcharSqlType)SpHelpSrvRoleSchema[1], FixedServerRoleDescriptions[i])]);
        }
        yield return new SimulatedSqlResultSet(SpHelpSrvRoleSchema, SpHelpSrvRoleColumnNames, rows);
    }
}
