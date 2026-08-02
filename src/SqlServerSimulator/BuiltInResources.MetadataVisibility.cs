using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

internal static partial class BuiltInResources
{
    // Object-scoped sys.* catalog views a restricted principal sees filtered to
    // the objects it may view metadata for (probe-confirmed SQL Server 2025,
    // 2026-07-21). Each row's governing object is its object_id (or, when the
    // view has no object_id column, its parent_object_id). Constraint / trigger
    // rows ride into the visible set through their parent object, so keying on
    // the row's own id works uniformly. Views deliberately left unfiltered
    // (broadly visible to restricted principals): sys.schemas / sys.types /
    // sys.databases / the principal / permission / role views / DMVs.
    private static readonly string[] ObjectIdKeyedMetadataViews =
    [
        "sys.all_columns",
        "sys.all_objects",
        "sys.all_parameters",
        "sys.all_sql_modules",
        "sys.all_views",
        "sys.check_constraints",
        "sys.columns",
        "sys.computed_columns",
        "sys.default_constraints",
        "sys.foreign_key_columns",
        "sys.foreign_keys",
        "sys.identity_columns",
        "sys.index_columns",
        "sys.indexes",
        "sys.key_constraints",
        "sys.objects",
        "sys.parameters",
        "sys.procedures",
        "sys.sequences",
        "sys.sql_modules",
        "sys.synonyms",
        "sys.tables",
        "sys.triggers",
        "sys.views",
    ];

    // Name-keyed INFORMATION_SCHEMA object views: the row carries the owning
    // schema + object name instead of an id, so the filter resolves visibility
    // by qualified name. (schemaColumn, objectColumn) name the two cells.
    private static readonly (string Key, string SchemaColumn, string ObjectColumn)[] NameKeyedMetadataViews =
    [
        ("INFORMATION_SCHEMA.COLUMNS", "TABLE_SCHEMA", "TABLE_NAME"),
        ("INFORMATION_SCHEMA.PARAMETERS", "SPECIFIC_SCHEMA", "SPECIFIC_NAME"),
        ("INFORMATION_SCHEMA.ROUTINES", "ROUTINE_SCHEMA", "ROUTINE_NAME"),
        ("INFORMATION_SCHEMA.TABLES", "TABLE_SCHEMA", "TABLE_NAME"),
        ("INFORMATION_SCHEMA.VIEWS", "TABLE_SCHEMA", "TABLE_NAME"),
    ];

    /// <summary>
    /// Stamps each object-scoped catalog view with the row-column geometry its
    /// metadata-visibility filter reads. Called once at catalog-view build time.
    /// A view not listed here keeps <c>MetadataKey == null</c> and is fully
    /// visible to every principal.
    /// </summary>
    private static void ApplyMetadataVisibility(Dictionary<string, CatalogView> views)
    {
        foreach (var key in ObjectIdKeyedMetadataViews)
        {
            if (views.TryGetValue(key, out var view))
                view.MetadataKey = new MetadataVisibilityKey(GoverningObjectIdOrdinal(view), -1, -1);
        }
        foreach (var (key, schemaColumn, objectColumn) in NameKeyedMetadataViews)
        {
            if (views.TryGetValue(key, out var view))
                view.MetadataKey = new MetadataVisibilityKey(-1, OrdinalOf(view, schemaColumn), OrdinalOf(view, objectColumn));
        }
    }

    private static int GoverningObjectIdOrdinal(CatalogView view)
    {
        for (var i = 0; i < view.Columns.Length; i++)
        {
            if (BuiltInToken.Equals(view.Columns[i].Name, "object_id"))
                return i;
        }
        return OrdinalOf(view, "parent_object_id");
    }

    private static int OrdinalOf(CatalogView view, string columnName)
    {
        for (var i = 0; i < view.Columns.Length; i++)
        {
            if (BuiltInToken.Equals(view.Columns[i].Name, columnName))
                return i;
        }
        throw new InvalidOperationException($"Catalog view '{view.Name}' has no column '{columnName}' for metadata-visibility filtering.");
    }

    /// <summary>
    /// Wraps a catalog view's row sequence with metadata-visibility filtering when
    /// the principal answering for <paramref name="targetDatabase"/> is
    /// restricted. Returns <paramref name="rows"/> untouched — zero added cost —
    /// for a <c>dbo</c> / full-visibility session (the overwhelming common case)
    /// or a view that isn't object-scoped.
    /// </summary>
    /// <remarks>
    /// A read of <em>another</em> database resolves the login's user there and
    /// filters by that principal's visibility, exactly as a data reference
    /// resolves it — including the Msg 916 a login with no user there earns,
    /// which real raises for every cross-database catalog view whether or not
    /// the view is one it would have filtered (probe-confirmed against SQL
    /// Server 2025: <c>other.sys.databases</c> and <c>other.sys.types</c> refuse
    /// alongside <c>other.sys.tables</c>). So the resolution runs ahead of the
    /// <see cref="CatalogView.MetadataKey"/> test, and the guest-served system
    /// databases pass it the same way they pass a data read.
    /// </remarks>
    internal static IEnumerable<SqlValue[]> ApplyMetadataFilter(
        CatalogView view,
        BatchContext batch,
        Database targetDatabase,
        IEnumerable<SqlValue[]> rows) =>
        FilteringPrincipal(view, batch, targetDatabase) is not { } principalId || view.MetadataKey is not { } key ? rows
            : key.IsNameKeyed ? FilterByName(rows, key, targetDatabase, principalId)
            : FilterByObjectId(rows, key, targetDatabase, principalId);

