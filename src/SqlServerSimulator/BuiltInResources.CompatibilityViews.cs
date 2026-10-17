using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The SQL Server 2000 compatibility views real defines as projections of the
// modern catalog views, built the same way.
partial class BuiltInResources
{
    /// <summary>
    /// Registers the compatibility views real defines over a modern catalog
    /// view — <c>sysforeignkeys</c>, <c>sysconstraints</c>,
    /// <c>sysreferences</c>, <c>sysmembers</c>, <c>sysfiles</c>,
    /// <c>sysfilegroups</c>, <c>sysfulltextcatalogs</c>, <c>systypes</c>,
    /// <c>sysprotects</c> and <c>syspermissions</c> — under their bare and
    /// <c>sys.</c> keys, so
    /// they also resolve through <c>dbo</c> as every compatibility view does.
    /// Each reads its source's rows filtered for the principal as the source's
    /// are, since real's definition selects from that view; the column shapes
    /// and the derived bitmaps follow real's definitions, read from
    /// <c>OBJECT_DEFINITION</c> and checked against their rows (probed
    /// 2026-10-15 against SQL Server 2025).
    /// </summary>
    private static void RegisterCompatibilityProjections(Dictionary<string, CatalogView> views)
    {
        var sysname = SqlType.SystemName;
        var path = NVarcharSqlType.Get(260, Collation.Baseline, Coercibility.Implicit);

        Compatibility(views, "sysforeignkeys",
        [
            new("constid", SqlType.Int32, null, false),
            new("fkeyid", SqlType.Int32, null, false),
            new("rkeyid", SqlType.Int32, null, false),
            new("fkey", SqlType.SmallInt, null, true),
            new("rkey", SqlType.SmallInt, null, true),
            new("keyno", SqlType.SmallInt, null, true),
        ], EnumerateSysforeignkeys);

        Compatibility(views, "sysconstraints",
        [
            new("constid", SqlType.Int32, null, false),
            new("id", SqlType.Int32, null, false),
            new("colid", SqlType.SmallInt, null, true),
            new("spare1", SqlType.TinyInt, null, true),
            new("status", SqlType.Int32, null, true),
            new("actions", SqlType.Int32, null, true),
            new("error", SqlType.Int32, null, true),
        ], EnumerateSysconstraints);

        HeapColumn[] referenceColumns =
        [
            new("constid", SqlType.Int32, null, false),
            new("fkeyid", SqlType.Int32, null, false),
            new("rkeyid", SqlType.Int32, null, false),
            new("rkeyindid", SqlType.SmallInt, null, true),
            new("keycnt", SqlType.SmallInt, null, true),
            new("forkeys", VarbinarySqlType.Get(32), 32, true),
            new("refkeys", VarbinarySqlType.Get(32), 32, true),
            new("fkeydbid", SqlType.SmallInt, null, true),
            new("rkeydbid", SqlType.SmallInt, null, true),
            .. Enumerable.Range(1, 16).Select(static i => new HeapColumn($"fkey{i}", SqlType.SmallInt, null, true)),
            .. Enumerable.Range(1, 16).Select(static i => new HeapColumn($"rkey{i}", SqlType.SmallInt, null, true)),
        ];
        Compatibility(views, "sysreferences", referenceColumns, EnumerateSysreferences);

        Compatibility(views, "sysmembers",
        [
            new("memberuid", SqlType.SmallInt, null, true),
            new("groupuid", SqlType.SmallInt, null, true),
        ], (batch, database) => Project(batch, database, "sys.database_role_members", static source =>
        {
            var member = OrdinalOf(source, "member_principal_id");
            var role = OrdinalOf(source, "role_principal_id");
            return row => [SmallInt(row[member]), SmallInt(row[role])];
        }));

        Compatibility(views, "sysfiles",
        [
            new("fileid", SqlType.SmallInt, null, true),
            new("groupid", SqlType.SmallInt, null, true),
            new("size", SqlType.Int32, null, false),
            new("maxsize", SqlType.Int32, null, false),
            new("growth", SqlType.Int32, null, false),
            new("status", SqlType.Int32, null, true),
            new("perf", SqlType.Int32, null, true),
            new("name", sysname, 128, false),
            new("filename", path, 260, true),
        ], EnumerateSysfiles);

        Compatibility(views, "sysfilegroups",
        [
            new("groupid", SqlType.SmallInt, null, true),
            new("allocpolicy", SqlType.SmallInt, null, true),
            new("status", SqlType.Int32, null, true),
            new("groupname", sysname, 128, false),
        ], (batch, database) => Project(batch, database, "sys.filegroups", static source =>
        {
            var id = OrdinalOf(source, "data_space_id");
            var isDefault = OrdinalOf(source, "is_default");
            var readOnly = OrdinalOf(source, "is_read_only");
            var name = OrdinalOf(source, "name");
            return row =>
            [
                SqlValue.FromInt16((short)(row[id].AsInt32 & 0x7fff)),
                SqlValue.FromInt16(0),
                SqlValue.FromInt32((Flag(row[isDefault]) * 16) + (Flag(row[readOnly]) * 8)),
                SqlValue.FromSystemName(row[name].AsString),
            ];
        }));

        Compatibility(views, "sysfulltextcatalogs",
        [
            new("ftcatid", SqlType.SmallInt, null, true),
            new("name", sysname, 128, false),
            new("status", SqlType.SmallInt, null, true),
            new("path", path, 260, true),
        ], (batch, database) => Project(batch, database, "sys.fulltext_catalogs", source =>
        {
            var id = OrdinalOf(source, "fulltext_catalog_id");
            var name = OrdinalOf(source, "name");
            var isDefault = OrdinalOf(source, "is_default");
            var catalogPath = OrdinalOf(source, "path");
            return row =>
            [
                SmallInt(row[id]),
                SqlValue.FromSystemName(row[name].AsString),
                SqlValue.FromInt16((short)Flag(row[isDefault])),
                row[catalogPath].IsNull ? SqlValue.Null(path) : SqlValue.FromString(path, row[catalogPath].AsString),
            ];
        }));

        Compatibility(views, "sysprotects",
        [
            new("id", SqlType.Int32, null, false),
            new("uid", SqlType.SmallInt, null, true),
            new("action", SqlType.TinyInt, null, true),
            new("protecttype", SqlType.TinyInt, null, true),
            new("columns", VarbinarySqlType.Get(8000), 8000, true),
            new("grantor", SqlType.SmallInt, null, true),
        ], EnumerateSysprotects);

        Compatibility(views, "systypes",
        [
            new("name", sysname, 128, false),
            new("xtype", SqlType.TinyInt, null, false),
            new("status", SqlType.TinyInt, null, true),
            new("xusertype", SqlType.SmallInt, null, true),
            new("length", SqlType.SmallInt, null, false),
            new("xprec", SqlType.TinyInt, null, false),
            new("xscale", SqlType.TinyInt, null, false),
            new("tdefault", SqlType.Int32, null, false),
            new("domain", SqlType.Int32, null, false),
            new("uid", SqlType.SmallInt, null, true),
            new("reserved", SqlType.SmallInt, null, true),
            new("collationid", SqlType.Int32, null, true),
            new("usertype", SqlType.SmallInt, null, true),
            new("variable", SqlType.Bit, null, false),
            new("allownulls", SqlType.Bit, null, true),
            new("type", SqlType.TinyInt, null, false),
            new("printfmt", VarcharSqlType.Get(255, Collation.Baseline, Coercibility.Implicit), 255, true),
            new("prec", SqlType.SmallInt, null, true),
            new("scale", SqlType.TinyInt, null, true),
            new("collation", sysname, 128, true),
        ], EnumerateSystypes);

        var bitmap = VarbinarySqlType.Get(4000);
        Compatibility(views, "syspermissions",
        [
            new("id", SqlType.Int32, null, false),
            new("grantee", SqlType.SmallInt, null, true),
            new("grantor", SqlType.SmallInt, null, true),
            new("actadd", SqlType.SmallInt, null, true),
            new("actmod", SqlType.SmallInt, null, true),
            new("seladd", bitmap, 4000, true),
            new("selmod", bitmap, 4000, true),
            new("updadd", bitmap, 4000, true),
            new("updmod", bitmap, 4000, true),
            new("refadd", bitmap, 4000, true),
            new("refmod", bitmap, 4000, true),
        ], EnumerateSyspermissions);
    }

