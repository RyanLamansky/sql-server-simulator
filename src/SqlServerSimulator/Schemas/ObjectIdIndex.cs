using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// A database's object ids — its schema objects, its Service Broker system
/// queues, its table types and its constraints, the last by name too — indexed
/// for the object scalars (<c>OBJECTPROPERTY</c>, <c>OBJECT_ID</c>,
/// <c>OBJECT_NAME</c>, <c>COLUMNPROPERTY</c> …), which otherwise walk every
/// object of every schema per call, so a catalog query calling one per row was
/// quadratic in the database's objects. Kept and dropped with
/// <see cref="CatalogRowCache"/>'s rowsets, whose invalidation rides every
/// change to what <c>sys.objects</c> lists; Debug builds check each answer
/// against the walk, so a change that misses the invalidation fails the test
/// that reads it.
/// </summary>
internal sealed class ObjectIdIndex
{
    private readonly Dictionary<int, (SchemaObject Object, Schema Schema)> schemaObjects = [];
    private readonly Dictionary<int, SchemaObject> systemQueues = [];
    private readonly Dictionary<int, (TableType Type, Schema Schema)> tableTypes = [];
    private readonly Dictionary<int, ConstraintLookup.ConstraintReference> constraints = [];
    private readonly Dictionary<Schema, Dictionary<string, ConstraintLookup.ConstraintReference>> constraintNames = [];

