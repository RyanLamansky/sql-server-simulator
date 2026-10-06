using SqlServerSimulator.Schemas;
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
/// current principal's permissions, the low half what it holds and the high
/// half what it may grant on (probed 2026-10-06 against SQL Server 2025).
/// Deprecated but still evaluated by real SQL Server (SSMS's Table Designer
/// pre-open probe batch calls the niladic form), so no deprecation warning is
/// raised.
/// <list type="bullet">
/// <item>niladic — the statement permissions: <c>CREATE DATABASE</c> 1 (only
/// in <c>master</c>), <c>CREATE TABLE</c> 2, <c>PROCEDURE</c> 4, <c>VIEW</c> 8,
/// <c>RULE</c> 16, <c>DEFAULT</c> 32, <c>BACKUP DATABASE</c> 64,
/// <c>BACKUP LOG</c> 128, <c>CREATE FUNCTION</c> 512.</item>
/// <item><c>permissions(object_id)</c> — for a rowset, <c>SELECT</c> /
/// <c>UPDATE</c> / <c>REFERENCES</c> on every column 1 / 2 / 4 and on any
/// column 0x1000 / 0x2000 / 0x4000, <c>INSERT</c> 8, <c>DELETE</c> 16, and
/// 0x4000000 alongside any of them; <c>EXECUTE</c> 32 for a procedure, with
/// <c>REFERENCES</c> 4 for a scalar function; NULL for an object the
/// principal can't see, 0 for one it sees and holds nothing on.</item>
/// <item><c>permissions(object_id, 'column')</c> — <c>SELECT</c> 1 (with
/// 0x4000), <c>UPDATE</c> 2, <c>REFERENCES</c> 4, and 0x80 under
/// <c>CONTROL</c>; NULL for an unknown column.</item>
/// </list>
/// <c>CONTROL</c> — <c>dbo</c>'s and <c>db_owner</c>'s included — makes every
/// bit grantable. Result type is <see cref="SqlType.Int32"/>.
/// </summary>
internal sealed class Permissions : Expression
{
    private const int StatementMask = 50201342;
    private const int ObjectMask = 1948217375;
    private const int ColumnMask = 1082605703;

    // The statement permissions in bit order, from 2 (CREATE DATABASE's 1 is master's alone).
    private static readonly (int Bit, string Name)[] StatementBits =
    [
        (2, "CREATE TABLE"), (4, "CREATE PROCEDURE"), (8, "CREATE VIEW"), (16, "CREATE RULE"),
        (32, "CREATE DEFAULT"), (64, "BACKUP DATABASE"), (128, "BACKUP LOG"), (512, "CREATE FUNCTION"),
    ];

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
        var batch = runtime.Batch;
        var database = batch.CurrentDatabase;
        var connection = batch.Connection;
        var isDbo = PermissionEnforcement.Bypasses(connection, database);
        var principalId = connection.Security.Effective.DatabasePrincipalId;
        if (this.objectIdArg is null)
        {
            return SqlValue.FromInt32(isDbo
                ? ReferenceEquals(database, connection.Simulation.Databases.GetValueOrDefault(Simulation.MasterDatabaseName)) ? StatementMask | 0x10001 : StatementMask
                : StatementBitmap(database, principalId, ServerLoginRights.For(connection)));
        }

        var idValue = this.objectIdArg.Run(runtime);
        if (idValue.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var id = ScalarArguments.CoerceToInt(idValue);
        if (FindObject(database, id) is not { } target)
            return SqlValue.Null(SqlType.Int32);
        var server = ServerLoginRights.For(connection);
        if (!isDbo && !PermissionChecker.CanViewMetadata(database, principalId, target.ObjectId, target.SchemaId, server))
            return SqlValue.Null(SqlType.Int32);

        if (this.columnArg is null)
            return SqlValue.FromInt32(isDbo ? DboObjectMask(target) : ObjectBitmap(database, PermissionChecker.BuildPrincipalClosure(database, principalId), target, server));

        var columnValue = this.columnArg.Run(runtime);
        if (columnValue.IsNull || ColumnsOf(target) is not { } columns)
            return SqlValue.Null(SqlType.Int32);
        var columnName = columnValue.CoerceTo(SqlType.NVarchar).AsString;
        var ordinal = Array.FindIndex(columns, column => database.Collation.Equals(column.Name, columnName)) + 1;
        if (ordinal == 0)
            return SqlValue.Null(SqlType.Int32);
        return SqlValue.FromInt32(isDbo ? ColumnMask : ColumnBitmap(database, PermissionChecker.BuildPrincipalClosure(database, principalId), target, ordinal, server));
    }

