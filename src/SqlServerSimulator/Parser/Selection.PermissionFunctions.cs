using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// Built-in system TVF <c>fn_builtin_permissions(class)</c>, bare or
    /// <c>sys.</c>-qualified: the permission catalog
    /// (<see cref="BuiltinPermissionRows.All"/>), every class for
    /// <c>DEFAULT</c>, NULL or an empty string, one class for its
    /// <c>class_desc</c> in any casing, and nothing for a name no class carries
    /// (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    public static Selection ParseBuiltinPermissions(ParserContext context, string functionName)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is Operator { Character: ')' })
            throw SimulatedSqlException.InsufficientArgumentsToFunction(functionName);
        Expression? classArgument = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Default })
            context.MoveNextRequired();
        else
            classArgument = Expression.Parse(context);
        if (context.Token is Operator { Character: ',' })
            throw SimulatedSqlException.TooManyArgumentsToFunction(functionName);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        var collation = Collation.Get("Latin1_General_CI_AS_KS_WS");
        var name = NVarcharSqlType.Get(60, collation, Coercibility.Implicit);
        SqlType[] schema = [name, name, VarcharSqlType.Get(4, collation, Coercibility.Implicit), name, name, name];
        string[] columnNames = ["class_desc", "permission_name", "type", "covering_permission_name", "parent_class_desc", "parent_covering_permission_name"];

        return new Selection(schema, columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateBuiltinPermissionRows(schema, classArgument, batch, outerResolver));
    }

    private static IEnumerable<byte[]> EnumerateBuiltinPermissionRows(SqlType[] schema, Expression? classArgument, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        string? classFilter = null;
        if (classArgument is not null)
        {
            var runtime = new RuntimeContext(outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n)), batch);
            if (classArgument.Run(runtime) is { IsNull: false } value && value.CoerceTo(SqlType.NVarchar).AsString is { Length: > 0 } text)
                classFilter = text;
        }
        var name = (NVarcharSqlType)schema[0];
        var type = (VarcharSqlType)schema[2];
        foreach (var row in BuiltinPermissionRows.All)
        {
            if (classFilter is not null && !BuiltInToken.Comparer.Equals(row.ClassDescription, classFilter))
                continue;
            yield return RowEncoder.EncodeRow(schema,
            [
                SqlValue.FromNVarchar(name, row.ClassDescription),
                SqlValue.FromNVarchar(name, row.PermissionName),
                SqlValue.FromVarchar(type, row.Type),
                SqlValue.FromNVarchar(name, row.Covering),
                SqlValue.FromNVarchar(name, row.ParentClass),
                SqlValue.FromNVarchar(name, row.ParentCovering),
            ]);
        }
    }

    /// <summary>
    /// Built-in system TVF <c>fn_my_permissions(securable, class)</c>, bare or
    /// <c>sys.</c>-qualified: the permissions the session's effective identity
    /// holds on the securable, one row each in
    /// <c>fn_builtin_permissions</c>' order — the same server model every
    /// server-scope gate reads. The <c>SERVER</c> class lists the server
    /// permissions (<c>entity_name</c> <c>server</c>, an empty
    /// <c>subentity_name</c>), and <c>LOGIN</c> the four a login carries on the
    /// named one (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    /// <remarks>
    /// The database-scope classes list more permissions than the simulator's
    /// checker models, so they raise <see cref="NotSupportedException"/>
    /// rather than answer with a partial list.
    /// </remarks>
    public static Selection ParseMyPermissions(ParserContext context, string functionName)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var securable = Expression.Parse(context);
        if (context.Token is Operator { Character: ')' })
            throw SimulatedSqlException.InsufficientArgumentsToFunction(functionName);
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var securableClass = Expression.Parse(context);
        if (context.Token is Operator { Character: ',' })
            throw SimulatedSqlException.TooManyArgumentsToFunction(functionName);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        var collation = Collation.Get("Latin1_General_CI_AI");
        var name = NVarcharSqlType.Get(128, collation, Coercibility.Implicit);
        SqlType[] schema = [name, name, NVarcharSqlType.Get(60, collation, Coercibility.Implicit)];
        string[] columnNames = ["entity_name", "subentity_name", "permission_name"];

        return new Selection(schema, columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateMyPermissionRows(schema, securable, securableClass, batch, outerResolver));
    }

    private static IEnumerable<byte[]> EnumerateMyPermissionRows(SqlType[] schema, Expression securable, Expression securableClass, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var runtime = new RuntimeContext(outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n)), batch);
        var classValue = securableClass.Run(runtime);
        var className = classValue.IsNull ? "" : classValue.CoerceTo(SqlType.NVarchar).AsString.Trim();
        var connection = batch.Connection;
        var simulation = connection.Simulation;
        string entity;
        Func<string, bool> holds;
        (Storage.HeapColumn[] Columns, Func<Permission, int, bool> Holds)? columns = null;
        if (BuiltInToken.Comparer.Equals(className, "SERVER"))
        {
            entity = "server";
            holds = permission => simulation.SessionHoldsServerPermission(connection, Permission.Resolve(permission));
        }
        else if (BuiltInToken.Comparer.Equals(className, "LOGIN"))
        {
            var loginValue = securable.Run(runtime);
            if (loginValue.IsNull)
                yield break;
            entity = loginValue.CoerceTo(SqlType.NVarchar).AsString;
            if (!(BuiltInToken.Comparer.Equals(entity, "sa") || simulation.Logins.ContainsKey(entity))
                || !simulation.TryResolveServerPrincipalId(entity, out var targetId))
            {
                yield break;
            }
            var effective = connection.Security.Effective;
            holds = permission =>
            {
                var requested = Permission.Resolve(permission);
                var serverWide = requested switch
                {
                    Permission.Alter => Permission.AlterAnyLogin,
                    Permission.Impersonate => Permission.ImpersonateAnyLogin,
                    Permission.ViewDefinition => Permission.ViewAnySecurityDefinition,
                    _ => Permission.ControlServer,
                };
                return !effective.IsDatabaseScoped
                    && (simulation.Logins.IsEmptyLockFree() || simulation.HoldsServerPrincipalPermission(effective.LoginName, targetId, requested, serverWide));
            };
        }
        else if (ResolveMyPermissionsSecurable(batch, securable.Run(runtime), className.ToUpperInvariant()) is { } resolved)
        {
            entity = resolved.Entity;
            className = resolved.ClassDescription;
            holds = resolved.Holds;
            columns = resolved.Columns;
        }
        else
        {
            yield break;
        }

        var nameType = (NVarcharSqlType)schema[0];
        var permissionType = (NVarcharSqlType)schema[2];
        var entityValue = SqlValue.FromNVarchar(nameType, entity);
        var subentity = SqlValue.FromNVarchar(nameType, "");
        foreach (var row in BuiltinPermissionRows.All)
        {
            if (BuiltInToken.Comparer.Equals(row.ClassDescription, className) && holds(row.PermissionName))
                yield return RowEncoder.EncodeRow(schema, [entityValue, subentity, SqlValue.FromNVarchar(permissionType, row.PermissionName)]);
        }
        // A table's or view's columns follow, each column-grantable permission
        // in turn (probed 2026-10-04 against SQL Server 2025).
        if (columns is not null)
        {
            foreach (var permission in (Permission[])[Permission.Select, Permission.Update, Permission.References])
            {
                for (var ordinal = 1; ordinal <= columns.Value.Columns.Length; ordinal++)
                {
                    if (columns.Value.Holds(permission, ordinal))
                        yield return RowEncoder.EncodeRow(schema, [entityValue, SqlValue.FromNVarchar(nameType, columns.Value.Columns[ordinal - 1].Name), SqlValue.FromNVarchar(permissionType, permission.CanonicalName)]);
                }
            }
        }
    }

    /// <summary>
    /// A database-side securable <c>fn_my_permissions</c> names: the entity
    /// name it reports, the built-in class its permissions are listed under,
    /// the test for each, and for a table or view the per-column test. A
    /// securable that isn't there lists nothing (probed 2026-10-04 against SQL
    /// Server 2025).
    /// </summary>
    private static (string Entity, string ClassDescription, Func<string, bool> Holds, (Storage.HeapColumn[] Columns, Func<Permission, int, bool> Holds)? Columns)? ResolveMyPermissionsSecurable(
        BatchContext batch, SqlValue securable, string className)
    {
        var database = batch.CurrentDatabase;
        var connection = batch.Connection;
        var security = connection.Security;
        var dbo = security.EffectiveIsDbo;
        var principalId = security.Effective.DatabasePrincipalId;
        var server = ServerLoginRights.For(connection);
        var name = securable.IsNull ? null : securable.CoerceTo(SqlType.NVarchar).AsString;
        Func<string, bool> Granted(byte securableClass, int majorId, int schemaId) =>
            dbo ? static _ => true : permission => PermissionChecker.IsGrantedByName(database, principalId, permission, securableClass, majorId, schemaId, server);
        switch (className)
        {
            case "DATABASE":
                return ("database", className, Granted(PermissionChecker.ClassDatabase, 0, 0), null);
            case "OBJECT":
                if (name is null || !Expressions.ObjectId.TryParseObjectName(name, out var objectName)
                    || !batch.TryResolveSchema(objectName, out var objectSchema) || !objectSchema.TryFindInSharedNamespace(objectName.Leaf, out var obj))
                {
                    return null;
                }
                var columns = obj switch
                {
                    Storage.HeapTable table => table.Columns,
                    Schemas.View view => view.OutputColumns,
                    _ => null,
                };
                // A restricted principal hears only the permissions the
                // object's kind takes (probed 2026-10-04 against SQL Server
                // 2025: a db_owner member's list for a table has no EXECUTE).
                var objectGranted = Granted(PermissionChecker.ClassObject, obj.ObjectId, obj.SchemaId);
                return (name, className, dbo ? objectGranted : permission => PermissionChecker.AppliesTo(obj, permission) && objectGranted(permission),
                    columns is null ? null : (columns, dbo
                        ? static (_, _) => true
                        : (permission, ordinal) => PermissionChecker.IsColumnGranted(database, principalId, permission, obj.ObjectId, obj.SchemaId, ordinal, server)));
            case "SCHEMA":
                return name is not null && database.Schemas.TryGetValue(name, out var schema)
                    ? (name, className, Granted(PermissionChecker.ClassSchema, schema.SchemaId, 0), null)
                    : null;
            case "APPLICATION ROLE" or "ROLE" or "USER":
                return name is not null && database.Principals.TryGetValue(name, out var principal)
                    && className == (principal.TypeCode switch { "A" => "APPLICATION ROLE", "R" => "ROLE", _ => "USER" })
                    ? (name, className, Granted(PermissionChecker.ClassDatabasePrincipal, principal.PrincipalId, 0), null)
                    : null;
            default:
                // A class real doesn't know lists nothing.
                return Array.Exists(BuiltinPermissionRows.All, row => row.ClassDescription == className)
                    ? throw new NotSupportedException($"fn_my_permissions is modeled for the server, database, object, schema and principal classes, not '{className}'.")
                    : null;
        }
    }
}
