using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses and applies <c>EXECUTE AS { LOGIN | USER } = 'name'</c>. Entered
    /// with the cursor on the <c>AS</c> keyword (the <see cref="ParseExec"/>
    /// dispatcher peeks it to disambiguate from proc invocation). Pushes an
    /// impersonation frame onto the session's
    /// <see cref="SimulatedDbConnection.Security"/> stack. <c>EXECUTE AS CALLER</c>
    /// is a no-op. The trailing <c>WITH { NO REVERT | COOKIE INTO @c }</c>
    /// options parse-and-discard.
    /// </summary>
    internal static void ExecuteAsStatement(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume AS

        bool isLogin;
        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.User }:
                isLogin = false;
                break;
            case UnquotedString { ContextualKeyword: ContextualKeyword.Login }:
                isLogin = true;
                break;
            case Name { Value: var callerWord } when callerWord.Equals("CALLER", StringComparison.OrdinalIgnoreCase):
                // EXECUTE AS CALLER — the explicit no-op form.
                context.MoveNextOptional();
                return;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        if (context.GetNextRequired() is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var targetName = context.Token switch
        {
            Literal { Value: { IsNull: false } literal } => literal.AsString,
            Name named => named.Value,
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
        context.MoveNextOptional();
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
            ConsumeToStatementBoundary(context);

        if (batch.IsSkipping)
            return;

        ApplyExecuteAs(context.Connection, context.CurrentDatabase, isLogin, targetName);
    }

    /// <summary>
    /// Resolves the impersonation target and pushes its frame. LOGIN maps to
    /// the login's database user in the current database
    /// (<c>SYSTEM_USER</c> becomes the login, <c>CURRENT_USER</c> the mapped
    /// user); USER pushes the database principal directly. Missing / non-
    /// impersonatable targets and the <c>USER = 'dbo'</c> quirk raise Msg 15517
    /// (15406 for LOGIN). Nested impersonation by a non-dbo principal requires
    /// IMPERSONATE on the target.
    /// </summary>
    private static void ApplyExecuteAs(SimulatedDbConnection connection, Database database, bool isLogin, string targetName)
    {
        var security = connection.Security;
        if (isLogin)
        {
            if (!LoginExists(connection.Simulation, targetName)
                || !TryMapLoginToDatabaseUser(connection.Simulation, database, targetName, out var mapped))
            {
                throw SimulatedSqlException.CannotExecuteAsServerPrincipal(targetName);
            }
            RequireImpersonatePermission(security, database, mapped.PrincipalId, isLogin: true, targetName);
            security.Push(new SecurityPrincipalFrame(mapped.PrincipalId, mapped.Name, targetName));
            return;
        }

        // EXECUTE AS USER = 'dbo' fails with 15517 even for a sysadmin session
        // (probe-confirmed quirk).
        if (BuiltInToken.Comparer.Equals(targetName, "dbo")
            || !database.Principals.TryGetValue(targetName, out var target)
            || target.TypeCode != "S")
        {
            throw SimulatedSqlException.CannotExecuteAsDatabasePrincipal(targetName);
        }
        RequireImpersonatePermission(security, database, target.PrincipalId, isLogin: false, targetName);
        security.Push(new SecurityPrincipalFrame(target.PrincipalId, target.Name, target.EffectiveLoginIdentity));
    }

    /// <summary>
    /// Gates nested impersonation: dbo may impersonate anyone; a non-dbo
    /// principal needs an explicit class-4 (DATABASE_PRINCIPAL) IMPERSONATE
    /// grant on the target (state G or W). A direct <see cref="Database.Permissions"/>
    /// scan against the current effective principal — role-closure expansion is
    /// a later stage.
    /// </summary>
    private static void RequireImpersonatePermission(SessionSecurityContext security, Database database, int targetPrincipalId, bool isLogin, string targetName)
    {
        if (security.EffectiveIsDbo)
            return;
        var granteeId = security.Effective.DatabasePrincipalId;
        foreach (var permission in database.Permissions)
        {
            if (permission.Class == 4
                && permission.MajorId == targetPrincipalId
                && permission.GranteePrincipalId == granteeId
                && permission.State is PermissionState.Grant or PermissionState.GrantWithGrantOption)
            {
                return;
            }
        }
        throw isLogin
            ? SimulatedSqlException.CannotExecuteAsServerPrincipal(targetName)
            : SimulatedSqlException.CannotExecuteAsDatabasePrincipal(targetName);
    }

    /// <summary>
    /// True when <paramref name="name"/> names an impersonatable server login —
    /// a registered <c>CREATE LOGIN</c> entry or the well-known <c>sa</c>.
    /// </summary>
    private static bool LoginExists(Simulation simulation, string name) =>
        simulation.Logins.ContainsKey(name) || BuiltInToken.Comparer.Equals(name, "sa");

    /// <summary>
    /// Applies <c>REVERT</c>: pops one impersonation frame (a stray REVERT at
    /// the base identity is a silent no-op). Entered with the cursor on the
    /// <c>REVERT</c> keyword; the optional <c>WITH COOKIE = @c</c> tail parses
    /// and discards.
    /// </summary>
    internal static void RevertStatement(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextOptional(); // consume REVERT
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
            ConsumeToStatementBoundary(context);
        if (batch.IsSkipping)
            return;
        context.Connection.Security.Revert();
    }

    /// <summary>
    /// Pushes a stored procedure's <c>WITH EXECUTE AS</c> frame at invocation.
    /// OWNER / SELF resolve to <c>dbo</c> (every module is dbo-owned and
    /// dbo-created); CALLER / absent is a no-op; a named user pushes that
    /// database principal, raising Msg 15517 at EXEC time if it's missing. The
    /// matching pop is the caller's <see cref="SessionSecurityContext.RevertTo"/>
    /// on body exit.
    /// </summary>
    private static void PushProcedureExecuteAsFrame(SimulatedDbConnection connection, Procedure procedure, Database database) =>
        PushModuleExecuteAsFrame(connection, procedure.ExecuteAsClause, database);

    /// <summary>
    /// Pushes a module's <c>WITH EXECUTE AS</c> frame (procedure, scalar UDF, or
    /// trigger) at invocation. OWNER / SELF resolve to <c>dbo</c>; CALLER /
    /// absent is a no-op; a named user pushes that database principal, raising
    /// Msg 15517 at invoke time if it's missing. The matching pop is the
    /// caller's <see cref="SessionSecurityContext.RevertTo"/> on body exit.
    /// </summary>
    internal static void PushModuleExecuteAsFrame(SimulatedDbConnection connection, string? clause, Database database)
    {
        if (clause is null || clause.Equals("CALLER", StringComparison.OrdinalIgnoreCase))
            return;
        if (clause.Equals("OWNER", StringComparison.OrdinalIgnoreCase) || clause.Equals("SELF", StringComparison.OrdinalIgnoreCase))
        {
            connection.Security.Push(new SecurityPrincipalFrame(Database.DboPrincipalId, "dbo", "dbo"));
            return;
        }
        if (!database.Principals.TryGetValue(clause, out var target))
            throw SimulatedSqlException.CannotExecuteAsDatabasePrincipal(clause);
        connection.Security.Push(new SecurityPrincipalFrame(target.PrincipalId, target.Name, target.EffectiveLoginIdentity));
    }
}
