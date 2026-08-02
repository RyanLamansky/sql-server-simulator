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
    /// constraint has no permissions of its own.
    /// </summary>
    internal readonly record struct ConstraintReference(int ObjectId, string Name, string TypeCode, HeapTable Table, Schema Schema);

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
        foreach (var table in schema.HeapTables.Values)
        {
            foreach (var reference in Constraints(table, schema))
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
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                foreach (var reference in Constraints(table, schema))
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
                yield return new(df.ObjectId, df.Name, "D", table, schema);
        }
        foreach (var check in table.CheckConstraints)
            yield return new(check.ObjectId, check.Name, "C", table, schema);
        foreach (var foreignKey in table.OutgoingForeignKeys)
            yield return new(foreignKey.ObjectId, foreignKey.Name, "F", table, schema);
    }
}
