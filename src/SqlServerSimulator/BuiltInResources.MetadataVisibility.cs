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
        "sys.numbered_procedure_parameters",
        "sys.numbered_procedures",
        "sys.objects",
        "sys.parameters",
        "sys.procedures",
        "sys.security_policies",
        "sys.security_predicates",
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
                view.MetadataKey = new MetadataVisibilityKey(GoverningObjectIdOrdinal(view), -1, -1, DefinitionOrdinal(key, view));
        }
        foreach (var (key, schemaColumn, objectColumn) in NameKeyedMetadataViews)
        {
            if (views.TryGetValue(key, out var view))
                view.MetadataKey = new MetadataVisibilityKey(-1, OrdinalOf(view, schemaColumn), OrdinalOf(view, objectColumn), DefinitionOrdinal(key, view));
        }
        // A restricted principal sees the fixed principals, itself, the roles
        // it belongs to, what it owns and what it holds a permission on; a
        // role membership when it sees the role or the member; and a
        // user-defined type it owns or holds a permission on (probed
        // 2026-10-04 against SQL Server 2025).
        // A dependency row is part of its referencing module's definition.
        if (views.TryGetValue("sys.sql_expression_dependencies", out var dependencies))
            dependencies.MetadataKey = new MetadataVisibilityKey(OrdinalOf(dependencies, "referencing_id"), -1, -1, kind: MetadataVisibilityKind.Definition);
        // A permission row shows to its grantee and the principals in it
        // (probed 2026-10-04 against SQL Server 2025: a fellow user's grants,
        // and dbo's CONNECT, stay hidden).
        if (views.TryGetValue("sys.database_permissions", out var permissions))
            permissions.MetadataKey = new MetadataVisibilityKey(OrdinalOf(permissions, "grantee_principal_id"), -1, -1, kind: MetadataVisibilityKind.Grantee);
        if (views.TryGetValue("sys.database_principals", out var principals))
            principals.MetadataKey = new MetadataVisibilityKey(OrdinalOf(principals, "principal_id"), -1, -1, kind: MetadataVisibilityKind.Principal);
        if (views.TryGetValue("sys.database_role_members", out var members))
            members.MetadataKey = new MetadataVisibilityKey(OrdinalOf(members, "role_principal_id"), OrdinalOf(members, "member_principal_id"), -1, kind: MetadataVisibilityKind.RoleMember);
        foreach (var key in (string[])["sys.table_types", "sys.types"])
        {
            if (views.TryGetValue(key, out var view))
                view.MetadataKey = new MetadataVisibilityKey(OrdinalOf(view, "user_type_id"), -1, -1, kind: MetadataVisibilityKind.Type);
        }
    }

    // The module views whose definition column reads NULL to a principal who
    // sees the module without VIEW DEFINITION or the like (probed 2026-10-04
    // against SQL Server 2025).
    private static int DefinitionOrdinal(string key, CatalogView view) => key switch
    {
        "INFORMATION_SCHEMA.ROUTINES" => OrdinalOf(view, "ROUTINE_DEFINITION"),
        "INFORMATION_SCHEMA.VIEWS" => OrdinalOf(view, "VIEW_DEFINITION"),
        "sys.all_sql_modules" or "sys.sql_modules" => OrdinalOf(view, "definition"),
        _ => -1,
    };

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
        view.MetadataKey is { Kind: MetadataVisibilityKind.Principal or MetadataVisibilityKind.RoleMember or MetadataVisibilityKind.Grantee } principalKey
            ? PermissionEnforcement.RestrictedPrincipal(batch, targetDatabase) is int restricted
                && (principalKey.Kind == MetadataVisibilityKind.Grantee
                    ? PermissionChecker.VisibleGrantees(targetDatabase, restricted, ServerLoginRights.For(batch.Connection))
                    : PermissionChecker.VisiblePrincipals(targetDatabase, restricted, ServerLoginRights.For(batch.Connection))) is { } visiblePrincipals
                ? FilterByPrincipal(rows, principalKey, visiblePrincipals)
                : rows
            : FilteringPrincipal(view, batch, targetDatabase) is not { } principalId || view.MetadataKey is not { } key ? rows
            : key.Kind switch
            {
                MetadataVisibilityKind.Type => FilterByType(rows, key, targetDatabase, principalId, ServerLoginRights.For(batch.Connection)),
                MetadataVisibilityKind.Definition => FilterByDefinition(rows, key, targetDatabase, principalId, ServerLoginRights.For(batch.Connection)),
                _ => key.IsNameKeyed ? FilterByName(rows, key, targetDatabase, principalId, ServerLoginRights.For(batch.Connection))
                    : FilterByObjectId(rows, key, targetDatabase, principalId, ServerLoginRights.For(batch.Connection)),
            };

    private static IEnumerable<SqlValue[]> FilterByPrincipal(IEnumerable<SqlValue[]> rows, MetadataVisibilityKey key, HashSet<int> visible)
    {
        foreach (var row in rows)
        {
            if (visible.Contains(row[key.ObjectIdOrdinal].AsInt32)
                || (key.Kind == MetadataVisibilityKind.RoleMember && visible.Contains(row[key.SchemaNameOrdinal].AsInt32)))
            {
                yield return row;
            }
        }
    }

    private static IEnumerable<SqlValue[]> FilterByDefinition(IEnumerable<SqlValue[]> rows, MetadataVisibilityKey key, Database database, int principalId, ServerLoginRights server)
    {
        var visible = BuildVisibleObjectIds(database, principalId, server);
        var definitions = new HashSet<int>();
        foreach (var (_, obj) in DefinitionVisibleObjects(database, principalId, server))
            _ = definitions.Add(obj.ObjectId);
        foreach (var row in rows)
        {
            var id = row[key.ObjectIdOrdinal].AsInt32;
            if (visible.Contains(id) && definitions.Contains(id))
                yield return row;
        }
    }

    private static IEnumerable<SqlValue[]> FilterByType(IEnumerable<SqlValue[]> rows, MetadataVisibilityKey key, Database database, int principalId, ServerLoginRights server)
    {
        var visible = new HashSet<int>();
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, type) in schema.AliasTypes)
            {
                if (PermissionChecker.CanViewTypeMetadata(database, principalId, type.UserTypeId, schema.SchemaId, Ownership.EffectiveOwnerId(type), server))
                    _ = visible.Add(type.UserTypeId);
            }
            foreach (var (_, type) in schema.TableTypes)
            {
                if (PermissionChecker.CanViewTypeMetadata(database, principalId, type.UserTypeId, schema.SchemaId, type.OwnerPrincipalId ?? schema.PrincipalId, server))
                    _ = visible.Add(type.UserTypeId);
            }
        }
        foreach (var row in rows)
        {
            var id = row[key.ObjectIdOrdinal].AsInt32;
            if (id <= 256 || visible.Contains(id))
                yield return row;
        }
    }

    /// <summary>
    /// Whether <see cref="ApplyMetadataFilter"/> would hand this read every row
    /// unchanged — raising Msg 916 as that filter would for a login with no
    /// user in <paramref name="targetDatabase"/>.
    /// </summary>
    internal static bool ReadsUnfiltered(CatalogView view, BatchContext batch, Database targetDatabase) =>
        view.MetadataKey is { Kind: MetadataVisibilityKind.Principal or MetadataVisibilityKind.RoleMember or MetadataVisibilityKind.Grantee }
            ? PermissionEnforcement.RestrictedPrincipal(batch, targetDatabase) is null
            : FilteringPrincipal(view, batch, targetDatabase) is null || view.MetadataKey is null;

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
        int principalId,
        ServerLoginRights server)
    {
        var visible = BuildVisibleObjectIds(database, principalId, server);
        var definitions = key.DefinitionOrdinal < 0 ? null : new HashSet<int>();
        if (definitions is not null)
        {
            foreach (var (_, obj) in DefinitionVisibleObjects(database, principalId, server))
                _ = definitions.Add(obj.ObjectId);
        }
        foreach (var row in rows)
        {
            var idCell = row[key.ObjectIdOrdinal];
            if (!idCell.IsNull && visible.Contains(idCell.AsInt32))
                yield return definitions is null || definitions.Contains(idCell.AsInt32) ? row : WithoutDefinition(row, key);
        }
    }

    private static IEnumerable<SqlValue[]> FilterByName(
        IEnumerable<SqlValue[]> rows,
        MetadataVisibilityKey key,
        Database database,
        int principalId,
        ServerLoginRights server)
    {
        var visible = BuildVisibleObjectNames(database, principalId, server);
        var definitions = key.DefinitionOrdinal < 0 ? null : new HashSet<string>(BuiltInToken.Comparer);
        if (definitions is not null)
        {
            foreach (var (schemaName, obj) in DefinitionVisibleObjects(database, principalId, server))
                _ = definitions.Add(QualifiedName(schemaName, obj.Name));
        }
        foreach (var row in rows)
        {
            var schemaCell = row[key.SchemaNameOrdinal];
            var nameCell = row[key.ObjectNameOrdinal];
            if (schemaCell.IsNull || nameCell.IsNull)
                continue;
            var qualified = QualifiedName(schemaCell.AsString, nameCell.AsString);
            if (visible.Contains(qualified))
                yield return definitions is null || definitions.Contains(qualified) ? row : WithoutDefinition(row, key);
        }
    }

    // The set of object ids whose catalog rows a restricted principal may see:
    // every schema object it can view metadata for, plus that object's child
    // constraint ids (which appear in sys.objects / the constraint views keyed
    // by their own id). Table types are metadata containers, not grantable
    // securables, so their ids are always included.
    private static HashSet<int> BuildVisibleObjectIds(Database database, int principalId, ServerLoginRights server)
    {
        var closure = PermissionChecker.BuildPrincipalClosure(database, principalId);
        var visible = new HashSet<int>();
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var obj in schema.SchemaObjects())
            {
                var (governingId, governingSchema) = GoverningObject(obj);
                if (!PermissionChecker.CanViewMetadata(database, closure, governingId, governingSchema, server))
                    continue;
                _ = visible.Add(obj.ObjectId);
                if (obj is HeapTable table)
                    AddConstraintIds(visible, table);
            }
            foreach (var (_, tableType) in schema.TableTypes)
                _ = visible.Add(tableType.ObjectId);
        }
        return visible;
    }

    private static HashSet<string> BuildVisibleObjectNames(Database database, int principalId, ServerLoginRights server)
    {
        var closure = PermissionChecker.BuildPrincipalClosure(database, principalId);
        var visible = new HashSet<string>(BuiltInToken.Comparer);
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var obj in schema.SchemaObjects())
            {
                var (governingId, governingSchema) = GoverningObject(obj);
                if (PermissionChecker.CanViewMetadata(database, closure, governingId, governingSchema, server))
                    _ = visible.Add(QualifiedName(schema.Name, obj.Name));
            }
        }
        return visible;
    }

    // The modules whose definition the principal may read.
    private static IEnumerable<(string SchemaName, SchemaObject Module)> DefinitionVisibleObjects(Database database, int principalId, ServerLoginRights server)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var obj in schema.SchemaObjects())
            {
                if (obj.DefinitionText is null)
                    continue;
                var (governingId, governingSchema) = GoverningObject(obj);
                if (PermissionChecker.CanViewDefinition(database, principalId, governingId, governingSchema, server))
                    yield return (schema.Name, obj);
            }
        }
    }

    private static SqlValue[] WithoutDefinition(SqlValue[] row, MetadataVisibilityKey key)
    {
        var copy = (SqlValue[])row.Clone();
        copy[key.DefinitionOrdinal] = SqlValue.Null(copy[key.DefinitionOrdinal].Type);
        return copy;
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
    private static string QualifiedName(string schema, string name) => schema + "\0" + name;
}
