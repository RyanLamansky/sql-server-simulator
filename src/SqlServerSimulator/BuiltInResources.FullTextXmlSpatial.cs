using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

internal static partial class BuiltInResources
{
    /// <summary>
    /// The 59 full-text languages a stock SQL Server 2025 instance ships in
    /// <c>sys.fulltext_languages</c> (probe-confirmed against the reference).
    /// Static reference data — the same registry every database exposes.
    /// </summary>
    private static readonly (int Lcid, string Name)[] FullTextLanguages =
    [
        (0, "Neutral"), (1025, "Arabic"), (1026, "Bulgarian"), (1027, "Catalan"),
        (1028, "Traditional Chinese"), (1029, "Czech"), (1030, "Danish"), (1031, "German"),
        (1032, "Greek"), (1033, "English"), (1035, "Finnish"), (1036, "French"),
        (1037, "Hebrew"), (1038, "Hungarian"), (1039, "Icelandic"), (1040, "Italian"),
        (1041, "Japanese"), (1042, "Korean"), (1043, "Dutch"), (1044, "Bokmål"),
        (1045, "Polish"), (1046, "Brazilian"), (1048, "Romanian"), (1049, "Russian"),
        (1050, "Croatian"), (1051, "Slovak"), (1053, "Swedish"), (1054, "Thai"),
        (1055, "Turkish"), (1056, "Urdu"), (1057, "Indonesian"), (1058, "Ukrainian"),
        (1060, "Slovenian"), (1061, "Estonian"), (1062, "Latvian"), (1063, "Lithuanian"),
        (1066, "Vietnamese"), (1081, "Hindi"), (1086, "Malay - Malaysia"), (1093, "Bengali (India)"),
        (1094, "Punjabi"), (1095, "Gujarati"), (1097, "Tamil"), (1098, "Telugu"),
        (1099, "Kannada"), (1100, "Malayalam"), (1102, "Marathi"), (2052, "Simplified Chinese"),
        (2057, "British English"), (2068, "Norwegian"), (2070, "Portuguese"), (2074, "Serbian (Latin)"),
        (2117, "Bangla"), (3076, "Chinese (Hong Kong SAR, PRC)"), (3082, "Spanish"), (3098, "Serbian (Cyrillic)"),
        (4100, "Chinese (Singapore)"), (5124, "Chinese (Macao SAR)"), (9242, "Serbian (Sr-Latin)"),
    ];

