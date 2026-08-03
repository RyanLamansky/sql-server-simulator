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
    /// impersonatable targets raise Msg 15517 (15406 for LOGIN). Nested
    /// impersonation by a non-dbo principal requires IMPERSONATE on the target,
    /// <c>dbo</c> included — see <see cref="RequireImpersonatePermission"/>.
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
            RequireImpersonateLoginPermission(connection, targetName);
            security.Push(new SecurityPrincipalFrame(mapped.PrincipalId, mapped.Name, targetName));
            return;
        }

        if (!database.Principals.TryGetValue(targetName, out var target) || target.TypeCode != "S")
            throw SimulatedSqlException.CannotExecuteAsDatabasePrincipal(targetName);
        RequireImpersonatePermission(security, database, target.PrincipalId, targetName);
        // Database-scoped: an EXECUTE AS USER token carries no server principal,
        // so it can't reach another database (Msg 916 at any cross-database
        // reference) — unlike the LOGIN form above.
        security.Push(new SecurityPrincipalFrame(target.PrincipalId, target.Name, target.EffectiveLoginIdentity, isDatabaseScoped: true));
    }

    /// <summary>
    /// Gates nested <c>EXECUTE AS USER</c>: dbo may impersonate anyone; anyone
    /// else needs IMPERSONATE on the target at class 4 (DATABASE_PRINCIPAL),
    /// which the ordinary <see cref="PermissionChecker.IsGranted"/> walk answers
    /// from an explicit grant, a role that holds one, <c>CONTROL</c> on the
    /// principal, or <c>db_owner</c> membership. The LOGIN form gates at server
    /// scope instead (<see cref="RequireImpersonateLoginPermission"/>).
    /// </summary>
    /// <remarks>
    /// The <c>dbo</c> target takes the same gate as any other user
    /// (probe-confirmed against SQL Server 2025 on two instances): a sysadmin
    /// session and a <c>db_owner</c> member both run <c>EXECUTE AS USER = 'dbo'</c>
    /// successfully, an explicit <c>GRANT IMPERSONATE ON USER::dbo</c> admits a
    /// restricted principal, and only a principal holding none of that gets
    /// Msg 15517.
    /// </remarks>
    private static void RequireImpersonatePermission(SessionSecurityContext security, Database database, int targetPrincipalId, string targetName)
    {
        if (security.EffectiveIsDbo)
            return;
        if (!PermissionChecker.IsGranted(
                database,
                security.Effective.DatabasePrincipalId,
                Permission.Impersonate,
                PermissionChecker.ClassDatabasePrincipal,
                targetPrincipalId,
                schemaId: 0))
        {
            throw SimulatedSqlException.CannotExecuteAsDatabasePrincipal(targetName);
        }
    }

    /// <summary>
    /// Gates <c>EXECUTE AS LOGIN</c>: a server-scope check, unlike the
    /// database-principal <see cref="RequireImpersonatePermission"/>. dbo /
    /// sysadmin may impersonate anyone; anyone else needs <c>IMPERSONATE ON
    /// LOGIN::&lt;target&gt;</c> (class 101) or the server-wide <c>IMPERSONATE
    /// ANY LOGIN</c> (class 100), with a class-101 DENY overriding the blanket
    /// grant. A refusal reports the same Msg 15406 as a missing login — real
    /// leaks no distinction (probe-confirmed).
    /// </summary>
    private static void RequireImpersonateLoginPermission(SimulatedDbConnection connection, string targetName)
    {
        var security = connection.Security;
        if (security.EffectiveIsDbo)
            return;
        var simulation = connection.Simulation;
        if (!simulation.TryResolveServerPrincipalId(targetName, out var targetId)
            || !simulation.HoldsServerPrincipalPermission(
                security.Effective.LoginName, targetId, Permission.Impersonate, Permission.ImpersonateAnyLogin))
        {
            throw SimulatedSqlException.CannotExecuteAsServerPrincipal(targetName);
        }
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
    /// Msg 15517 at invoke time if it's missing. Every form the clause names is
    /// <see cref="SecurityPrincipalFrame.IsDatabaseScoped"/> — including the
    /// <c>dbo</c> that OWNER / SELF resolve to, whose privilege stops at the
    /// database boundary. The matching pop is the caller's
    /// <see cref="SessionSecurityContext.RevertTo"/> on body exit.
    /// </summary>
    internal static void PushModuleExecuteAsFrame(SimulatedDbConnection connection, string? clause, Database database)
    {
        if (clause is null || clause.Equals("CALLER", StringComparison.OrdinalIgnoreCase))
            return;
        if (clause.Equals("OWNER", StringComparison.OrdinalIgnoreCase) || clause.Equals("SELF", StringComparison.OrdinalIgnoreCase))
        {
            // Database-scoped like the named-user form: the token is minted in
            // this database and carries no server principal, so it reaches
            // another one only out of a TRUSTWORTHY source however privileged
            // the session is (probe-confirmed — real refuses an OWNER / SELF
            // body's cross-database reference even for an `sa` session).
            connection.Security.Push(new SecurityPrincipalFrame(Database.DboPrincipalId, "dbo", "dbo", isDatabaseScoped: true));
            return;
        }
        if (!database.Principals.TryGetValue(clause, out var target))
            throw SimulatedSqlException.CannotExecuteAsDatabasePrincipal(clause);
        connection.Security.Push(new SecurityPrincipalFrame(target.PrincipalId, target.Name, target.EffectiveLoginIdentity, isDatabaseScoped: true));
    }

    /// <summary>
    /// The <c>sys.sql_modules.execute_as_principal_id</c> a module's
    /// <c>WITH EXECUTE AS</c> clause resolves to at CREATE:
    /// <see langword="null"/> for <c>CALLER</c> / no clause,
    /// <see cref="OwnerExecuteAsPrincipalId"/> for <c>OWNER</c>, the creating
    /// session's database principal for <c>SELF</c>, and the named user's
    /// principal id otherwise (probe-confirmed across procedures, functions
    /// and triggers). A named user the database doesn't hold resolves to
    /// <see langword="null"/>; real refuses the CREATE outright, which the
    /// simulator defers to invocation time (Msg 15517).
    /// </summary>
    internal static int? ResolveExecuteAsPrincipalId(ParserContext context, string? clause) =>
        clause is null || clause.Equals("CALLER", StringComparison.OrdinalIgnoreCase) ? null
        : clause.Equals("OWNER", StringComparison.OrdinalIgnoreCase) ? OwnerExecuteAsPrincipalId
        : clause.Equals("SELF", StringComparison.OrdinalIgnoreCase) ? context.Batch.Connection.Security.Effective.DatabasePrincipalId
        : context.CurrentDatabase.Principals.TryGetValue(clause, out var target) ? target.PrincipalId
        : null;

    /// <summary>
    /// Real SQL Server's sentinel for <c>WITH EXECUTE AS OWNER</c> in
    /// <c>sys.sql_modules.execute_as_principal_id</c> — the owner is resolved
    /// per execution rather than pinned at CREATE, so the catalog records
    /// <c>-2</c> instead of a principal id.
    /// </summary>
    private const int OwnerExecuteAsPrincipalId = -2;
}
