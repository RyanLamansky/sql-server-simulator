using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The server-scope reports sp_helplogins and sp_helpextendedproc.
partial class Simulation
{
    private static readonly string[] SpHelpLoginsColumnNames = ["LoginName", "SID", "DefDBName", "DefLangName", "AUser", "ARemote"];

    private static readonly string[] SpHelpLoginsUserColumnNames = ["LoginName", "DBName", "UserName", "UserOrAlias"];

    private static readonly CharSqlType HelpLoginsAUserType = CharSqlType.Get(5, Collation.Baseline, Coercibility.Implicit);

    private static readonly CharSqlType HelpLoginsARemoteType = CharSqlType.Get(7, Collation.Baseline, Coercibility.Implicit);

    private static readonly CharSqlType HelpLoginsUserOrAliasType = CharSqlType.Get(8, Collation.Baseline, Coercibility.Implicit);

    private static readonly SqlType[] SpHelpExtendedProcSchema =
        [SqlType.SystemName, NVarcharSqlType.Get(255, Collation.Baseline, Coercibility.Implicit)];

    private static readonly string[] SpHelpExtendedProcColumnNames = ["name", "dll"];

    /// <summary>
    /// <c>EXEC sp_helplogins [@LoginNamePattern]</c>: two result sets — each
    /// login with its SID, default database and language, whether any
    /// database has a user for it (<c>AUser</c>, <c>yes</c> or <c>NO</c>) and
    /// the fixed <c>no</c> of <c>ARemote</c>; then, for every database in name
    /// order, each user a login maps to (<c>User</c>) and each role that user
    /// is directly a member of (<c>MemberOf</c>), sorted by login, database
    /// and name. The argument is an exact name under the server collation,
    /// not a pattern, and a name matching no login answers two empty sets
    /// (probed 2026-10-11 against SQL Server 2025).
    /// </summary>
    /// <remarks>
    /// The name columns are typed as real's report types them: each is
    /// <c>nvarchar</c> as wide as the longest value's <c>datalength</c>, or
    /// as its header is long when there are no rows, as <c>sp_helpuser</c>'s.
    /// </remarks>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpLogins(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var (name, _) = ParseHelpArgs(arguments, "sp_helplogins", firstName: "LoginNamePattern");
        var simulation = batch.Connection.Simulation;
        var collation = simulation.ServerCollation;

        var logins = new List<(string Name, string DefaultDatabase, string DefaultLanguage)>();
        if (name is null || collation.Equals(name, "sa"))
            logins.Add(("sa", "master", "us_english"));
        foreach (var (_, login) in simulation.Logins)
        {
            if (name is null || collation.Equals(login.Name, name))
                logins.Add((login.Name, login.DefaultDatabase, login.DefaultLanguage));
        }
        logins.Sort((a, b) => collation.Compare(a.Name, b.Name));

        var databases = new List<Database>();
        foreach (var (_, database) in simulation.Databases)
            databases.Add(database);
        databases.Sort((a, b) => collation.Compare(a.Name, b.Name));

        var users = new List<(string Login, string Database, string Name, string Kind)>();
        var hasUser = new HashSet<string>(BuiltInToken.Comparer);
        foreach (var (login, _, _) in logins)
        {
            var before = users.Count;
            foreach (var database in databases)
                HelpLoginsUserRows(database, login, users);
            if (users.Count > before)
                _ = hasUser.Add(login);
        }
        users.Sort((a, b) =>
        {
            var order = collation.Compare(a.Login, b.Login);
            if (order == 0)
                order = collation.Compare(a.Database, b.Database);
            return order != 0 ? order : collation.Compare(a.Name, b.Name);
        });

        var loginName = HelpLoginsWidth(logins.ConvertAll(static l => l.Name), "LoginName");
        var defaultDatabase = HelpLoginsWidth(logins.ConvertAll(static l => l.DefaultDatabase), "DefDBName");
        var defaultLanguage = HelpLoginsWidth(logins.ConvertAll(static l => l.DefaultLanguage), "DefLangName");
        yield return new SimulatedSqlResultSet(
            [loginName, VarbinarySqlType.Get(85), defaultDatabase, defaultLanguage, HelpLoginsAUserType, HelpLoginsARemoteType],
            SpHelpLoginsColumnNames,
            logins.ConvertAll(login => new SqlValue[]
            {
                SqlValue.FromNVarchar(loginName, login.Name),
                SqlValue.FromVarbinary(LoginSid(simulation, login.Name)),
                SqlValue.FromNVarchar(defaultDatabase, login.DefaultDatabase),
                SqlValue.FromNVarchar(defaultLanguage, login.DefaultLanguage),
                SqlValue.FromChar(HelpLoginsAUserType, hasUser.Contains(login.Name) ? "yes  " : "NO   "),
                SqlValue.FromChar(HelpLoginsARemoteType, "no     "),
            }));

