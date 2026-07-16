using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>USER_ID([name])</c>, <c>DATABASE_PRINCIPAL_ID([name])</c>,
/// and <c>SUSER_ID([name])</c>: return the principal-id for the named
/// (or current) principal. The simulator's seeded principals
/// (<c>public</c>=0, <c>dbo</c>=1, <c>guest</c>=2,
/// <c>INFORMATION_SCHEMA</c>=3, <c>sys</c>=4) drive USER_ID and
/// DATABASE_PRINCIPAL_ID; SUSER_ID returns the fixed login id (1).
/// NULL argument or unknown name returns NULL.
/// </summary>
internal sealed class PrincipalIdLookup : Expression
{
    private readonly Expression? nameArg;
    private readonly PrincipalIdKind kind;

    public PrincipalIdLookup(ParserContext context, PrincipalIdKind kind)
    {
        this.kind = kind;
        if (context.Token is Tokens.Operator { Character: ')' })
            return;
        this.nameArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        if (this.nameArg is null)
        {
            // SUSER_ID = login id (1); USER_ID = dbo's id (1); DATABASE_PRINCIPAL_ID = dbo's id (1).
            return SqlValue.FromInt32(1);
        }
        var v = this.nameArg.Run(runtime);
        if (v.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var name = v.CoerceTo(SqlType.NVarchar).AsString;
        if (this.kind == PrincipalIdKind.SUserId)
        {
            // SUSER_ID at server level — simulator has one login, so any
            // recognized server-principal name maps to id 1; unknown → NULL.
            return BuiltInToken.Comparer.Equals(name, PrincipalPlaceholders.CurrentLogin)
                ? SqlValue.FromInt32(1)
                : SqlValue.Null(SqlType.Int32);
        }
        return runtime.Batch.CurrentDatabase.Principals.TryGetValue(name, out var p)
            ? SqlValue.FromInt32(p.PrincipalId)
            : SqlValue.Null(SqlType.Int32);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => this.kind switch
    {
        PrincipalIdKind.UserId => "USER_ID(...)",
        PrincipalIdKind.SUserId => "SUSER_ID(...)",
        PrincipalIdKind.DatabasePrincipalId => "DATABASE_PRINCIPAL_ID(...)",
        _ => "PRINCIPAL_ID(...)",
    };
}

internal enum PrincipalIdKind
{
    UserId,
    SUserId,
    DatabasePrincipalId,
}

/// <summary>
/// SQL <c>HAS_PERMS_BY_NAME(securable, securable_class, permission [, ...])</c>:
/// returns 1 when the current principal has the given permission, 0
/// otherwise. The simulator doesn't enforce permissions (GRANT/REVOKE
/// modify metadata only), so this returns 1 for any non-NULL
/// <c>permission</c>. A NULL <c>permission</c> returns NULL; NULL
/// <c>securable</c> / <c>securable_class</c> are legal (real reads them as
/// "the current server or database" — DacFx's export permission gate sends
/// <c>HAS_PERMS_BY_NAME(NULL, N'DATABASE', N'VIEW DEFINITION')</c>) and
/// don't affect the result.
/// </summary>
internal sealed class HasPermsByName : Expression
{
    private readonly Expression[] args;

    public HasPermsByName(ParserContext context)
    {
        var list = new List<Expression> { Parse(context) };
        while (context.Token is Tokens.Operator { Character: ',' })
            list.Add(Parse(context.MoveNextRequiredReturnSelf()));
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (list.Count < 3)
            throw SimulatedSqlException.FunctionRequiresNArguments("has_perms_by_name", 3);
        this.args = [.. list];
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var permission = SqlValue.Null(SqlType.Int32);
        for (var i = 0; i < this.args.Length; i++)
        {
            var v = this.args[i].Run(runtime);
            if (i == 2)
                permission = v;
        }
        return permission.IsNull ? SqlValue.Null(SqlType.Int32) : SqlValue.FromInt32(1);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => $"HAS_PERMS_BY_NAME(...{this.args.Length} args)";
}

/// <summary>
/// SQL <c>IS_MEMBER(group_or_role)</c>, <c>IS_ROLEMEMBER(role [, principal])</c>,
/// and <c>IS_SRVROLEMEMBER(role [, login])</c>: role-membership checks for
/// the session principal (<c>dbo</c> at the database level; the single
/// login at the server level). Probe-confirmed shape: member → 1, known
/// role without membership → 0, anything that isn't a role at that scope →
/// NULL. Database scope: <c>public</c> and <c>db_owner</c> → 1 (dbo is
/// always a member of both), the other fixed roles → 0, user-created roles
/// consult <see cref="Database.RoleMembers"/>, non-role principals and
/// unknown names → NULL. Server scope: <c>public</c> → 1, the other fixed
/// server roles → 0 (no server-role membership model), everything else →
/// NULL. NULL argument returns NULL.
/// </summary>
internal sealed class RoleMemberCheck : Expression
{
    private readonly Expression roleArg;
    private readonly Expression? principalArg;
    private readonly bool serverScope;

    /// <summary>Fixed database roles other than <c>public</c> / <c>db_owner</c> — dbo is not a member.</summary>
    private static readonly string[] FixedDatabaseRolesWithoutDbo =
    [
        "db_accessadmin", "db_backupoperator", "db_datareader", "db_datawriter",
        "db_ddladmin", "db_denydatareader", "db_denydatawriter", "db_securityadmin",
    ];

    /// <summary>Fixed server roles other than <c>public</c> — the simulator has no server-role membership.</summary>
    private static readonly string[] FixedServerRolesWithoutPublic =
    [
        "bulkadmin", "dbcreator", "diskadmin", "processadmin",
        "securityadmin", "serveradmin", "setupadmin", "sysadmin",
    ];

    public RoleMemberCheck(ParserContext context, bool serverScope)
    {
        this.serverScope = serverScope;
        this.roleArg = Parse(context);
        if (context.Token is Tokens.Operator { Character: ',' })
            this.principalArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var role = this.roleArg.Run(runtime);
        if (role.IsNull)
            return SqlValue.Null(SqlType.Int32);
        if (this.principalArg?.Run(runtime).IsNull == true)
            return SqlValue.Null(SqlType.Int32);
        var roleName = role.CoerceTo(SqlType.NVarchar).AsString;
        if (BuiltInToken.Comparer.Equals(roleName, "public"))
            return SqlValue.FromInt32(1);
        if (this.serverScope)
        {
            foreach (var fixedRole in FixedServerRolesWithoutPublic)
            {
                if (BuiltInToken.Comparer.Equals(roleName, fixedRole))
                    return SqlValue.FromInt32(0);
            }
            return SqlValue.Null(SqlType.Int32);
        }

        if (BuiltInToken.Comparer.Equals(roleName, "db_owner"))
            return SqlValue.FromInt32(1);
        foreach (var fixedRole in FixedDatabaseRolesWithoutDbo)
        {
            if (BuiltInToken.Comparer.Equals(roleName, fixedRole))
                return SqlValue.FromInt32(0);
        }

        var database = runtime.Batch.CurrentDatabase;
        if (database.Principals.TryGetValue(roleName, out var principal)
            && principal.TypeCode == "R")
        {
            // dbo's principal id is 1; user-created role membership rides
            // the GRANT-era membership list.
            foreach (var (roleId, memberId) in database.RoleMembers)
            {
                if (roleId == principal.PrincipalId && memberId == 1)
                    return SqlValue.FromInt32(1);
            }
            return SqlValue.FromInt32(0);
        }
        return SqlValue.Null(SqlType.Int32);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => $"IS_MEMBER({this.roleArg.DebugDisplay()})";
}
