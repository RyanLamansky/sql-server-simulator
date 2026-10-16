using System.Collections.Concurrent;
using System.Globalization;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// The shape of one of real's system base tables (<c>sys.objects</c> type
/// <c>S</c>) or internal tables (type <c>IT</c>): its columns and indexes, as
/// <c>sys.columns</c>, <c>sys.indexes</c> and <c>sys.index_columns</c> list
/// them. One definition serves every database that holds a table of its name,
/// and a queue's, FILESTREAM tombstone's or FileTable's internal table shares
/// the definition named without its <c>_&lt;id&gt;</c> suffix.
/// </summary>
internal sealed class SystemTableDefinition(string name, SystemTableColumn[] columns, SystemTableIndex[] indexes)
{
    public readonly string Name = name;
    public readonly SystemTableColumn[] Columns = columns;
    public readonly SystemTableIndex[] Indexes = indexes;

    private readonly ConcurrentDictionary<string, HeapColumn[]> heapColumns = new(StringComparer.Ordinal);

    /// <summary>
    /// The columns as <see cref="HeapColumn"/>s, which the column views
    /// project — a column of the server's collation carrying
    /// <paramref name="serverCollation"/> by name, one of the database's none,
    /// so that it reads the database's.
    /// </summary>
    public HeapColumn[] HeapColumns(string serverCollation) =>
        this.heapColumns.GetOrAdd(serverCollation, static (collation, columns) =>
        {
            var built = new HeapColumn[columns.Length];
            for (var i = 0; i < columns.Length; i++)
                built[i] = columns[i].ToHeapColumn(collation);
            return built;
        }, this.Columns);
}

/// <summary>One column of a <see cref="SystemTableDefinition"/>, as real's <c>sys.columns</c> lists it.</summary>
internal sealed class SystemTableColumn(int id, string name, int userTypeId, short maxLength, byte precision, byte scale, bool nullable, bool hidden, long? identitySeed, byte generatedAlwaysType, string? collation, HeapColumn? copyOf = null)
{
    /// <summary>The user table column this one copies the type of, as a change-tracking table's key columns do; null for a fixed column.</summary>
    public readonly HeapColumn? CopyOf = copyOf;

    public readonly int Id = id;
    public readonly string Name = name;
    public readonly int UserTypeId = userTypeId;
    public readonly short MaxLength = maxLength;
    public readonly byte Precision = precision;
    public readonly byte Scale = scale;
    public readonly bool Nullable = nullable;
    public readonly bool Hidden = hidden;

    /// <summary>The identity seed, null for a column without the property; every one increments by 1.</summary>
    public readonly long? IdentitySeed = identitySeed;

    public readonly byte GeneratedAlwaysType = generatedAlwaysType;

    /// <summary>The column's collation: null for a non-string column, <c>db</c> for the database's, <c>srv</c> for the server's, else its name.</summary>
    public readonly string? Collation = collation;