    private static void Compatibility(Dictionary<string, CatalogView> views, string name, HeapColumn[] columns, Func<BatchContext, Database, IEnumerable<SqlValue[]>> rows)
    {
        var view = new CatalogView(name, columns, rows);
        views[name] = view;
        views["sys." + name] = view;
    }

    /// <summary>
    /// A modern view's rows as the principal sees them, the view looked up as
    /// it is served — after the registry's reordering and visibility stamping
    /// replaced the instance registration saw.
    /// </summary>
    private static IEnumerable<SqlValue[]> SourceRows(BatchContext batch, Database database, string sourceKey, out CatalogView source)
    {
        source = Simulation.CatalogViews[sourceKey];
        return ApplyMetadataFilter(source, batch, database, source.RowGenerator(batch, database));
    }

    /// <summary>
    /// A catalog view's rows as the principal sees them, with the ordinal of
    /// each column by name — what the system procedures that report over the
    /// catalog read, as real's read the views.
    /// </summary>
    internal static IEnumerable<SqlValue[]> CatalogViewRows(BatchContext batch, Database database, string view, out Func<string, int> ordinal)
    {
        var rows = SourceRows(batch, database, view, out var source);
        ordinal = name => OrdinalOf(source, name);
        return rows;
    }

    private static IEnumerable<SqlValue[]> Project(BatchContext batch, Database database, string sourceKey, Func<CatalogView, Func<SqlValue[], SqlValue[]>> bind)
    {
        var rows = SourceRows(batch, database, sourceKey, out var source);
        var project = bind(source);
        foreach (var row in rows)
            yield return project(row);
    }

    private static SqlValue SmallInt(SqlValue value) => value.IsNull ? SqlValue.Null(SqlType.SmallInt) : SqlValue.FromInt16((short)value.AsInt32);

    private static int Flag(SqlValue value) => !value.IsNull && value.AsBoolean ? 1 : 0;

