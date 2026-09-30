using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>USER_ID([name])</c>, <c>DATABASE_PRINCIPAL_ID([name])</c>,
/// and <c>SUSER_ID([name])</c>: return the principal-id for the named
/// (or current) principal. The simulator's seeded principals
/// (<c>public</c>=0, <c>dbo</c>=1, <c>guest</c>=2,
/// <c>INFORMATION_SCHEMA</c>=3, <c>sys</c>=4) drive USER_ID and
/// DATABASE_PRINCIPAL_ID; SUSER_ID reads <c>sys.server_principals</c>, and
/// with no argument answers the session's login.
/// NULL argument or unknown name returns NULL. <c>USER_ID</c> answers
/// <c>smallint</c>, the other two <c>int</c> (probed 2026-09-26 against SQL
/// Server 2025).
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
        var id = this.RunAsInt(runtime);
        return this.kind != PrincipalIdKind.UserId ? id
            : id.IsNull ? SqlValue.Null(SqlType.SmallInt)
            : SqlValue.FromInt16((short)id.AsInt32);
    }

    private SqlValue RunAsInt(RuntimeContext runtime)
    {
        if (this.nameArg is null)
        {
            // SUSER_ID = the session's login; USER_ID / DATABASE_PRINCIPAL_ID
            // = the effective database principal (the impersonation-stack top,
            // or dbo's id 1 for an unimpersonated session).
            if (this.kind != PrincipalIdKind.SUserId)
                return SqlValue.FromInt32(runtime.Batch.Connection.Security.Effective.DatabasePrincipalId);
            var loginName = runtime.Batch.Connection.Security.Effective.LoginName;
            foreach (var row in BuiltInResources.EnumerateSysServerPrincipals(runtime.Batch, runtime.Batch.CurrentDatabase))
            {
                if (BuiltInToken.Comparer.Equals(row[0].AsString, loginName))
                    return SqlValue.FromInt32(row[1].AsInt32);
            }
            return SqlValue.FromInt32(1);
        }
        var v = this.nameArg.Run(runtime);
        if (v.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var name = v.CoerceTo(SqlType.NVarchar).AsString;
        if (this.kind == PrincipalIdKind.SUserId)
        {
            // Any server principal sys.server_principals lists — a login, a
            // fixed or user-defined server role — by its principal_id
            // (probed 2026-09-25: sa 1, public 2, sysadmin 3, bulkadmin 10);
            // unknown → NULL.
            foreach (var row in BuiltInResources.EnumerateSysServerPrincipals(runtime.Batch, runtime.Batch.CurrentDatabase))
            {
                if (BuiltInToken.Comparer.Equals(row[0].AsString, name))
                    return SqlValue.FromInt32(row[1].AsInt32);
            }
            return SqlValue.Null(SqlType.Int32);
        }
        return runtime.Batch.CurrentDatabase.Principals.TryGetValue(name, out var p)
            ? SqlValue.FromInt32(p.PrincipalId)
            : SqlValue.Null(SqlType.Int32);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        if (this.nameArg is not null)
            _ = AssignmentRules.ArgumentType(this.nameArg, SqlType.NVarchar, batch, resolveColumnType);
        return this.kind == PrincipalIdKind.UserId ? SqlType.SmallInt : SqlType.Int32;
    }

    internal override string DebugDisplay() => this.kind switch
    {
        PrincipalIdKind.UserId => "USER_ID(...)",
        PrincipalIdKind.SUserId => "SUSER_ID(...)",
        PrincipalIdKind.DatabasePrincipalId => "DATABASE_PRINCIPAL_ID(...)",
        _ => "PRINCIPAL_ID(...)",
    };

    internal override void Describe(NodeShape shape) => shape.Local(this.kind).Child(this.nameArg);
}

internal enum PrincipalIdKind
{
    UserId,
    SUserId,
    DatabasePrincipalId,
}