    internal HeapColumn ToHeapColumn(string serverCollation)
    {
        if (this.CopyOf is { } source)
            return new HeapColumn(this.Name, source.Type, source.MaxLength, nullable: false, collation: source.Collation, spelledNumeric: source.SpelledNumeric) { ColumnId = this.Id };
        var collation = this.Collation switch
        {
            null or "db" => null,
            "srv" => serverCollation,
            _ => this.Collation,
        };
        var baseline = SqlServerSimulator.Collation.Baseline;
        var maxLength = this.MaxLength;
        var type = this.UserTypeId switch
        {
            36 => SqlType.UniqueIdentifier,
            42 => SqlType.GetDateTime2(this.Scale),
            43 => SqlType.GetDateTimeOffset(this.Scale),
            48 => SqlType.TinyInt,
            52 => SqlType.SmallInt,
            56 => SqlType.Int32,
            61 => SqlType.DateTime,
            62 => SqlType.Float,
            98 => SqlType.SqlVariant,
            104 => SqlType.Bit,
            106 or 108 => DecimalSqlType.Get(this.Precision, this.Scale),
            127 => SqlType.BigInt,
            165 => VarbinarySqlType.Get(maxLength),
            167 => VarcharSqlType.Get(maxLength, baseline, Coercibility.Implicit),
            173 => BinarySqlType.Get(maxLength),
            175 => CharSqlType.Get(maxLength, baseline, Coercibility.Implicit),
            231 => NVarcharSqlType.Get(maxLength < 0 ? maxLength : maxLength / 2, baseline, Coercibility.Implicit),
            239 => NCharSqlType.Get(maxLength / 2, baseline, Coercibility.Implicit),
            256 => SqlType.SystemName,
            _ => throw new InvalidOperationException($"System table column type {this.UserTypeId} is not modeled."),
        };
        int? declaredLength = this.UserTypeId switch
        {
            165 or 167 or 173 or 175 => maxLength,
            231 or 239 => maxLength < 0 ? maxLength : maxLength / 2,
            256 => 128,
            _ => null,
        };
        return new HeapColumn(this.Name, type, declaredLength, this.Nullable,
            identity: this.IdentitySeed is { } seed ? new IdentityState(seed, 1) : null,
            generatedAs: (GeneratedAlwaysAsRow)this.GeneratedAlwaysType,
            isHidden: this.Hidden,
            collation: collation,
            spelledNumeric: this.UserTypeId == 108)
        { ColumnId = this.Id };
    }
}

/// <summary>One index of a <see cref="SystemTableDefinition"/>: the heap (index 0, no name) or a clustered or nonclustered index.</summary>
internal sealed class SystemTableIndex(int id, string? name, byte type, bool unique, bool allowPageLocks, SystemIndexKey[] keys)
{
    public readonly int Id = id;
    public readonly string? Name = name;
    public readonly byte Type = type;
    public readonly bool Unique = unique;
    public readonly bool AllowPageLocks = allowPageLocks;

    /// <summary>The index's columns in <c>index_column_id</c> order.</summary>
    public readonly SystemIndexKey[] Keys = keys;
}

/// <summary>One <c>sys.index_columns</c> row of a <see cref="SystemTableIndex"/>; an included column's key ordinal is 0.</summary>
internal readonly struct SystemIndexKey(int columnId, int keyOrdinal, bool descending, bool included)
{
    public readonly int ColumnId = columnId;
    public readonly int KeyOrdinal = keyOrdinal;
    public readonly bool Descending = descending;
    public readonly bool Included = included;
}

/// <summary>
/// One system base table or internal table a database holds: its
/// <c>sys.objects</c> row, the rowsets <c>sys.partitions</c> lists for it and
/// the identity values its columns have handed out.
/// </summary>
internal sealed class SystemTableObject(string name, int objectId, bool isInternal, int schemaId, int parentObjectId, DateTime? createDate, DateTime? modifyDate, byte internalType, int lobDataSpaceId, SystemTableDefinition definition, SystemRowset[] rowsets, (int ColumnId, long Value)[] identityLastValues)
{
    public readonly string Name = name;
    public readonly int ObjectId = objectId;

    /// <summary>Whether the table is an internal table (<c>IT</c>) rather than a system base table (<c>S</c>).</summary>
    public readonly bool IsInternal = isInternal;

    public readonly int SchemaId = schemaId;
    public readonly int ParentObjectId = parentObjectId;

    /// <summary>The creation date, null when the table was made with its database and dates from it.</summary>
    public readonly DateTime? CreateDate = createDate;

    public readonly DateTime? ModifyDate = modifyDate;

    /// <summary><c>sys.internal_tables.internal_type</c>, 0 for a system base table.</summary>
    public readonly byte InternalType = internalType;

    public readonly int LobDataSpaceId = lobDataSpaceId;
    public readonly SystemTableDefinition Definition = definition;
    public readonly SystemRowset[] Rowsets = rowsets;