    private static int StatementBitmap(Database database, int principalId, ServerLoginRights server)
    {
        var closure = PermissionChecker.BuildPrincipalClosure(database, principalId);
        var control = PermissionChecker.IsGrantedInClosure(database, closure, Permission.Control, PermissionChecker.ClassDatabase, 0, 0, server);
        var bitmap = 0;
        foreach (var (bit, name) in StatementBits)
        {
            if (!PermissionChecker.IsGrantedByName(database, principalId, name, PermissionChecker.ClassDatabase, 0, 0, server))
                continue;
            bitmap |= bit;
            if (control || PermissionChecker.IsGrantable(database, closure, name, PermissionChecker.ClassDatabase, 0, 0))
                bitmap |= bit << 16;
        }
        return bitmap;
    }

    // dbo's bitmap per kind of object: a rowset's every bit, a procedure's
    // EXECUTE, a scalar function's EXECUTE and REFERENCES, a synonym's SELECT /
    // UPDATE / INSERT / DELETE / EXECUTE, a sequence's nothing.
    private static int DboObjectMask(SchemaObject target) => target switch
    {
        Procedure => 0x200020,
        ScalarFunction or ClrScalarFunction => 0x240024,
        Sequence => 0,
        Synonym => 0x3B003B,
        _ => ObjectMask,
    };

    private static int ObjectBitmap(Database database, HashSet<int> closure, SchemaObject target, ServerLoginRights server)
    {
        var (objectId, schemaId) = (target.ObjectId, target.SchemaId);
        var control = PermissionChecker.IsGrantedInClosure(database, closure, Permission.Control, PermissionChecker.ClassObject, objectId, schemaId, server, target);
        int Bits(Permission permission, int bit)
        {
            if (!PermissionChecker.IsGrantedInClosure(database, closure, permission, PermissionChecker.ClassObject, objectId, schemaId, server, target))
                return 0;
            return control || PermissionChecker.IsGrantable(database, closure, permission.CanonicalName, PermissionChecker.ClassObject, objectId, schemaId) ? bit | (bit << 16) : bit;
        }
        switch (target)
        {
            case Procedure:
                return Bits(Permission.Execute, 32);
            case ScalarFunction or ClrScalarFunction:
                return Bits(Permission.Execute, 32) | Bits(Permission.References, 4);
            case Sequence:
                return 0;
            case Synonym:
                return Bits(Permission.Select, 1) | Bits(Permission.Update, 2) | Bits(Permission.Insert, 8) | Bits(Permission.Delete, 16) | Bits(Permission.Execute, 32);
        }
        if (ColumnsOf(target) is not { } columns)
            return 0;
        var bitmap = Bits(Permission.Insert, 8) | Bits(Permission.Delete, 16);
        foreach (var (permission, all, any) in (ReadOnlySpan<(Permission, int, int)>)[(Permission.Select, 1, 0x1000), (Permission.Update, 2, 0x2000), (Permission.References, 4, 0x4000)])
        {
            int held = 0, grantable = 0;
            for (var ordinal = 1; ordinal <= columns.Length; ordinal++)
            {
                if (!PermissionChecker.IsColumnGranted(database, closure, permission, objectId, schemaId, ordinal, server, target))
                    continue;
                held++;
                if (control || PermissionChecker.IsGrantable(database, closure, permission.CanonicalName, PermissionChecker.ClassObject, objectId, schemaId, ordinal))
                    grantable++;
            }
            var heldBits = (held == columns.Length && held > 0 ? all : 0) | (held > 0 ? any : 0);
            var grantableBits = (grantable == columns.Length && grantable > 0 ? all : 0) | (grantable > 0 ? any : 0);
            bitmap |= heldBits | (grantableBits << 16);
        }
        // A multi-statement function's rows are read-only to a principal
        // short of dbo, CONTROL notwithstanding.
        if (target is MultiStatementTableValuedFunction)
            bitmap &= ~(2 | 8 | 16 | 0x2000);
        return (bitmap & 0xFFFF) != 0 ? bitmap | 0x4000000 : bitmap;
    }