    private static void RegisterFullTextXmlSpatial(Dictionary<string, CatalogView> views)
    {
        void Sys(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["sys." + name] = new CatalogView(name, columns, rows);
        // sys.fulltext_catalogs: per-database full-text catalog metadata.
        // Column subset matches Microsoft Learn's documented surface for
        // SQL Server 2022+ (the reference instance doesn't have full-text
        // installed, so probe-confirmation isn't available — column shapes
        // are taken from learn.microsoft.com/sql/relational-databases/system-catalog-views/sys-fulltext-catalogs-transact-sql).
        Sys("fulltext_catalogs",
        [
            new("fulltext_catalog_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("path", SqlType.NVarchar, 260, true),
            new("is_default", SqlType.Bit, null, false),
            new("is_accent_sensitivity_on", SqlType.Bit, null, false),
            new("data_space_id", SqlType.Int32, null, true),
            new("file_id", SqlType.Int32, null, true),
            new("principal_id", SqlType.Int32, null, false),
            new("is_importing", SqlType.Bit, null, false),
        ], EnumerateSysFullTextCatalogs);

        // sys.fulltext_indexes: per-database full-text indexes. One row per
        // indexed table. Column subset from Microsoft Learn.
        Sys("fulltext_indexes",
        [
            new("object_id", SqlType.Int32, null, false),
            new("unique_index_id", SqlType.Int32, null, false),
            new("fulltext_catalog_id", SqlType.Int32, null, false),
            new("is_enabled", SqlType.Bit, null, false),
            new("change_tracking_state", charOne, 1, false),
            new("change_tracking_state_desc", nvarchar60Catalog, 60, true),
            new("has_crawl_completed", SqlType.Bit, null, false),
            new("crawl_type", charOne, 1, false),
            new("crawl_type_desc", nvarchar60Catalog, 60, true),
            new("crawl_start_date", SqlType.DateTime, null, true),
            new("crawl_end_date", SqlType.DateTime, null, true),
            new("stoplist_id", SqlType.Int32, null, true),
            new("data_space_id", SqlType.Int32, null, true),
            new("property_list_id", SqlType.Int32, null, true),
        ], EnumerateSysFullTextIndexes);

        // sys.fulltext_index_columns: one row per indexed column inside each
        // full-text index. column_id = 1-based storage ordinal of the
        // indexed column; type_column_id = nullable ordinal of the paired
        // doc-extension column for varbinary indexes.
        Sys("fulltext_index_columns",
        [
            new("object_id", SqlType.Int32, null, false),
            new("column_id", SqlType.Int32, null, false),
            new("type_column_id", SqlType.Int32, null, true),
            new("language_id", SqlType.Int32, null, false),
            new("statistical_semantics", SqlType.Bit, null, false),
        ], EnumerateSysFullTextIndexColumns);

        // sys.fulltext_stoplists / sys.registered_search_property_lists:
        // full-text stoplists and search property lists aren't modeled, so both
        // are empty views with the documented SQL Server 2025 shape. SMO's
        // CREATE-scripting full-text-index query LEFT JOINs both by id.
        Sys("fulltext_stoplists",
        [
            new("stoplist_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("principal_id", SqlType.Int32, null, true),
        ], static (batch, database) => []);

        Sys("registered_search_property_lists",
        [
            new("property_list_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("principal_id", SqlType.Int32, null, true),
        ], static (batch, database) => []);

        // sys.registered_search_properties: per-search-property-list entries.
        // Search property lists aren't populated (sys.registered_search_
        // property_lists is empty), so this ships empty with the full probe-
        // confirmed shape (SQL Server 2025, 2026-07-16). DacFx references it.
        Sys("registered_search_properties",
        [
            new("property_list_id", SqlType.Int32, null, false),
            new("property_id", SqlType.Int32, null, false),
            new("property_name", SqlType.NVarchar, 256, false),
            new("property_set_guid", SqlType.UniqueIdentifier, null, false),
            new("property_int_id", SqlType.Int32, null, false),
            new("property_description", SqlType.NVarchar, 512, true),
        ], static (_, _) => EmptyCatalogRows);

        // sys.fulltext_languages: the per-LCID full-text language registry
        // (the 59 languages a stock SQL Server 2025 instance ships, probed
        // from the reference). DacFx's full-text-index-column reverse-
        // engineering INNER JOINs this view by language_id to resolve the
        // column's language name; an empty view drops the join row and DacFx
        // NREs building the SqlFullTextIndexColumnSpecifier. AW's indexes use
        // LanguageId 1033 (English).
        Sys("fulltext_languages",
        [
            new("lcid", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
        ], static (batch, database) =>
        {
            _ = (batch, database);
            return FullTextLanguages.Select(static lang => new[]
            {
                SqlValue.FromInt32(lang.Lcid),
                SqlValue.FromSystemName(lang.Name),
            });
        });

        // sys.syslanguages: legacy per-language compatibility view, projecting
        // every installed language from Language.All in langid order — the set
        // SET LANGUAGE resolves against, and what a stock instance's
        // default-language configuration (configuration_id 124, value_in_use 0)
        // joins by langid to name the default. The three name-list columns
        // (months / shortmonths / days) are nullable on real and left NULL
        // here: no surface reads them, since message and date-name
        // localization aren't modeled.
        Sys("syslanguages",
        [
            new("langid", SqlType.SmallInt, null, false),
            new("dateformat", SqlType.GetNChar(3), 3, false),
            new("datefirst", SqlType.TinyInt, null, false),
            new("upgrade", SqlType.Int32, null, true),
            new("name", SqlType.NVarchar, 128, false),
            new("alias", SqlType.NVarchar, 128, false),
            new("months", SqlType.NVarchar, 372, true),
            new("shortmonths", SqlType.NVarchar, 132, true),
            new("days", SqlType.NVarchar, 217, true),
            new("lcid", SqlType.Int32, null, false),
            new("msglangid", SqlType.SmallInt, null, false),
        ], static (batch, database) =>
        {
            _ = (batch, database);
            var rows = new List<SqlValue[]>(Language.All.Length);
            foreach (var language in Language.All)
            {
                rows.Add([
                    SqlValue.FromInt16(language.LangId),
                    SqlValue.FromString(SqlType.GetNChar(3), language.DateFormat),
                    SqlValue.FromByte(language.DateFirst),
                    SqlValue.FromInt32(0),
                    SqlValue.FromNVarchar(language.Name),
                    SqlValue.FromNVarchar(language.Alias),
                    SqlValue.Null(NVarcharSqlType.Get(372, Collation.Baseline, Coercibility.CoercibleDefault)),
                    SqlValue.Null(NVarcharSqlType.Get(132, Collation.Baseline, Coercibility.CoercibleDefault)),
                    SqlValue.Null(NVarcharSqlType.Get(217, Collation.Baseline, Coercibility.CoercibleDefault)),
                    SqlValue.FromInt32(language.Lcid),
                    SqlValue.FromInt16(language.MsgLangId),
                ]);
            }
            return rows;
        });

        // sys.xml_schema_collections: probe-confirmed 6-col shipped subset
        // against SQL Server 2025 (2026-05-15). Real SQL Server's
        // principal_id column is nullable; the simulator's pre-seeded
        // collections leave it NULL.
        Sys("xml_schema_collections",
        [
            new("xml_collection_id", SqlType.Int32, null, false),
            new("schema_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("name", SqlType.SystemName, 128, false),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
        ], EnumerateSysXmlSchemaCollections);

        // sys.xml_indexes: full 26-col shape (probe-confirmed against SQL
        // Server 2025 WideWorldImporters, 2026-07-16). The load-bearing core
        // (identity, primary/secondary discriminator, FOR-PATH/VALUE/PROPERTY
        // classifier) keeps its original positions; the remaining columns
        // DacFx's XML-index reverse-engineering query reads (fill_factor /
        // is_padded / allow_row_locks / allow_page_locks / is_disabled /
        // xml_index_type / xml_index_type_description / path_id + the shared
        // index-admin tail) are appended after them. Values are the
        // fresh-index defaults consistent with the sys.indexes / spatial_index
        // modeled defaults. Real orders using_xml_index_id / secondary_type
        // after the admin flags; the appended layout differs cosmetically but
        // all consumers read by name.
        Sys("xml_indexes",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("index_id", SqlType.Int32, null, false),
            new("type", SqlType.TinyInt, null, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("using_xml_index_id", SqlType.Int32, null, true),
            new("secondary_type", charOne, 1, true),
            new("secondary_type_desc", nvarchar60Catalog, 60, true),
            new("is_primary_key", SqlType.Bit, null, true),
            new("is_unique", SqlType.Bit, null, true),
            new("data_space_id", SqlType.Int32, null, false),
            new("ignore_dup_key", SqlType.Bit, null, true),
            new("is_unique_constraint", SqlType.Bit, null, true),
            new("fill_factor", SqlType.TinyInt, null, false),
            new("is_padded", SqlType.Bit, null, true),
            new("is_disabled", SqlType.Bit, null, true),
            new("is_hypothetical", SqlType.Bit, null, true),
            new("is_ignored_in_optimization", SqlType.Bit, null, true),
            new("allow_row_locks", SqlType.Bit, null, true),
            new("allow_page_locks", SqlType.Bit, null, true),
            new("has_filter", SqlType.Bit, null, true),
            new("filter_definition", NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault), SqlType.MaxLengthSentinel, true),
            new("xml_index_type", SqlType.TinyInt, null, true),
            new("xml_index_type_description", nvarchar60Catalog, 60, true),
            new("path_id", SqlType.Int32, null, true),
            new("auto_created", SqlType.Bit, null, true),
        ], EnumerateSysXmlIndexes);

        // sys.spatial_indexes: probe-confirmed 23-col shape against SQL Server
        // 2025 (2026-05-15). Same shape as sys.indexes except (type, type_desc)
        // are fixed to (4, 'SPATIAL') and the four trailing spatial-specific
        // columns describe the tessellation. The simulator surfaces the
        // load-bearing identity + spatial classification subset; the
        // is_disabled / is_padded / allow_row_locks tail mirrors real values
        // (false / false / true / true) but isn't read by any application
        // path the loader cares about.
        Sys("spatial_indexes",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("index_id", SqlType.Int32, null, false),
            new("type", SqlType.TinyInt, null, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("is_unique", SqlType.Bit, null, true),
            new("data_space_id", SqlType.Int32, null, false),
            new("ignore_dup_key", SqlType.Bit, null, true),
            new("is_primary_key", SqlType.Bit, null, true),
            new("is_unique_constraint", SqlType.Bit, null, true),
            new("fill_factor", SqlType.TinyInt, null, false),
            new("is_padded", SqlType.Bit, null, true),
            new("is_disabled", SqlType.Bit, null, true),
            new("is_hypothetical", SqlType.Bit, null, true),
            new("is_ignored_in_optimization", SqlType.Bit, null, true),
            new("allow_row_locks", SqlType.Bit, null, true),
            new("allow_page_locks", SqlType.Bit, null, true),
            new("spatial_index_type", SqlType.Int32, null, false),
            new("spatial_index_type_desc", nvarchar60Catalog, 60, true),
            new("tessellation_scheme", SqlType.NVarchar, 60, true),
            new("has_filter", SqlType.Bit, null, false),
            new("filter_definition", NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault), null, true),
            new("auto_created", SqlType.Bit, null, true),
        ], EnumerateSysSpatialIndexes);

        // sys.spatial_index_tessellations: probe-confirmed 16-col shape
        // against SQL Server 2025 (2026-05-15). One row per spatial index
        // carrying the per-index bounding-box + 4-level grid detail.
        Sys("spatial_index_tessellations",
        [
            new("object_id", SqlType.Int32, null, false),
            new("index_id", SqlType.Int32, null, false),
            new("tessellation_scheme", SqlType.NVarchar, 60, true),
            new("bounding_box_xmin", SqlType.Float, null, true),
            new("bounding_box_ymin", SqlType.Float, null, true),
            new("bounding_box_xmax", SqlType.Float, null, true),
            new("bounding_box_ymax", SqlType.Float, null, true),
            new("level_1_grid", SqlType.SmallInt, null, true),
            new("level_1_grid_desc", SqlType.NVarchar, 60, true),
            new("level_2_grid", SqlType.SmallInt, null, true),
            new("level_2_grid_desc", SqlType.NVarchar, 60, true),
            new("level_3_grid", SqlType.SmallInt, null, true),
            new("level_3_grid_desc", SqlType.NVarchar, 60, true),
            new("level_4_grid", SqlType.SmallInt, null, true),
            new("level_4_grid_desc", SqlType.NVarchar, 60, true),
            new("cells_per_object", SqlType.Int32, null, true),
        ], EnumerateSysSpatialIndexTessellations);

        // sys.spatial_reference_systems: real SQL Server seeds this view with
        // ~390 rows of authoritative SRID definitions (EPSG / ESRI). The
        // simulator surfaces an empty view — the column shape matches probe
        // and the catalog is reachable, but no SRID rows pre-populate. This
        // keeps applications that reference the view's schema from breaking
        // without the byte-tonnage of the WKT-laden seed data.
        Sys("spatial_reference_systems",
        [
            new("spatial_reference_id", SqlType.Int32, null, true),
            new("authority_name", SqlType.NVarchar, 256, true),
            new("authorized_spatial_reference_id", SqlType.Int32, null, true),
            new("well_known_text", SqlType.NVarchar, 8000, true),
            new("unit_of_measure", SqlType.NVarchar, 256, true),
            new("unit_conversion_factor", SqlType.Float, null, true),
        ], EnumerateSysSpatialReferenceSystems);
    }

    /// <summary>
    /// Rows for <c>sys.fulltext_catalogs</c>. One row per
    /// <see cref="FullTextCatalog"/> in <see cref="Database.FullTextCatalogs"/>.
    /// Filesystem-placement columns (<c>path</c>, <c>data_space_id</c>,
    /// <c>file_id</c>) surface as NULL — the simulator has no on-disk catalog
    /// storage. <c>is_importing</c> is always false (no concurrent bacpac
    /// import to observe).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysFullTextCatalogs(Parser.BatchContext batch, Database database)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var nullPath = SqlValue.Null(SqlType.NVarchar);
        var nullDataSpaceId = SqlValue.Null(SqlType.Int32);
        var nullFileId = SqlValue.Null(SqlType.Int32);
        foreach (var cat in database.FullTextCatalogs.Values.OrderBy(c => c.Id))
        {
            yield return [
                SqlValue.FromInt32(cat.Id),
                SqlValue.FromSystemName(cat.Name),
                nullPath,
                cat.IsDefault ? trueBit : falseBit,
                cat.IsAccentSensitive ? trueBit : falseBit,
                nullDataSpaceId,
                nullFileId,
                SqlValue.FromInt32(cat.PrincipalId),
                falseBit,
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sys.fulltext_indexes</c>. One row per table that has a
    /// <see cref="HeapTable.FullTextIndex"/> populated. <c>is_enabled</c> /
    /// <c>has_crawl_completed</c> default to true (no crawl is performed
    /// but the FT index is "ready" from the catalog's POV);
    /// <c>change_tracking_state</c> = 'A' (AUTO) / 'AUTO';
    /// <c>crawl_type</c> = 'F' (FULL) / 'FULL'.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysFullTextIndexes(Parser.BatchContext batch, Database database)
    {
        var charOneType = CharSqlType.Get(1, Collation.Catalog, Coercibility.Implicit);
        var trueBit = SqlValue.FromBoolean(true);
        var autoCode = SqlValue.FromChar(charOneType, "A");
        var autoDesc = SqlValue.FromNVarchar("AUTO");
        var fullCode = SqlValue.FromChar(charOneType, "F");
        var fullDesc = SqlValue.FromNVarchar("FULL");
        var nullDate = SqlValue.Null(SqlType.DateTime);
        var nullInt = SqlValue.Null(SqlType.Int32);
        // data_space_id points at the filegroup the FT index lives on — always
        // PRIMARY (1) in the simulator's single-filegroup storage model. It
        // must be non-NULL: DacFx's SqlFullTextIndex reverse-engineering query
        // INNER JOINs sys.data_spaces on it, and a NULL drops the parent index
        // element (orphaning its column specifiers → NRE in DacFx).
        var primaryDataSpaceId = SqlValue.FromInt32(Database.PrimaryFilegroupId);
        // stoplist_id = 0 → the built-in SYSTEM stoplist (the default when
        // CREATE FULLTEXT INDEX omits WITH STOPLIST). Probe-confirmed against
        // the reference AW database's sys.fulltext_indexes. A NULL here would
        // make DacFx script IsStopListOff=True (stoplist disabled), diverging
        // from the source which uses the system stoplist (DoUseSystemStopList
        // default).
        var systemStoplistId = SqlValue.FromInt32(0);
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                if (table.FullTextIndex is not { } fti)
                    continue;
                yield return [
                    SqlValue.FromInt32(table.ObjectId),
                    SqlValue.FromInt32(fti.UniqueIndexId),
                    SqlValue.FromInt32(fti.CatalogId),
                    trueBit,
                    autoCode,
                    autoDesc,
                    trueBit,
                    fullCode,
                    fullDesc,
                    nullDate,
                    nullDate,
                    systemStoplistId,
                    primaryDataSpaceId,
                    nullInt,
                ];
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.fulltext_index_columns</c>. One row per
    /// <see cref="FullTextIndexColumn"/> across every indexed table.
    /// <c>statistical_semantics</c> always false (the simulator doesn't
    /// expose the WITH STATISTICAL_SEMANTICS option at the column level
    /// since the index parser parse-and-discards it).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysFullTextIndexColumns(Parser.BatchContext batch, Database database)
    {
        var falseBit = SqlValue.FromBoolean(false);
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                if (table.FullTextIndex is not { } fti)
                    continue;
                foreach (var col in fti.Columns)
                {
                    yield return [
                        SqlValue.FromInt32(table.ObjectId),
                        SqlValue.FromInt32(col.ColumnId),
                        col.TypeColumnId is int tcid ? SqlValue.FromInt32(tcid) : SqlValue.Null(SqlType.Int32),
                        SqlValue.FromInt32(col.LanguageId),
                        falseBit,
                    ];
                }
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.xml_schema_collections</c>. One row per
    /// <see cref="XmlSchemaCollection"/> across every schema. The
    /// principal_id surfaces as NULL — the simulator's CREATE XML SCHEMA
    /// COLLECTION grammar doesn't support an AUTHORIZATION clause and
    /// every collection's principal_id field is left null at construction.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysXmlSchemaCollections(Parser.BatchContext batch, Database database)
    {
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var coll in schema.XmlSchemaCollections.Values.OrderBy(c => c.Id))
            {
                yield return [
                    SqlValue.FromInt32(coll.Id),
                    SqlValue.FromInt32(coll.SchemaId),
                    coll.PrincipalId is int p ? SqlValue.FromInt32(p) : SqlValue.Null(SqlType.Int32),
                    SqlValue.FromSystemName(coll.Name),
                    SqlValue.FromDateTime(coll.CreateDate),
                    SqlValue.FromDateTime(coll.ModifyDate),
                ];
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.xml_indexes</c>. One row per
    /// <see cref="XmlIndex"/> across every table. type=3 / type_desc='XML'
    /// for both primary and secondary forms (probe-confirmed). The
    /// is_primary_key column surfaces always false — primary xml indexes
    /// aren't xml-typed PKs. <c>index_id</c> comes from real's per-table
    /// 256000+ XML range, and a secondary's <c>using_xml_index_id</c> is its
    /// primary's value from that same range.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysXmlIndexes(Parser.BatchContext batch, Database database)
    {
        var charOneType = CharSqlType.Get(1, Collation.Catalog, Coercibility.Implicit);
        var typeCode = SqlValue.FromByte(3);
        var typeDesc = SqlValue.FromNVarchar("XML");
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var pathCode = SqlValue.FromChar(charOneType, "P");
        var pathDesc = SqlValue.FromNVarchar("PATH");
        var valueCode = SqlValue.FromChar(charOneType, "V");
        var valueDesc = SqlValue.FromNVarchar("VALUE");
        var propertyCode = SqlValue.FromChar(charOneType, "R");
        var propertyDesc = SqlValue.FromNVarchar("PROPERTY");
        var nullChar = SqlValue.Null(charOneType);
        var nullDesc = SqlValue.Null(SqlType.NVarchar);
        var nullInt = SqlValue.Null(SqlType.Int32);
        // Appended-column constants: fresh-index defaults mirroring the
        // sys.indexes / spatial_index modeled shape (data_space_id=1 PRIMARY,
        // fill_factor=0, allow_row_locks / allow_page_locks true).
        var zeroByte = SqlValue.FromByte(0);
        var oneInt = SqlValue.FromInt32(1);
        var nullFilter = SqlValue.Null(NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault));
        var primaryXmlType = SqlValue.FromByte(0);
        var secondaryXmlType = SqlValue.FromByte(1);
        var primaryXmlDesc = SqlValue.FromNVarchar("PRIMARY_XML");
        var secondaryXmlDesc = SqlValue.FromNVarchar("SECONDARY_XML");
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                if (table.XmlIndexes.Count == 0)
                    continue;
                // Build a quick name→objectId map so secondary indexes can
                // resolve their using_xml_index_id from the recorded
                // UsingPrimaryIndexName.
                var primaryIds = new Dictionary<string, int>(database.Collation);
                foreach (var ix in table.XmlIndexes)
                {
                    if (ix.IsPrimary)
                        primaryIds[ix.Name] = ix.IndexId;
                }
                foreach (var ix in table.XmlIndexes)
                {
                    var usingId = ix.UsingPrimaryIndexName is { } u && primaryIds.TryGetValue(u, out var v)
                        ? SqlValue.FromInt32(v)
                        : nullInt;
                    var (secCode, secDesc) = ix.SecondaryType switch
                    {
                        XmlSecondaryIndexType.Path => (pathCode, pathDesc),
                        XmlSecondaryIndexType.Value => (valueCode, valueDesc),
                        XmlSecondaryIndexType.Property => (propertyCode, propertyDesc),
                        _ => (nullChar, nullDesc),
                    };
                    var (xmlType, xmlDesc) = ix.IsPrimary
                        ? (primaryXmlType, primaryXmlDesc)
                        : (secondaryXmlType, secondaryXmlDesc);
                    yield return [
                        SqlValue.FromInt32(table.ObjectId),
                        SqlValue.FromSystemName(ix.Name),
                        SqlValue.FromInt32(ix.IndexId),
                        typeCode,
                        typeDesc,
                        usingId,
                        secCode,
                        secDesc,
                        falseBit,
                        falseBit,   // is_unique
                        oneInt,     // data_space_id (PRIMARY)
                        falseBit,   // ignore_dup_key
                        falseBit,   // is_unique_constraint
                        zeroByte,   // fill_factor
                        falseBit,   // is_padded
                        falseBit,   // is_disabled
                        falseBit,   // is_hypothetical
                        falseBit,   // is_ignored_in_optimization
                        trueBit,    // allow_row_locks
                        trueBit,    // allow_page_locks
                        falseBit,   // has_filter
                        nullFilter, // filter_definition
                        xmlType,    // xml_index_type
                        xmlDesc,    // xml_index_type_description
                        // path_id names the promoted path a selective XML
                        // index tracks; an ordinary primary or secondary index
                        // reports NULL (probe-confirmed).
                        nullInt,    // path_id
                        falseBit,   // auto_created
                    ];
                }
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.spatial_indexes</c>. One row per
    /// <see cref="SpatialIndex"/> across every table. Fixed values:
    /// type=4 / type_desc='SPATIAL', is_unique=false, data_space_id=1
    /// (the simulator's only filegroup), spatial_index_type=3 / 'GEOMETRY' or
    /// 4 / 'GEOGRAPHY' driven by <see cref="SpatialIndexKind"/>. The trailing
    /// admin flags (is_padded / allow_row_locks / etc.) mirror real-server
    /// defaults so applications reading the column shape don't see NULL where
    /// they expect a bool.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysSpatialIndexes(Parser.BatchContext batch, Database database)
    {
        var typeCode = SqlValue.FromByte(4);
        var typeDesc = SqlValue.FromNVarchar("SPATIAL");
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var zeroByte = SqlValue.FromByte(0);
        var oneInt = SqlValue.FromInt32(1);
        var nullDesc = SqlValue.Null(NVarcharSqlType.Get(60, Collation.Catalog, Coercibility.Implicit));
        var geometryTypeDesc = SqlValue.FromNVarchar("GEOMETRY");
        var geographyTypeDesc = SqlValue.FromNVarchar("GEOGRAPHY");
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                if (table.SpatialIndexes.Count == 0)
                    continue;
                foreach (var ix in table.SpatialIndexes)
                {
                    yield return [
                        SqlValue.FromInt32(table.ObjectId),
                        SqlValue.FromSystemName(ix.Name),
                        SqlValue.FromInt32(ix.IndexId),
                        typeCode,
                        typeDesc,
                        falseBit,
                        oneInt,
                        falseBit,
                        falseBit,
                        falseBit,
                        zeroByte,
                        falseBit,
                        falseBit,
                        falseBit,
                        falseBit,
                        trueBit,
                        trueBit,
                        SqlValue.FromInt32((int)ix.Kind),
                        ix.Kind == SpatialIndexKind.Geography ? geographyTypeDesc : geometryTypeDesc,
                        SqlValue.FromNVarchar(ix.TessellationScheme),
                        falseBit,
                        nullDesc,
                        falseBit,
                    ];
                }
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.spatial_index_tessellations</c>. One row per
    /// spatial index across every table, carrying the bounding box +
    /// 4-level grid detail captured at CREATE time. Levels not specified
    /// in the DDL surface as NULL. The level_*_grid_desc columns mirror
    /// SQL Server's enumeration ('LOW' / 'MEDIUM' / 'HIGH' for codes 1/2/3).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysSpatialIndexTessellations(Parser.BatchContext batch, Database database)
    {
        var nullDouble = SqlValue.Null(SqlType.Float);
        var nullShort = SqlValue.Null(SqlType.SmallInt);
        var nullDesc = SqlValue.Null(SqlType.NVarchar);
        var nullInt = SqlValue.Null(SqlType.Int32);
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                if (table.SpatialIndexes.Count == 0)
                    continue;
                foreach (var ix in table.SpatialIndexes)
                {
                    yield return [
                        SqlValue.FromInt32(table.ObjectId),
                        SqlValue.FromInt32(ix.IndexId),
                        SqlValue.FromNVarchar(ix.TessellationScheme),
                        ix.BoundingBoxXmin.HasValue ? SqlValue.FromDouble(ix.BoundingBoxXmin.Value) : nullDouble,
                        ix.BoundingBoxYmin.HasValue ? SqlValue.FromDouble(ix.BoundingBoxYmin.Value) : nullDouble,
                        ix.BoundingBoxXmax.HasValue ? SqlValue.FromDouble(ix.BoundingBoxXmax.Value) : nullDouble,
                        ix.BoundingBoxYmax.HasValue ? SqlValue.FromDouble(ix.BoundingBoxYmax.Value) : nullDouble,
                        ix.Level1Grid.HasValue ? SqlValue.FromInt16(ix.Level1Grid.Value) : nullShort,
                        GridLevelDesc(ix.Level1Grid, nullDesc),
                        ix.Level2Grid.HasValue ? SqlValue.FromInt16(ix.Level2Grid.Value) : nullShort,
                        GridLevelDesc(ix.Level2Grid, nullDesc),
                        ix.Level3Grid.HasValue ? SqlValue.FromInt16(ix.Level3Grid.Value) : nullShort,
                        GridLevelDesc(ix.Level3Grid, nullDesc),
                        ix.Level4Grid.HasValue ? SqlValue.FromInt16(ix.Level4Grid.Value) : nullShort,
                        GridLevelDesc(ix.Level4Grid, nullDesc),
                        ix.CellsPerObject.HasValue ? SqlValue.FromInt32(ix.CellsPerObject.Value) : nullInt,
                    ];
                }
            }
        }
    }

    private static SqlValue GridLevelDesc(short? code, SqlValue nullDesc) =>
        code switch
        {
            1 => SqlValue.FromNVarchar("LOW"),
            2 => SqlValue.FromNVarchar("MEDIUM"),
            3 => SqlValue.FromNVarchar("HIGH"),
            _ => nullDesc,
        };

    /// <summary>
    /// Rows for <c>sys.spatial_reference_systems</c>. Real SQL Server
    /// pre-seeds this with ~390 authoritative SRID rows; the simulator
    /// surfaces an empty view (no rows yielded) so the column shape is
    /// reachable but the WKT-laden seed payload doesn't ship.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysSpatialReferenceSystems(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        yield break;
    }
}