    /// <summary>The <c>last_value</c> of each identity column that has handed one out.</summary>
    public readonly (int ColumnId, long Value)[] IdentityLastValues = identityLastValues;

    public string TypeCode => this.IsInternal ? "IT" : "S ";

    public string TypeDescription => this.IsInternal ? "INTERNAL_TABLE" : "SYSTEM_TABLE";
}

/// <summary>
/// One rowset (heap or index) of a <see cref="SystemTableObject"/>: its
/// <c>sys.partitions</c> row, the row-format figures
/// <c>sys.system_internals_partitions</c> adds, and its allocation units.
/// </summary>
internal sealed class SystemRowset(int indexId, long partitionId, long rows, byte compression, byte droppedLobColumnState, short maxNullBitUsed, int maxLeafLength, short minLeafLength, short maxInternalLength, short minInternalLength, bool allowsNullableKeys, bool isDataRowFormat, bool isNotVersioned, SystemAllocationUnit[] units)
{
    public readonly int IndexId = indexId;
    public readonly long PartitionId = partitionId;
    public readonly long Rows = rows;
    public readonly byte Compression = compression;
    public readonly byte DroppedLobColumnState = droppedLobColumnState;
    public readonly short MaxNullBitUsed = maxNullBitUsed;
    public readonly int MaxLeafLength = maxLeafLength;
    public readonly short MinLeafLength = minLeafLength;
    public readonly short MaxInternalLength = maxInternalLength;
    public readonly short MinInternalLength = minInternalLength;
    public readonly bool AllowsNullableKeys = allowsNullableKeys;
    public readonly bool IsDataRowFormat = isDataRowFormat;
    public readonly bool IsNotVersioned = isNotVersioned;
    public readonly SystemAllocationUnit[] Units = units;
}

/// <summary>One allocation unit of a <see cref="SystemRowset"/>.</summary>
internal sealed class SystemAllocationUnit(long id, byte type, long totalPages, long usedPages, long dataPages)
{
    public readonly long Id = id;
    public readonly byte Type = type;
    public readonly long TotalPages = totalPages;
    public readonly long UsedPages = usedPages;
    public readonly long DataPages = dataPages;
}

/// <summary>
/// The system base tables and internal tables real SQL Server 2025 holds in a
/// fresh database of each kind — a user database (a copy of <c>model</c> plus
/// the <c>wpr_bucket_table</c> it makes as it is created), <c>model</c>,
/// <c>tempdb</c>, <c>msdb</c> and <c>master</c> — read from
/// <c>SystemTables.txt</c> (probed 2026-10-16 against SQL Server 2025). Their
/// row counts and pages are a fresh database's and stay put: nothing the
/// simulator does writes to these tables.
/// </summary>
internal static class SystemTables
{
    private static readonly Lazy<(Dictionary<string, SystemTableDefinition> Definitions, Dictionary<string, SystemTableObject[]> Flavors)> Catalog = new(Load);

    /// <summary>The system tables of <paramref name="databaseName"/>'s kind, in object-id order.</summary>
    public static SystemTableObject[] Of(string databaseName) =>
        Catalog.Value.Flavors[IsUserDatabase(databaseName) ? "user" : databaseName];

    /// <summary>Whether <paramref name="databaseName"/> names a user database rather than one of the four system databases.</summary>
    public static bool IsUserDatabase(string databaseName) =>
        !BuiltInToken.Equals(databaseName, "master") && !BuiltInToken.Equals(databaseName, "model")
        && !BuiltInToken.Equals(databaseName, "msdb") && !BuiltInToken.Equals(databaseName, "tempdb");

    /// <summary>The definition named <paramref name="name"/>.</summary>
    public static SystemTableDefinition Definition(string name) => Catalog.Value.Definitions[name];