        var userLogin = HelpLoginsWidth(users.ConvertAll(static u => u.Login), "LoginName");
        var userDatabase = HelpLoginsWidth(users.ConvertAll(static u => u.Database), "DBName");
        var userName = HelpLoginsWidth(users.ConvertAll(static u => u.Name), "UserName");
        yield return new SimulatedSqlResultSet(
            [userLogin, userDatabase, userName, HelpLoginsUserOrAliasType],
            SpHelpLoginsUserColumnNames,
            users.ConvertAll(user => new SqlValue[]
            {
                SqlValue.FromNVarchar(userLogin, user.Login),
                SqlValue.FromNVarchar(userDatabase, user.Database),
                SqlValue.FromNVarchar(userName, user.Name),
                SqlValue.FromChar(HelpLoginsUserOrAliasType, user.Kind),
            }));
    }

    // The user a login maps to in one database — dbo for the database's owner
    // — and the roles that user is directly a member of, dbo's db_owner
    // membership included.
    private static void HelpLoginsUserRows(
        Database database, string login, List<(string Login, string Database, string Name, string Kind)> rows)
    {
        foreach (var (_, principal) in database.Principals)
        {
            if (principal.TypeCode == "R")
                continue;
            var mapped = principal.PrincipalId == Database.DboPrincipalId ? database.OwnerLoginName : principal.LoginName;
            if (mapped is null || !BuiltInToken.Comparer.Equals(mapped, login))
                continue;
            rows.Add((login, database.Name, principal.Name, "User    "));
            foreach (var (roleId, memberId) in HelpUserRoleMembers(database))
            {
                if (memberId != principal.PrincipalId)
                    continue;
                foreach (var (_, role) in database.Principals)
                {
                    if (role.PrincipalId == roleId)
                        rows.Add((login, database.Name, role.Name, "MemberOf"));
                }
            }
        }
    }

    private static NVarcharSqlType HelpLoginsWidth(List<string> values, string header)
    {
        var width = 0;
        foreach (var value in values)
            width = Math.Max(width, value.Length * 2);
        return NVarcharSqlType.Get(width == 0 ? header.Length : Math.Min(width, 128), Collation.Baseline, Coercibility.Implicit);
    }

    /// <summary>
    /// <c>EXEC sp_helpextendedproc [@funcname]</c>: each extended procedure
    /// with the library real names for it — <c>(server internal)</c> for the
    /// <c>sp_</c>-named ones — in name order, sending no row count; a name
    /// that is no extended procedure is Msg 15019 and return code 1 (probed
    /// 2026-10-11 against SQL Server 2025).
    /// </summary>
    /// <remarks>
    /// Lists the extended procedures the simulator carries, where real lists
    /// its several hundred.
    /// </remarks>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpExtendedProc(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var (name, _) = ParseHelpArgs(arguments, "sp_helpextendedproc", firstName: "funcname");
        var collation = batch.Connection.Simulation.ServerCollation;
        var names = new List<string>();
        foreach (var proc in BuiltInResources.SystemProcedureNames)
        {
            if (BuiltInResources.IsExtendedProcedure(proc) && (name is null || collation.Equals(proc, name)))
                names.Add(proc);
        }
        if (name is not null && names.Count == 0)
            throw SimulatedSqlException.NotAnExtendedProcedure(name);
        names.Sort(collation.Compare);
        yield return new SimulatedSqlResultSet(
            SpHelpExtendedProcSchema,
            SpHelpExtendedProcColumnNames,
            names.ConvertAll(static proc => new SqlValue[] { SqlValue.FromSystemName(proc), SqlValue.FromNVarchar((NVarcharSqlType)SpHelpExtendedProcSchema[1], ExtendedProcedureLibrary(proc)) }));
    }

    private static string ExtendedProcedureLibrary(string proc) => proc switch
    {
        "xp_msver" => "xplog70.dll",
        "xp_qv" => "xpsqlbot.dll",
        _ when proc.StartsWith("xp_", StringComparison.Ordinal) => "xpstar.dll",
        _ => "(server internal)",
    };
}
