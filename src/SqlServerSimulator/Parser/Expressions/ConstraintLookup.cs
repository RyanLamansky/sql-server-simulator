using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Shared lookup machinery for constraint objects — the <c>C</c> / <c>D</c> /
/// <c>PK</c> / <c>UQ</c> / <c>F</c> rows <c>sys.objects</c> projects under a
/// table's <c>parent_object_id</c>. A constraint isn't a
/// <see cref="Schemas.SchemaObject"/> (its identity hangs off the owning
/// <see cref="HeapTable"/>), so the object scalars can't reach one through the
/// schema dictionaries and go through here instead: <c>OBJECT_ID</c> resolves a
/// constraint name the way real does, and <c>OBJECT_NAME</c> /
/// <c>OBJECT_SCHEMA_NAME</c> / <c>OBJECTPROPERTY</c> read the id back.
/// </summary>
/// <remarks>
/// Real scopes a constraint name to the schema of the table that owns it
/// (probe-confirmed: a constraint on a table in schema <c>s</c> answers only
/// through <c>OBJECT_ID('s.<i>name</i>')</c>, never unqualified from
/// <c>dbo</c>), which is what makes the by-name walk a per-schema one.
/// </remarks>
internal static class ConstraintLookup
{
    /// <summary>
    /// One constraint's catalog identity: its own object id and name, the
    /// trimmed <c>sys.objects</c> type code (<c>C</c> / <c>D</c> / <c>PK</c> /
    /// <c>UQ</c> / <c>F</c>), plus the table it hangs off and that table's
    /// schema — the table is the metadata-visibility governor, since a
    /// constraint has no permissions of its own. <see cref="Definition"/> is the
    /// stored expression text a CHECK or DEFAULT carries, and null for the key
    /// and reference families, which have none.
    /// </summary>
    internal readonly struct ConstraintReference(int objectId, string name, string typeCode, HeapTable table, Schema schema, string? definition = null)
    {
        public readonly int ObjectId = objectId;
        public readonly string Name = name;
        public readonly string TypeCode = typeCode;
        public readonly HeapTable Table = table;
        public readonly Schema Schema = schema;
        public readonly string? Definition = definition;
    }

    /// <summary>
    /// Resolves a 1- to 3-part constraint name against the schema it names
    /// (<see cref="Database.DefaultSchemaName"/> when unqualified), scanning
    /// that schema's tables for a constraint of any family.
    /// </summary>
    public static bool TryResolveByName(BatchContext batch, MultiPartName name, out ConstraintReference found)
    {
        found = default;
        if (!batch.TryResolveSchema(name, out var schema))
            return false;
        var collation = schema.Database.Collation;
        foreach (var (table, owner) in ConstraintOwners(schema, includeTableTypes: false))
        {
            foreach (var reference in Constraints(table, owner))
            {
                if (collation.Equals(reference.Name, name.Leaf))
                {
                    found = reference;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Resolves a constraint's object id across every schema of
    /// <paramref name="database"/> — the reverse of
    /// <see cref="TryResolveByName"/>.
    /// </summary>
    public static bool TryResolveById(Database database, int objectId, out ConstraintReference found)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (table, owner) in ConstraintOwners(schema, includeTableTypes: true))
            {
                foreach (var reference in Constraints(table, owner))
                {
                    if (reference.ObjectId == objectId)
                    {
                        found = reference;
                        return true;
                    }
                }
            }
        }
        found = default;
        return false;
    }

    /// <summary>
    /// Every table shape declared in <paramref name="schema"/> whose
    /// constraints are catalog objects, with the schema those constraints
    /// live in: the tables, a multi-statement function's return table, and a
    /// table type's backing table, whose constraints sit in <c>sys</c> — real
    /// resolves the last two's constraint ids through the object scalars as it
    /// does a table's (probed 2026-10-02 against SQL Server 2025). A table
    /// type's constraint names don't resolve through the type's own schema,
    /// so the by-name walk leaves them out.
    /// </summary>
    private static IEnumerable<(HeapTable Table, Schema Owner)> ConstraintOwners(Schema schema, bool includeTableTypes)
    {
        foreach (var (_, table) in schema.HeapTables)
            yield return (table, schema);
        foreach (var (_, function) in schema.Functions)
        {
            if (function is Schemas.MultiStatementTableValuedFunction multiStatement)
                yield return (multiStatement.CatalogShape(), schema);
        }
        if (!includeTableTypes)
            yield break;
        foreach (var (_, tableType) in schema.TableTypes)
            yield return (tableType.CatalogShape, schema.Database.Schemas["sys"]);
    }

    /// <summary>
    /// Every constraint <paramref name="table"/> owns, in the order
    /// <c>sys.objects</c> emits them.
    /// </summary>
    private static IEnumerable<ConstraintReference> Constraints(HeapTable table, Schema schema)
    {
        foreach (var key in table.KeyConstraints)
            yield return new(key.ObjectId, key.Name, key.Kind == KeyConstraintKind.PrimaryKey ? "PK" : "UQ", table, schema);
        foreach (var column in table.Columns)
        {
            if (column.DefaultConstraint is { } df)
                yield return new(df.ObjectId, df.Name, "D", table, schema, df.Definition);
        }
        foreach (var check in table.CheckConstraints)
            yield return new(check.ObjectId, check.Name, "C", table, schema, check.Definition);
        foreach (var foreignKey in table.OutgoingForeignKeys)
            yield return new(foreignKey.ObjectId, foreignKey.Name, "F", table, schema);
        foreach (var edge in table.EdgeConstraints)
            yield return new(edge.ObjectId, edge.Name, "EC", table, schema);
    }
}