/// <summary>
/// Legacy SQL <c>permissions([object_id [, 'column']])</c>: a bitmap of the
/// current principal's permissions. Deprecated but still evaluated by real
/// SQL Server (SSMS's Table Designer pre-open probe batch calls the niladic
/// form), so no deprecation warning is raised. The simulator's session
/// principal is always the database-owning <c>dbo</c> (consistent with
/// <see cref="HasPermsByName"/> always returning 1 and the current-principal
/// placeholders resolving to <c>dbo</c>), so the returned masks are the fixed
/// privileged (owner) defaults probed against SQL Server 2025 rather than a
/// per-grant computation:
/// <list type="bullet">
/// <item>niladic → <c>50201342</c> — the statement-permission mask a db_owner
/// carries (CREATE TABLE/PROCEDURE/VIEW/RULE/DEFAULT/FUNCTION + BACKUP
/// DATABASE/LOG, each mirrored into the with-grant-option high half; the
/// server-scope CREATE DATABASE bit is absent in a user database).</item>
/// <item><c>permissions(object_id)</c> → <c>1948217375</c> for an object that
/// resolves in the current database (the owner mask for a user table/view);
/// NULL argument or an id that resolves to no object → NULL.</item>
/// <item><c>permissions(object_id, 'column')</c> → <c>1082605703</c> when the
/// id resolves to a table carrying the named column; NULL argument, an
/// unresolved id, or an unknown column → NULL.</item>
/// </list>
/// Result type is <see cref="SqlType.Int32"/>.
/// </summary>
internal sealed class Permissions : Expression
{
    private const int StatementMask = 50201342;
    private const int ObjectMask = 1948217375;
    private const int ColumnMask = 1082605703;

    private readonly Expression? objectIdArg;
    private readonly Expression? columnArg;

    public Permissions(ParserContext context)
    {
        if (context.Token is Tokens.Operator { Character: ')' })
            return;
        this.objectIdArg = Parse(context);
        if (context.Token is Tokens.Operator { Character: ',' })
            this.columnArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        if (this.objectIdArg is null)
            return SqlValue.FromInt32(StatementMask);

        var idValue = this.objectIdArg.Run(runtime);
        if (idValue.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var id = ScalarArguments.CoerceToInt(idValue);

        if (this.columnArg is null)
        {
            return ObjectExists(runtime.Batch.CurrentDatabase, id)
                ? SqlValue.FromInt32(ObjectMask)
                : SqlValue.Null(SqlType.Int32);
        }

        var columnValue = this.columnArg.Run(runtime);
        if (columnValue.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var columnName = columnValue.CoerceTo(SqlType.NVarchar).AsString;
        return TableColumnExists(runtime.Batch.CurrentDatabase, id, columnName)
            ? SqlValue.FromInt32(ColumnMask)
            : SqlValue.Null(SqlType.Int32);
    }

    private static bool ObjectExists(Database database, int objectId)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var obj in schema.SchemaObjects())
            {
                if (obj.ObjectId == objectId)
                    return true;
            }
            foreach (var (_, tableType) in schema.TableTypes)
            {
                if (tableType.ObjectId == objectId)
                    return true;
            }
        }
        return false;
    }

    internal static bool TableColumnExists(Database database, int objectId, string columnName)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, table) in schema.HeapTables)
            {
                if (table.ObjectId != objectId)
                    continue;
                foreach (var column in table.Columns)
                {
                    if (BuiltInToken.Comparer.Equals(column.Name, columnName))
                        return true;
                }
                return false;
            }
        }
        return false;
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        if (this.objectIdArg is not null)
            _ = AssignmentRules.ArgumentType(this.objectIdArg, SqlType.Int32, batch, resolveColumnType);
        return SqlType.Int32;
    }

    internal override string DebugDisplay() => this.objectIdArg is null
        ? "PERMISSIONS()"
        : $"PERMISSIONS({this.objectIdArg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.objectIdArg).Child(this.columnArg);
}