    public ObjectIdIndex(Database database)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var obj in schema.SchemaObjects())
                _ = this.schemaObjects.TryAdd(obj.ObjectId, (obj, schema));
            foreach (var (_, tableType) in schema.TableTypes)
                _ = this.tableTypes.TryAdd(tableType.ObjectId, (tableType, schema));
            var named = new Dictionary<string, ConstraintLookup.ConstraintReference>(database.Collation);
            foreach (var constraint in ConstraintLookup.NamedIn(schema))
                _ = named.TryAdd(constraint.Name, constraint);
            this.constraintNames[schema] = named;
        }
        if (database.ServiceBrokerIfUsed is { } broker)
        {
            foreach (var queue in broker.SystemQueues)
                _ = this.systemQueues.TryAdd(queue.ObjectId, queue);
        }
        foreach (var constraint in ConstraintLookup.All(database))
            _ = this.constraints.TryAdd(constraint.ObjectId, constraint);
    }

    /// <summary>
    /// <paramref name="database"/>'s index as <paramref name="batch"/> reads
    /// it, or null where the catalog row cache serves no rowset either:
    /// <c>tempdb</c>, whose objects include the reading session's own, and a
    /// database the simulation doesn't hold.
    /// </summary>
    private static ObjectIdIndex? For(BatchContext batch, Database database)
    {
        if (database.Name == Simulation.TempdbDatabaseName)
            return null;
        var simulation = batch.Connection.Simulation;
        return simulation.Databases.TryGetValue(database.Name, out var held) && ReferenceEquals(held, database)
            ? simulation.CatalogRows.ObjectIds(database)
            : null;
    }

    /// <summary>The schema object or Service Broker system queue with object id <paramref name="id"/>: <see cref="ObjectProperty.FindObject(Database, int)"/>, through the index where one serves.</summary>
    public static SchemaObject? FindObject(BatchContext batch, Database database, int id)
    {
        if (For(batch, database) is not { } index)
            return ObjectProperty.FindObject(database, id);
        var found = index.schemaObjects.TryGetValue(id, out var entry) ? entry.Object : index.systemQueues.GetValueOrDefault(id);
#if DEBUG
        if (!ReferenceEquals(found, ObjectProperty.FindObject(database, id)))
            throw Stale(database);
#endif
        return found;
    }

    /// <summary>
    /// The schema object with object id <paramref name="id"/> and the schema
    /// holding it, as a walk of every schema's
    /// <see cref="Schema.SchemaObjects"/> finds it — no system queue.
    /// </summary>
    public static bool TryFindSchemaObject(BatchContext batch, Database database, int id, out SchemaObject obj, out Schema schema)
    {
        if (For(batch, database) is not { } index)
            return WalkSchemaObjects(database, id, out obj, out schema);
        var found = index.schemaObjects.TryGetValue(id, out var entry);
        (obj, schema) = entry;
#if DEBUG
        if (found != WalkSchemaObjects(database, id, out var walked, out var walkedSchema) || (found && (!ReferenceEquals(obj, walked) || !ReferenceEquals(schema, walkedSchema))))
            throw Stale(database);
#endif
        return found;
    }

    /// <summary>The table type whose backing type table carries object id <paramref name="id"/>, and the schema holding it.</summary>
    public static bool TryFindTableType(BatchContext batch, Database database, int id, out TableType type, out Schema schema)
    {
        if (For(batch, database) is not { } index)
            return WalkTableTypes(database, id, out type, out schema);
        var found = index.tableTypes.TryGetValue(id, out var entry);
        (type, schema) = entry;
#if DEBUG
        if (found != WalkTableTypes(database, id, out var walked, out var walkedSchema) || (found && (!ReferenceEquals(type, walked) || !ReferenceEquals(schema, walkedSchema))))
            throw Stale(database);
#endif
        return found;
    }

    /// <summary>The constraint with object id <paramref name="id"/>: <see cref="ConstraintLookup.TryResolveById"/>, through the index where one serves.</summary>
    public static bool TryFindConstraint(BatchContext batch, Database database, int id, out ConstraintLookup.ConstraintReference constraint)
    {
        if (For(batch, database) is not { } index)
            return ConstraintLookup.TryResolveById(database, id, out constraint);
        var found = index.constraints.TryGetValue(id, out constraint);
#if DEBUG
        if (found != ConstraintLookup.TryResolveById(database, id, out var walked) || (found && !SameConstraint(constraint, walked)))
            throw Stale(database);
#endif
        return found;
    }

    /// <summary>
    /// The constraint of <paramref name="schema"/> named <paramref name="name"/>
    /// under the database's collation, as <see cref="ConstraintLookup.NamedIn"/>
    /// lists them, through the index where one serves.
    /// </summary>
    public static bool TryFindConstraintNamed(BatchContext batch, Schema schema, string name, out ConstraintLookup.ConstraintReference constraint)
    {
        if (For(batch, schema.Database) is not { } index || !index.constraintNames.TryGetValue(schema, out var named))
            return WalkConstraintNames(schema, name, out constraint);
        var found = named.TryGetValue(name, out constraint);
#if DEBUG
        if (found != WalkConstraintNames(schema, name, out var walked) || (found && !SameConstraint(constraint, walked)))
            throw Stale(schema.Database);
#endif
        return found;
    }

    private static bool WalkSchemaObjects(Database database, int id, out SchemaObject obj, out Schema schema)
    {
        foreach (var (_, candidateSchema) in database.Schemas)
        {
            foreach (var candidate in candidateSchema.SchemaObjects())
            {
                if (candidate.ObjectId == id)
                {
                    (obj, schema) = (candidate, candidateSchema);
                    return true;
                }
            }
        }
        (obj, schema) = (null!, null!);
        return false;
    }

    private static bool WalkTableTypes(Database database, int id, out TableType type, out Schema schema)
    {
        foreach (var (_, candidateSchema) in database.Schemas)
        {
            foreach (var (_, candidate) in candidateSchema.TableTypes)
            {
                if (candidate.ObjectId == id)
                {
                    (type, schema) = (candidate, candidateSchema);
                    return true;
                }
            }
        }
        (type, schema) = (null!, null!);
        return false;
    }

    private static bool WalkConstraintNames(Schema schema, string name, out ConstraintLookup.ConstraintReference constraint)
    {
        foreach (var candidate in ConstraintLookup.NamedIn(schema))
        {
            if (schema.Database.Collation.Equals(candidate.Name, name))
            {
                constraint = candidate;
                return true;
            }
        }
        constraint = default;
        return false;
    }

#if DEBUG
    private static bool SameConstraint(ConstraintLookup.ConstraintReference a, ConstraintLookup.ConstraintReference b) =>
        a.ObjectId == b.ObjectId && a.Name == b.Name && a.TypeCode == b.TypeCode && ReferenceEquals(a.Table, b.Table) && ReferenceEquals(a.Schema, b.Schema) && a.Definition == b.Definition;

    private static InvalidOperationException Stale(Database database) =>
        new($"The object id index of {database.Name} is stale: a change to what sys.objects lists did not invalidate the catalog row cache.");
#endif
}
