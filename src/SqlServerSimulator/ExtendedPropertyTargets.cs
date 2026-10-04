using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// Which <see cref="Database.ExtendedProperties"/> entries still have a
/// target, and the level chain each one's target sits at, as of one snapshot
/// of the database's schema. Real removes an extended property with whatever
/// it describes — a table, column, index, constraint, trigger, parameter,
/// type, user — along every drop path (probed 2026-09-26 against SQL Server
/// 2025); the store keeps the entry and every reader filters it through here
/// instead, which covers each of those paths and a rolled-back drop alike.
/// </summary>
internal sealed class ExtendedPropertyTargets
{
    // An object id's target, with the table or view a constraint or DML
    // trigger hangs off.
    private readonly Dictionary<int, (object Target, SchemaObject? Owner)> objects = [];
    private readonly Dictionary<int, string> schemaNames = [];
    private readonly Dictionary<int, string> principalNames = [];
    private readonly Dictionary<int, (string Schema, string Name, TableType? Table)> userTypes = [];
    private readonly Dictionary<int, (string Schema, string Name)> xmlSchemaCollections = [];
    private readonly Dictionary<int, string> dataSpaceNames = [];

    public ExtendedPropertyTargets(Database database)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            this.schemaNames[schema.SchemaId] = schema.Name;
            foreach (var obj in schema.SchemaObjects())
            {
                this.objects[obj.ObjectId] = (obj, obj is Trigger trigger ? trigger.Parent : null);
                if (obj is not HeapTable table)
                    continue;
                foreach (var key in table.KeyConstraints)
                    this.objects[key.ObjectId] = (key, table);
                foreach (var check in table.CheckConstraints)
                    this.objects[check.ObjectId] = (check, table);
                foreach (var foreignKey in table.OutgoingForeignKeys)
                    this.objects[foreignKey.ObjectId] = (foreignKey, table);
                foreach (var column in table.Columns)
                {
                    if (column.DefaultConstraint is { } defaultConstraint)
                        this.objects[defaultConstraint.ObjectId] = (defaultConstraint, table);
                }
            }
            foreach (var (_, tableType) in schema.TableTypes)
                this.userTypes[tableType.UserTypeId] = (schema.Name, tableType.Name, tableType);
            foreach (var (_, aliasType) in schema.AliasTypes)
                this.userTypes[aliasType.UserTypeId] = (schema.Name, aliasType.Name, null);
            foreach (var (_, collection) in schema.XmlSchemaCollections)
                this.xmlSchemaCollections[collection.Id] = (schema.Name, collection.Name);
        }
        foreach (var (_, ddlTrigger) in database.DdlTriggers)
            this.objects[ddlTrigger.ObjectId] = (ddlTrigger, null);
        foreach (var (_, principal) in database.Principals)
            this.principalNames[principal.PrincipalId] = principal.Name;
        foreach (var (name, dataSpaceId) in database.Filegroups)
            this.dataSpaceNames[dataSpaceId] = name;
    }

    /// <summary>Whether <paramref name="key"/>'s target still exists.</summary>
    public bool IsLive(ExtendedPropertyKey key) => this.TryDescribe(key, out _);

    /// <summary>
    /// The <c>(level type, name)</c> chain <paramref name="key"/>'s target
    /// sits at, as the extended-property procedures address it — empty for the
    /// database itself — or false when the target is gone.
    /// </summary>
    public bool TryDescribe(ExtendedPropertyKey key, out (string Type, string Name)[] chain)
    {
        chain = [];
        switch (key.Class)
        {
            case 0:
                return true;
            case 1:
                if (!this.objects.TryGetValue(key.MajorId, out var entry))
                    return false;
                switch (entry.Target)
                {
                    case DdlTrigger ddlTrigger:
                        chain = [("TRIGGER", ddlTrigger.Name)];
                        return true;
                    case Trigger trigger when entry.Owner is { } parent:
                        chain = [.. this.ObjectChain(parent), ("TRIGGER", trigger.Name)];
                        return true;
                    case SchemaObject obj when key.MinorId == 0:
                        chain = this.ObjectChain(obj);
                        return true;
                    case SchemaObject obj when ColumnName(obj, key.MinorId) is { } column:
                        chain = [.. this.ObjectChain(obj), ("COLUMN", column)];
                        return true;
                    case SchemaObject:
                        return false;
                    default:
                        chain = [.. this.ObjectChain(entry.Owner!), ("CONSTRAINT", ConstraintName(entry.Target))];
                        return true;
                }
            case 2:
                if (!this.objects.TryGetValue(key.MajorId, out var routine) || routine.Target is not SchemaObject routineObject)
                    return false;
                var parameterName = routine.Target switch
                {
                    Procedure procedure when key.MinorId <= procedure.Parameters.Length => procedure.Parameters[key.MinorId - 1].Name,
                    UserDefinedFunction function when key.MinorId <= function.Parameters.Length => function.Parameters[key.MinorId - 1].Name,
                    _ => null,
                };
                if (parameterName is null)
                    return false;
                chain = [.. this.ObjectChain(routineObject), ("PARAMETER", "@" + parameterName)];
                return true;
            case 3:
                if (!this.schemaNames.TryGetValue(key.MajorId, out var schemaName))
                    return false;
                chain = [("SCHEMA", schemaName)];
                return true;
            case 4:
                if (!this.principalNames.TryGetValue(key.MajorId, out var userName))
                    return false;
                chain = [("USER", userName)];
                return true;
            case 6:
                if (!this.userTypes.TryGetValue(key.MajorId, out var type))
                    return false;
                chain = [("SCHEMA", type.Schema), ("TYPE", type.Name)];
                return true;
            case 7:
                if (!this.objects.TryGetValue(key.MajorId, out var indexed) || indexed.Target is not SchemaObject indexedObject || IndexName(indexedObject, key.MinorId) is not { } indexName)
                    return false;
                chain = [.. this.ObjectChain(indexedObject), ("INDEX", indexName)];
                return true;
            case 8:
                if (!this.userTypes.TryGetValue(key.MajorId, out var tableType) || tableType.Table is null || key.MinorId > tableType.Table.Columns.Length)
                    return false;
                chain = [("SCHEMA", tableType.Schema), ("TYPE", tableType.Name), ("COLUMN", tableType.Table.Columns[key.MinorId - 1].Name)];
                return true;
            case 10:
                if (!this.xmlSchemaCollections.TryGetValue(key.MajorId, out var collection))
                    return false;
                chain = [("SCHEMA", collection.Schema), ("XML SCHEMA COLLECTION", collection.Name)];
                return true;
            case 20:
                if (!this.dataSpaceNames.TryGetValue(key.MajorId, out var filegroup))
                    return false;
                chain = [("FILEGROUP", filegroup)];
                return true;
            default:
                return false;
        }
    }

    private (string Type, string Name)[] ObjectChain(SchemaObject obj)
    {
        var kind = obj switch
        {
            HeapTable => "TABLE",
            View => "VIEW",
            Procedure => "PROCEDURE",
            UserDefinedFunction => "FUNCTION",
            Sequence => "SEQUENCE",
            Synonym => "SYNONYM",
            RuleObject => "RULE",
            DefaultObject => "DEFAULT",
            SecurityPolicy => "SECURITY POLICY",
            _ => obj.ObjectTypeDescription,
        };
        return [("SCHEMA", this.schemaNames.GetValueOrDefault(obj.SchemaId, "")), (kind, obj.Name)];
    }

    private static string ConstraintName(object constraint) => constraint switch
    {
        KeyConstraint key => key.Name,
        CheckConstraint check => check.Name,
        ForeignKey foreignKey => foreignKey.Name,
        DefaultConstraint defaultConstraint => defaultConstraint.Name,
        _ => "",
    };

    // A table's columns are addressed by their stable column_id, a view's or
    // table-valued function's by position.
    private static string? ColumnName(SchemaObject obj, int minorId)
    {
        var columns = obj switch
        {
            HeapTable table => table.Columns,
            View view => view.OutputColumns,
            InlineTableValuedFunction inline => inline.OutputColumns,
            MultiStatementTableValuedFunction multi => multi.OutputColumns,
            _ => [],
        };
        if (obj is HeapTable)
        {
            foreach (var column in columns)
            {
                if (column.ColumnId == minorId)
                    return column.Name;
            }
            return null;
        }
        return minorId <= columns.Length ? columns[minorId - 1].Name : null;
    }

    private static string? IndexName(SchemaObject obj, int indexId)
    {
        var identities = obj switch
        {
            HeapTable table => table.IndexIdentities(),
            View view => view.IndexIdentities(),
            _ => null,
        };
        if (identities is null)
            return null;
        foreach (var identity in identities)
        {
            if (identity.IndexId == indexId && !identity.IsHeap)
                return identity.Name;
        }
        if (obj is not HeapTable withXmlOrSpatial)
            return null;
        foreach (var xmlIndex in withXmlOrSpatial.XmlIndexes)
        {
            if (xmlIndex.IndexId == indexId)
                return xmlIndex.Name;
        }
        foreach (var spatialIndex in withXmlOrSpatial.SpatialIndexes)
        {
            if (spatialIndex.IndexId == indexId)
                return spatialIndex.Name;
        }
        return null;
    }
}