    /// <summary>
    /// <c>sys.internal_tables.internal_type_desc</c> for an internal type
    /// (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    public static string InternalTypeDescription(byte internalType) => internalType switch
    {
        3 => "QUERY_DISK_STORE_QUERY_HINTS",
        4 => "QUERY_DISK_STORE_QUERY_TEMPLATE_PARAMETERIZATION",
        6 => "QUERY_DISK_STORE_WAIT_STATS",
        8 => "PLAN_PERSIST_QUERY_VARIANT_TABLE",
        9 => "QUERY_DISK_STORE_REPLICAS",
        10 => "QUERY_DISK_STORE_PLAN_FORCING_LOCATIONS",
        11 => "QUERY_DISK_STORE_RUNTIME_STATS_V2",
        12 => "QUERY_DISK_STORE_WAIT_STATS_V2",
        13 => "QUERY_DISK_STORE_ADHOC_CE_FEEDBACK",
        201 => "QUEUE_MESSAGES",
        202 => "XML_INDEX_NODES",
        206 => "SERVICE_BROKER_MAP",
        207 => "EXTENDED_INDEXES",
        208 => "FILESTREAM_TOMBSTONE",
        209 => "CHANGE_TRACKING",
        210 => "TRACKED_COMMITTED_TRANSACTIONS",
        211 => "FULLTEXT_AVDL",
        213 => "FULLTEXT_DOCID_STATUS",
        214 => "FULLTEXT_INDEXED_DOCID",
        215 => "FULLTEXT_DOCID_FILTER",
        217 => "FULLTEXT_THESAURUS_METADATA_TABLE",
        218 => "FULLTEXT_THESAURUS_STATE_TABLE",
        219 => "FULLTEXT_THESAURUS_PHRASE_TABLE",
        220 => "CONTAINED_FEATURES",
        225 => "FILETABLE_UPDATES",
        235 => "JSON_INDEX_TABLE",
        240 => "QUERY_DISK_STORE_QUERY_TEXT",
        241 => "QUERY_DISK_STORE_QUERY",
        242 => "QUERY_DISK_STORE_PLAN",
        243 => "QUERY_DISK_STORE_RUNTIME_STATS",
        244 => "QUERY_DISK_STORE_RUNTIME_STATS_INTERVAL",
        245 => "QUERY_CONTEXT_SETTINGS",
        250 => "QUERY_DISK_STORE_PLAN_FEEDBACK",
        _ => "UNKNOWN",
    };

