using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE USER name [{FOR | FROM} LOGIN name | WITHOUT LOGIN |
    /// WITH PASSWORD = '…' | …] [WITH DEFAULT_SCHEMA = name …]</c>. The
    /// simulator has no permission enforcement; only the principal name +
    /// allocated id land in <see cref="Database.Principals"/> for catalog-
    /// view round-trip. The post-name grammar (FROM LOGIN / WITH PASSWORD /
    /// FROM EXTERNAL PROVIDER / DEFAULT_SCHEMA / etc.) parses-and-discards
    /// up to the next statement boundary.
    /// </summary>
    /// <remarks>
    /// Returns true on success so the dispatch loop's match-when-success
    /// pattern fires; pre-existing principal name raises Msg 15023 verbatim.
    /// </remarks>
    internal static bool TryParseCreateUser(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = nameToken.Value;
        context.MoveNextOptional();
        var (loginLink, withoutLogin) = ParseCreateUserSource(context);
        var (defaultSchema, _) = ParsePrincipalWithOptions(context);
        ConsumeToStatementBoundary(context);
        if (context.Batch.IsSkipping)
            return true;
        context.CurrentDatabase.RejectWriteWhenReadOnly();
        // CREATE USER isn't a modeled named permission — a non-privileged
        // principal gets Msg 15247 (probe M3).
        if (!PermissionEnforcement.HasDdlAdminCapability(context.Batch, context.CurrentDatabase))
            throw SimulatedSqlException.UserDoesNotHavePermission();
        if (context.CurrentDatabase.Principals.ContainsKey(name))
            throw SimulatedSqlException.PrincipalAlreadyExists(name);
        var id = context.CurrentDatabase.AllocatePrincipalId();
        RecordSecurityUndo(context, context.CurrentDatabase);
        context.CurrentDatabase.Principals[name] = new DatabasePrincipal(
            id, name, "S", "SQL_USER", isFixedRole: false, context.Batch.CurrentStatement.UtcNow,
            loginName: loginLink,
            securityIdentifierString: withoutLogin ? DeriveSyntheticUserSid(name) : null)
        {
            DefaultSchemaName = defaultSchema,
        };
        // CREATE USER auto-seeds a CONNECT grant (class 0 DATABASE, grantor dbo,
        // state G) — probe-confirmed against sys.database_permissions.
        context.CurrentDatabase.Permissions.Add(new DatabasePermission(
            PermissionChecker.ClassDatabase, majorId: 0, minorId: 0,
            granteePrincipalId: id, grantorPrincipalId: Database.DboPrincipalId,
            permission: Permission.Connect, state: PermissionState.Grant));
        // Real emits no SchemaName for a principal event; ObjectType names the
        // user kind (the simulator models only the SQL flavor).
        RecordDdlEvent(context, "CREATE_USER", null, name, "SQL USER");
        return true;
    }

    /// <summary>
    /// Reads the modeled <c>CREATE USER</c> source clauses — <c>{FOR | FROM}
    /// LOGIN login</c> (stores the login link) and <c>WITHOUT LOGIN</c> (stores
    /// the synthetic-SID marker). Every other source form
    /// (<c>FROM EXTERNAL PROVIDER</c>, <c>FROM CERTIFICATE</c>, …) is left for
    /// the <see cref="ConsumeToStatementBoundary"/> parse-and-discard tail: the
    /// cursor is restored to the clause start on any non-match so nothing is
    /// consumed. Cursor on entry: the first token after the user name.
    /// </summary>
    private static (string? LoginLink, bool WithoutLogin) ParseCreateUserSource(ParserContext context)
    {
        if (context.Token is ReservedKeyword { Keyword: Keyword.For or Keyword.From })
        {
            var checkpoint = context.SaveCheckpoint();
            context.MoveNextOptional();
            if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Login })
            {
                context.MoveNextRequired();
                if (context.Token is Name loginNameToken)
                {
                    context.MoveNextOptional();
                    return (loginNameToken.Value, false);
                }
            }
            context.RestoreCheckpoint(checkpoint);
            return (null, false);
        }
        if (context.Token is Name { Value: var word } && word.Equals("WITHOUT", StringComparison.OrdinalIgnoreCase))
        {
            var checkpoint = context.SaveCheckpoint();
            context.MoveNextOptional();
            if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Login })
            {
                context.MoveNextOptional();
                return (null, true);
            }
            context.RestoreCheckpoint(checkpoint);
        }
        return (null, false);
    }

    /// <summary>
    /// Reads a user's or role's <c>WITH option = value [, …]</c> list, handing
    /// back the <c>DEFAULT_SCHEMA</c> and <c>NAME</c> values; every other
    /// option (<c>LOGIN</c>, <c>PASSWORD</c>, <c>LANGUAGE</c>, <c>SID</c>, …)
    /// is read and discarded. No-op when the cursor isn't on <c>WITH</c>;
    /// otherwise the cursor ends on the first token past the list.
    /// </summary>
    private static (string? DefaultSchema, string? NewName) ParsePrincipalWithOptions(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
            return (null, null);
        string? defaultSchema = null, newName = null;
        do
        {
            if (context.GetNextRequired() is not StringToken option || context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var value = context.GetNextRequired();
            if (option.Span.Equals("DEFAULT_SCHEMA", StringComparison.OrdinalIgnoreCase))
                defaultSchema = value is Name schemaName ? schemaName.Value : throw SimulatedSqlException.SyntaxErrorNear(context);
            else if (option.Span.Equals("NAME", StringComparison.OrdinalIgnoreCase))
                newName = value is Name nameValue ? nameValue.Value : throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        while (context.GetNextOptional() is Operator { Character: ',' });
        return (defaultSchema, newName);
    }

    /// <summary>
    /// Parses <c>ALTER USER name WITH option = value [, …]</c>: <c>NAME</c>
    /// renames the user and <c>DEFAULT_SCHEMA</c> sets its default schema; the
    /// other options are read and discarded. A user that doesn't exist is Msg
    /// 15151, and a name already taken Msg 15023 (probed 2026-09-25 against
    /// SQL Server 2025). Cursor on entry: <c>USER</c>.
    /// </summary>
    internal static bool TryParseAlterUser(ParserContext context)
    {
        if (context.GetNextRequired() is not Name userNameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var userName = userNameToken.Value;
        context.MoveNextRequired();
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var (defaultSchema, newName) = ParsePrincipalWithOptions(context);
        context.RejectTrailingToken();
        if (context.Batch.IsSkipping)
            return true;

        var database = context.CurrentDatabase;
        database.RejectWriteWhenReadOnly();
        if (!PermissionEnforcement.HasDdlAdminCapability(context.Batch, database)
            || !database.Principals.TryGetValue(userName, out var user)
            || user.TypeCode == "R")
        {
            throw SimulatedSqlException.CannotAlterUser(userName);
        }
        RecordSecurityUndo(context, database);
        if (newName is not null)
            RenamePrincipal(database, user, newName);
        if (defaultSchema is not null)
            user.DefaultSchemaName = defaultSchema;
        RecordDdlEvent(context, "ALTER_USER", null, user.Name, "SQL USER");
        return true;
    }

    /// <summary>
    /// Moves <paramref name="principal"/> to <paramref name="newName"/> in the
    /// database's principal map; memberships and permissions key on its id, so
    /// they follow. A taken name is Msg 15023 state 10.
    /// </summary>
    private static void RenamePrincipal(Database database, DatabasePrincipal principal, string newName)
    {
        if (database.Principals.ContainsKey(newName))
            throw SimulatedSqlException.PrincipalAlreadyExists(newName, state: 10);
        _ = database.Principals.TryRemove(principal.Name, out _);
        principal.Name = newName;
        database.Principals[newName] = principal;
    }

    /// <summary>
    /// Derives the deterministic <c>S-1-9-3-…</c> security-identifier string a
    /// <c>WITHOUT LOGIN</c> user reports through <c>SYSTEM_USER</c> and Msg 916.
    /// Real SQL Server assigns these users a random SID in the S-1-9 (SQL
    /// Server) authority; the simulator fills the four sub-authorities with a
    /// per-position-salted FNV-1a hash of the name so the same name always maps
    /// to the same string (the synthetic-identity precedent
    /// <see cref="BuiltInResources.DeriveLoginSid"/> uses for logins).
    /// </summary>
    private static string DeriveSyntheticUserSid(string name)
    {
        Span<uint> parts = stackalloc uint[4];
        for (var i = 0; i < parts.Length; i++)
        {
            var hash = Fnv1a32.Initial;
            hash.Mix(name);
            hash.Mix((byte)i);
            parts[i] = hash.Value;
        }
        return $"S-1-9-3-{parts[0]}-{parts[1]}-{parts[2]}-{parts[3]}";
    }

    /// <summary>
    /// Parses <c>CREATE ROLE name [AUTHORIZATION owner]</c>. Like
    /// <see cref="TryParseCreateUser"/>, only the role name + id land in
    /// the catalog; the AUTHORIZATION clause parse-and-discards. The
    /// post-create role is empty (no members) until
    /// <see cref="TryParseAlterRole"/> adds them.
    /// </summary>
    internal static bool TryParseCreateRole(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = nameToken.Value;
        ConsumeToStatementBoundary(context);
        if (context.Batch.IsSkipping)
            return true;
        context.CurrentDatabase.RejectWriteWhenReadOnly();
        // CREATE ROLE isn't a modeled named permission — Msg 15247 for a
        // non-privileged principal (probe M3).
        if (!PermissionEnforcement.HasDdlAdminCapability(context.Batch, context.CurrentDatabase))
            throw SimulatedSqlException.UserDoesNotHavePermission();
        if (context.CurrentDatabase.Principals.ContainsKey(name))
            throw SimulatedSqlException.PrincipalAlreadyExists(name);
        var id = context.CurrentDatabase.AllocatePrincipalId();
        RecordSecurityUndo(context, context.CurrentDatabase);
        context.CurrentDatabase.Principals[name] = new DatabasePrincipal(
            id, name, "R", "DATABASE_ROLE", isFixedRole: false, context.Batch.CurrentStatement.UtcNow);
        RecordDdlEvent(context, "CREATE_ROLE", null, name, "ROLE");
        return true;
    }

    /// <summary>
    /// Adds <paramref name="memberName"/> to, or drops it from,
    /// <paramref name="roleName"/> — the whole of <c>ALTER ROLE … ADD | DROP
    /// MEMBER</c> and of <c>sp_addrolemember</c> / <c>sp_droprolemember</c>,
    /// which real runs as that statement. The procedure differs only in how it
    /// words a member it can't find to add (Msg 15410 rather than 15151), and
    /// both raise <c>ADD_ROLE_MEMBER</c> / <c>DROP_ROLE_MEMBER</c> naming the
    /// member as the object (probed 2026-09-25 against SQL Server 2025).
    /// </summary>
    private static void ChangeRoleMembership(ParserContext context, string roleName, string memberName, bool isAdd, bool viaProcedure)
    {
        var database = context.CurrentDatabase;
        database.RejectWriteWhenReadOnly();
        // sp_addrolemember looks the member up itself before its inner ALTER
        // ROLE reads the role, so a missing member outranks a missing role
        // there (probed 2026-09-26 against SQL Server 2025).
        if (isAdd && viaProcedure && !database.Principals.ContainsKey(memberName))
            throw SimulatedSqlException.UserOrRoleDoesNotExist(memberName);
        // Membership changes need ALTER ANY ROLE (or ALTER / CONTROL on the
        // role, which the covering walk folds in). db_ddladmin does NOT
        // carry it — probe-confirmed, which is why ALTER ANY ROLE isn't in
        // the DDL category. Msg 15151 at state 2 for the denial, state 1 for
        // a name that isn't a role.
        if (!PermissionEnforcement.HasDatabasePermission(context.Batch, database, Permission.AlterAnyRole))
            throw SimulatedSqlException.CannotAlterRole(roleName);
        if (!database.Principals.TryGetValue(roleName, out var role) || role.TypeCode != "R")
            throw SimulatedSqlException.CannotAlterRole(roleName, state: 1);
        if (role.PrincipalId == 0)
            throw SimulatedSqlException.PublicRoleMembershipFixed();
        if (!database.Principals.TryGetValue(memberName, out var member))
        {
            throw isAdd && viaProcedure
                ? SimulatedSqlException.UserOrRoleDoesNotExist(memberName)
                : SimulatedSqlException.CannotChangeMembershipOfPrincipal(isAdd ? "add" : "drop", memberName);
        }
        // dbo can't be a member of any role, nor a role of itself (both
        // probed 2026-09-25 against SQL Server 2025).
        if (isAdd && member.PrincipalId == Database.DboPrincipalId)
            throw SimulatedSqlException.CannotUseSpecialPrincipal(member.Name);
        if (isAdd && member.PrincipalId == role.PrincipalId)
            throw SimulatedSqlException.RoleMemberOfItself();
        RecordSecurityUndo(context, database);
        if (isAdd)
        {
            if (!database.RoleMembers.Contains((role.PrincipalId, member.PrincipalId)))
                database.RoleMembers.Add((role.PrincipalId, member.PrincipalId));
        }
        else
        {
            _ = database.RoleMembers.Remove((role.PrincipalId, member.PrincipalId));
        }
        RecordDdlEvent(context, isAdd ? "ADD_ROLE_MEMBER" : "DROP_ROLE_MEMBER", null, member.Name, member.TypeCode == "R" ? "ROLE" : "SQL USER", roleName: role.Name);
    }

    /// <summary>
    /// <c>sp_addrolemember</c> / <c>sp_droprolemember</c>: the legacy spelling of
    /// <c>ALTER ROLE … ADD | DROP MEMBER</c>, with real's own signature — a
    /// missing argument is Msg 201, a third Msg 8144, an unknown name Msg 8145
    /// and a NULL one Msg 15004 (probed 2026-09-25 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpRoleMember(BatchContext batch, bool isAdd)
    {
        var procLabel = isAdd ? "sp_addrolemember" : "sp_droprolemember";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        string[] parameters = ["rolename", "membername"];
        var values = new SqlValue?[parameters.Length];
        string? unknownParameter = null;
        for (var i = 0; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            if (arg.Name is null && i >= parameters.Length)
                throw SimulatedSqlException.TooManyArgumentsToFunction(procLabel);
            var slot = Array.FindIndex(parameters, p => BuiltInToken.Equals(arg.Name ?? parameters[i], p));
            if (slot < 0)
                unknownParameter ??= arg.Name;
            else
                values[slot] = arg.Value;
        }
        for (var i = 0; i < parameters.Length; i++)
        {
            if (values[i] is null)
                throw SimulatedSqlException.ProcedureExpectsParameter(procLabel, parameters[i]);
        }
        if (unknownParameter is not null)
            throw SimulatedSqlException.NotAParameterForProcedure(unknownParameter, procLabel);
        if (values[0]!.Value.IsNull || values[1]!.Value.IsNull)
            throw SimulatedSqlException.NameCannotBeNull();

        ChangeRoleMembership(
            batch.Parser,
            values[0]!.Value.CoerceTo(SqlType.SystemName).AsString,
            values[1]!.Value.CoerceTo(SqlType.SystemName).AsString,
            isAdd,
            viaProcedure: true);
    }

    /// <summary>
    /// Parses <c>ALTER ROLE name { ADD MEMBER name | DROP MEMBER name |
    /// WITH NAME = newname }</c>. ADD/DROP MEMBER mutates
    /// <see cref="Database.RoleMembers"/>; WITH NAME parse-and-discards
    /// (the simulator's principal dict is keyed by name so a rename
    /// requires care that AW doesn't need).
    /// </summary>
    internal static bool TryParseAlterRole(ParserContext context)
    {
        // Cursor on ROLE (caller has already matched ALTER + ROLE).
        context.MoveNextRequired();
        if (context.Token is not Name roleNameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var roleName = roleNameToken.Value;
        context.MoveNextRequired();

        // Action: ADD MEMBER / DROP MEMBER / WITH NAME = ... ADD and DROP
        // are reserved keywords; MEMBER is a bare identifier (UnquotedString).
        if (context.Token is ReservedKeyword { Keyword: Keyword.Add or Keyword.Drop } addOrDrop)
        {
            var isAdd = addOrDrop.Keyword == Keyword.Add;
            context.MoveNextRequired();
            if (context.Token is not UnquotedString { Value: var memberWord }
                || !memberWord.Equals("MEMBER", StringComparison.OrdinalIgnoreCase))
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextRequired();
            if (context.Token is not Name memberNameToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var memberName = memberNameToken.Value;
            context.MoveNextOptional();

            if (context.Batch.IsSkipping)
                return true;
            ChangeRoleMembership(context, roleName, memberName, isAdd, viaProcedure: false);
            return true;
        }

        // WITH NAME = newname renames the role; a taken name is Msg 15023.
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            var (_, newName) = ParsePrincipalWithOptions(context);
            context.RejectTrailingToken();
            if (context.Batch.IsSkipping || newName is null)
                return true;
            context.CurrentDatabase.RejectWriteWhenReadOnly();
            if (!PermissionEnforcement.HasDatabasePermission(context.Batch, context.CurrentDatabase, Permission.AlterAnyRole)
                || !context.CurrentDatabase.Principals.TryGetValue(roleName, out var renamed)
                || renamed.TypeCode != "R")
            {
                throw SimulatedSqlException.CannotAlterRole(roleName);
            }
            RecordSecurityUndo(context, context.CurrentDatabase);
            RenamePrincipal(context.CurrentDatabase, renamed, newName);
            RecordDdlEvent(context, "ALTER_ROLE", null, newName, "ROLE");
            return true;
        }
        throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>
    /// Parses <c>DROP USER [IF EXISTS] name</c>. Routes to the per-database
    /// principal dict (rather than the per-schema object dict that the
    /// generic <c>DROP &lt;target&gt;</c> path handles).
    /// </summary>
    internal static bool TryParseDropUser(ParserContext context, bool isRole = false)
    {
        context.MoveNextRequired();
        var ifExists = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.If })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Exists })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            ifExists = true;
            context.MoveNextRequired();
        }
        if (context.Token is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = nameToken.Value;
        context.MoveNextOptional();
        if (context.Batch.IsSkipping)
            return true;
        context.CurrentDatabase.RejectWriteWhenReadOnly();
        // DROP USER needs db_owner (no ALTER ANY USER model) — a non-privileged
        // principal gets Msg 15151 (probe B). DROP ROLE takes ALTER ANY ROLE
        // (or ALTER / CONTROL on the role) and its own 15151 wording, at
        // state 1 rather than ALTER ROLE's state 2.
        if (isRole)
        {
            if (!PermissionEnforcement.HasDatabasePermission(context.Batch, context.CurrentDatabase, Permission.AlterAnyRole))
                throw SimulatedSqlException.CannotDropRole(name);
        }
        else if (!PermissionEnforcement.IsOwner(context.Batch, context.CurrentDatabase))
        {
            throw SimulatedSqlException.DropUserPermissionDenied(name);
        }
        if (!context.CurrentDatabase.Principals.TryGetValue(name, out var removed))
        {
            return ifExists ? true : throw SimulatedSqlException.CannotFindPrincipal(name);
        }
        // A principal that owns a schema can't be dropped — Msg 15138, which
        // names neither the principal nor the schema (probe-confirmed).
        foreach (var schema in context.CurrentDatabase.Schemas.Values)
        {
            if (schema.PrincipalId == removed.PrincipalId)
                throw SimulatedSqlException.PrincipalOwnsASchema();
        }
        // A role that still has members can't go (probed 2026-09-25 against
        // SQL Server 2025); a user in roles can, its memberships going with it.
        if (isRole && context.CurrentDatabase.RoleMembers.Exists(rm => rm.RoleId == removed.PrincipalId))
            throw SimulatedSqlException.RoleHasMembers();
        RecordSecurityUndo(context, context.CurrentDatabase);
        _ = context.CurrentDatabase.Principals.TryRemove(name, out _);
        // Cascade: drop role memberships that reference the removed principal.
        _ = context.CurrentDatabase.RoleMembers.RemoveAll(rm =>
            rm.RoleId == removed.PrincipalId || rm.MemberId == removed.PrincipalId);
        RecordDdlEvent(context, isRole ? "DROP_ROLE" : "DROP_USER", null, name, isRole ? "ROLE" : "SQL USER");
        return true;
    }

    /// <summary>
    /// Parses <c>DROP ROLE [IF EXISTS] name</c>. Same shape as
    /// <see cref="TryParseDropUser"/>; the principal dict is shared.
    /// </summary>
    internal static bool TryParseDropRole(ParserContext context) => TryParseDropUser(context, isRole: true);

    /// <summary>
    /// Consumes tokens through end-of-batch or the next <c>;</c> /
    /// statement-starting keyword. Used by the parse-and-discard tails
    /// (FROM LOGIN / WITH PASSWORD / DEFAULT_SCHEMA / etc.) that the
    /// simulator doesn't model. Leaves the cursor on the boundary token.
    /// </summary>
    private static void ConsumeToStatementBoundary(ParserContext context)
    {
        while (!IsStatementBoundary(context.Token))
            context.MoveNextOptional();
    }
}
