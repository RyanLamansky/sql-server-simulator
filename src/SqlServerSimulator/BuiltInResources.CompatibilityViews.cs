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
    /// <c>sysfilegroups</c>, <c>sysfulltextcatalogs</c>, <c>sysprotects</c>
    /// and <c>syspermissions</c> — under their bare and <c>sys.</c> keys, so
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
        var path = NVarcharSqlType.Get(260, Collation.Catalog, Coercibility.Implicit);

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

    private static IEnumerable<SqlValue[]> Project(BatchContext batch, Database database, string sourceKey, Func<CatalogView, Func<SqlValue[], SqlValue[]>> bind)
    {
        var rows = SourceRows(batch, database, sourceKey, out var source);
        var project = bind(source);
        foreach (var row in rows)
            yield return project(row);
    }

    private static SqlValue SmallInt(SqlValue value) => value.IsNull ? SqlValue.Null(SqlType.SmallInt) : SqlValue.FromInt16((short)value.AsInt32);

    private static int Flag(SqlValue value) => !value.IsNull && value.AsBoolean ? 1 : 0;

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
}
