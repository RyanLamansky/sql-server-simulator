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
        // The name may be a variable (probed 2026-10-04 against SQL Server 2025).
        Expression? targetVariable = null;
        string? targetName = null;
        if (context.Token is AtPrefixedString)
        {
            targetVariable = Expression.Parse(context);
        }
        else
        {
            targetName = context.Token switch
            {
                Literal { Value: { IsNull: false } literal } => literal.AsString,
                Name named => named.Value,
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
            context.MoveNextOptional();
        }

        // WITH NO REVERT | WITH COOKIE INTO @c.
        var noRevert = false;
        VariableSlot? cookieSlot = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            context.MoveNextRequired();
            if (context.Token is Name { Value: var noWord } && string.Equals(noWord, "NO", StringComparison.OrdinalIgnoreCase))
            {
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Revert })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                noRevert = true;
            }
            else if (context.Token is Name { Value: var cookieWord } && string.Equals(cookieWord, "COOKIE", StringComparison.OrdinalIgnoreCase))
            {
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Into })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (context.GetNextRequired() is not AtPrefixedString { Value: var cookieVariable })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                cookieSlot = batch.IsSkipping ? null : batch.GetVariableSlot(cookieVariable);
            }
            else
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextOptional();
        }

        if (batch.IsSkipping)
            return;

        targetName ??= targetVariable!.Run(new RuntimeContext(NoColumns, batch)) is { IsNull: false } value
            ? value.CoerceTo(Storage.SqlType.NVarchar).AsString
            : throw SimulatedSqlException.CannotExecuteAsDatabasePrincipal("");
        var cookie = cookieSlot is null ? null : System.Security.Cryptography.RandomNumberGenerator.GetBytes(ApplicationRoleCookieLength);
        ApplyExecuteAs(context.Connection, context.CurrentDatabase, isLogin, targetName, new ExecuteAsGuard(context.CurrentDatabase.Name, noRevert, cookie));
        if (cookieSlot is { } slot && cookie is not null)
            slot.Value = Storage.SqlValue.FromVarbinary(cookie).CoerceTo(slot.DeclaredType);
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
    private static void ApplyExecuteAs(SimulatedDbConnection connection, Database database, bool isLogin, string targetName, ExecuteAsGuard guard)
    {
        var security = connection.Security;
        if (isLogin)
        {
            if (!LoginExists(connection.Simulation, targetName))
                throw SimulatedSqlException.CannotExecuteAsServerPrincipal(targetName);
            // A login with no way into the current database is refused as the
            // database access it would need, state 4, ending the batch and
            // rolling back (probed 2026-09-27 against SQL Server 2025).
            if (!TryMapLoginToDatabaseUser(connection.Simulation, database, targetName, out var mapped))
                throw SimulatedSqlException.CannotAccessDatabaseUnderSecurityContext(targetName, database.Name, state: 4);
            RequireImpersonateLoginPermission(connection, targetName);
            security.Push(new SecurityPrincipalFrame(mapped.PrincipalId, mapped.Name, targetName, guard: guard));
            return;
        }

        // The two catalog principals can't be impersonated (probed 2026-10-04
        // against SQL Server 2025).
        if (!database.Principals.TryGetValue(targetName, out var target) || target.TypeCode != "S"
            || target.PrincipalId is Database.InformationSchemaPrincipalId or Database.SysPrincipalId)
        {
            throw SimulatedSqlException.CannotExecuteAsDatabasePrincipal(targetName);
        }
        RequireImpersonatePermission(security, database, target.PrincipalId, targetName);
        // A user that may not connect — CONNECT revoked or denied, a DENY of
        // CONTROL, or guest where it isn't enabled — can be impersonated by
        // no one: Msg 916 state 4, which ends the batch (probed 2026-10-04
        // against SQL Server 2025, naming guest's identity as public).
        var loginIdentity = Ownership.LoginIdentity(database, target);
        if (target.PrincipalId != Database.DboPrincipalId
            && !PermissionChecker.IsGranted(database, target.PrincipalId, Permission.Connect, PermissionChecker.ClassDatabase, 0, 0))
        {
            throw SimulatedSqlException.CannotAccessDatabaseUnderSecurityContext(
                target.PrincipalId == Database.GuestPrincipalId ? "public" : loginIdentity, database.Name, state: 4);
        }
        // Database-scoped: an EXECUTE AS USER token carries no server principal,
        // so it can't reach another database (Msg 916 at any cross-database
        // reference) — unlike the LOGIN form above. Impersonating dbo reports
        // the database owner's login.
        security.Push(new SecurityPrincipalFrame(target.PrincipalId, target.Name, loginIdentity, isDatabaseScoped: true, guard));
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
        // A principal may always impersonate itself, so a nested EXECUTE AS
        // of the user already in effect needs no grant (probed 2026-09-27
        // against SQL Server 2025).
        if (security.EffectiveIsDbo || security.Effective.DatabasePrincipalId == targetPrincipalId)
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
        // A login may always impersonate itself (probed 2026-09-29 against
        // SQL Server 2025, as the USER form's rule).
        if (security.EffectiveIsDbo
            || (!security.Effective.IsDatabaseScoped && BuiltInToken.Comparer.Equals(security.Effective.LoginName, targetName)))
        {
            return;
        }
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
        Expression? cookie = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            if (context.GetNextRequired() is not Name { Value: var cookieWord } || !string.Equals(cookieWord, "COOKIE", StringComparison.OrdinalIgnoreCase)
                || context.GetNextRequired() is not Operator { Character: '=' })
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextRequired();
            cookie = Expression.Parse(context);
        }
        // The cookie must be a varbinary(100) variable, which the batch's
        // compile settles (probed 2026-10-04 against SQL Server 2025: a
        // literal is Msg 15533 and nothing in the batch runs).
        Storage.SqlValue? presentedValue = null;
        if (cookie is not null)
        {
            if (cookie is not Parser.Expressions.VariableReference variable
                || batch.GetVariableSlot(variable.VariableName) is not { DeclaredType: Storage.VarbinarySqlType, DeclaredMaxLength: 100 } slot)
            {
                throw SimulatedSqlException.RevertCookieWrongType();
            }
            presentedValue = slot.Value;
        }
        if (batch.IsSkipping)
            return;
        var presented = presentedValue is { IsNull: false } value ? value.AsBytes : null;
        context.Connection.Security.Revert(context.CurrentDatabase.Name, presented);
    }

    /// <summary>
    /// Applies the legacy <c>SETUSER ['user' [WITH NORESET]]</c>: with a name
    /// it impersonates that user as <c>EXECUTE AS USER</c> does, and bare it
    /// returns to the session's own identity (probed 2026-10-04 against SQL
    /// Server 2025).
    /// </summary>
    internal static void SetUserStatement(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextOptional(); // consume SETUSER
        string? targetName = null;
        if (context.Token is Literal { Value: { IsNull: false } literal })
        {
            targetName = literal.AsString;
            context.MoveNextOptional();
            if (context.Token is ReservedKeyword { Keyword: Keyword.With })
            {
                if (context.GetNextRequired() is not Name { Value: var noReset } || !string.Equals(noReset, "NORESET", StringComparison.OrdinalIgnoreCase))
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextOptional();
            }
        }
        if (batch.IsSkipping)
            return;
        if (targetName is null)
        {
            context.Connection.Security.RevertTo(0);
            return;
        }
        ApplyExecuteAs(context.Connection, context.CurrentDatabase, isLogin: false, targetName, default);
    }

    /// <summary>
    /// Pushes a stored procedure's <c>WITH EXECUTE AS</c> frame at invocation
    /// (<see cref="PushModuleExecuteAsFrame"/>). The matching pop is the
    /// caller's <see cref="SessionSecurityContext.RevertTo"/> on body exit.
    /// </summary>
    private static void PushProcedureExecuteAsFrame(SimulatedDbConnection connection, Procedure procedure, Database database) =>
        PushModuleExecuteAsFrame(connection, procedure.ExecuteAsClause, procedure.ExecuteAsPrincipalId, database, Ownership.EffectiveOwnerId(procedure.Schema.Database, procedure), procedure.Name);

    /// <summary>
    /// Pushes a module's <c>WITH EXECUTE AS</c> frame (procedure, scalar UDF, or
    /// trigger) at invocation. OWNER resolves to <paramref name="ownerPrincipalId"/>
    /// — the module's effective owner, a trigger's being its parent's — and
    /// SELF to <paramref name="executeAsPrincipalId"/>, the principal that ran
    /// the CREATE or the last ALTER, which an ownership change leaves alone
    /// (probed 2026-09-27 against SQL Server 2025); an owner that is a role or
    /// an application role can't be impersonated, which is Msg 15517 naming it
    /// (probed 2026-09-27 against SQL Server 2025, attributed to a procedure's
    /// unqualified <paramref name="procedureName"/> at line 0). CALLER / absent
    /// is a no-op; a named user pushes that database principal, raising
    /// Msg 15517 at invoke time if it's missing. Every form the clause names is
    /// <see cref="SecurityPrincipalFrame.IsDatabaseScoped"/> — including a
    /// <c>dbo</c> that OWNER / SELF resolve to, whose privilege stops at the
    /// database boundary. The matching pop is the caller's
    /// <see cref="SessionSecurityContext.RevertTo"/> on body exit.
    /// </summary>
    internal static void PushModuleExecuteAsFrame(SimulatedDbConnection connection, string? clause, int? executeAsPrincipalId, Database database, int ownerPrincipalId, string? procedureName = null)
    {
        if (clause is null || clause.Equals("CALLER", StringComparison.OrdinalIgnoreCase))
            return;
        var principalId = clause.Equals("OWNER", StringComparison.OrdinalIgnoreCase) ? ownerPrincipalId : executeAsPrincipalId;
        if (principalId == Database.DboPrincipalId)
        {
            // Database-scoped like the named-user form: the token is minted in
            // this database and carries no server principal, so it reaches
            // another one only out of a TRUSTWORTHY source however privileged
            // the session is (probe-confirmed — real refuses an OWNER / SELF
            // body's cross-database reference even for an `sa` session). Its
            // login is the database owner's, which is what SYSTEM_USER reports
            // and a TRUSTWORTHY crossing maps into the target.
            connection.Security.Push(new SecurityPrincipalFrame(Database.DboPrincipalId, "dbo", database.OwnerLoginName, isDatabaseScoped: true, ModuleGuard));
            return;
        }
        if (principalId is int id)
        {
            foreach (var (_, principal) in database.Principals)
            {
                if (principal.PrincipalId != id)
                    continue;
                if (principal.TypeCode is "R" or "A")
                {
                    // A procedure's refusal reports its unqualified name at line 0.
                    var refusal = SimulatedSqlException.CannotExecuteAsDatabasePrincipal(principal.Name);
                    if (procedureName is not null)
                        refusal.PreserveDiagnostics(0, procedureName);
                    throw refusal;
                }
                connection.Security.Push(new SecurityPrincipalFrame(principal.PrincipalId, principal.Name, principal.EffectiveLoginIdentity, isDatabaseScoped: true, ModuleGuard));
                return;
            }
        }
        if (!database.Principals.TryGetValue(clause, out var target))
            throw SimulatedSqlException.CannotExecuteAsDatabasePrincipal(clause);
        connection.Security.Push(new SecurityPrincipalFrame(target.PrincipalId, target.Name, target.EffectiveLoginIdentity, isDatabaseScoped: true, ModuleGuard));
    }

    private static Storage.SqlValue NoColumns(MultiPartName name) => throw SimulatedSqlException.InvalidColumnName(name);

    /// <summary>The guard a module's own <c>WITH EXECUTE AS</c> frame carries: a <c>REVERT</c> in its body leaves it in place.</summary>
    private static readonly ExecuteAsGuard ModuleGuard = new(module: true);

    /// <summary>
    /// The <c>sys.sql_modules.execute_as_principal_id</c> a module's
    /// <c>WITH EXECUTE AS</c> clause resolves to at CREATE:
    /// <see langword="null"/> for <c>CALLER</c> / no clause,
    /// <see cref="OwnerExecuteAsPrincipalId"/> for <c>OWNER</c>, the creating
    /// session's database principal for <c>SELF</c>, and the named user's
    /// principal id otherwise (probe-confirmed across procedures, functions
    /// and triggers). A named user the database doesn't hold resolves to
    /// <see langword="null"/>, which <see cref="RequireExecuteAsUser"/> has
    /// already refused for a procedure or function.
    /// </summary>
    internal static int? ResolveExecuteAsPrincipalId(ParserContext context, string? clause) =>
        clause is null || clause.Equals("CALLER", StringComparison.OrdinalIgnoreCase) ? null
        : clause.Equals("OWNER", StringComparison.OrdinalIgnoreCase) ? OwnerExecuteAsPrincipalId
        : clause.Equals("SELF", StringComparison.OrdinalIgnoreCase) ? context.Batch.Connection.Security.Effective.DatabasePrincipalId
        : context.CurrentDatabase.Principals.TryGetValue(clause, out var target) ? target.PrincipalId
        : null;

    /// <summary>
    /// Refuses a module's <c>EXECUTE AS 'user'</c> naming no user of the
    /// database with Msg 15151, as the module is created (probed 2026-10-02
    /// against SQL Server 2025).
    /// </summary>
    internal static void RequireExecuteAsUser(ParserContext context, string? clause)
    {
        if (clause is null || clause.Equals("CALLER", StringComparison.OrdinalIgnoreCase)
            || clause.Equals("OWNER", StringComparison.OrdinalIgnoreCase) || clause.Equals("SELF", StringComparison.OrdinalIgnoreCase)
            || context.CurrentDatabase.Principals.ContainsKey(clause))
        {
            return;
        }
        throw SimulatedSqlException.CannotExecuteAsUser(clause);
    }

    /// <summary>
    /// Real SQL Server's sentinel for <c>WITH EXECUTE AS OWNER</c> in
    /// <c>sys.sql_modules.execute_as_principal_id</c> — the owner is resolved
    /// per execution rather than pinned at CREATE, so the catalog records
    /// <c>-2</c> instead of a principal id.
    /// </summary>
    private const int OwnerExecuteAsPrincipalId = -2;
}
