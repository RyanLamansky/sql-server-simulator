using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// Which <see cref="Database.ExtendedProperties"/> entries still have a
/// target, as of one snapshot of the database's schema. Real removes an
/// extended property with whatever it describes — a table, column, index,
/// constraint, trigger, parameter, type, user — along every drop path
/// (probed 2026-09-26 against SQL Server 2025); the store keeps the entry and
/// every reader filters it through here instead, which covers each of those
/// paths and a rolled-back drop alike.
/// </summary>
internal sealed class ExtendedPropertyTargets
{
    private readonly Dictionary<int, object> objects = [];
    private readonly HashSet<int> schemaIds = [];
    private readonly HashSet<int> principalIds = [];
    private readonly Dictionary<int, TableType?> userTypes = [];
    private readonly HashSet<int> xmlSchemaCollectionIds = [];
    private readonly HashSet<int> dataSpaceIds = [];

    public ExtendedPropertyTargets(Database database)
    {
        foreach (var schema in database.Schemas.Values)
        {
            _ = this.schemaIds.Add(schema.SchemaId);
            foreach (var obj in schema.SchemaObjects())
            {
                this.objects[obj.ObjectId] = obj;
                if (obj is not HeapTable table)
                    continue;
                foreach (var key in table.KeyConstraints)
                    this.objects[key.ObjectId] = key;
                foreach (var check in table.CheckConstraints)
                    this.objects[check.ObjectId] = check;
                foreach (var foreignKey in table.OutgoingForeignKeys)
                    this.objects[foreignKey.ObjectId] = foreignKey;
                foreach (var column in table.Columns)
                {
                    if (column.DefaultConstraint is { } defaultConstraint)
                        this.objects[defaultConstraint.ObjectId] = defaultConstraint;
                }
            }
            foreach (var tableType in schema.TableTypes.Values)
                this.userTypes[tableType.UserTypeId] = tableType;
            foreach (var aliasType in schema.AliasTypes.Values)
                this.userTypes[aliasType.UserTypeId] = null;
            foreach (var collection in schema.XmlSchemaCollections.Values)
                _ = this.xmlSchemaCollectionIds.Add(collection.Id);
        }
        foreach (var ddlTrigger in database.DdlTriggers.Values)
            this.objects[ddlTrigger.ObjectId] = ddlTrigger;
        foreach (var principal in database.Principals.Values)
            _ = this.principalIds.Add(principal.PrincipalId);
        foreach (var dataSpaceId in database.Filegroups.Values)
            _ = this.dataSpaceIds.Add(dataSpaceId);
    }

    /// <summary>Whether <paramref name="key"/>'s target still exists.</summary>
    public bool IsLive(ExtendedPropertyKey key) => key.Class switch
    {
        0 => true,
        1 => this.objects.TryGetValue(key.MajorId, out var obj) && (key.MinorId == 0 || HasColumn(obj, key.MinorId)),
        2 => this.objects.TryGetValue(key.MajorId, out var routine) && key.MinorId <= routine switch
        {
            Procedure procedure => procedure.Parameters.Length,
            UserDefinedFunction function => function.Parameters.Length,
            _ => 0,
        },
        3 => this.schemaIds.Contains(key.MajorId),
        4 => this.principalIds.Contains(key.MajorId),
        6 => this.userTypes.ContainsKey(key.MajorId),
        7 => this.objects.TryGetValue(key.MajorId, out var indexed) && HasIndex(indexed, key.MinorId),
        8 => this.userTypes.TryGetValue(key.MajorId, out var tableType) && tableType is not null && key.MinorId <= tableType.Columns.Length,
        10 => this.xmlSchemaCollectionIds.Contains(key.MajorId),
        20 => this.dataSpaceIds.Contains(key.MajorId),
        _ => true,
    };

    // A table's columns are addressed by their stable column_id, a view's or
    // table-valued function's by position.
    private static bool HasColumn(object obj, int minorId)
    {
        switch (obj)
        {
            case HeapTable table:
                foreach (var column in table.Columns)
                {
                    if (column.ColumnId == minorId)
                        return true;
                }
                return false;
            case View view:
                return minorId <= view.OutputColumns.Length;
            case InlineTableValuedFunction inline:
                return minorId <= inline.OutputColumns.Length;
            case MultiStatementTableValuedFunction multi:
                return minorId <= multi.OutputColumns.Length;
            default:
                return false;
        }
    }

    private static bool HasIndex(object obj, int indexId)
    {
        var identities = obj switch
        {
            HeapTable table => table.IndexIdentities(),
            View view => view.IndexIdentities(),
            _ => null,
        };
        if (identities is null)
            return false;
        foreach (var identity in identities)
        {
            if (identity.IndexId == indexId && !identity.IsHeap)
                return true;
        }
        return obj is HeapTable withXmlOrSpatial
            && (withXmlOrSpatial.XmlIndexes.Exists(index => index.IndexId == indexId)
                || withXmlOrSpatial.SpatialIndexes.Exists(index => index.IndexId == indexId));
    }
}
