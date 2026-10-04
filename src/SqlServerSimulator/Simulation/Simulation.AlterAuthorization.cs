using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>ALTER AUTHORIZATION ON [&lt;class&gt;::]&lt;entity&gt; TO
    /// {&lt;principal&gt; | SCHEMA OWNER}</c>; the cursor is on
    /// <c>AUTHORIZATION</c>. The modeled classes are <c>OBJECT</c> (the
    /// default), <c>SCHEMA</c>, <c>TYPE</c>, <c>XML SCHEMA COLLECTION</c>,
    /// <c>FULLTEXT CATALOG</c>, <c>ROLE</c>, <c>ASSEMBLY</c> and
    /// <c>DATABASE</c>; <c>USER</c> and <c>APPLICATION ROLE</c> carry no owner
    /// (Msg 15344), and every other class is not modeled yet.
    /// </summary>
    /// <remarks>
    /// Real's order, probed 2026-09-27 against SQL Server 2025: the entity
    /// resolves first (a missing one, or one the caller holds no
    /// <c>TAKE OWNERSHIP</c> on, is Msg 15151 naming it), then the new owner (an
    /// unknown name is the "user" wording, <c>sys</c> / <c>INFORMATION_SCHEMA</c>
    /// the "principal" wording at state 2, and one the caller may not
    /// impersonate the "principal" wording at state 1). A change of effective
    /// owner drops every permission granted on the securable; naming the owner
    /// it already has keeps them. Every change rolls back with the transaction.
    /// </remarks>
    private static bool TryParseAlterAuthorization(ParserContext context)
    {
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var securableClass = ParseAuthorizationClass(context);
        var entityName = BatchContext.ParseObjectName(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.To })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        string? ownerName;
        switch (context.GetNextRequired())
        {
            case ReservedKeyword { Keyword: Keyword.Schema }:
                if (context.GetNextRequired() is not Name { Value: var ownerWord } || !BuiltInToken.Equals(ownerWord, "OWNER"))
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                ownerName = null;
                break;
            case Name name:
                ownerName = name.Value;
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        context.MoveNextOptional();
        // One owner only: real stops at the comma (Msg 102 near ',').
        if (context.Token is Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.Batch.IsSkipping)
            return true;

        switch (securableClass)
        {
            case "APPLICATION ROLE":
                throw SimulatedSqlException.OwnershipChangeNotSupported("application role");
            case "ASSEMBLY":
                ChangeAssemblyOwner(context, entityName.Leaf, ownerName);
                break;
            case "DATABASE":
                ChangeDatabaseOwner(context, entityName.Leaf, ownerName);
                break;
            case "FULLTEXT CATALOG":
                ChangeFullTextCatalogOwner(context, entityName.Leaf, ownerName);
                break;
            case "OBJECT":
                ChangeObjectOwner(context, entityName, ownerName);
                break;
            case "ROLE":
                ChangeRoleOwner(context, entityName.Leaf, ownerName);
                break;
            case "SCHEMA":
                ChangeSchemaOwner(context, entityName.Leaf, ownerName);
                break;
            case "TYPE":
                ChangeTypeOwner(context, entityName, ownerName);
                break;
            case "USER":
                throw SimulatedSqlException.OwnershipChangeNotSupported("user");
            case "XML SCHEMA COLLECTION":
                ChangeXmlSchemaCollectionOwner(context, entityName, ownerName);
                break;
            default:
                throw new NotSupportedException($"ALTER AUTHORIZATION on the {securableClass} class is not modeled.");
        }
        return true;
    }

    /// <summary>
    /// Reads an optional <c>&lt;class&gt; ::</c> prefix — one to three words
    /// followed by two adjacent <c>:</c> operators — leaving the cursor on the
    /// entity name. Answers the upper-cased words joined by single spaces, or
    /// <c>OBJECT</c> when there's no prefix.
    /// </summary>
    private static string ParseAuthorizationClass(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        var words = new List<string>(3);
        while (words.Count < 3 && ClassWord(context.Token) is { } word)
        {
            words.Add(word);
            if (!context.MoveNext())
                break;
            if (context.Token is Operator { Character: ':' })
            {
                if (context.GetNextRequired() is not Operator { Character: ':' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
                return string.Join(' ', words);
            }
        }
        context.RestoreCheckpoint(checkpoint);
        return "OBJECT";

        static string? ClassWord(Token? token) => token switch
        {
            ReservedKeyword keyword => keyword.Keyword.ToString().ToUpperInvariant(),
            UnquotedString word => word.Value.ToUpperInvariant(),
            _ => null,
        };
    }

    /// <summary>
    /// Resolves the new owner of a database-scoped securable to its principal
    /// id, or null for <c>SCHEMA OWNER</c> where <paramref name="acceptsSchemaOwner"/>
    /// allows it; checks the caller may name that owner.
    /// </summary>
    private static int? ResolveNewOwner(ParserContext context, Database database, string? ownerName, bool acceptsSchemaOwner)
    {
        if (ownerName is null)
        {
            return acceptsSchemaOwner ? null : throw SimulatedSqlException.CannotFindUser("SCHEMA OWNER");
        }
        if (!database.Principals.TryGetValue(ownerName, out var owner))
            throw SimulatedSqlException.CannotFindUser(ownerName);
        if (owner.PrincipalId is Database.InformationSchemaPrincipalId or Database.SysPrincipalId)
            throw SimulatedSqlException.CannotFindSecurable("principal", ownerName, state: 2);
        if (!PermissionEnforcement.MayActAs(context.Batch, database, owner.PrincipalId))
            throw SimulatedSqlException.CannotFindSecurable("principal", ownerName);
        return owner.PrincipalId;
    }

    /// <summary>Drops every permission row on the securable a completed ownership change moved to a new owner.</summary>
    private static void DropSecurablePermissions(ParserContext context, Database database, byte securableClass, int majorId)
    {
        if (!database.Permissions.Exists(row => row.Class == securableClass && row.MajorId == majorId))
            return;
        RecordSecurityUndo(context, database);
        _ = database.Permissions.RemoveAll(row => row.Class == securableClass && row.MajorId == majorId);
    }

    private static void ChangeObjectOwner(ParserContext context, MultiPartName name, string? ownerName)
    {
        var batch = context.Batch;
        var database = context.CurrentDatabase;
        if (name.Leaf.StartsWith('#'))
        {
            if (batch.TryResolveTable(name, out _))
                throw SimulatedSqlException.OwnershipChangeNotSupported("object");
            throw SimulatedSqlException.CannotFindObject(name.Leaf);
        }
        // A three-part name is never found, even naming the current database.
        if (name.Count > 2 || !batch.TryResolveSchema(name, out var schema))
            throw SimulatedSqlException.CannotFindObject(name.Leaf);
        if (!schema.TryFindInSharedNamespace(name.Leaf, out var target))
        {
            throw schema.HasConstraintNamed(name.Leaf)
                ? SimulatedSqlException.OwnerFollowsParentObject()
                : SimulatedSqlException.CannotFindObject(name.Leaf);
        }
        if (target is Trigger)
            throw SimulatedSqlException.OwnerFollowsParentObject();
        if (!PermissionEnforcement.HoldsPermission(batch, database, Permission.TakeOwnership, PermissionChecker.ClassObject, target.ObjectId, target.SchemaId))
            throw SimulatedSqlException.CannotFindObject(name.Leaf);
        database.RejectWriteWhenReadOnly();
        var newOwner = ResolveNewOwner(context, database, ownerName, acceptsSchemaOwner: true);

        var previous = target.OwnerPrincipalId;
        var previousEffective = Ownership.EffectiveOwnerId(database, target);
        batch.AcquireStatementLock(target.SchemaLock, LockMode.SchemaModification);
        target.OwnerPrincipalId = newOwner;
        RecordDdlUndo(context, () => target.OwnerPrincipalId = previous);
        var effective = Ownership.EffectiveOwnerId(database, target);
        if (effective != previousEffective)
            DropSecurablePermissions(context, database, PermissionChecker.ClassObject, target.ObjectId);
        RecordDdlEvent(context, "ALTER_AUTHORIZATION_DATABASE", schema.Name, target.Name, AuthorizationObjectType(target),
            ownerName: Ownership.PrincipalName(database, effective));
    }

    /// <summary>The <c>ObjectType</c> an <c>ALTER_AUTHORIZATION_DATABASE</c> event reports for an object.</summary>
    private static string AuthorizationObjectType(SchemaObject target) => target switch
    {
        HeapTable => "TABLE",
        View => "VIEW",
        Procedure => "PROCEDURE",
        UserDefinedFunction => "FUNCTION",
        Sequence => "SEQUENCE",
        Synonym => "SYNONYM",
        RuleObject => "RULE",
        SecurityPolicy => "SECURITY POLICY",
        _ => "DEFAULT",
    };

    private static void ChangeSchemaOwner(ParserContext context, string schemaName, string? ownerName)
    {
        var database = context.CurrentDatabase;
        var fixedRoleSchemaId = 0;
        foreach (var (roleId, roleName) in Database.FixedDatabaseRoles)
        {
            if (database.Collation.Equals(roleName, schemaName))
                fixedRoleSchemaId = roleId;
        }
        Schema? schema = null;
        if (fixedRoleSchemaId == 0)
        {
            if (!database.Schemas.TryGetValue(schemaName, out schema) && !BuiltInToken.Equals(schemaName, "guest"))
                throw SimulatedSqlException.CannotFindSecurable("schema", schemaName);
            if (schema is null || schema.SchemaId is Database.DboSchemaId or Database.InformationSchemaId or Database.SysSchemaId)
                throw SimulatedSqlException.CannotAlterFixedSchema(schema?.Name ?? schemaName);
        }
        var schemaId = schema?.SchemaId ?? fixedRoleSchemaId;
        if (!PermissionEnforcement.HoldsPermission(context.Batch, database, Permission.TakeOwnership, PermissionChecker.ClassSchema, schemaId, 0))
            throw SimulatedSqlException.CannotFindSecurable("schema", schemaName);
        database.RejectWriteWhenReadOnly();
        var newOwner = ResolveNewOwner(context, database, ownerName, acceptsSchemaOwner: false)!.Value;

        var previous = Ownership.SchemaOwnerId(database, schemaId);
        if (schema is not null)
        {
            schema.PrincipalId = newOwner;
            RecordDdlUndo(context, () => schema.PrincipalId = previous);
        }
        else
        {
            var hadEntry = database.FixedRoleSchemaOwners.TryGetValue(schemaId, out var moved);
            database.FixedRoleSchemaOwners[schemaId] = newOwner;
            RecordDdlUndo(context, () =>
            {
                if (hadEntry)
                    database.FixedRoleSchemaOwners[schemaId] = moved;
                else
                    _ = database.FixedRoleSchemaOwners.TryRemove(schemaId, out _);
            });
        }
        if (previous != newOwner)
            DropSecurablePermissions(context, database, PermissionChecker.ClassSchema, schemaId);
        RecordDdlEvent(context, "ALTER_AUTHORIZATION_DATABASE", "", schema?.Name ?? schemaName, "SCHEMA", ownerName: ownerName);
    }

    private static void ChangeTypeOwner(ParserContext context, MultiPartName name, string? ownerName)
    {
        var database = context.CurrentDatabase;
        if (name.Count == 1 && IsSystemTypeName(name.Leaf))
            throw SimulatedSqlException.UserDoesNotHavePermission();
        if (name.Count > 2 || !context.Batch.TryResolveSchema(name, out var schema))
            throw SimulatedSqlException.CannotFindType(name.Leaf);
        _ = schema.TableTypes.TryGetValue(name.Leaf, out var tableType);
        _ = schema.AliasTypes.TryGetValue(name.Leaf, out var aliasType);
        if (tableType is null && aliasType is null)
            throw SimulatedSqlException.CannotFindType(name.Leaf);
        var typeId = tableType?.UserTypeId ?? aliasType!.UserTypeId;
        if (!PermissionEnforcement.HoldsPermission(context.Batch, database, Permission.TakeOwnership, PermissionChecker.ClassType, typeId, schema.SchemaId))
            throw SimulatedSqlException.CannotFindType(name.Leaf);
        database.RejectWriteWhenReadOnly();
        var newOwner = ResolveNewOwner(context, database, ownerName, acceptsSchemaOwner: true);
        var schemaOwner = Ownership.SchemaOwnerId(database, schema.SchemaId);
        int? previousOwner;
        if (tableType is not null)
        {
            var previous = previousOwner = tableType.OwnerPrincipalId;
            tableType.OwnerPrincipalId = newOwner;
            RecordDdlUndo(context, () => tableType.OwnerPrincipalId = previous);
        }
        else
        {
            var previous = previousOwner = aliasType!.OwnerPrincipalId;
            aliasType.OwnerPrincipalId = newOwner;
            RecordDdlUndo(context, () => aliasType.OwnerPrincipalId = previous);
        }
        // A change of effective owner drops the grants on the type (probed
        // 2026-09-29 against SQL Server 2025, as for an object).
        if ((previousOwner ?? schemaOwner) != (newOwner ?? schemaOwner))
            DropSecurablePermissions(context, database, PermissionChecker.ClassType, typeId);
        RecordDdlEvent(context, "ALTER_AUTHORIZATION_DATABASE", schema.Name, name.Leaf, "TYPE",
            ownerName: Ownership.PrincipalName(database, newOwner ?? schema.PrincipalId));
    }

    /// <summary>Whether <paramref name="name"/> is a system type's name — <c>ALTER AUTHORIZATION ON TYPE::int</c> is Msg 15247.</summary>
    private static bool IsSystemTypeName(string name)
    {
        foreach (var row in BuiltInResources.SystypesRowData)
        {
            if (BuiltInToken.Equals((string)row[0]!, name))
                return true;
        }
        return false;
    }

    private static void ChangeXmlSchemaCollectionOwner(ParserContext context, MultiPartName name, string? ownerName)
    {
        var database = context.CurrentDatabase;
        if (name.Count > 2 || !context.Batch.TryResolveSchema(name, out var schema)
            || !schema.XmlSchemaCollections.TryGetValue(name.Leaf, out var collection)
            || !PermissionEnforcement.HoldsPermission(context.Batch, database, Permission.TakeOwnership, PermissionChecker.ClassXmlSchemaCollection, collection.Id, schema.SchemaId))
        {
            throw SimulatedSqlException.CannotFindXmlSchemaCollection(name.Leaf);
        }
        database.RejectWriteWhenReadOnly();
        var newOwner = ResolveNewOwner(context, database, ownerName, acceptsSchemaOwner: true);
        var previous = collection.PrincipalId;
        collection.PrincipalId = newOwner;
        RecordDdlUndo(context, () => collection.PrincipalId = previous);
        var collectionSchemaOwner = Ownership.SchemaOwnerId(database, schema.SchemaId);
        if ((previous ?? collectionSchemaOwner) != (newOwner ?? collectionSchemaOwner))
            DropSecurablePermissions(context, database, PermissionChecker.ClassXmlSchemaCollection, collection.Id);
        RecordDdlEvent(context, "ALTER_AUTHORIZATION_DATABASE", schema.Name, collection.Name, "XML SCHEMA COLLECTION",
            ownerName: Ownership.PrincipalName(database, newOwner ?? schema.PrincipalId));
    }

    private static void ChangeRoleOwner(ParserContext context, string roleName, string? ownerName)
    {
        var database = context.CurrentDatabase;
        if (!database.Principals.TryGetValue(roleName, out var role) || role.TypeCode != "R"
            || !PermissionEnforcement.HoldsPermission(context.Batch, database, Permission.TakeOwnership, PermissionChecker.ClassDatabasePrincipal, role.PrincipalId, 0))
        {
            throw SimulatedSqlException.CannotFindSecurable("role", roleName);
        }
        database.RejectWriteWhenReadOnly();
        var newOwner = ResolveNewOwner(context, database, ownerName, acceptsSchemaOwner: false)!.Value;
        var previous = role.OwningPrincipalId;
        role.OwningPrincipalId = newOwner;
        RecordDdlUndo(context, () => role.OwningPrincipalId = previous);
        if (previous != newOwner)
            DropSecurablePermissions(context, database, PermissionChecker.ClassDatabasePrincipal, role.PrincipalId);
        RecordDdlEvent(context, "ALTER_AUTHORIZATION_DATABASE", null, role.Name, "ROLE", ownerName: ownerName);
    }

    private static void ChangeFullTextCatalogOwner(ParserContext context, string catalogName, string? ownerName)
    {
        var database = context.CurrentDatabase;
        if (!database.FullTextCatalogs.TryGetValue(catalogName, out var catalog)
            || !PermissionEnforcement.HoldsPermission(context.Batch, database, Permission.TakeOwnership, PermissionChecker.ClassFulltextCatalog, catalog.Id, 0))
        {
            throw SimulatedSqlException.CannotFindSecurable("fulltext catalog", catalogName);
        }
        database.RejectWriteWhenReadOnly();
        var newOwner = ResolveNewOwner(context, database, ownerName, acceptsSchemaOwner: false)!.Value;
        var previous = catalog.PrincipalId;
        catalog.PrincipalId = newOwner;
        RecordDdlUndo(context, () => catalog.PrincipalId = previous);
        if (previous != newOwner)
            DropSecurablePermissions(context, database, PermissionChecker.ClassFulltextCatalog, catalog.Id);
        RecordDdlEvent(context, "ALTER_AUTHORIZATION_DATABASE", null, catalog.Name, "FULLTEXT CATALOG", ownerName: ownerName);
    }

    private static void ChangeAssemblyOwner(ParserContext context, string assemblyName, string? ownerName)
    {
        var database = context.CurrentDatabase;
        if (!database.Assemblies.TryGetValue(assemblyName, out var assembly)
            || !PermissionEnforcement.HoldsPermission(context.Batch, database, Permission.TakeOwnership, PermissionChecker.ClassDatabase, 0, 0))
        {
            throw SimulatedSqlException.CannotFindSecurable("assembly", assemblyName);
        }
        database.RejectWriteWhenReadOnly();
        var newOwner = ResolveNewOwner(context, database, ownerName, acceptsSchemaOwner: false)!.Value;
        var previous = assembly.PrincipalId;
        assembly.PrincipalId = newOwner;
        RecordDdlUndo(context, () => assembly.PrincipalId = previous);
        RecordDdlEvent(context, "ALTER_AUTHORIZATION_DATABASE", null, assembly.Name, "ASSEMBLY", ownerName: ownerName);
    }

    /// <summary>
    /// <c>ALTER AUTHORIZATION ON DATABASE::</c>, shared with
    /// <c>sp_changedbowner</c>: the new owner is a login, which then connects
    /// to the database as <c>dbo</c>.
    /// </summary>
    private static void ChangeDatabaseOwner(ParserContext context, string databaseName, string? loginName)
    {
        var simulation = context.Connection.Simulation;
        if (!simulation.Databases.TryGetValue(databaseName, out var target) || !CanTakeDatabaseOwnership(context.Batch, target))
            throw SimulatedSqlException.CannotFindSecurable("database", databaseName);
        ChangeDatabaseOwner(context, target, loginName);
    }

    private static bool CanTakeDatabaseOwnership(BatchContext batch, Database target)
    {
        var connection = batch.Connection;
        if (PermissionEnforcement.Bypasses(connection, target))
            return true;
        if (!PermissionEnforcement.TryResolveCrossDatabasePrincipal(connection, target, out var principal))
            return false;
        return principal.PrincipalId == Database.DboPrincipalId
            || PermissionChecker.IsGranted(target, principal.PrincipalId, Permission.TakeOwnership, PermissionChecker.ClassDatabase, 0, 0);
    }

    private static void ChangeDatabaseOwner(ParserContext context, Database target, string? loginName)
    {
        var simulation = context.Connection.Simulation;
        if (loginName is null)
            throw SimulatedSqlException.CannotFindUser("SCHEMA OWNER");
        if (BuiltInToken.Equals(target.Name, "master") || BuiltInToken.Equals(target.Name, ModelDatabaseName) || BuiltInToken.Equals(target.Name, "tempdb"))
            throw SimulatedSqlException.CannotChangeSystemDatabaseOwner();
        string canonical;
        if (BuiltInToken.Equals(loginName, "sa"))
            canonical = "sa";
        else if (simulation.Logins.TryGetValue(loginName, out var login))
            canonical = login.Name;
        else if (BuiltInToken.Equals(loginName, "public") || simulation.TryResolveServerRole(loginName, out _, out _))
            throw SimulatedSqlException.DatabaseCannotBeOwnedByRole();
        else
            throw SimulatedSqlException.CannotFindSecurable("principal", loginName);
        // A caller short of dbo needs IMPERSONATE on the new owner's login —
        // a db_owner member included, sa included — and is refused as though
        // the login didn't exist, ahead of the Msg 15110 check (probed
        // 2026-09-27 against SQL Server 2025).
        var security = context.Connection.Security;
        if (!security.EffectiveIsDbo
            && !(simulation.TryResolveServerPrincipalId(canonical, out var ownerId)
                && simulation.HoldsServerPrincipalPermission(security.Effective.LoginName, ownerId, Permission.Impersonate, Permission.ImpersonateAnyLogin)))
        {
            throw SimulatedSqlException.CannotFindSecurable("principal", loginName);
        }
        foreach (var (_, principal) in target.Principals)
        {
            if (principal.LoginName is { } mapped && target.Collation.Equals(mapped, canonical))
                throw SimulatedSqlException.ProposedDatabaseOwnerIsUser();
        }
        var previous = target.OwnerLoginName;
        target.OwnerLoginName = canonical;
        RecordDdlUndo(context, () => target.OwnerLoginName = previous);
        // The event fires in the session's database whichever database changed
        // hands, with an empty SchemaName (probed 2026-09-27 against SQL Server
        // 2025).
        RecordDdlEvent(context, "ALTER_AUTHORIZATION_DATABASE", "", target.Name, "DATABASE", ownerName: canonical);
    }

    /// <summary>
    /// <c>sp_changedbowner @loginame [, @map]</c>: <c>ALTER AUTHORIZATION ON
    /// DATABASE::</c> for the current database. A missing <c>@loginame</c> is
    /// Msg 201, a NULL one a silent no-op, and <c>@map</c> is read and ignored
    /// (probed 2026-09-27 against SQL Server 2025, where the proc's refusals
    /// carry line 1 and no procedure attribution).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpChangeDbOwner(BatchContext batch)
    {
        const string procLabel = "sp_changedbowner";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        string[] parameters = ["loginame", "map"];
        var values = new SqlValue?[parameters.Length];
        for (var i = 0; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            if (arg.Name is null && i >= parameters.Length)
                throw SimulatedSqlException.TooManyArgumentsToFunction(procLabel);
            var slot = Array.FindIndex(parameters, p => BuiltInToken.Equals(arg.Name ?? parameters[i], p));
            if (slot < 0)
                throw SimulatedSqlException.NotAParameterForProcedure(arg.Name!, procLabel);
            values[slot] = arg.Value;
        }
        if (values[0] is not { } login)
            throw SimulatedSqlException.ProcedureExpectsParameter(procLabel, parameters[0]);
        if (login.IsNull)
            yield break;
        if (!CanTakeDatabaseOwnership(batch, batch.CurrentDatabase))
            throw SimulatedSqlException.CannotFindSecurable("database", batch.CurrentDatabase.Name);
        try
        {
            ChangeDatabaseOwner(batch.Parser, batch.CurrentDatabase, login.CoerceTo(SqlType.SystemName).AsString);
        }
        catch (SimulatedSqlException refusal)
        {
            // Raised from inside the proc's own body, at its line 1.
            throw refusal.PinLine(1);
        }
    }
}