/// <summary>
/// SQL <c>HAS_PERMS_BY_NAME(securable, securable_class, permission [, ...])</c>:
/// returns 1 when the current principal has the given permission, 0
/// otherwise, through the same checkers the enforcement gates use — the
/// database one for <c>DATABASE</c> / <c>OBJECT</c> / <c>SCHEMA</c>, the
/// server one for a NULL class (the server) and <c>LOGIN</c>. A NULL
/// <c>permission</c> returns NULL, and so does a class or securable real
/// doesn't answer for.
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
        var securableVal = this.args[0].Run(runtime);
        var classVal = this.args[1].Run(runtime);
        var permissionVal = this.args[2].Run(runtime);
        if (permissionVal.IsNull)
            return SqlValue.Null(SqlType.Int32);

        var connection = runtime.Batch.Connection;
        // A NULL class is the server, which only a NULL securable names and
        // only a SERVER-class permission is asked of — anything else is NULL
        // (probed 2026-09-29 against SQL Server 2025: CREATE TABLE and CONNECT
        // there answer NULL even for sa). The server answer reads the same
        // model the server-scope gates do.
        // The SERVER class answers the same question for any non-NULL
        // securable, and NULL for a NULL one.
        var serverClass = !classVal.IsNull && string.Equals(classVal.CoerceTo(SqlType.NVarchar).AsString.Trim(), "SERVER", StringComparison.OrdinalIgnoreCase);
        if (classVal.IsNull || serverClass)
        {
            return securableVal.IsNull == !serverClass && Permission.Resolve(permissionVal.CoerceTo(SqlType.NVarchar).AsString) is var serverPermission && serverPermission.IsServerClass
                ? SqlValue.FromInt32(connection.Simulation.SessionHoldsServerPermission(connection, serverPermission) ? 1 : 0)
                : SqlValue.Null(SqlType.Int32);
        }
        if (string.Equals(classVal.CoerceTo(SqlType.NVarchar).AsString.Trim(), "LOGIN", StringComparison.OrdinalIgnoreCase))
            return LoginAnswer(connection, securableVal, permissionVal.CoerceTo(SqlType.NVarchar).AsString);

        // dbo holds every permission on whatever exists — DacFx's bacpac-export
        // gate (HAS_PERMS_BY_NAME(NULL, N'DATABASE', N'VIEW DEFINITION')) reads 1
        // — but real still answers NULL for a class it doesn't know or a
        // NULL object, and 0 for an object or column that isn't there (probed
        // 2026-09-26 against SQL Server 2025).
        if (connection.Security.EffectiveIsDbo)
            return this.DboAnswer(runtime, securableVal, classVal);

        var permission = permissionVal.CoerceTo(SqlType.NVarchar).AsString;
        var className = classVal.CoerceTo(SqlType.NVarchar).AsString;
        var database = runtime.Batch.CurrentDatabase;
        var principalId = connection.Security.Effective.DatabasePrincipalId;

        byte securableClass;
        var majorId = 0;
        var schemaId = 0;
        Span<char> classBuf = stackalloc char[className.Length];
        _ = className.AsSpan().ToUpperInvariant(classBuf);
        switch (classBuf)
        {
            case "DATABASE":
                securableClass = PermissionChecker.ClassDatabase;
                break;
            case "OBJECT":
                if (securableVal.IsNull)
                    return SqlValue.Null(SqlType.Int32);
                var objectName = securableVal.CoerceTo(SqlType.NVarchar).AsString;
                if (!TryResolveObjectByName(database, objectName, out majorId, out schemaId))
                {
                    // A catalog view answers by its own read rule (probed
                    // 2026-09-28 against SQL Server 2025).
                    if (permission.Trim().Equals("SELECT", StringComparison.OrdinalIgnoreCase)
                        && ObjectId.TryParseObjectName(objectName, out var parsed)
                        && runtime.Batch.TryResolveCatalogView(parsed, out var catalogView, out var catalogDatabase)
                        && BuiltInResources.CatalogViewsById.Value.TryGetValue(catalogView.ObjectId, out var entry))
                    {
                        var viewSchemaId = entry.SchemaName == "INFORMATION_SCHEMA" ? Database.InformationSchemaId : Database.SysSchemaId;
                        return SqlValue.FromInt32(PermissionChecker.CanReadCatalogView(catalogDatabase, principalId, catalogView.ObjectId, viewSchemaId) ? 1 : 0);
                    }
                    return SqlValue.Null(SqlType.Int32);
                }
                securableClass = PermissionChecker.ClassObject;
                break;
            case "SCHEMA":
                if (securableVal.IsNull || !TryResolveSchemaByName(database, securableVal.CoerceTo(SqlType.NVarchar).AsString, out schemaId))
                    return SqlValue.Null(SqlType.Int32);
                securableClass = PermissionChecker.ClassSchema;
                majorId = schemaId;
                break;
            default:
                return SqlValue.Null(SqlType.Int32);
        }

        return SqlValue.FromInt32(
            PermissionChecker.IsGranted(database, principalId, Permission.Resolve(permission), securableClass, majorId, schemaId, ServerLoginRights.For(connection)) ? 1 : 0);
    }

    /// <summary>
    /// The <c>LOGIN</c> class: <paramref name="permissionName"/> on the named
    /// login, answered by its <c>ON LOGIN::</c> grants and the server-wide
    /// permission each implies (<c>IMPERSONATE ANY LOGIN</c>, <c>VIEW ANY
    /// SECURITY DEFINITION</c>, <c>ALTER ANY LOGIN</c>, <c>CONTROL SERVER</c>)
    /// — the model <c>EXECUTE AS LOGIN</c> and the login DDL gates read. A
    /// NULL securable, or a permission the class doesn't carry, is NULL; a
    /// name that is no login is 0 (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    private static SqlValue LoginAnswer(SimulatedDbConnection connection, SqlValue securableVal, string permissionName)
    {
        var simulation = connection.Simulation;
        var permission = Permission.Resolve(permissionName);
        Permission? blanket = permission switch
        {
            Permission.Alter => Permission.AlterAnyLogin,
            Permission.Control => Permission.ControlServer,
            Permission.Impersonate => Permission.ImpersonateAnyLogin,
            Permission.ViewDefinition => Permission.ViewAnySecurityDefinition,
            _ => null,
        };
        if (blanket is not { } serverWide || securableVal.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var loginName = securableVal.CoerceTo(SqlType.NVarchar).AsString;
        if (!(BuiltInToken.Comparer.Equals(loginName, "sa") || simulation.Logins.ContainsKey(loginName))
            || !simulation.TryResolveServerPrincipalId(loginName, out var targetId))
        {
            return SqlValue.FromInt32(0);
        }
        var effective = connection.Security.Effective;
        var holds = !effective.IsDatabaseScoped
            && (simulation.Logins.IsEmpty
                || simulation.HoldsServerPrincipalPermission(effective.LoginName, targetId, permission, serverWide));
        return SqlValue.FromInt32(holds ? 1 : 0);
    }

    private SqlValue DboAnswer(RuntimeContext runtime, SqlValue securableVal, SqlValue classVal)
    {
        var batch = runtime.Batch;
        var className = classVal.CoerceTo(SqlType.NVarchar).AsString.Trim();
        if (!IsSecurableClass(className))
            return SqlValue.Null(SqlType.Int32);
        var isObject = string.Equals(className, "OBJECT", StringComparison.OrdinalIgnoreCase);
        if (!isObject && !string.Equals(className, "SCHEMA", StringComparison.OrdinalIgnoreCase))
            return SqlValue.FromInt32(1);
        if (securableVal.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var name = securableVal.CoerceTo(SqlType.NVarchar).AsString;
        var database = batch.CurrentDatabase;
        if (!isObject)
            return SqlValue.FromInt32(TryResolveSchemaByName(database, name, out _) ? 1 : 0);

        string? column = null;
        if (this.args.Length >= 5 && this.args[3].Run(runtime) is { IsNull: false } sub)
            column = sub.CoerceTo(SqlType.NVarchar).AsString;
        if (TryResolveObjectByName(database, name, out var objectId, out _))
            return SqlValue.FromInt32(column is null || !IsTable(database, objectId) || Permissions.TableColumnExists(database, objectId, column) ? 1 : 0);
        if (ObjectId.TryParseObjectName(name, out var parsed) && batch.TryResolveCatalogView(parsed, out var catalogView, out _))
        {
            if (column is null)
                return SqlValue.FromInt32(1);
            foreach (var catalogColumn in catalogView.Columns)
            {
                if (BuiltInToken.Comparer.Equals(catalogColumn.Name, column))
                    return SqlValue.FromInt32(1);
            }
        }
        return SqlValue.FromInt32(0);
    }

    private static bool IsTable(Database database, int objectId)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, table) in schema.HeapTables)
            {
                if (table.ObjectId == objectId)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether real's <c>HAS_PERMS_BY_NAME</c> knows <paramref name="className"/>
    /// as a securable class; any other name answers NULL.
    /// </summary>
    private static bool IsSecurableClass(string className)
    {
        Span<char> upper = stackalloc char[className.Length];
        _ = className.AsSpan().ToUpperInvariant(upper);
        return upper switch
        {
            "APPLICATION ROLE" or "ASSEMBLY" or "ASYMMETRIC KEY" or "AVAILABILITY GROUP" or "CERTIFICATE" or "CONTRACT"
                or "DATABASE" or "DATABASE SCOPED CREDENTIAL" or "ENDPOINT" or "FULLTEXT CATALOG" or "FULLTEXT STOPLIST"
                or "LOGIN" or "MESSAGE TYPE" or "OBJECT" or "REMOTE SERVICE BINDING" or "ROLE" or "ROUTE" or "SCHEMA"
                or "SEARCH PROPERTY LIST" or "SERVER" or "SERVER ROLE" or "SERVICE" or "SYMMETRIC KEY" or "TYPE"
                or "USER" or "XML SCHEMA COLLECTION" => true,
            _ => false,
        };
    }

    private static bool TryResolveSchemaByName(Database database, string name, out int schemaId)
    {
        var leaf = name.Contains('.', StringComparison.Ordinal) ? name[(name.LastIndexOf('.') + 1)..] : name;
        if (database.Schemas.TryGetValue(leaf, out var schema))
        {
            schemaId = schema.SchemaId;
            return true;
        }
        schemaId = 0;
        return false;
    }

    private static bool TryResolveObjectByName(Database database, string name, out int objectId, out int schemaId)
    {
        var leaf = name.Contains('.', StringComparison.Ordinal) ? name[(name.LastIndexOf('.') + 1)..] : name;
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var obj in schema.SchemaObjects())
            {
                if (BuiltInToken.Comparer.Equals(obj.Name, leaf))
                {
                    objectId = obj.ObjectId;
                    schemaId = obj.SchemaId;
                    return true;
                }
            }
        }
        objectId = 0;
        schemaId = 0;
        return false;
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => $"HAS_PERMS_BY_NAME(...{this.args.Length} args)";

    internal override void Describe(NodeShape shape) => shape.Children(this.args);
}