    // An unfiltered view of the session's own database asks nothing of the
    // principal, so it short-circuits ahead of the closure build; the
    // cross-database form still resolves it, because that resolution is what
    // raises Msg 916 for a login with no user in the target.
    private static int? FilteringPrincipal(CatalogView view, BatchContext batch, Database targetDatabase) =>
        view.MetadataKey is null && ReferenceEquals(targetDatabase, batch.CurrentDatabase)
            ? null
            : PermissionEnforcement.MetadataVisibilityPrincipal(batch, targetDatabase);

    private static IEnumerable<SqlValue[]> FilterByObjectId(
        IEnumerable<SqlValue[]> rows,
        MetadataVisibilityKey key,
        Database database,
        int principalId)
    {
        var visible = BuildVisibleObjectIds(database, principalId);
        foreach (var row in rows)
        {
            var idCell = row[key.ObjectIdOrdinal];
            if (!idCell.IsNull && visible.Contains(idCell.AsInt32))
                yield return row;
        }
    }

    private static IEnumerable<SqlValue[]> FilterByName(
        IEnumerable<SqlValue[]> rows,
        MetadataVisibilityKey key,
        Database database,
        int principalId)
    {
        var visible = BuildVisibleObjectNames(database, principalId);
        foreach (var row in rows)
        {
            var schemaCell = row[key.SchemaNameOrdinal];
            var nameCell = row[key.ObjectNameOrdinal];
            if (!schemaCell.IsNull && !nameCell.IsNull
                && visible.Contains(QualifiedName(schemaCell.AsString, nameCell.AsString)))
            {
                yield return row;
            }
        }
    }

    // The set of object ids whose catalog rows a restricted principal may see:
    // every schema object it can view metadata for, plus that object's child
    // constraint ids (which appear in sys.objects / the constraint views keyed
    // by their own id). Table types are metadata containers, not grantable
    // securables, so their ids are always included.
    private static HashSet<int> BuildVisibleObjectIds(Database database, int principalId)
    {
        var closure = PermissionChecker.BuildPrincipalClosure(database, principalId);
        var visible = new HashSet<int>();
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var obj in schema.SchemaObjects())
            {
                var (governingId, governingSchema) = GoverningObject(obj);
                if (!PermissionChecker.CanViewMetadata(database, closure, governingId, governingSchema))
                    continue;
                _ = visible.Add(obj.ObjectId);
                if (obj is HeapTable table)
                    AddConstraintIds(visible, table);
            }
            foreach (var tableType in schema.TableTypes.Values)
                _ = visible.Add(tableType.ObjectId);
        }
        return visible;
    }

    private static HashSet<string> BuildVisibleObjectNames(Database database, int principalId)
    {
        var closure = PermissionChecker.BuildPrincipalClosure(database, principalId);
        var visible = new HashSet<string>(BuiltInToken.Comparer);
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var obj in schema.SchemaObjects())
            {
                var (governingId, governingSchema) = GoverningObject(obj);
                if (PermissionChecker.CanViewMetadata(database, closure, governingId, governingSchema))
                    _ = visible.Add(QualifiedName(schema.Name, obj.Name));
            }
        }
        return visible;
    }

    private static void AddConstraintIds(HashSet<int> visible, HeapTable table)
    {
        foreach (var key in table.KeyConstraints)
            _ = visible.Add(key.ObjectId);
        foreach (var check in table.CheckConstraints)
            _ = visible.Add(check.ObjectId);
        foreach (var foreignKey in table.OutgoingForeignKeys)
            _ = visible.Add(foreignKey.ObjectId);
        foreach (var column in table.Columns)
        {
            if (column.DefaultConstraint is { } defaultConstraint)
                _ = visible.Add(defaultConstraint.ObjectId);
        }
    }

    // A trigger's metadata visibility follows its parent table / view (probe:
    // trg on a SELECT-granted table is visible); every other schema object is
    // governed by its own id.
    private static (int ObjectId, int SchemaId) GoverningObject(SchemaObject obj) =>
        obj is Trigger trigger
            ? (trigger.Parent.ObjectId, trigger.Parent.SchemaId)
            : (obj.ObjectId, obj.SchemaId);

    // NUL joins the segments — it can't occur in a SQL identifier, so the pair
    // "s" / "chema.name" can never collide with "s.chema" / "name".
    private static string QualifiedName(string schema, string name) => schema + " " + name;
}
