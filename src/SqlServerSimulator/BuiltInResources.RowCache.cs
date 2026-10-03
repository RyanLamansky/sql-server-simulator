using SqlServerSimulator.Schemas;

namespace SqlServerSimulator;

internal static partial class BuiltInResources
{
    // The catalog views whose rows CatalogRowCache keeps across statements: the
    // object, column, index, constraint and type metadata the introspection
    // tools (SMO, SSMS's Object Explorer, EF Core's migrations and scaffolding)
    // read in bulk. Each projects only schema metadata that DDL, sp_rename, the
    // rules-and-defaults binders, the extended-property procedures,
    // ENABLE / DISABLE TRIGGER, sp_settriggerorder and SELECT … INTO change —
    // every one of which invalidates the cache. A view is left out when any
    // column follows ordinary DML (sys.partitions' rows, sys.sequences'
    // current_value) or session state (principals, permissions, role
    // membership, the dynamic management views) — except where a live stamp
    // (below) says when the DML-driven part moved.
    private static readonly string[] CacheableCatalogViews =
    [
        "INFORMATION_SCHEMA.CHECK_CONSTRAINTS",
        "INFORMATION_SCHEMA.COLUMN_DOMAIN_USAGE",
        "INFORMATION_SCHEMA.COLUMNS",
        "INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE",
        "INFORMATION_SCHEMA.CONSTRAINT_TABLE_USAGE",
        "INFORMATION_SCHEMA.DOMAINS",
        "INFORMATION_SCHEMA.KEY_COLUMN_USAGE",
        "INFORMATION_SCHEMA.PARAMETERS",
        "INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS",
        "INFORMATION_SCHEMA.ROUTINE_COLUMNS",
        "INFORMATION_SCHEMA.ROUTINES",
        "INFORMATION_SCHEMA.TABLE_CONSTRAINTS",
        "INFORMATION_SCHEMA.TABLES",
        "INFORMATION_SCHEMA.VIEW_COLUMN_USAGE",
        "INFORMATION_SCHEMA.VIEW_TABLE_USAGE",
        "INFORMATION_SCHEMA.VIEWS",
        "sys.all_columns",
        "sys.all_objects",
        "sys.all_parameters",
        "sys.all_sql_modules",
        "sys.all_views",
        "sys.check_constraints",
        "sys.columns",
        "sys.computed_columns",
        "sys.data_spaces",
        "sys.default_constraints",
        "sys.extended_properties",
        "sys.filegroups",
        "sys.foreign_key_columns",
        "sys.foreign_keys",
        "sys.hash_indexes",
        "sys.identity_columns",
        "sys.index_columns",
        "sys.indexes",
        "sys.key_constraints",
        "sys.masked_columns",
        "sys.objects",
        "sys.parameters",
        "sys.procedures",
        "sys.schemas",
        "sys.spatial_index_tessellations",
        "sys.spatial_indexes",
        "sys.sql_modules",
        "sys.stats",
        "sys.stats_columns",
        "sys.synonyms",
        "sys.system_columns",
        "sys.system_objects",
        "sys.system_sql_modules",
        "sys.system_views",
        "sys.table_types",
        "sys.tables",
        "sys.triggers",
        "sys.types",
        "sys.views",
        "sys.xml_indexes",
        "sys.xml_schema_collections",
    ];

    /// <summary>
    /// Marks the <see cref="CacheableCatalogViews"/> and ranks each one's
    /// seekable columns. Called once at catalog-view build time, after every
    /// wrapper that replaces a registered view has run.
    /// </summary>
    private static void ApplyRowCaching(Dictionary<string, CatalogView> views)
    {
        foreach (var key in CacheableCatalogViews)
        {
            if (!views.TryGetValue(key, out var view))
                throw new InvalidOperationException($"The cacheable catalog view {key} is not registered.");
            view.Cacheable = true;
            view.SeekColumns = RankSeekColumns(view);
        }
        views["sys.identity_columns"].LiveStamp = IdentityStamp;
    }

    /// <summary>
    /// Every identity column's last generated value in <paramref name="database"/>,
    /// in a fixed order — what sys.identity_columns' <c>last_value</c> projects,
    /// and the one part of that view ordinary DML moves.
    /// </summary>
    private static Int128[] IdentityStamp(Database database)
    {
        // The dictionaries are enumerated directly: their Values properties
        // take every lock and copy, which a stamp read on each cache hit
        // can't afford.
        var stamp = new List<Int128>();
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, table) in schema.HeapTables)
            {
                foreach (var column in table.Columns)
                {
                    if (column.Identity is { } identity)
                        stamp.Add(identity.Snapshot() ?? Int128.MinValue);
                }
            }
        }
        return [.. stamp];
    }

    /// <summary>
    /// The columns a pushed-down equality may seek a cached view on, best
    /// first: the object-id family (what a correlated catalog read joins on,
    /// and at most a handful of rows per value), then name columns, then the
    /// remaining id and type-code columns. Flags and other low-cardinality
    /// columns aren't offered — a seek on one returns most of the view and
    /// costs the join the whole-view index it would otherwise probe.
    /// </summary>
    private static string[] RankSeekColumns(CatalogView view)
    {
        static int Rank(string name) =>
            BuiltInToken.Equals(name, "object_id") || BuiltInToken.Equals(name, "major_id")
                || BuiltInToken.Equals(name, "parent_object_id") || BuiltInToken.Equals(name, "referenced_object_id")
                || BuiltInToken.Equals(name, "constraint_object_id") || BuiltInToken.Equals(name, "parent_id")
                ? 0
                : BuiltInToken.Equals(name, "name") || name.EndsWith("_NAME", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : name.EndsWith("_id", StringComparison.OrdinalIgnoreCase) || BuiltInToken.Equals(name, "type")
                        ? 2
                        : -1;
        // Rank by rank, each in column order: the order a stable sort on the
        // rank gives, without the generic sort machinery a first catalog
        // read would compile for it.
        var columns = view.Columns;
        var ranks = new int[columns.Length];
        for (var i = 0; i < columns.Length; i++)
            ranks[i] = Rank(columns[i].Name);
        var ranked = new List<string>();
        for (var rank = 0; rank <= 2; rank++)
        {
            for (var i = 0; i < columns.Length; i++)
            {
                if (ranks[i] == rank)
                    ranked.Add(columns[i].Name);
            }
        }
        return [.. ranked];
    }
}
