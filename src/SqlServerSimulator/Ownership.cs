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
        foreach (var schema in database.Schemas.Values)
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
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var obj in schema.SchemaObjects())
            {
                if (obj.OwnerPrincipalId == principalId)
                    throw SimulatedSqlException.PrincipalOwnsObjects();
            }
        }
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var type in schema.TableTypes.Values)
            {
                if (type.OwnerPrincipalId == principalId)
                    throw SimulatedSqlException.PrincipalOwnsTypes();
            }
            foreach (var type in schema.AliasTypes.Values)
            {
                if (type.OwnerPrincipalId == principalId)
                    throw SimulatedSqlException.PrincipalOwnsTypes();
            }
        }
        foreach (var schema in database.Schemas.Values)
        {
            if (schema.PrincipalId == principalId)
                throw SimulatedSqlException.PrincipalOwnsASchema();
        }
        foreach (var owner in database.FixedRoleSchemaOwners.Values)
        {
            if (owner == principalId)
                throw SimulatedSqlException.PrincipalOwnsASchema();
        }
        foreach (var principal in database.Principals.Values)
        {
            if (principal.TypeCode == "R" && principal.OwningPrincipalId == principalId)
                throw SimulatedSqlException.PrincipalOwnsRole();
        }
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var collection in schema.XmlSchemaCollections.Values)
            {
                if (collection.PrincipalId == principalId)
                    throw SimulatedSqlException.PrincipalOwnsA("a XML namespace");
            }
        }
        foreach (var catalog in database.FullTextCatalogs.Values)
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
        foreach (var principal in database.Principals.Values)
        {
            if (principal.PrincipalId == principalId)
                return principal.Name;
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