    private static int ColumnBitmap(Database database, HashSet<int> closure, SchemaObject target, int ordinal, ServerLoginRights server)
    {
        var (objectId, schemaId) = (target.ObjectId, target.SchemaId);
        var control = PermissionChecker.IsGrantedInClosure(database, closure, Permission.Control, PermissionChecker.ClassObject, objectId, schemaId, server, target);
        var bitmap = 0;
        foreach (var (permission, bit) in (ReadOnlySpan<(Permission, int)>)[(Permission.Select, 1), (Permission.Update, 2), (Permission.References, 4)])
        {
            if (!PermissionChecker.IsColumnGranted(database, closure, permission, objectId, schemaId, ordinal, server, target))
                continue;
            bitmap |= bit;
            if (control || PermissionChecker.IsGrantable(database, closure, permission.CanonicalName, PermissionChecker.ClassObject, objectId, schemaId, ordinal))
                bitmap |= bit << 16;
        }
        if ((bitmap & 1) != 0)
            bitmap |= 0x4000;
        // CONTROL adds 0x80, which a view's column shows only in the grantable
        // half short of dbo.
        if (control)
            bitmap |= (target is HeapTable ? 0x80 : 0) | 0x40800000;
        return bitmap;
    }

    private static SchemaObject? FindObject(Database database, int objectId)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var obj in schema.SchemaObjects())
            {
                if (obj.ObjectId == objectId)
                    return obj;
            }
            foreach (var (_, tableType) in schema.TableTypes)
            {
                if (tableType.ObjectId == objectId)
                    return tableType;
            }
        }
        return null;
    }

    // The columns a column-grain bitmap names, or null for an object without them.
    private static HeapColumn[]? ColumnsOf(SchemaObject target) => target switch
    {
        HeapTable table => table.Columns,
        View view => view.OutputColumns,
        InlineTableValuedFunction inline => inline.OutputColumns,
        MultiStatementTableValuedFunction multi => multi.OutputColumns,
        ClrTableValuedFunction clr => clr.OutputColumns,
        _ => null,
    };

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

        var permission = permissionVal.CoerceTo(SqlType.NVarchar).AsString.Trim().ToUpperInvariant();
        var className = classVal.CoerceTo(SqlType.NVarchar).AsString;
        var database = runtime.Batch.CurrentDatabase;
        var principalId = connection.Security.Effective.DatabasePrincipalId;
        var server = ServerLoginRights.For(connection);

        byte securableClass;
        var majorId = 0;
        var schemaId = 0;
        string classDescription;
        Span<char> classBuf = stackalloc char[className.Length];
        _ = className.AsSpan().ToUpperInvariant(classBuf);
        switch (classBuf)
        {
            case "DATABASE":
                securableClass = PermissionChecker.ClassDatabase;
                classDescription = "DATABASE";
                break;
            case "OBJECT":
                if (securableVal.IsNull)
                    return SqlValue.Null(SqlType.Int32);
                var objectName = securableVal.CoerceTo(SqlType.NVarchar).AsString;
                if (!TryResolveObjectByName(database, objectName, out majorId, out schemaId))
                {
                    // A catalog view answers by its own read rule (probed
                    // 2026-09-28 against SQL Server 2025).
                    if (permission == "SELECT"
                        && ObjectId.TryParseObjectName(objectName, out var parsed)
                        && runtime.Batch.TryResolveCatalogView(parsed, out var catalogView, out var catalogDatabase)
                        && BuiltInResources.CatalogViewsById.Value.TryGetValue(catalogView.ObjectId, out var entry))
                    {
                        var viewSchemaId = entry.SchemaName == "INFORMATION_SCHEMA" ? Database.InformationSchemaId : Database.SysSchemaId;
                        return SqlValue.FromInt32(PermissionChecker.CanReadCatalogView(catalogDatabase, principalId, catalogView.ObjectId, viewSchemaId) ? 1 : 0);
                    }
                    // An object that isn't there is 0, as for dbo (probed
                    // 2026-10-04 against SQL Server 2025).
                    return SqlValue.FromInt32(0);
                }
                securableClass = PermissionChecker.ClassObject;
                classDescription = "OBJECT";
                break;
            case "SCHEMA":
                if (securableVal.IsNull || !TryResolveSchemaByName(database, securableVal.CoerceTo(SqlType.NVarchar).AsString, out schemaId))
                    return SqlValue.Null(SqlType.Int32);
                securableClass = PermissionChecker.ClassSchema;
                classDescription = "SCHEMA";
                majorId = schemaId;
                break;
            default:
                return SqlValue.Null(SqlType.Int32);
        }

        // ANY asks for any permission on the securable; a name the class
        // doesn't carry is NULL (probed 2026-10-04 against SQL Server 2025).
        if (permission == "ANY")
            return SqlValue.FromInt32(HoldsAny(database, principalId, classDescription, securableClass, majorId, schemaId, server) ? 1 : 0);
        if (!PermissionGraph.IsPermissionOf(classDescription, permission))
            return SqlValue.Null(SqlType.Int32);

        var columns = securableClass == PermissionChecker.ClassObject ? ColumnsOf(database, majorId) : null;
        var enumPermission = Permission.Resolve(permission);
        // A column sub-securable answers for that column, 0 for one the object
        // lacks; the object itself answers a column-grantable permission only
        // when every column passes, so a column DENY under a table GRANT reads
        // 0 (probed 2026-10-04 against SQL Server 2025).
        if (this.args.Length >= 5 && this.args[3].Run(runtime) is { IsNull: false } subName && columns is not null)
        {
            var ordinal = Array.FindIndex(columns, c => BuiltInToken.Comparer.Equals(c.Name, subName.CoerceTo(SqlType.NVarchar).AsString)) + 1;
            return SqlValue.FromInt32(ordinal > 0 && PermissionChecker.IsColumnGranted(database, principalId, enumPermission, majorId, schemaId, ordinal, server) ? 1 : 0);
        }
        if (columns is not null && enumPermission is Permission.Select or Permission.Update or Permission.References or Permission.Unmask)
        {
            for (var ordinal = 1; ordinal <= columns.Length; ordinal++)
            {
                if (!PermissionChecker.IsColumnGranted(database, principalId, enumPermission, majorId, schemaId, ordinal, server))
                    return SqlValue.FromInt32(0);
            }
            return SqlValue.FromInt32(1);
        }
        return SqlValue.FromInt32(
            PermissionChecker.IsGrantedByName(database, principalId, permission, securableClass, majorId, schemaId, server) ? 1 : 0);
    }

    /// <summary>Whether the principal holds any permission <paramref name="classDescription"/> securables take on this one.</summary>
    private static bool HoldsAny(Database database, int principalId, string classDescription, byte securableClass, int majorId, int schemaId, ServerLoginRights server)
    {
        foreach (var row in BuiltinPermissionRows.All)
        {
            if (row.ClassDescription == classDescription
                && PermissionChecker.IsGrantedByName(database, principalId, row.PermissionName, securableClass, majorId, schemaId, server))
            {
                return true;
            }
        }
        return securableClass == PermissionChecker.ClassObject && ColumnsOf(database, majorId) is not null
            && (PermissionChecker.HasAccessibleColumn(database, principalId, Permission.Select, majorId, schemaId, server)
                || PermissionChecker.HasAccessibleColumn(database, principalId, Permission.Update, majorId, schemaId, server));
    }

    /// <summary>The columns of the table or view with id <paramref name="objectId"/>, or null for any other object.</summary>
    private static Storage.HeapColumn[]? ColumnsOf(Database database, int objectId) =>
        ObjectProperty.FindObject(database, objectId) switch
        {
            Storage.HeapTable table => table.Columns,
            Schemas.View view => view.OutputColumns,
            _ => null,
        };

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
            && (simulation.Logins.IsEmptyLockFree()
                || simulation.HoldsServerPrincipalPermission(effective.LoginName, targetId, permission, serverWide));
        return SqlValue.FromInt32(holds ? 1 : 0);
    }

    private SqlValue DboAnswer(RuntimeContext runtime, SqlValue securableVal, SqlValue classVal)
    {
        var batch = runtime.Batch;
        var className = classVal.CoerceTo(SqlType.NVarchar).AsString.Trim();
        if (!IsSecurableClass(className))
            return SqlValue.Null(SqlType.Int32);
        var permission = this.args[2].Run(runtime).CoerceTo(SqlType.NVarchar).AsString.Trim().ToUpperInvariant();
        if (permission != "ANY" && !PermissionGraph.IsPermissionOf(className.ToUpperInvariant(), permission))
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
        var connection = runtime.Batch.Connection;
        if (!this.serverScope && principalValue is { } named)
        {
            var visible = connection.Security.EffectiveIsDbo ? null
                : PermissionChecker.VisiblePrincipals(runtime.Batch.CurrentDatabase, connection.Security.Effective.DatabasePrincipalId, ServerLoginRights.For(connection));
            return DatabaseMemberOf(runtime.Batch.CurrentDatabase, roleName, named.CoerceTo(SqlType.NVarchar).AsString, visible);
        }
        // An identity minted inside one database has no server principal and
        // belongs to no server role, public included (probed 2026-10-04
        // against SQL Server 2025).
        if (this.serverScope && this.principalArg is null && connection.Security.Effective.IsDatabaseScoped)
        {
            return connection.Simulation.TryResolveServerRole(roleName, out _, out _) || BuiltInToken.Comparer.Equals(roleName, "public")
                ? SqlValue.FromInt32(0)
                : SqlValue.Null(SqlType.Int32);
        }
        if (BuiltInToken.Comparer.Equals(roleName, "public"))
            return SqlValue.FromInt32(1);
        if (this.serverScope)
        {
            var simulation = connection.Simulation;
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
        // A user's name asks whether the effective user is that one (probed
        // 2026-10-04 against SQL Server 2025).
        return principal is { TypeCode: "S" or "U" }
            ? SqlValue.FromInt32(principal.PrincipalId == effectiveId ? 1 : 0)
            : SqlValue.Null(SqlType.Int32);
    }

    /// <summary>
    /// <c>IS_ROLEMEMBER(role, principal)</c>: the named principal is resolved
    /// first, so a missing one is NULL even for <c>public</c>; a principal is a
    /// member of itself whatever it is, and otherwise of the roles it reaches
    /// through nesting (probed 2026-09-25 against SQL Server 2025).
    /// </summary>
    private static SqlValue DatabaseMemberOf(Database database, string roleName, string principalName, HashSet<int>? visible)
    {
        // A member the caller can't see answers as no member — NULL for a
        // role, 0 for a user (probed 2026-10-04 against SQL Server 2025).
        if (!database.Principals.TryGetValue(principalName, out var member))
            return SqlValue.Null(SqlType.Int32);
        if (visible?.Contains(member.PrincipalId) == false)
            return member.TypeCode == "R" ? SqlValue.Null(SqlType.Int32) : SqlValue.FromInt32(0);
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