/// <summary>
/// SQL <c>IS_MEMBER(group_or_role)</c>, <c>IS_ROLEMEMBER(role [, principal])</c>,
/// and <c>IS_SRVROLEMEMBER(role [, login])</c>: role-membership checks for
/// the session principal (<c>dbo</c> at the database level; the single
/// login at the server level). Probe-confirmed shape: member → 1, known
/// role without membership → 0, anything that isn't a role at that scope →
/// NULL. Database scope: <c>public</c> → 1, and for <c>dbo</c> every fixed
/// role but the deny pair; other roles consult
/// <see cref="Database.RoleMembers"/>, non-role principals and
/// unknown names → NULL. Server scope: <c>public</c> → 1, the other fixed
/// server roles → 0 (no server-role membership model), everything else →
/// NULL. NULL argument returns NULL.
/// </summary>
internal sealed class RoleMemberCheck : Expression
{
    private readonly Expression roleArg;
    private readonly Expression? principalArg;
    private readonly bool serverScope;

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
        var principalValue = this.principalArg?.Run(runtime);
        if (principalValue?.IsNull == true)
            return SqlValue.Null(SqlType.Int32);
        var roleName = role.CoerceTo(SqlType.NVarchar).AsString;
        if (!this.serverScope && principalValue is { } named)
            return DatabaseMemberOf(runtime.Batch.CurrentDatabase, roleName, named.CoerceTo(SqlType.NVarchar).AsString);
        if (BuiltInToken.Comparer.Equals(roleName, "public"))
            return SqlValue.FromInt32(1);
        if (this.serverScope)
        {
            var simulation = runtime.Batch.Connection.Simulation;
            // A non-role name → NULL.
            if (!simulation.TryResolveServerRole(roleName, out var roleId, out var isFixed))
                return SqlValue.Null(SqlType.Int32);
            // The login checked: the 2-arg named login, else the session's
            // effective login. A named login that doesn't exist → NULL.
            string loginName;
            if (this.principalArg is not null)
            {
                loginName = this.principalArg.Run(runtime).CoerceTo(SqlType.NVarchar).AsString;
                if (!BuiltInToken.Comparer.Equals(loginName, "sa") && !simulation.TryResolveServerPrincipalId(loginName, out _))
                    return SqlValue.Null(SqlType.Int32);
            }
            else
            {
                loginName = runtime.Batch.Connection.Security.Effective.LoginName;
            }
            // A sysadmin-member login reports 1 for every fixed server role
            // (probe6 N2); otherwise real registry membership.
            var isMember = (isFixed && simulation.IsLoginSysadmin(loginName))
                || (simulation.TryResolveServerPrincipalId(loginName, out var loginId) && simulation.IsServerPrincipalInRole(loginId, roleId));
            return SqlValue.FromInt32(isMember ? 1 : 0);
        }

