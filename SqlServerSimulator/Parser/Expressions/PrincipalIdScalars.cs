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
/// modify metadata only), so this always returns 1 for non-NULL inputs.
/// NULL on any required argument returns NULL.
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
        foreach (var a in this.args)
        {
            if (a.Run(runtime).IsNull)
                return SqlValue.Null(SqlType.Int32);
        }
        return SqlValue.FromInt32(1);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => $"HAS_PERMS_BY_NAME(...{this.args.Length} args)";
}

/// <summary>
/// SQL <c>IS_MEMBER(group_or_role)</c>, <c>IS_ROLEMEMBER(role [, principal])</c>,
/// and <c>IS_SRVROLEMEMBER(role [, login])</c>: role-membership checks.
/// The simulator pre-seeds <c>public</c> at the database level (every
/// principal is a member) and returns 1 for that case; other roles return
/// 0. Unknown role or NULL argument returns NULL.
/// </summary>
internal sealed class RoleMemberCheck : Expression
{
    private readonly Expression roleArg;
    private readonly Expression? principalArg;

    public RoleMemberCheck(ParserContext context)
    {
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
        // `public` is the universal role; everyone is a member.
        return BuiltInToken.Comparer.Equals(roleName, "public")
            ? SqlValue.FromInt32(1)
            : SqlValue.FromInt32(0);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => $"IS_MEMBER({this.roleArg.DebugDisplay()})";
}