    private static (Dictionary<string, SystemTableDefinition>, Dictionary<string, SystemTableObject[]>) Load()
    {
        using var stream = typeof(SystemTables).Assembly.GetManifestResourceStream("SqlServerSimulator.SystemTables.txt")
            ?? throw new InvalidOperationException("Embedded resource SqlServerSimulator.SystemTables.txt is missing.");
        using var reader = new StreamReader(stream);
        var definitions = new Dictionary<string, SystemTableDefinition>(StringComparer.Ordinal);
        var flavors = new Dictionary<string, SystemTableObject[]>(StringComparer.OrdinalIgnoreCase);

        string? definitionName = null;
        var columns = new List<SystemTableColumn>();
        var indexes = new List<SystemTableIndex>();
        void EndDefinition()
        {
            if (definitionName is not null)
                definitions[definitionName] = new SystemTableDefinition(definitionName, [.. columns], [.. indexes]);
            definitionName = null;
            columns.Clear();
            indexes.Clear();
        }

        string? flavor = null;
        SortedDictionary<int, SystemTableObject>? objects = null;
        string[]? objectFields = null;
        var rowsets = new List<(string[] Fields, List<SystemAllocationUnit> Units)>();
        var lastValues = new List<(int, long)>();
        void EndObject()
        {
            if (objectFields is null)
                return;
            var f = objectFields;
            var name = f[1];
            var objectId = Int(f[2]);
            var definition = definitions.TryGetValue(name, out var own) ? own : definitions[name[..name.LastIndexOf('_')]];
            var built = new SystemRowset[rowsets.Count];
            for (var i = 0; i < built.Length; i++)
            {
                var (p, units) = rowsets[i];
                built[i] = new SystemRowset(Int(p[2]), Long(p[3]), Long(p[4]), (byte)Int(p[5]), (byte)Int(p[6]), (short)Int(p[7]), Int(p[8]),
                    (short)Int(p[9]), (short)Int(p[10]), (short)Int(p[11]), p[12] == "1", p[13] == "1", p[14] == "1", [.. units]);
            }
            objects![objectId] = new SystemTableObject(name, objectId, f[3] == "IT", Int(f[4]), Int(f[5]), Date(f[6]), Date(f[7]),
                f[8].Length == 0 ? (byte)0 : (byte)Int(f[8]), f[9].Length == 0 ? 0 : Int(f[9]), definition, built, [.. lastValues]);
            objectFields = null;
            rowsets.Clear();
            lastValues.Clear();
        }
        void EndFlavor()
        {
            EndObject();
            if (flavor is not null)
                flavors[flavor] = [.. objects!.Values];
        }

        while (reader.ReadLine() is { } line)
        {
            if (line.Length < 2 || line[0] == '#')
                continue;
            var f = line.Split('|');
            switch (f[0])
            {
                case "A":
                    rowsets.Find(r => r.Fields[3] == f[1]).Units.Add(new SystemAllocationUnit(Long(f[2]), (byte)Int(f[3]), Long(f[4]), Long(f[5]), Long(f[6])));
                    break;
                case "C":
                    columns.Add(new SystemTableColumn(Int(f[1]), f[2], Int(f[3]), (short)Int(f[4]), (byte)Int(f[5]), (byte)Int(f[6]),
                        f[7].Contains('n', StringComparison.Ordinal), f[7].Contains('h', StringComparison.Ordinal),
                        f[8].Length == 0 ? null : Long(f[8]), f[9].Length == 0 ? (byte)0 : (byte)Int(f[9]), f[10].Length == 0 ? null : f[10]));
                    break;
                case "F":
                    EndDefinition();
                    EndFlavor();
                    flavor = f[1];
                    objects = f[2].Length == 0 ? [] : new SortedDictionary<int, SystemTableObject>(flavors[f[2]].ToDictionary(static o => o.ObjectId));
                    break;
                case "I":
                    indexes.Add(new SystemTableIndex(Int(f[1]), f[2].Length == 0 ? null : f[2], (byte)Int(f[3]),
                        f[4].Contains('u', StringComparison.Ordinal), !f[4].Contains('P', StringComparison.Ordinal), Keys(f[5])));
                    break;
                case "O":
                    EndObject();
                    objectFields = f;
                    break;
                case "P":
                    rowsets.Add((f, []));
                    break;
                case "T":
                    EndDefinition();
                    definitionName = f[1];
                    break;
                case "X":
                    EndObject();
                    _ = objects!.Remove(Int(f[1]));
                    break;
                case "Y":
                    lastValues.Add((Int(f[2]), Long(f[3])));
                    break;
                default:
                    throw new InvalidOperationException($"Unrecognized SystemTables.txt line: {line}");
            }
        }
        EndFlavor();
        return (definitions, flavors);
    }

    private static SystemIndexKey[] Keys(string text)
    {
        if (text.Length == 0)
            return [];
        var parts = text.Split(',');
        var keys = new SystemIndexKey[parts.Length];
        var ordinal = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part[0] == '+')
            {
                keys[i] = new SystemIndexKey(Int(part[1..]), 0, descending: false, included: true);
                continue;
            }
            ordinal++;
            var at = part.IndexOf('@', StringComparison.Ordinal);
            var keyOrdinal = at < 0 ? ordinal : Int(part[(at + 1)..]);
            var column = at < 0 ? part : part[..at];
            var descending = column.EndsWith('-');
            keys[i] = new SystemIndexKey(Int(descending ? column[..^1] : column), keyOrdinal, descending, included: false);
        }
        return keys;
    }

    private static int Int(string text) => int.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    private static long Long(string text) => long.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    private static DateTime? Date(string text) => text == "db" ? null : DateTime.ParseExact(text, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
}