    /// <summary>
    /// <c>sys.types</c> in the 2000 shape, the database's own types included:
    /// a built-in type keeps its legacy <c>usertype</c>, TDS <c>type</c> code
    /// and character precision (<see cref="SystypesRowData"/>), and a
    /// user-defined one takes its base type's code, its own id as
    /// <c>usertype</c>, its length in characters (or its precision) as
    /// <c>prec</c> and its scale where the base type has one; a table type
    /// is code 0 with <c>prec</c> 0. Each row's collation is the one
    /// <c>sys.types</c> reports (probed 2026-10-15 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSystypes(BatchContext batch, Database database)
    {
        var rows = SourceRows(batch, database, "sys.types", out var source);
        int Ordinal(string name) => OrdinalOf(source, name);
        object?[]? Legacy(int userTypeId) => Array.Find(SystypesRowData, row => Convert.ToInt32(row[3], System.Globalization.CultureInfo.InvariantCulture) == userTypeId);
        var printfmt = SqlValue.Null(VarcharSqlType.Get(255, Collation.Baseline, Coercibility.Implicit));
        foreach (var row in rows)
        {
            var systemTypeId = row[Ordinal("system_type_id")].AsByte;
            var userTypeId = row[Ordinal("user_type_id")].AsInt32;
            var maxLength = row[Ordinal("max_length")].AsInt16;
            var nullable = Flag(row[Ordinal("is_nullable")]) == 1;
            var collationName = row[Ordinal("collation_name")];
            var own = Legacy(userTypeId);
            var baseRow = own ?? Legacy(systemTypeId);
            short? prec;
            byte? scale;
            if (own is not null)
            {
                prec = own[17] is { } legacyPrecision ? Convert.ToInt16(legacyPrecision, System.Globalization.CultureInfo.InvariantCulture) : null;
                scale = own[18] is { } legacyScale ? Convert.ToByte(legacyScale, System.Globalization.CultureInfo.InvariantCulture) : null;
            }
            else if (baseRow is null)
            {
                prec = 0;
                scale = null;
            }
            else
            {
                prec = baseRow[17] is null ? null
                    : systemTypeId is 165 or 167 or 173 or 175 or 231 or 239
                        ? (short)(maxLength == -1 ? -1 : systemTypeId is 231 or 239 ? maxLength / 2 : maxLength)
                        : row[Ordinal("precision")].AsByte;
                scale = baseRow[18] is null ? null : row[Ordinal("scale")].AsByte;
            }
            yield return
            [
                SqlValue.FromSystemName(row[Ordinal("name")].AsString),
                SqlValue.FromByte(systemTypeId),
                SqlValue.FromByte((byte)(nullable ? 0 : 1)),
                SqlValue.FromInt16((short)userTypeId),
                SqlValue.FromInt16(maxLength),
                row[Ordinal("precision")],
                row[Ordinal("scale")],
                row[Ordinal("default_object_id")],
                row[Ordinal("rule_object_id")],
                SmallInt(row[Ordinal("schema_id")]),
                SqlValue.FromInt16(0),
                !collationName.IsNull && Collation.TryGetMetrics(collationName.AsString, out var metrics) ? SqlValue.FromInt32(metrics.CollationId) : SqlValue.Null(SqlType.Int32),
                SqlValue.FromInt16(own is null ? (short)userTypeId : Convert.ToInt16(own[12], System.Globalization.CultureInfo.InvariantCulture)),
                SqlValue.FromBoolean(systemTypeId is 165 or 167 or 231),
                SqlValue.FromBoolean(nullable),
                SqlValue.FromByte(baseRow is null ? (byte)0 : Convert.ToByte(baseRow[15], System.Globalization.CultureInfo.InvariantCulture)),
                printfmt,
                prec is { } p ? SqlValue.FromInt16(p) : SqlValue.Null(SqlType.SmallInt),
                scale is { } sc ? SqlValue.FromByte(sc) : SqlValue.Null(SqlType.TinyInt),
                collationName.IsNull ? SqlValue.Null(SqlType.SystemName) : SqlValue.FromSystemName(collationName.AsString),
            ];
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateSysforeignkeys(BatchContext batch, Database database) =>
        Project(batch, database, "sys.foreign_key_columns", static source =>
        {
            var constraint = OrdinalOf(source, "constraint_object_id");
            var parent = OrdinalOf(source, "parent_object_id");
            var referenced = OrdinalOf(source, "referenced_object_id");
            var parentColumn = OrdinalOf(source, "parent_column_id");
            var referencedColumn = OrdinalOf(source, "referenced_column_id");
            var keyNumber = OrdinalOf(source, "constraint_column_id");
            return row =>
            [
                row[constraint],
                row[parent],
                row[referenced],
                SmallInt(row[parentColumn]),
                SmallInt(row[referencedColumn]),
                SmallInt(row[keyNumber]),
            ];
        });

    /// <summary>
    /// One row per CHECK, DEFAULT, PRIMARY KEY, UNIQUE and FOREIGN KEY
    /// constraint, in object id order: <c>colid</c> the column a CHECK, a
    /// DEFAULT or a single-column FOREIGN KEY names (a table-level CHECK over
    /// one column names it too) and 0 otherwise, and <c>status</c> real's sum — the kind (PK 1, UQ 2, FK 3,
    /// CHECK 4, DEFAULT 5), 16 for a column's constraint and 32 for a table's,
    /// 512 for a clustered key and 1024 for a nonclustered one, 2048,
    /// 131072 when the name is the system's, 16384 when disabled and 2097152
    /// when NOT FOR REPLICATION.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysconstraints(BatchContext batch, Database database)
    {
        var clustered = new HashSet<(int, int)>();
        foreach (var index in SourceRows(batch, database, "sys.indexes", out var indexes))
        {
            if (index[OrdinalOf(indexes, "type")].AsByte == 1)
                _ = clustered.Add((index[OrdinalOf(indexes, "object_id")].AsInt32, index[OrdinalOf(indexes, "index_id")].AsInt32));
        }

        var rows = new SortedList<int, SqlValue[]>();
        void Add(CatalogView source, SqlValue[] row, int kind, int column, bool disabled, bool notForReplication, int keyFlags)
        {
            var id = row[OrdinalOf(source, "object_id")].AsInt32;
            var status = kind + (column != 0 ? 16 : 32) + keyFlags + 2048
                + (Flag(row[OrdinalOf(source, "is_system_named")]) * 131072)
                + (disabled ? 16384 : 0) + (notForReplication ? 2097152 : 0);
            rows[id] =
            [
                SqlValue.FromInt32(id),
                row[OrdinalOf(source, "parent_object_id")],
                SqlValue.FromInt16((short)column),
                SqlValue.FromByte(0),
                SqlValue.FromInt32(status),
                SqlValue.FromInt32(4096),
                SqlValue.FromInt32(0),
            ];
        }

        foreach (var row in SourceRows(batch, database, "sys.check_constraints", out var checks))
            Add(checks, row, 4, row[OrdinalOf(checks, "parent_column_id")].AsInt32, Flag(row[OrdinalOf(checks, "is_disabled")]) == 1, Flag(row[OrdinalOf(checks, "is_not_for_replication")]) == 1, 0);
        foreach (var row in SourceRows(batch, database, "sys.default_constraints", out var defaults))
            Add(defaults, row, 5, row[OrdinalOf(defaults, "parent_column_id")].AsInt32, false, false, 0);
        // A foreign key over one column names it, as real's objects$ property
        // does; a composite one names none.
        var foreignKeyColumns = new Dictionary<int, (int Column, int Count)>();
        foreach (var row in SourceRows(batch, database, "sys.foreign_key_columns", out var keyColumns))
        {
            var id = row[OrdinalOf(keyColumns, "constraint_object_id")].AsInt32;
            foreignKeyColumns[id] = foreignKeyColumns.TryGetValue(id, out var seen)
                ? (seen.Column, seen.Count + 1)
                : (row[OrdinalOf(keyColumns, "parent_column_id")].AsInt32, 1);
        }
        foreach (var row in SourceRows(batch, database, "sys.foreign_keys", out var foreignKeys))
            Add(foreignKeys, row, 3, foreignKeyColumns.TryGetValue(row[OrdinalOf(foreignKeys, "object_id")].AsInt32, out var single) && single.Count == 1 ? single.Column : 0, Flag(row[OrdinalOf(foreignKeys, "is_disabled")]) == 1, Flag(row[OrdinalOf(foreignKeys, "is_not_for_replication")]) == 1, 0);
        foreach (var row in SourceRows(batch, database, "sys.key_constraints", out var keys))
        {
            var isPrimary = row[OrdinalOf(keys, "type")].AsString.StartsWith('P');
            var index = (row[OrdinalOf(keys, "parent_object_id")].AsInt32, row[OrdinalOf(keys, "unique_index_id")].AsInt32);
            Add(keys, row, isPrimary ? 1 : 2, 0, false, false, clustered.Contains(index) ? 512 : 1024);
        }
        return rows.Values;
    }

    /// <summary>
    /// One row per foreign key: its key count and the parent and referenced
    /// column ids in key order, 0 past the count, and the two key bitmaps
    /// NULL as real's definition casts them.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysreferences(BatchContext batch, Database database)
    {
        var columns = new Dictionary<int, List<(int Number, int Parent, int Referenced)>>();
        foreach (var row in SourceRows(batch, database, "sys.foreign_key_columns", out var source))
        {
            var id = row[OrdinalOf(source, "constraint_object_id")].AsInt32;
            if (!columns.TryGetValue(id, out var list))
                columns[id] = list = [];
            list.Add((row[OrdinalOf(source, "constraint_column_id")].AsInt32, row[OrdinalOf(source, "parent_column_id")].AsInt32, row[OrdinalOf(source, "referenced_column_id")].AsInt32));
        }

        var nullKeys = SqlValue.Null(VarbinarySqlType.Get(32));
        var zero = SqlValue.FromInt16(0);
        foreach (var row in SourceRows(batch, database, "sys.foreign_keys", out var foreignKeys))
        {
            var id = row[OrdinalOf(foreignKeys, "object_id")].AsInt32;
            var keys = columns.TryGetValue(id, out var list) ? list.OrderBy(static key => key.Number).ToArray() : [];
            var result = new SqlValue[41];
            result[0] = row[OrdinalOf(foreignKeys, "object_id")];
            result[1] = row[OrdinalOf(foreignKeys, "parent_object_id")];
            result[2] = row[OrdinalOf(foreignKeys, "referenced_object_id")];
            result[3] = SmallInt(row[OrdinalOf(foreignKeys, "key_index_id")]);
            result[4] = SqlValue.FromInt16((short)keys.Length);
            result[5] = nullKeys;
            result[6] = nullKeys;
            result[7] = zero;
            result[8] = zero;
            for (var i = 0; i < 16; i++)
            {
                result[9 + i] = i < keys.Length ? SqlValue.FromInt16((short)keys[i].Parent) : zero;
                result[25 + i] = i < keys.Length ? SqlValue.FromInt16((short)keys[i].Referenced) : zero;
            }
            yield return result;
        }
    }

    /// <summary>
    /// The database's data and log files: <c>status</c> 66 for a log file and
    /// 2 for a data file, plus 4096 when read-only, 1048576 for percentage
    /// growth and 268435456 offline, as real's definition sums them.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysfiles(BatchContext batch, Database database)
    {
        var rows = SourceRows(batch, database, "sys.database_files", out var source);
        var id = OrdinalOf(source, "file_id");
        var type = OrdinalOf(source, "type");
        var space = OrdinalOf(source, "data_space_id");
        var size = OrdinalOf(source, "size");
        var maxSize = OrdinalOf(source, "max_size");
        var growth = OrdinalOf(source, "growth");
        var percent = OrdinalOf(source, "is_percent_growth");
        var readOnly = OrdinalOf(source, "is_read_only");
        var state = OrdinalOf(source, "state");
        var name = OrdinalOf(source, "name");
        var physical = OrdinalOf(source, "physical_name");
        foreach (var row in rows)
        {
            // Rows and log files only: no FILESTREAM or memory-optimized container.
            if (row[type].AsByte > 1)
                continue;
            yield return
            [
                SqlValue.FromInt16((short)(row[id].AsInt32 & 0x7fff)),
                SmallInt(row[space]),
                row[size],
                row[maxSize],
                row[growth],
                SqlValue.FromInt32((row[type].AsByte == 1 ? 66 : 2)
                    + (Flag(row[readOnly]) * 4096)
                    + (Flag(row[percent]) * 1048576)
                    + (row[state].AsByte == 6 ? 268435456 : 0)),
                SqlValue.FromInt32(0),
                SqlValue.FromSystemName(row[name].AsString),
                row[physical],
            ];
        }
    }

    /// <summary>
    /// <c>sys.database_permissions</c> in the 2000 shape, as real's three-part
    /// union builds it: a database or object permission real names an
    /// action for (INSERT, DELETE, EXECUTE and the CREATE / BACKUP
    /// statement permissions) with NULL columns; a SELECT, UPDATE or
    /// REFERENCES held on an object as a whole, with the column bitmap of
    /// every column it also covers; and a column permission's own, the
    /// bitmap setting bit <c>n</c> for column <c>n</c>.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysprotects(BatchContext batch, Database database)
    {
        var permissions = SourceRows(batch, database, "sys.database_permissions", out var source).ToArray();
        var classOrdinal = OrdinalOf(source, "class");
        var major = OrdinalOf(source, "major_id");
        var minor = OrdinalOf(source, "minor_id");
        var grantee = OrdinalOf(source, "grantee_principal_id");
        var grantor = OrdinalOf(source, "grantor_principal_id");
        var type = OrdinalOf(source, "type");
        var state = OrdinalOf(source, "state");
        var nullColumns = SqlValue.Null(VarbinarySqlType.Get(8000));

        static SqlValue[] Row(int id, int granteeId, byte? action, string permissionState, SqlValue columns, int grantorId) =>
        [
            SqlValue.FromInt32(id),
            SqlValue.FromInt16((short)granteeId),
            action is { } a ? SqlValue.FromByte(a) : SqlValue.Null(SqlType.TinyInt),
            permissionState switch
            {
                "D" => SqlValue.FromByte(206),
                "G" => SqlValue.FromByte(205),
                "W" => SqlValue.FromByte(204),
                _ => SqlValue.Null(SqlType.TinyInt),
            },
            columns,
            SqlValue.FromInt16((short)grantorId),
        ];

        foreach (var row in permissions)
        {
            if (row[classOrdinal].AsByte < 2 && row[minor].AsInt32 == 0 && StatementAction(row[type].AsString.TrimEnd()) is { } action)
                yield return Row(row[major].AsInt32, row[grantee].AsInt32, action, row[state].AsString.TrimEnd(), nullColumns, row[grantor].AsInt32);
        }

        // An object-wide SELECT / UPDATE / REFERENCES, its bitmap covering the
        // column rows the same grantee holds from the same grantor.
        foreach (var group in permissions
            .Where(row => row[classOrdinal].AsByte == 1 && ColumnAction(row[type].AsString.TrimEnd()) is not null)
            .Select(row => (Row: row, Object: permissions.FirstOrDefault(o => o[classOrdinal].AsByte == 1 && o[major].AsInt32 == row[major].AsInt32 && o[minor].AsInt32 == 0
                && o[grantee].AsInt32 == row[grantee].AsInt32 && o[grantor].AsInt32 == row[grantor].AsInt32
                && o[type].AsString == row[type].AsString && o[state].AsString.TrimEnd() != "R")))
            .Where(static pair => pair.Object is not null)
            .GroupBy(pair => (Id: pair.Row[major].AsInt32, Grantee: pair.Row[grantee].AsInt32, Grantor: pair.Row[grantor].AsInt32, Type: pair.Row[type].AsString.TrimEnd(), State: pair.Object![state].AsString.TrimEnd())))
        {
            yield return Row(group.Key.Id, group.Key.Grantee, ColumnAction(group.Key.Type), group.Key.State, ColumnBitmap(group.Select(pair => pair.Row[minor].AsInt32)), group.Key.Grantor);
        }

        foreach (var group in permissions
            .Where(row => row[classOrdinal].AsByte == 1 && row[minor].AsInt32 != 0 && row[state].AsString.TrimEnd() != "R")
            .GroupBy(row => (Id: row[major].AsInt32, Grantee: row[grantee].AsInt32, Grantor: row[grantor].AsInt32, Type: row[type].AsString.TrimEnd(), State: row[state].AsString.TrimEnd())))
        {
            yield return Row(group.Key.Id, group.Key.Grantee, ColumnAction(group.Key.Type), group.Key.State, ColumnBitmap(group.Select(row => row[minor].AsInt32)), group.Key.Grantor);
        }
    }

    private static byte? StatementAction(string type) => type switch
    {
        "BADB" => 228,
        "BALO" => 235,
        "CRDB" => 203,
        "CRDF" => 233,
        "CRFN" => 178,
        "CRPR" => 222,
        "CRRU" => 236,
        "CRTB" => 198,
        "CRVW" => 207,
        "DL" => 196,
        "EX" => 224,
        "IN" => 195,
        _ => null,
    };

    private static byte? ColumnAction(string type) => type switch
    {
        "RF" => 26,
        "SL" => 193,
        "UP" => 197,
        _ => null,
    };

    /// <summary>Real's <c>PowerSum</c>: bit <c>n</c> of byte <c>n / 8</c> for each column id, as short as the highest bit allows.</summary>
    private static SqlValue ColumnBitmap(IEnumerable<int> columnIds)
    {
        var ids = columnIds.Distinct().ToArray();
        var bytes = new byte[(ids.Max() / 8) + 1];
        foreach (var id in ids)
            bytes[id / 8] |= (byte)(1 << (id % 8));
        return SqlValue.FromVarbinary(VarbinarySqlType.Get(8000), bytes);
    }

    /// <summary>
    /// One row per (class, securable, grantee, grantor) among the database
    /// and object permissions, every bitmap NULL as real's definition casts
    /// them.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSyspermissions(BatchContext batch, Database database)
    {
        var rows = SourceRows(batch, database, "sys.database_permissions", out var source);
        var classOrdinal = OrdinalOf(source, "class");
        var major = OrdinalOf(source, "major_id");
        var grantee = OrdinalOf(source, "grantee_principal_id");
        var grantor = OrdinalOf(source, "grantor_principal_id");
        var nullSmall = SqlValue.Null(SqlType.SmallInt);
        var nullBitmap = SqlValue.Null(VarbinarySqlType.Get(4000));
        foreach (var (_, id, granteeId, grantorId) in rows
            .Where(row => row[classOrdinal].AsByte < 2)
            .Select(row => (Class: row[classOrdinal].AsByte, Id: row[major].AsInt32, Grantee: row[grantee].AsInt32, Grantor: row[grantor].AsInt32))
            .Distinct())
        {
            yield return
            [
                SqlValue.FromInt32(id),
                SqlValue.FromInt16((short)granteeId),
                SqlValue.FromInt16((short)grantorId),
                nullSmall,
                nullSmall,
                nullBitmap,
                nullBitmap,
                nullBitmap,
                nullBitmap,
                nullBitmap,
                nullBitmap,
            ];
        }
    }

    /// <summary>
    /// Registers the server-scope compatibility views: <c>sysdatabases</c>,
    /// <c>syslogins</c>, <c>sysaltfiles</c>, <c>syscurconfigs</c>,
    /// <c>syscharsets</c>, <c>sysmessages</c>, <c>sysremotelogins</c>,
    /// <c>sysoledbusers</c>, <c>sysdevices</c> and <c>sysopentapes</c>, each
    /// as real defines it and as visible as its source (probed 2026-10-15
    /// against SQL Server 2025).
    /// </summary>
    private static void RegisterServerCompatibilityProjections(Dictionary<string, CatalogView> views)
    {
        var sysname = SqlType.SystemName;
        var path = NVarcharSqlType.Get(260, Collation.Baseline, Coercibility.Implicit);
        var sid = VarbinarySqlType.Get(85);

        Compatibility(views, "sysdatabases",
        [
            new("name", sysname, 128, false),
            new("dbid", SqlType.SmallInt, null, true),
            new("sid", sid, 85, true),
            new("mode", SqlType.SmallInt, null, true),
            new("status", SqlType.Int32, null, true),
            new("status2", SqlType.Int32, null, true),
            new("crdate", SqlType.DateTime, null, false),
            new("reserved", SqlType.DateTime, null, true),
            new("category", SqlType.Int32, null, true),
            new("cmptlevel", SqlType.TinyInt, null, false),
            new("filename", path, 260, true),
            new("version", SqlType.SmallInt, null, true),
        ], EnumerateSysdatabases);

        HeapColumn[] loginColumns =
        [
            new("sid", sid, 85, true),
            new("status", SqlType.SmallInt, null, true),
            new("createdate", SqlType.DateTime, null, false),
            new("updatedate", SqlType.DateTime, null, false),
            new("accdate", SqlType.DateTime, null, false),
            new("totcpu", SqlType.Int32, null, true),
            new("totio", SqlType.Int32, null, true),
            new("spacelimit", SqlType.Int32, null, true),
            new("timelimit", SqlType.Int32, null, true),
            new("resultlimit", SqlType.Int32, null, true),
            new("name", sysname, 128, false),
            new("dbname", sysname, 128, true),
            new("password", sysname, 128, true),
            new("language", sysname, 128, true),
            new("denylogin", SqlType.Int32, null, true),
            new("hasaccess", SqlType.Int32, null, true),
            new("isntname", SqlType.Int32, null, true),
            new("isntgroup", SqlType.Int32, null, true),
            new("isntuser", SqlType.Int32, null, true),
            .. SysloginsRoleColumns.Select(static role => new HeapColumn(role, SqlType.Int32, null, true)),
            new("loginname", sysname, 128, false),
        ];
        Compatibility(views, "syslogins", loginColumns, EnumerateSyslogins);

        Compatibility(views, "sysaltfiles",
        [
            new("fileid", SqlType.SmallInt, null, true),
            new("groupid", SqlType.SmallInt, null, true),
            new("size", SqlType.Int32, null, false),
            new("maxsize", SqlType.Int32, null, false),
            new("growth", SqlType.Int32, null, false),
            new("status", SqlType.Int32, null, true),
            new("perf", SqlType.Int32, null, true),
            new("dbid", SqlType.SmallInt, null, true),
            new("name", NVarcharSqlType.Get(128, Collation.Baseline, Coercibility.Implicit), 128, true),
            new("filename", path, 260, true),
        ], EnumerateSysaltfiles);

        Compatibility(views, "syscurconfigs",
        [
            new("value", SqlType.Int32, null, false),
            new("config", SqlType.SmallInt, null, true),
            new("comment", NVarcharSqlType.Get(255, Collation.Baseline, Coercibility.Implicit), 255, false),
            new("status", SqlType.SmallInt, null, true),
        ], (batch, database) => Project(batch, database, "sys.configurations", static source =>
        {
            var id = OrdinalOf(source, "configuration_id");
            var inUse = OrdinalOf(source, "value_in_use");
            var description = OrdinalOf(source, "description");
            var dynamic = OrdinalOf(source, "is_dynamic");
            var advanced = OrdinalOf(source, "is_advanced");
            return row =>
            [
                SqlValue.FromInt32(row[inUse].CoerceTo(SqlType.Int32).AsInt32),
                SmallInt(row[id]),
                SqlValue.FromString(NVarcharSqlType.Get(255, Collation.Baseline, Coercibility.Implicit), row[description].AsString),
                SqlValue.FromInt16((short)(Flag(row[dynamic]) + (Flag(row[advanced]) * 2))),
            ];
        }));

        var charsetName = NVarcharSqlType.Get(128, Collation.Baseline, Coercibility.Implicit);
        var charsetDescription = NVarcharSqlType.Get(255, Collation.Baseline, Coercibility.Implicit);
        var charsetRows = Array.ConvertAll(Charsets, charset => new SqlValue[]
        {
            SqlValue.FromInt16(charset.Type),
            SqlValue.FromByte(charset.Id),
            SqlValue.FromByte(charset.CsId),
            SqlValue.FromInt16(0),
            SqlValue.FromString(charsetName, charset.Name),
            SqlValue.FromString(charsetDescription, charset.Description),
            SqlValue.Null(VarbinarySqlType.Get(6000)),
            SqlValue.Null(SqlType.Image),
        });
        Compatibility(views, "syscharsets",
        [
            new("type", SqlType.SmallInt, null, false),
            new("id", SqlType.TinyInt, null, false),
            new("csid", SqlType.TinyInt, null, false),
            new("status", SqlType.SmallInt, null, true),
            new("name", charsetName, 128, false),
            new("description", charsetDescription, 255, false),
            new("binarydefinition", VarbinarySqlType.Get(6000), 6000, true),
            new("definition", SqlType.Image, null, true),
        ], (_, _) => charsetRows);

        var messageText = NVarcharSqlType.Get(255, Collation.Baseline, Coercibility.Implicit);
        Compatibility(views, "sysmessages",
        [
            new("error", SqlType.Int32, null, false),
            new("severity", SqlType.TinyInt, null, true),
            new("dlevel", SqlType.SmallInt, null, true),
            new("description", messageText, 255, true),
            new("msglangid", SqlType.SmallInt, null, false),
        ], (batch, database) => Project(batch, database, "sys.messages", source =>
        {
            var id = OrdinalOf(source, "message_id");
            var severity = OrdinalOf(source, "severity");
            var logged = OrdinalOf(source, "is_event_logged");
            var text = OrdinalOf(source, "text");
            var language = OrdinalOf(source, "language_id");
            return row =>
            [
                row[id],
                row[severity],
                SqlValue.FromInt16((short)(Flag(row[logged]) * 128)),
                SqlValue.FromString(messageText, row[text].AsString.Length > 255 ? row[text].AsString[..255] : row[text].AsString),
                row[language],
            ];
        }));

        Compatibility(views, "sysremotelogins",
        [
            new("remoteserverid", SqlType.SmallInt, null, true),
            new("remoteusername", sysname, 128, true),
            new("status", SqlType.SmallInt, null, true),
            new("sid", sid, 85, true),
            new("changedate", SqlType.DateTime, null, false),
        ], (batch, database) => RemoteLoginRows(batch, database, "sys.remote_logins", oledb: false));

        Compatibility(views, "sysoledbusers",
        [
            new("rmtsrvid", SqlType.SmallInt, null, true),
            new("rmtloginame", sysname, 128, true),
            new("rmtpassword", sysname, 128, true),
            new("loginsid", sid, 85, true),
            new("status", SqlType.SmallInt, null, true),
            new("changedate", SqlType.DateTime, null, false),
        ], (batch, database) => RemoteLoginRows(batch, database, "sys.linked_logins", oledb: true));

        // The logical backup devices, each a dump device (status 16) of no
        // size; open tapes aren't modeled, so that view is empty, as a server
        // with none is.
        Compatibility(views, "sysdevices",
        [
            new("name", sysname, 128, false),
            new("size", SqlType.Int32, null, true),
            new("low", SqlType.Int32, null, true),
            new("high", SqlType.Int32, null, true),
            new("status", SqlType.SmallInt, null, true),
            new("cntrltype", SqlType.SmallInt, null, true),
            new("phyname", path, 260, true),
        ], static (batch, _) => batch.Connection.Simulation.LogicalBackupDevicesInOrder().Select(static device => new[]
        {
            SqlValue.FromSystemName(device.Name),
            SqlValue.FromInt32(0),
            SqlValue.FromInt32(0),
            SqlValue.FromInt32(0),
            SqlValue.FromInt16(16),
            SqlValue.FromInt16(device.Type),
            SqlValue.FromNVarchar(device.PhysicalName),
        }));
        Compatibility(views, "sysopentapes",
        [
            new("openTape", NVarcharSqlType.Get(64, Collation.Baseline, Coercibility.Implicit), 64, false),
        ], static (_, _) => EmptyCatalogRows);
    }

    /// <summary>The fixed server roles <c>syslogins</c> flags, in column order and in <c>role_principal_id</c> order 3 through 20.</summary>
    private static readonly string[] SysloginsRoleColumns =
    [
        "sysadmin", "securityadmin", "serveradmin", "setupadmin", "processadmin", "diskadmin", "dbcreator", "bulkadmin",
        "##MS_ServerStateReader##", "##MS_ServerStateManager##", "##MS_DefinitionReader##", "##MS_DatabaseConnector##",
        "##MS_DatabaseManager##", "##MS_LoginManager##", "##MS_SecurityDefinitionReader##", "##MS_PermissionDefinitionReader##",
        "##MS_ServerSecurityStateReader##", "##MS_ServerPermissionStateReader##",
    ];

    /// <summary>
    /// One row per database <c>sys.databases</c> shows, its options packed as
    /// real's <c>status</c> (auto close 1, bulk-logged 4, simple 8, torn-page
    /// 16, offline 512, read-only 1024, restricted 2048, single-user 4096,
    /// emergency 32768 with 256, checksum 65536, auto shrink 4194304, and
    /// 0x40000000 read-only or offline) and <c>status2</c> (DB_CHAINING 0x400,
    /// NUMERIC_ROUNDABORT 0x800, ARITHABORT 0x1000, ANSI_PADDING 0x2000,
    /// ANSI_NULL_DEFAULT 0x4000, CONCAT_NULL_YIELDS_NULL 0x10000,
    /// RECURSIVE_TRIGGERS 0x20000, local cursors 0x100000, QUOTED_IDENTIFIER
    /// 0x800000, auto create statistics 0x1000000, CURSOR_CLOSE_ON_COMMIT
    /// 0x2000000, ANSI_NULLS 0x4000000, ANSI_WARNINGS 0x10000000, full-text
    /// 0x20000000, auto update statistics 0x40000000) — each bit set by its
    /// option alone on a probe database.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysdatabases(BatchContext batch, Database database)
    {
        var rows = SourceRows(batch, database, "sys.databases", out var source);
        int Ordinal(string name) => OrdinalOf(source, name);
        bool On(SqlValue[] row, string name) => Flag(row[Ordinal(name)]) == 1;
        var reserved = SqlValue.FromDateTime(new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var path = NVarcharSqlType.Get(260, Collation.Baseline, Coercibility.Implicit);
        foreach (var row in rows)
        {
            var name = row[Ordinal("name")].AsString;
            var state = row[Ordinal("state")].AsByte;
            var readOnly = On(row, "is_read_only");
            var status = (On(row, "is_auto_close_on") ? 1 : 0)
                + row[Ordinal("recovery_model")].AsByte switch { 2 => 4, 3 => 8, _ => 0 }
                + row[Ordinal("page_verify_option")].AsByte switch { 1 => 16, 2 => 65536, _ => 0 }
                + (state == 6 ? 512 : 0)
                + (readOnly ? 1024 : 0)
                + row[Ordinal("user_access")].AsByte switch { 1 => 4096, 2 => 2048, _ => 0 }
                + (state == 5 ? 32768 + 256 : 0)
                + (On(row, "is_auto_shrink_on") ? 4194304 : 0)
                + (readOnly || state == 6 ? 0x40000000 : 0);
            var status2 = (On(row, "is_db_chaining_on") ? 0x400 : 0)
                + (On(row, "is_numeric_roundabort_on") ? 0x800 : 0)
                + (On(row, "is_arithabort_on") ? 0x1000 : 0)
                + (On(row, "is_ansi_padding_on") ? 0x2000 : 0)
                + (On(row, "is_ansi_null_default_on") ? 0x4000 : 0)
                + (On(row, "is_concat_null_yields_null_on") ? 0x10000 : 0)
                + (On(row, "is_recursive_triggers_on") ? 0x20000 : 0)
                + (On(row, "is_local_cursor_default") ? 0x100000 : 0)
                + (On(row, "is_quoted_identifier_on") ? 0x800000 : 0)
                + (On(row, "is_auto_create_stats_on") ? 0x1000000 : 0)
                + (On(row, "is_cursor_close_on_commit_on") ? 0x2000000 : 0)
                + (On(row, "is_ansi_nulls_on") ? 0x4000000 : 0)
                + (On(row, "is_ansi_warnings_on") ? 0x10000000 : 0)
                + (On(row, "is_fulltext_enabled") ? 0x20000000 : 0)
                + (On(row, "is_auto_update_stats_on") ? 0x40000000 : 0);
            var primary = batch.Connection.Simulation.Databases.TryGetValue(name, out var target)
                ? target.Files.Find(static file => file.FileId == 1)
                : null;
            yield return
            [
                SqlValue.FromSystemName(name),
                SmallInt(row[Ordinal("database_id")]),
                row[Ordinal("owner_sid")],
                SqlValue.FromInt16(0),
                SqlValue.FromInt32(status),
                SqlValue.FromInt32(status2),
                row[Ordinal("create_date")],
                reserved,
                SqlValue.FromInt32((On(row, "is_published") ? 1 : 0) + (On(row, "is_subscribed") ? 2 : 0) + (On(row, "is_merge_published") ? 4 : 0) + (On(row, "is_distributor") ? 16 : 0)),
                row[Ordinal("compatibility_level")],
                primary is null ? SqlValue.Null(path) : SqlValue.FromString(path, primary.PhysicalName),
                SqlValue.FromInt16(998),
            ];
        }
    }

    /// <summary>
    /// One row per login <c>sys.server_principals</c> shows, a role excluded:
    /// <c>status</c> 9 when its CONNECT SQL from <c>sa</c> is granted and 10
    /// otherwise, <c>hasaccess</c> / <c>denylogin</c> from that grant, and a
    /// flag for each fixed server role it is a member of.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSyslogins(BatchContext batch, Database database)
    {
        var connect = new Dictionary<int, string>();
        foreach (var row in SourceRows(batch, database, "sys.server_permissions", out var permissions))
        {
            if (row[OrdinalOf(permissions, "class")].AsByte == 100 && row[OrdinalOf(permissions, "type")].AsString.TrimEnd() == "COSQ"
                && row[OrdinalOf(permissions, "grantor_principal_id")].AsInt32 == 1)
            {
                connect[row[OrdinalOf(permissions, "grantee_principal_id")].AsInt32] = row[OrdinalOf(permissions, "state")].AsString.TrimEnd();
            }
        }
        var members = new HashSet<(int Member, int Role)>();
        foreach (var row in SourceRows(batch, database, "sys.server_role_members", out var memberships))
            _ = members.Add((row[OrdinalOf(memberships, "member_principal_id")].AsInt32, row[OrdinalOf(memberships, "role_principal_id")].AsInt32));

        var principals = SourceRows(batch, database, "sys.server_principals", out var source);
        int Ordinal(string name) => OrdinalOf(source, name);
        foreach (var row in principals)
        {
            var type = row[Ordinal("type")].AsString.TrimEnd();
            if (type == "R")
                continue;
            var id = row[Ordinal("principal_id")].AsInt32;
            var state = connect.GetValueOrDefault(id);
            var granted = state is "G" or "W";
            var name = row[Ordinal("name")].AsString;
            var result = new List<SqlValue>(38)
            {
                row[Ordinal("sid")],
                SqlValue.FromInt16((short)(8 + (granted ? 1 : 2))),
                row[Ordinal("create_date")],
                row[Ordinal("modify_date")],
                row[Ordinal("create_date")],
                SqlValue.FromInt32(0),
                SqlValue.FromInt32(0),
                SqlValue.FromInt32(0),
                SqlValue.FromInt32(0),
                SqlValue.FromInt32(0),
                SqlValue.FromSystemName(name),
                row[Ordinal("default_database_name")],
                SqlValue.Null(SqlType.SystemName),
                row[Ordinal("default_language_name")],
                SqlValue.FromInt32(state == "D" ? 1 : 0),
                SqlValue.FromInt32(granted ? 1 : 0),
                SqlValue.FromInt32(type is "G" or "U" ? 1 : 0),
                SqlValue.FromInt32(type == "G" ? 1 : 0),
                SqlValue.FromInt32(type == "U" ? 1 : 0),
            };
            for (var role = 3; role < 3 + SysloginsRoleColumns.Length; role++)
                result.Add(SqlValue.FromInt32(members.Contains((id, role)) ? 1 : 0));
            result.Add(SqlValue.FromSystemName(name));
            yield return [.. result];
        }
    }

    /// <summary>
    /// Every database's data and log files, as <c>sysfiles</c> lists one
    /// database's, with its id — readable with <c>CONTROL SERVER</c> alone
    /// (the gate is <see cref="DmvGateKind.ControlServer"/>).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysaltfiles(BatchContext batch, Database database)
    {
        var rows = SourceRows(batch, database, "sys.master_files", out var source);
        int Ordinal(string name) => OrdinalOf(source, name);
        var name = NVarcharSqlType.Get(128, Collation.Baseline, Coercibility.Implicit);
        foreach (var row in rows)
        {
            if (row[Ordinal("type")].AsByte > 1)
                continue;
            yield return
            [
                SqlValue.FromInt16((short)(row[Ordinal("file_id")].AsInt32 & 0x7fff)),
                SmallInt(row[Ordinal("data_space_id")]),
                row[Ordinal("size")],
                row[Ordinal("max_size")],
                row[Ordinal("growth")],
                SqlValue.FromInt32((row[Ordinal("type")].AsByte == 1 ? 66 : 2)
                    + (Flag(row[Ordinal("is_read_only")]) * 4096)
                    + (Flag(row[Ordinal("is_percent_growth")]) * 1048576)
                    + (row[Ordinal("state")].AsByte == 6 ? 268435456 : 0)),
                SqlValue.FromInt32(0),
                SmallInt(row[Ordinal("database_id")]),
                SqlValue.FromString(name, row[Ordinal("name")].AsString),
                row[Ordinal("physical_name")],
            ];
        }
    }

    /// <summary>
    /// <c>sysremotelogins</c> over <c>sys.remote_logins</c> and
    /// <c>sysoledbusers</c> over <c>sys.linked_logins</c>, each naming its
    /// local login by <c>sid</c>.
    /// </summary>
    private static IEnumerable<SqlValue[]> RemoteLoginRows(BatchContext batch, Database database, string sourceKey, bool oledb)
    {
        var sids = new Dictionary<int, SqlValue>();
        foreach (var row in SourceRows(batch, database, "sys.server_principals", out var principals))
            sids[row[OrdinalOf(principals, "principal_id")].AsInt32] = row[OrdinalOf(principals, "sid")];
        var nullSid = SqlValue.Null(VarbinarySqlType.Get(85));
        var rows = SourceRows(batch, database, sourceKey, out var source);
        int Ordinal(string name) => OrdinalOf(source, name);
        foreach (var row in rows)
        {
            var local = row[Ordinal("local_principal_id")];
            var sid = !local.IsNull && sids.TryGetValue(local.AsInt32, out var found) ? found : nullSid;
            var remoteName = row[Ordinal("remote_name")].IsNull ? SqlValue.Null(SqlType.SystemName) : SqlValue.FromSystemName(row[Ordinal("remote_name")].AsString);
            yield return oledb
                ?
                [
                    SmallInt(row[Ordinal("server_id")]),
                    remoteName,
                    SqlValue.Null(SqlType.SystemName),
                    sid,
                    SqlValue.FromInt16((short)Flag(row[Ordinal("uses_self_credential")])),
                    row[Ordinal("modify_date")],
                ]
                :
                [
                    SmallInt(row[Ordinal("server_id")]),
                    remoteName,
                    SqlValue.FromInt16(0),
                    sid,
                    row[Ordinal("modify_date")],
                ];
        }
    }

    /// <summary><c>syscharsets</c>' rows: the character sets and sort orders a SQL Server 2025 instance lists, copied from it (probed 2026-10-15).</summary>
    private static readonly (short Type, byte Id, byte CsId, string Name, string Description)[] Charsets =
    [
        (1001, 1, 0, "iso_1", "ISO 8859-1 (Latin-1) - Western European 8-bit character set."),
        (1001, 2, 0, "cp850", "Code Page 850 (Multilingual) character set."),
        (1001, 3, 0, "cp437", "Code Page 437 (United States) character set."),
        (1001, 4, 0, "cp1250", "Code page 1250"),
        (1001, 5, 0, "cp1251", "Code Page 1251"),
        (1001, 6, 0, "cp1253", "Code page 1253"),
        (1001, 7, 0, "cp1254", "Code page 1254"),
        (1001, 8, 0, "cp1255", "Code page 1255"),
        (1001, 9, 0, "cp1256", "Code page 1256"),
        (1001, 10, 0, "cp1257", "Code page 1257"),
        (1001, 12, 0, "cp949", "Code page 949"),
        (1001, 13, 0, "cp950", "Code page 950"),
        (1001, 14, 0, "cp936", "Code page 936"),
        (1001, 15, 0, "cp874", "Code page 874"),
        (1001, 16, 0, "cp1258", "Code page 1258"),
        (1001, 108, 0, "utf8", "Code page 65001"),
        (1001, 109, 0, "cp932", "Code page 932"),
        (2001, 30, 3, "bin_cp437", "Binary Sort Order for the Code Page 437 Character Set"),
        (2001, 31, 3, "dictionary_437", "General purpose dictionary sort order for use in the US.  Uses the Code Page 437 character set and is case-sensitive."),
        (2001, 32, 3, "nocase_437", "Case-insensitive dictionary sort order for use in the US.  Uses the Code Page 437 character set."),
        (2001, 33, 3, "nocasepref_437", "Case-insensitive dictionary sort order, with case preference for collating purposes, for use in the US.  Uses the Code Page 437 character set."),
        (2001, 34, 3, "noaccents_437", "Case-insensitive dictionary sort order for use in the US.  Uses the Code Page 437 character set.  Diacritical marks are ignored."),
        (2001, 40, 2, "bin_cp850", "Binary Sort Order for the CodePage 850 Character Set"),
        (2001, 41, 2, "dictionary_850", "General purpose dictionary sort order for use with several Western-European languages including English, French, and German.  Uses the Code Page 850 character set and is case-sensitive."),
        (2001, 42, 2, "nocase_850", "Case-insensitive dictionary sort order for use with several Western-European languages including English, French, and German.  Uses the Code Page 850 character set."),
        (2001, 43, 2, "nocasepref_850", "Case-insensitive dictionary sort order, with case preference for collating purposes, for use with several Western-European languages including English, French, and German.  Uses the Code Page 850 character set."),
        (2001, 44, 2, "noaccents_850", "Case-insensitive dictionary sort order for use with several Western-European languages including English, French, and German.  Uses the Code Page 850 character set.  Diacritical marks are ignored."),
        (2001, 49, 2, "caseless_34", "Case-insensitive sort order for 3.4.1 caseless database."),
        (2001, 50, 1, "bin_iso_1", "Binary Sort Order for the ISO 8859-1 (Latin 1) Character Set"),
        (2001, 51, 1, "dictionary_iso", "General purpose dictionary sort order for use with several Western-European languages including English, French, and German.  Uses the ISO 8859-1 character set and is case-sensitive."),
        (2001, 52, 1, "nocase_iso", "Case-insensitive dictionary sort order for use with several Western-European languages including English, French, and German.  Uses the ISO 8859-1 character set."),
        (2001, 53, 1, "nocasepref_iso", "Case-insensitive dictionary sort order, with case preference for collating purposes, for use with several Western-European languages including English, French, and German.  Uses the ISO 8859-1 character set."),
        (2001, 54, 1, "noaccents_iso", "Case-insensitive dictionary sort order for use with several Western-European languages including English, French, and German.  Uses the ISO 8859-1 character set.  Diacritical marks are ignored."),
        (2001, 55, 2, "alt_dictionary", "General purpose dictionary sort order for use with several Western-European languages. Uses the Code Page 850 character set and is case-sensitive."),
        (2001, 56, 2, "alt_nocasepref", "Case-insensitive dictionary sort order, with case preference for collating purposes, for use with several Western-European languages including English, French, and German.  Uses the Code Page 850 character set."),
        (2001, 57, 2, "alt_noaccents", "Case-insensitive dictionary sort order for most of the European languages.  Uses the Code Page 850 character set.  Diacritical marks are ignored."),
        (2001, 58, 2, "scand_nocasepref", "Case-insensitive Scandinavian dictionary sort order, with  case preference for collating purposes.  Uses the Code Page 850  character set."),
        (2001, 59, 2, "scand_dictionary", "General purpose Scandinavian dictionary sort order.  Uses the Code Page 850 character set and is case-sensitive."),
        (2001, 60, 2, "scand_nocase", "Case-insensitive Scandinavian dictionary sort order, without  case preference for collating purposes.  Uses the Code Page 850  character set."),
        (2001, 61, 2, "alt_nocase", "Case-insensitive dictionary sort order, without case preference for collating purposes, for use with several Western-European languages including English, French, and German.  Uses the Code Page 850 character set."),
        (2001, 71, 1, "dictionary_1252", "NT Latin-1 case sensitive sort order for code page 1252"),
        (2001, 72, 1, "nocase_1252", "NT Latin-1 case insensitive sort order for code page 1252"),
        (2001, 80, 4, "bin_cp1250", "Binary Sort Order for the Code Page 1250 Character Set"),
        (2001, 81, 4, "dictionary_1250", "Dictionary case-sensitive sort order for use with code page 1250"),
        (2001, 82, 4, "nocase_1250", "Dictionary case-insensitive sort order for use with code page 1250"),
        (2001, 83, 4, "csydic", "Czech dictionary case-sensitive sort order for use with code page 1250"),
        (2001, 84, 4, "csync", "Czech dictionary case-insensitive sort order for use with code page 1250"),
        (2001, 85, 4, "hundic", "Hungarian dictionary case-sensitive sort order for use with code page 1250"),
        (2001, 86, 4, "hunnc", "Hungarian dictionary case-insensitive sort order for use with code page 1250"),
        (2001, 87, 4, "plkdic", "Polish dictionary case-sensitive sort order for use with code page 1250"),
        (2001, 88, 4, "plknc", "Polish dictionary case-insensitive sort order for use with code page 1250"),
        (2001, 89, 4, "romdic", "Romanian dictionary case-sensitive sort order for use with code page 1250"),
        (2001, 90, 4, "romnc", "Romanian dictionary case-insensitive sort order for use with code page 1250"),
        (2001, 91, 4, "shldic", "Croatian dictionary case-sensitive sort order for use with code page 1250"),
        (2001, 92, 4, "shlnc", "Croatian dictionary case-insensitive sort order for use with code page 1250"),
        (2001, 93, 4, "skydic", "Slovak dictionary case-sensitive sort order for use with code page 1250"),
        (2001, 94, 4, "skync", "Slovak dictionary case-insensitive sort order for use with code page 1250"),
        (2001, 95, 4, "slvdic", "Slovenian dictionary case-sensitive sort order for use with code page 1250"),
        (2001, 96, 4, "slvnc", "Slovenian dictionary case-insensitive sort order for use with code page 1250"),
        (2001, 104, 5, "bin_cp1251", "Binary Sort Order for the Code Page 1251 Character Set"),
        (2001, 105, 5, "dictionary_251", "Dictionary case-sensitive sort order for use with code page 1251"),
        (2001, 106, 5, "nocase_1251", "Dictionary case-insensitive sort order for use with code page 1251"),
        (2001, 107, 5, "ukrdic", "Ukrainian dictionary case-sensitive sort order for use with code page 1251"),
        (2001, 108, 5, "ukrnc", "Ukrainian dictionary case-insensitive sort order for use with code page 1251"),
        (2001, 112, 6, "bin_cp1253", "Binary Sort Order for the Code Page 1253 Character Set"),
        (2001, 113, 6, "dictionary_1253", "Greek case sensitive dictionary sort order for use with code page 1253"),
        (2001, 114, 6, "nocase_1253", "Greek case insensitive dictionary sort order for use with code page 1253"),
        (2001, 120, 6, "greek_mixeddictionary", "Greek English mixed dictionary sort order. Uses the ISO 8859-7 (ANSI 1253) Greek Windows character set and is case-sensitive."),
        (2001, 122, 6, "greek_altdictionary2", "Greek dictionary sort order for use with Greek and English. Uses the ISO 8859-7 (ANSI 1253) Greek Windows character set and is case-sensitive."),
        (2001, 124, 6, "greek_nocasedict", "Greek dictionary sort order for use with Greek and English. Uses the ISO 8859-7 (ANSI 1253) Greek Windows character set and is not case-sensitive."),
        (2001, 128, 7, "bin_cp1254", "Binary Sort Order for the Code Page 1254 Character Set"),
        (2001, 129, 7, "dictionary_1254", "Case sensitive dictionary sort order for use with code page 1254"),
        (2001, 130, 7, "nocase_1254", "Case insensitive dictionary order for use with code page 1254"),
        (2001, 136, 8, "bin_cp1255", "Binary Sort Order for the Code Page 1255 Character Set"),
        (2001, 137, 8, "dictionary_1255", "Case-sensitive dictionary sort order for use with code page 1255"),
        (2001, 138, 8, "nocase_1255", "Case-insensitive dictionary sort order for use with code page 1255"),
        (2001, 144, 9, "bin_cp1256", "Binary Sort Order for the Code Page 1256 Character Set"),
        (2001, 145, 9, "dictionary_1256", "Case-sensitive dictionary order for use with code page 1256"),
        (2001, 146, 9, "nocase_1256", "Case-insensitive dictionary sort order for use with code page 1256"),
        (2001, 152, 10, "bin_cp1257", "Binary Sort Order for the Code Page 1257 Character Set"),
        (2001, 153, 10, "dictionary_1257", "Case sensitive sort order for use with code page 1257"),
        (2001, 154, 10, "nocase_1257", "Case insensitive dictionary order for use with code page 1257"),
        (2001, 155, 10, "etidic", "Estonian case sensitive dictionary sort order for use with code page 1257"),
        (2001, 156, 10, "etinc", "Estonian case insensitive dictionary sort order for use with code page 1257"),
        (2001, 157, 10, "lvidic", "Latvian case sensitive dictionary sort order for use with code page 1257"),
        (2001, 158, 10, "lvinc", "Latvian case insensitive dictionary sort order for use with code page 1257"),
        (2001, 159, 10, "lthdic", "Lithuanian case sensitive dictionary sort order for use with code page 1257"),
        (2001, 160, 10, "lthnc", "Lithuanian case insensitive dictionary sort order for use with code page 1257"),
        (2001, 183, 1, "danno_nocasepref", "Case-insensitive Danish/Norwegian dictionary sort order, with case preference for collating purposes. Uses the ISO 8859-1 character set with Windows 3.1 additional characters."),
        (2001, 184, 1, "svfi1_nocasepref", "Case-insensitive Swedish/Finnish dictionary sort order, with case preference for collating purposes. Uses the ISO 8859-1 character set with Windows 3.1 additional characters. The letters V and W are treated as variants of the same letter."),
        (2001, 185, 1, "svfi2_nocasepref", "Case-insensitive Swedish/Finnish dictionary sort order, with case preference for collating purposes. Uses the ISO 8859-1 character set with Windows 3.1 additional characters. The letters V and W are treated as separate letters."),
        (2001, 186, 1, "islan_nocasepref", "Case-insensitive Icelandic dictionary sort order, with case preference for collating purposes. Uses the ISO 8859-1 character set with Windows 3.1 additional characters."),
        (2001, 192, 109, "bin_cp932", "Binary Sort Order for the Code Page 932 Character Set"),
        (2001, 193, 109, "nls_cp932", "NLS Case-Insensitive Sort Order for the Code Page 932 Character Set"),
        (2001, 194, 12, "bin_cp949", "Binary Sort Order for the Code Page 949 Character Set"),
        (2001, 195, 12, "nls_cp949", "NLS Case-Insensitive Sort Order for the Code Page 949 Character Set"),
        (2001, 196, 13, "bin_cp950", "Binary Sort Order for the Code Page 950 Character Set"),
        (2001, 197, 13, "nls_cp950", "NLS Case-Insensitive Sort Order for the Code Page 950 Character Set"),
        (2001, 198, 14, "bin_cp936", "Binary Sort Order for the Code Page 936 Character Set"),
        (2001, 199, 14, "nls_cp936", "NLS Case-Insensitive Sort Order for the Code Page 936 Character Set"),
        (2001, 200, 109, "nls_cp932_cs", "NLS Case-Sensitive Sort Order for the Code Page 932 Character Set"),
        (2001, 201, 12, "nls_cp949_cs", "NLS Case-Sensitive Sort Order for the Code Page 949 Character Set"),
        (2001, 202, 13, "nls_cp950_cs", "NLS Case-Sensitive Sort Order for the Code Page 950 Character Set"),
        (2001, 203, 14, "nls_cp936_cs", "NLS Case-Sensitive Sort Order for the Code Page 936 Character Set"),
        (2001, 204, 15, "bin_cp874", "Binary Sort Order for the Code Page 874 Character Set"),
        (2001, 205, 15, "nls_cp874", "NLS Case-Insensitive Sort Order for the Code Page 874 Character Set"),
        (2001, 206, 15, "nls_cp874_cs", "NLS Case-Sensitive Sort Order for the Code Page 874 Character Set"),
        (2001, 210, 1, "EBCDIC_037", "EBCDIC 037 code page sort - United States/Canada"),
        (2001, 211, 1, "EBCDIC_273", "EBCDIC 273 code page sort - Austria/Germany F.R."),
        (2001, 212, 1, "EBCDIC_277", "EBCDIC 277 code page sort - Denmark/Norway"),
        (2001, 213, 1, "EBCDIC_278", "EBCDIC 278 code page sort - Finland/Sweden"),
        (2001, 214, 1, "EBCDIC_280", "EBCDIC 280 code page sort - Italy"),
        (2001, 215, 1, "EBCDIC_284", "EBCDIC 284 code page sort - Latin American/Spain"),
        (2001, 216, 1, "EBCDIC_285", "EBCDIC 285 code page sort - United Kingdom"),
        (2001, 217, 1, "EBCDIC_297", "EBCDIC 297 code page sort - France"),
        (2001, 218, 1, "EBCDIC_277", "EBCDIC 277 code page sort - Denmark/Norway"),
        (2001, 219, 1, "EBCDIC_1141", "EBCDIC 1141 code page sort - Austria/Germany"),
    ];
}
