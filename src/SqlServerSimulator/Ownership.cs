using SqlServerSimulator.Schemas;

namespace SqlServerSimulator;

/// <summary>
/// Who owns a securable. An object, type or XML schema collection with no
/// explicit owner is owned by its schema's owner; a trigger or constraint is
/// owned through its parent. The answers feed <c>OBJECTPROPERTY(…, 'OwnerId')</c>,
/// the permission checker's owner path, ownership chaining and <c>WITH EXECUTE
/// AS OWNER</c>, so they must agree with each other.
/// </summary>
internal static class Ownership
{
    /// <summary>The principal that owns <paramref name="obj"/> — its explicit owner, else its schema's; a trigger answers for its parent.</summary>
    internal static int EffectiveOwnerId(Database database, SchemaObject obj) =>
        obj is Trigger trigger
            ? EffectiveOwnerId(database, trigger.Parent)
            : obj.OwnerPrincipalId ?? SchemaOwnerId(database, obj.SchemaId);

    /// <summary>
    /// <see cref="EffectiveOwnerId(Database, SchemaObject)"/> by object id, or
    /// null when <paramref name="objectId"/> names no user object in
    /// <paramref name="database"/>. A linear walk, which only the restricted-
    /// principal paths reach.
    /// </summary>
    internal static int? EffectiveOwnerId(Database database, int objectId) =>
        Parser.Expressions.ObjectProperty.FindObject(database, objectId) is { } obj
            ? EffectiveOwnerId(database, obj)
            : null;

    /// <summary>The principal that owns an alias type — its explicit owner, else its schema's.</summary>
    internal static int EffectiveOwnerId(AliasType type) =>
        type.OwnerPrincipalId ?? type.Schema.PrincipalId;

    /// <summary>
    /// The owner of the schema with id <paramref name="schemaId"/>: a
    /// materialized schema's own owner, a fixed-role schema's moved owner or
    /// the like-id role, and <c>dbo</c> for anything else.
    /// </summary>
    internal static int SchemaOwnerId(Database database, int schemaId)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            if (schema.SchemaId == schemaId)
                return schema.PrincipalId;
        }
        return database.FixedRoleSchemaOwners.TryGetValue(schemaId, out var moved) ? moved
            : schemaId is Database.GuestPrincipalId or >= 16384 ? schemaId
            : Database.DboPrincipalId;
    }

    /// <summary>
    /// Refuses to drop a principal that still owns something, in real's order
    /// (probed 2026-09-27 against SQL Server 2025): an object, then a type, a
    /// schema, a role (itself included), an XML schema collection and a
    /// full-text catalog. Only an explicit object owner counts — an object
    /// owned through its schema answers to the schema.
    /// </summary>
    internal static void RejectDropOfOwner(Database database, int principalId)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var obj in schema.SchemaObjects())
            {
                if (obj.OwnerPrincipalId == principalId)
                    throw SimulatedSqlException.PrincipalOwnsObjects();
            }
        }
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, type) in schema.TableTypes)
            {
                if (type.OwnerPrincipalId == principalId)
                    throw SimulatedSqlException.PrincipalOwnsTypes();
            }
            foreach (var (_, type) in schema.AliasTypes)
            {
                if (type.OwnerPrincipalId == principalId)
                    throw SimulatedSqlException.PrincipalOwnsTypes();
            }
        }
        foreach (var (_, schema) in database.Schemas)
        {
            if (schema.PrincipalId == principalId)
                throw SimulatedSqlException.PrincipalOwnsASchema();
        }
        foreach (var owner in database.FixedRoleSchemaOwners.Values)
        {
            if (owner == principalId)
                throw SimulatedSqlException.PrincipalOwnsASchema();
        }
        foreach (var (_, principal) in database.Principals)
        {
            if (principal.TypeCode == "R" && principal.OwningPrincipalId == principalId)
                throw SimulatedSqlException.PrincipalOwnsRole();
        }
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, collection) in schema.XmlSchemaCollections)
            {
                if (collection.PrincipalId == principalId)
                    throw SimulatedSqlException.PrincipalOwnsA("a XML namespace");
            }
        }
        foreach (var (_, catalog) in database.FullTextCatalogs)
        {
            if (catalog.PrincipalId == principalId)
                throw SimulatedSqlException.PrincipalOwnsA("a fulltext catalog");
        }
    }

    /// <summary>The <c>principal_id</c> a catalog view projects for an explicit owner — NULL when ownership follows the schema.</summary>
    internal static Storage.SqlValue PrincipalIdValue(int? explicitOwner) =>
        explicitOwner is int owner ? Storage.SqlValue.FromInt32(owner) : Storage.SqlValue.Null(Storage.SqlType.Int32);

    /// <summary>The name <paramref name="principalId"/> carries in <paramref name="database"/>, or null when no principal has it.</summary>
    internal static string? PrincipalName(Database database, int principalId)
    {
        foreach (var (_, principal) in database.Principals)
        {
            if (principal.PrincipalId == principalId)
                return principal.Name;
        }
        return null;
    }

    /// <summary>
    /// The server identity <paramref name="principal"/> stands for — what a
    /// token made from it reports through <c>SYSTEM_USER</c> and names in
    /// another database: <c>dbo</c> is the database owner's login (probed
    /// 2026-09-27 against SQL Server 2025), everyone else their own login or
    /// SID.
    /// </summary>
    internal static string LoginIdentity(Database database, DatabasePrincipal principal) =>
        principal.PrincipalId == Database.DboPrincipalId ? database.OwnerLoginName : principal.EffectiveLoginIdentity;

    /// <summary>
    /// The login <paramref name="principalId"/> maps to in
    /// <paramref name="database"/> — the database owner's for <c>dbo</c>, the
    /// mapped login for a <c>FOR LOGIN</c> user — or null for a principal with
    /// none, which therefore shares an owner with nothing across a database
    /// boundary.
    /// </summary>
    internal static string? OwnerLogin(Database database, int principalId)
    {
        if (principalId == Database.DboPrincipalId)
            return database.OwnerLoginName;
        foreach (var (_, principal) in database.Principals)
        {
            if (principal.PrincipalId == principalId)
                return principal.LoginName;
        }
        return null;
    }

    /// <summary>
    /// The SID <c>sys.databases.owner_sid</c> and <c>dbo</c>'s
    /// <c>sys.database_principals.sid</c> report: <c>sa</c>'s well-known
    /// <c>0x01</c>, or the owning login's derived SID.
    /// </summary>
    internal static byte[] OwnerSid(Database database) =>
        BuiltInToken.Comparer.Equals(database.OwnerLoginName, "sa")
            ? [0x01]
            : BuiltInResources.DeriveLoginSid(database.OwnerLoginName);
}