        var database = runtime.Batch.CurrentDatabase;
        var effectiveId = runtime.Batch.Connection.Security.Effective.DatabasePrincipalId;
        if (database.Principals.TryGetValue(roleName, out var principal) && principal.TypeCode == "R")
        {
            if (effectiveId == Database.DboPrincipalId && DboBelongsTo(principal))
                return SqlValue.FromInt32(1);
            // Transitive membership of the effective principal (incl. nested
            // roles) via the permission checker's role closure.
            return SqlValue.FromInt32(PermissionChecker.IsRoleMember(database, effectiveId, principal) ? 1 : 0);
        }
        return SqlValue.Null(SqlType.Int32);
    }

    /// <summary>
    /// <c>IS_ROLEMEMBER(role, principal)</c>: the named principal is resolved
    /// first, so a missing one is NULL even for <c>public</c>; a principal is a
    /// member of itself whatever it is, and otherwise of the roles it reaches
    /// through nesting (probed 2026-09-25 against SQL Server 2025).
    /// </summary>
    private static SqlValue DatabaseMemberOf(Database database, string roleName, string principalName)
    {
        if (!database.Principals.TryGetValue(principalName, out var member))
            return SqlValue.Null(SqlType.Int32);
        if (BuiltInToken.Comparer.Equals(roleName, "public"))
            return SqlValue.FromInt32(1);
        if (!database.Principals.TryGetValue(roleName, out var roleP))
            return SqlValue.Null(SqlType.Int32);
        if (roleP.PrincipalId == member.PrincipalId
            || (member.PrincipalId == Database.DboPrincipalId && DboBelongsTo(roleP)))
        {
            return SqlValue.FromInt32(1);
        }
        return roleP.TypeCode == "R"
            ? SqlValue.FromInt32(PermissionChecker.IsRoleMember(database, member.PrincipalId, roleP) ? 1 : 0)
            : SqlValue.Null(SqlType.Int32);
    }

    /// <summary>
    /// The <c>dbo</c> user belongs to every fixed database role but the two
    /// deny roles without a membership row, where a <c>db_owner</c> member
    /// belongs to <c>db_owner</c> alone (probed 2026-09-30 against SQL Server
    /// 2025).
    /// </summary>
    private static bool DboBelongsTo(DatabasePrincipal role) =>
        role.IsFixedRole && role.PrincipalId != 0
        && !BuiltInToken.EqualsAny(role.Name, "db_denydatareader", "db_denydatawriter");

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        _ = AssignmentRules.ArgumentType(this.roleArg, SqlType.NVarchar, batch, resolveColumnType);
        if (this.principalArg is not null)
            _ = AssignmentRules.ArgumentType(this.principalArg, SqlType.NVarchar, batch, resolveColumnType);
        return SqlType.Int32;
    }

    internal override string DebugDisplay() => $"IS_MEMBER({this.roleArg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Local(this.serverScope).Child(this.roleArg).Child(this.principalArg);
}
