using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The SSMS-era sp_MS* helpers tools and old scripts still call —
// sp_MStablespace, sp_MSindexspace, sp_MShelpindex, sp_MShelptype,
// sp_MShelpcolumns and sp_MSdependencies — each reading the catalog views as
// real's T-SQL reads them (probed 2026-10-17 against SQL Server 2025).
partial class Simulation
{
    private static readonly NVarcharSqlType MsNameType = NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit);

    private static readonly SystemProcedureParameter[] MsTableSpaceParameters =
    [
        new("name", SqlType.NVarchar, 517),
        new("id", SqlType.Int32, 0, SqlValue.Null(SqlType.Int32)),
    ];

    private static readonly SystemProcedureParameter[] MsIndexSpaceParameters =
    [
        new("tablename", SqlType.NVarchar, 517),
        new("index_name", SqlType.NVarchar, 258, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] MsHelpIndexParameters =
    [
        new("tablename", SqlType.NVarchar, 517),
        new("indexname", SqlType.NVarchar, 258, SqlValue.Null(SqlType.NVarchar)),
        new("flags", SqlType.Int32, 0, SqlValue.Null(SqlType.Int32)),
    ];

    private static readonly SystemProcedureParameter[] MsHelpTypeParameters =
    [
        new("typename", SqlType.NVarchar, 517, SqlValue.Null(SqlType.NVarchar)),
        new("flags", SqlType.NVarchar, 10, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] MsHelpColumnsParameters =
    [
        new("tablename", SqlType.NVarchar, 517),
        new("flags", SqlType.Int32, 0, SqlValue.FromInt32(0)),
        new("orderby", SqlType.NVarchar, 10, SqlValue.Null(SqlType.NVarchar)),
        new("flags2", SqlType.Int32, 0, SqlValue.FromInt32(0)),
    ];

    private static readonly SystemProcedureParameter[] MsDependenciesParameters =
    [
        new("objname", SqlType.NVarchar, 517, SqlValue.Null(SqlType.NVarchar)),
        new("objtype", SqlType.Int32, 0, SqlValue.Null(SqlType.Int32)),
        new("flags", SqlType.Int32, 0, SqlValue.FromInt32(0x1fd)),
        new("objlist", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("intrans", SqlType.Int32, 0, SqlValue.Null(SqlType.Int32)),
    ];

    /// <summary>One <c>sys.objects</c> row an <c>sp_MS*</c> helper resolved by name.</summary>
    private readonly struct MsObject(int objectId, string name, string schema, string type)
    {
        public readonly int ObjectId = objectId;
        public readonly string Name = name;
        public readonly string Schema = schema;
        public readonly string Type = type;
    }

    /// <summary>
    /// Resolves an <c>sp_MS*</c> helper's object name as <c>OBJECT_ID</c> does in
    /// the current database — one to three parts, the schema the principal's
    /// default then <c>dbo</c> when unwritten — to a user object.
    /// </summary>
    private static MsObject? ResolveMsObject(BatchContext batch, string? name)
    {
        if (name is null || !ObjectId.TryParseObjectName(name, out var parsed) || parsed.Count > 3)
            return null;
        var database = batch.CurrentDatabase;
        if (parsed.Count == 3 && !database.Collation.Equals(parsed[0], database.Name))
            return null;
        var schemaName = parsed.Count >= 2 ? parsed[parsed.Count - 2] : null;
        MsObject? fallback = null;
        foreach (var candidate in MsObjects(batch))
        {
            if (!database.Collation.Equals(candidate.Name, parsed.Leaf))
                continue;
            if (schemaName is not null)
            {
                if (database.Collation.Equals(candidate.Schema, schemaName))
                    return candidate;
            }
            else if (database.Collation.Equals(candidate.Schema, batch.DefaultSchemaName))
            {
                return candidate;
            }
            else if (database.Collation.Equals(candidate.Schema, Database.DefaultSchemaName))
            {
                fallback = candidate;
            }
        }
        return fallback;
    }

    /// <summary>The current database's user objects as <c>sys.objects</c> lists them, each with its schema's name.</summary>
    private static List<MsObject> MsObjects(BatchContext batch)
    {
        var database = batch.CurrentDatabase;
        var schemas = new Dictionary<int, string>();
        foreach (var row in BuiltInResources.CatalogViewRows(batch, database, "sys.schemas", out var schemaOrdinal))
            schemas[row[schemaOrdinal("schema_id")].AsInt32] = row[schemaOrdinal("name")].AsString;
        var objects = new List<MsObject>();
        foreach (var row in BuiltInResources.CatalogViewRows(batch, database, "sys.objects", out var ordinal))
        {
            objects.Add(new MsObject(row[ordinal("object_id")].AsInt32, row[ordinal("name")].AsString,
                schemas.GetValueOrDefault(row[ordinal("schema_id")].AsInt32, ""), row[ordinal("type")].AsString.TrimEnd()));
        }
        return objects;
    }

    /// <summary>The <c>sp_MS*</c> helpers' refusal of a name they can't resolve: real's own <c>RAISERROR</c> trips over its format, Msg 2786, at the procedure's <paramref name="line"/>.</summary>
    private static SimulatedSqlException MsFormatMismatch(string calledAs, int line) =>
        AtSystemProcedureLine(calledAs, SimulatedSqlException.RaiserrorTypeMismatch(1), line);

    /// <summary>Per index of <paramref name="objectId"/>: the pages it uses and, for the heap or clustered index, the pages its data takes, from <c>sys.dm_db_partition_stats</c>.</summary>
    private static SortedDictionary<int, (long Used, long Data, long Rows)> MsIndexPages(BatchContext batch, int objectId)
    {
        var pages = new SortedDictionary<int, (long Used, long Data, long Rows)>();
        foreach (var row in BuiltInResources.CatalogViewRows(batch, batch.CurrentDatabase, "sys.dm_db_partition_stats", out var ordinal))
        {
            if (row[ordinal("object_id")].AsInt32 != objectId)
                continue;
            var indexId = row[ordinal("index_id")].AsInt32;
            long Pages(string column) => row[ordinal(column)].IsNull ? 0 : row[ordinal(column)].CoerceTo(SqlType.BigInt).AsInt64;
            var data = indexId <= 1 ? Pages("in_row_data_page_count") + Pages("lob_used_page_count") + Pages("row_overflow_used_page_count") : 0;
            var (used, held, rows) = pages.GetValueOrDefault(indexId);
            pages[indexId] = (used + Pages("used_page_count"), held + data, rows + Pages("row_count"));
        }
        return pages;
    }

    /// <summary>
    /// <c>sp_MStablespace @name [, @id]</c>: a user table's rows and the
    /// kilobytes its data and its indexes use, as <c>sp_spaceused</c> counts them.
    /// <c>@id</c>, when given, names the table in place of <c>@name</c>, an id no
    /// table has answering a row of NULLs; a name that is no user table is Msg
    /// 15009 from line 13.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpMsTableSpace(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments("sp_MStablespace", calledAs, arguments, MsTableSpaceParameters);
        int? objectId;
        if (!values[1].IsNull)
        {
            var id = values[1].AsInt32;
            objectId = MsObjects(batch).Exists(candidate => candidate.ObjectId == id && candidate.Type == "U") ? id : null;
        }
        else
        {
            var name = values[0].IsNull ? null : values[0].AsString;
            objectId = ResolveMsObject(batch, name) is { Type: "U" } table ? table.ObjectId
                : throw AtSystemProcedureLine(calledAs, SimulatedSqlException.HelpObjectDoesNotExist(name ?? "(null)", batch.CurrentDatabase.Name), 13);
        }
        SqlValue[] row;
        if (objectId is { } tableId)
        {
            long used = 0, data = 0, rows = 0;
            foreach (var (indexId, pages) in MsIndexPages(batch, tableId))
            {
                used += pages.Used;
                data += pages.Data;
                if (indexId <= 1)
                    rows += pages.Rows;
            }
            row = [SqlValue.FromInt32((int)rows), SqlValue.FromInt32((int)(data * 8)), SqlValue.FromInt32((int)(Math.Max(0, used - data) * 8))];
        }
        else
        {
            row = [SqlValue.Null(SqlType.Int32), SqlValue.Null(SqlType.Int32), SqlValue.Null(SqlType.Int32)];
        }
        yield return new SimulatedSqlResultSet([SqlType.Int32, SqlType.Int32, SqlType.Int32], ["Rows", "DataSpaceUsed", "IndexSpaceUsed"], [row]);
    }

    /// <summary>
    /// <c>sp_MSindexspace @tablename [, @index_name]</c>: the kilobytes each index
    /// of a table or view uses, the clustered one's excluding the data it holds
    /// (<c>Size excludes actual data.</c>) — or, for one index named, its size
    /// alone as a single <c>Size (KB)</c> value, the clustered index's being the
    /// table's whole index space. A name that resolves to nothing is Msg 2786
    /// from line 29, an index the table lacks Msg 2786 from line 41.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpMsIndexSpace(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments("sp_MSindexspace", calledAs, arguments, MsIndexSpaceParameters);
        if (ResolveMsObject(batch, values[0].IsNull ? null : values[0].AsString) is not { Type: "U" or "V" } target)
            throw MsFormatMismatch(calledAs, 29);
        var pages = MsIndexPages(batch, target.ObjectId);
        long Size(int indexId) => pages.TryGetValue(indexId, out var index) ? Math.Max(0, index.Used - index.Data) * 8 : 0;
        var indexes = MsIndexes(batch, target.ObjectId);
        if (!values[1].IsNull)
        {
            var wanted = values[1].AsString;
            if (indexes.Find(index => batch.CurrentDatabase.Collation.Equals(index.Name, wanted)) is not { Name: not null } named)
                throw MsFormatMismatch(calledAs, 41);
            var size = named.IndexId == 1 ? pages.Keys.Sum(Size) : Size(named.IndexId);
            yield return new SimulatedSqlResultSet([SqlType.Int32], ["Size (KB)"], [[SqlValue.FromInt32((int)size)]]);
            yield break;
        }
        var comments = NVarcharSqlType.Get(28, Collation.Catalog, Coercibility.Implicit);
        var rows = new List<SqlValue[]>();
        foreach (var index in indexes)
        {
            rows.Add([
                SqlValue.FromByte((byte)index.IndexId), SqlValue.FromString(MsNameType, index.Name ?? ""), SqlValue.FromInt32((int)Size(index.IndexId)),
                SqlValue.FromString(comments, index.IndexId == 1 ? "Size excludes actual data." : "(None)"),
            ]);
        }
        yield return new SimulatedSqlResultSet([SqlType.TinyInt, MsNameType, SqlType.Int32, comments], ["Index ID", "Index Name", "Size (KB)", "Comments"], rows)
        {
            ColumnNullability = [false, false, false, false],
        };
    }

    /// <summary>One index an <c>sp_MS*</c> helper reports, from <c>sys.indexes</c>, <c>sys.index_columns</c> and <c>sys.stats</c>.</summary>
    private sealed class MsIndex(int indexId, string? name)
    {
        public readonly int IndexId = indexId;
        public readonly string? Name = name;
        public int Status;
        public byte FillFactor;
        public int DataSpaceId;
        public readonly List<(string Name, bool Descending, bool Computed)> Keys = [];
    }

    /// <summary>The indexes of <paramref name="objectId"/> past its heap, by index id, each with its <c>sp_MShelpindex</c> status bits and key columns.</summary>
    private static List<MsIndex> MsIndexes(BatchContext batch, int objectId)
    {
        var database = batch.CurrentDatabase;
        var indexes = new SortedDictionary<int, MsIndex>();
        foreach (var row in BuiltInResources.CatalogViewRows(batch, database, "sys.indexes", out var ordinal))
        {
            var indexId = row[ordinal("index_id")].AsInt32;
            if (row[ordinal("object_id")].AsInt32 != objectId || indexId == 0)
                continue;
            bool Flag(string column) => !row[ordinal(column)].IsNull && row[ordinal(column)].CoerceTo(SqlType.Bit).AsBoolean;
            indexes[indexId] = new MsIndex(indexId, row[ordinal("name")].IsNull ? null : row[ordinal("name")].AsString)
            {
                Status = (Flag("ignore_dup_key") ? 1 : 0) | (Flag("is_unique") ? 2 : 0) | (row[ordinal("type")].AsByte == 1 ? 16 : 0) | (Flag("is_padded") ? 256 : 0)
                    | (Flag("is_primary_key") ? 2048 : 0) | (Flag("is_unique_constraint") ? 4096 : 0),
                FillFactor = row[ordinal("fill_factor")].AsByte,
                DataSpaceId = row[ordinal("data_space_id")].IsNull ? 0 : row[ordinal("data_space_id")].AsInt32,
            };
        }
        foreach (var row in BuiltInResources.CatalogViewRows(batch, database, "sys.stats", out var ordinal))
        {
            if (row[ordinal("object_id")].AsInt32 == objectId && indexes.TryGetValue(row[ordinal("stats_id")].AsInt32, out var index)
                && !row[ordinal("no_recompute")].IsNull && row[ordinal("no_recompute")].CoerceTo(SqlType.Bit).AsBoolean)
            {
                index.Status |= 16777216;
            }
        }
        var columns = new Dictionary<int, (string Name, bool Computed)>();
        foreach (var row in BuiltInResources.CatalogViewRows(batch, database, "sys.columns", out var ordinal))
        {
            if (row[ordinal("object_id")].AsInt32 == objectId)
                columns[row[ordinal("column_id")].AsInt32] = (row[ordinal("name")].AsString, row[ordinal("is_computed")].CoerceTo(SqlType.Bit).AsBoolean);
        }
        var keys = new List<(int IndexId, int Ordinal, int ColumnId, bool Descending)>();
        foreach (var row in BuiltInResources.CatalogViewRows(batch, database, "sys.index_columns", out var ordinal))
        {
            if (row[ordinal("object_id")].AsInt32 == objectId && row[ordinal("key_ordinal")].AsByte > 0)
                keys.Add((row[ordinal("index_id")].AsInt32, row[ordinal("key_ordinal")].AsByte, row[ordinal("column_id")].AsInt32, row[ordinal("is_descending_key")].CoerceTo(SqlType.Bit).AsBoolean));
        }
        keys.Sort();
        foreach (var (indexId, _, columnId, descending) in keys)
        {
            if (indexes.TryGetValue(indexId, out var index) && columns.TryGetValue(columnId, out var column))
                index.Keys.Add((column.Name, descending, column.Computed));
        }
        return [.. indexes.Values];
    }

    /// <summary>
    /// <c>sp_MShelpindex @tablename [, @indexname [, @flags]]</c>: each index of a
    /// table or view — or the one named — with its status bits (1
    /// <c>IGNORE_DUP_KEY</c>, 2 unique, 16 clustered, 256 <c>PAD_INDEX</c>, 2048
    /// primary key, 4096 unique constraint, 16777216
    /// <c>STATISTICS_NORECOMPUTE</c>), fill factor, up to sixteen key columns,
    /// filegroup, whether it is the full-text key, bitmasks of its descending
    /// and computed keys, and whether the object is a table. A name that
    /// resolves to nothing lists nothing.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpMsHelpIndex(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments("sp_MShelpindex", calledAs, arguments, MsHelpIndexParameters);
        var schema = new List<SqlType> { MsNameType, SqlType.Int32, SqlType.SmallInt, SqlType.TinyInt };
        var names = new List<string> { "name", "status", "indid", "OrigFillFactor" };
        for (var i = 1; i <= 16; i++)
        {
            schema.Add(MsNameType);
            names.Add("IndCol" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        schema.AddRange([MsNameType, SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.Int32]);
        names.AddRange(["SegName", "FullTextKey", "Descending", "Computed", "IsTable"]);
        var rows = new List<SqlValue[]>();
        if (ResolveMsObject(batch, values[0].IsNull ? null : values[0].AsString) is { Type: "U" or "V" } target)
        {
            var database = batch.CurrentDatabase;
            var dataSpaces = new Dictionary<int, string>();
            foreach (var row in BuiltInResources.CatalogViewRows(batch, database, "sys.data_spaces", out var ordinal))
                dataSpaces[row[ordinal("data_space_id")].AsInt32] = row[ordinal("name")].AsString;
            var fullTextKey = 0;
            foreach (var row in BuiltInResources.CatalogViewRows(batch, database, "sys.fulltext_indexes", out var ordinal))
            {
                if (row[ordinal("object_id")].AsInt32 == target.ObjectId)
                    fullTextKey = row[ordinal("unique_index_id")].AsInt32;
            }
            foreach (var index in MsIndexes(batch, target.ObjectId))
            {
                if (!values[1].IsNull && !database.Collation.Equals(index.Name ?? "", values[1].AsString))
                    continue;
                var row = new List<SqlValue>
                {
                    index.Name is null ? SqlValue.Null(MsNameType) : SqlValue.FromString(MsNameType, index.Name), SqlValue.FromInt32(index.Status),
                    SqlValue.FromInt16((short)index.IndexId), SqlValue.FromByte(index.FillFactor),
                };
                int descending = 0, computed = 0;
                for (var i = 0; i < 16; i++)
                {
                    if (i < index.Keys.Count)
                    {
                        row.Add(SqlValue.FromString(MsNameType, index.Keys[i].Name));
                        descending |= index.Keys[i].Descending ? 1 << i : 0;
                        computed |= index.Keys[i].Computed ? 1 << i : 0;
                    }
                    else
                    {
                        row.Add(SqlValue.Null(MsNameType));
                    }
                }
                row.AddRange([
                    SqlValue.FromString(MsNameType, dataSpaces.GetValueOrDefault(index.DataSpaceId, "PRIMARY")), SqlValue.FromInt32(fullTextKey == index.IndexId ? 1 : 0),
                    SqlValue.FromInt32(descending), SqlValue.FromInt32(computed), SqlValue.FromInt32(target.Type == "U" ? 1 : 0),
                ]);
                rows.Add([.. row]);
            }
        }
        var nullability = new bool[schema.Count];
        Array.Fill(nullability, true);
        nullability[20] = false;
        yield return new SimulatedSqlResultSet([.. schema], [.. names], rows) { ColumnNullability = nullability };
    }

    /// <summary>
    /// The system types <c>sp_MShelptype</c> reports, as SQL Server 2025 lists
    /// them: name, the varying-length cap, whether it allows NULL, whether it
    /// takes a precision and scale, whether it can be an identity, whether it
    /// varies in length, its byte length, its precision or character length,
    /// and whether it carries a collation.
    /// </summary>
    private static readonly (string Name, short? VarLenMax, int AllowNulls, int IsNumeric, int AllowIdentity, bool Variable, short MaxLen, short? Precision, bool Collated)[] MsSystemTypes =
    [
        ("bigint", null, 1, 0, 1, false, 8, 19, false),
        ("binary", 8000, 1, 0, 0, false, 8000, 8000, false),
        ("bit", null, 1, 0, 0, false, 1, 1, false),
        ("char", 8000, 1, 0, 0, false, 8000, 8000, true),
        ("date", null, 1, 0, 0, false, 3, 10, false),
        ("datetime", null, 1, 0, 0, false, 8, 23, false),
        ("datetime2", null, 1, 0, 0, false, 8, 27, false),
        ("datetimeoffset", null, 1, 0, 0, false, 10, 34, false),
        ("decimal", null, 1, 1, 1, false, 17, 38, false),
        ("float", null, 1, 0, 0, false, 8, 53, false),
        ("geography", null, 1, 0, 0, false, -1, -1, false),
        ("geometry", null, 1, 0, 0, false, -1, -1, false),
        ("hierarchyid", null, 1, 0, 0, false, 892, 892, false),
        ("image", null, 1, 0, 0, false, 16, null, false),
        ("int", null, 1, 0, 1, false, 4, 10, false),
        ("json", null, 1, 0, 0, false, -1, -1, false),
        ("money", null, 1, 0, 0, false, 8, 19, false),
        ("nchar", 8000, 1, 0, 0, false, 8000, 4000, true),
        ("ntext", null, 1, 0, 0, false, 16, null, true),
        ("numeric", null, 1, 1, 1, false, 17, 38, false),
        ("nvarchar", 8000, 1, 0, 0, true, 8000, 4000, true),
        ("real", null, 1, 0, 0, false, 4, 24, false),
        ("smalldatetime", null, 1, 0, 0, false, 4, 16, false),
        ("smallint", null, 1, 0, 1, false, 2, 5, false),
        ("smallmoney", null, 1, 0, 0, false, 4, 10, false),
        ("sql_variant", null, 1, 0, 0, false, 8016, 0, false),
        ("sysname", null, 0, 0, 0, true, 256, 128, true),
        ("text", null, 1, 0, 0, false, 16, null, true),
        ("time", null, 1, 0, 0, false, 5, 16, false),
        ("timestamp", null, 1, 0, 0, false, 8, 8, false),
        ("tinyint", null, 1, 0, 1, false, 1, 3, false),
        ("uniqueidentifier", null, 1, 0, 0, false, 16, 16, false),
        ("varbinary", 8000, 1, 0, 0, true, 8000, 8000, false),
        ("varchar", 8000, 1, 0, 0, true, 8000, 8000, true),
        ("vector", null, 1, 0, 0, true, 8000, 8000, false),
        ("xml", null, 1, 0, 0, false, -1, -1, false),
    ];

    /// <summary>
    /// <c>sp_MShelptype [@typename [, @flags]]</c>: the system types, then the
    /// user-defined types in name order — alias types with their base, bound
    /// default and rule, and table types — <c>@flags = 'sdt'</c> sending only
    /// the first set and <c>'uddt'</c> only the second. A type named answers its
    /// own set alone; a name that is neither (a qualified one included) is Msg
    /// 2786 from line 57.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpMsHelpType(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments("sp_MShelptype", calledAs, arguments, MsHelpTypeParameters);
        var database = batch.CurrentDatabase;
        var flags = values[1].IsNull ? "" : values[1].AsString.TrimEnd(' ');
        var typeName = values[0].IsNull ? null : values[0].AsString;
        var userTypes = MsUserTypes(batch);
        bool systemSet = !flags.Equals("uddt", StringComparison.OrdinalIgnoreCase), userSet = !flags.Equals("sdt", StringComparison.OrdinalIgnoreCase);
        (string Name, short? VarLenMax, int AllowNulls, int IsNumeric, int AllowIdentity, bool Variable, short MaxLen, short? Precision, bool Collated)? onlySystem = null;
        SqlValue[]? onlyUser = null;
        if (typeName is not null)
        {
            onlySystem = Array.Find(MsSystemTypes, type => type.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            if (onlySystem.Value.Name is null)
            {
                onlySystem = null;
                onlyUser = userTypes.Find(type => database.Collation.Equals(type[0].AsString, typeName)) ?? throw MsFormatMismatch(calledAs, 57);
            }
            systemSet = onlySystem is not null;
            userSet = onlyUser is not null;
        }
        var collation = SqlValue.FromString(MsNameType, database.CollationName);
        var nullName = SqlValue.Null(MsNameType);
        if (systemSet)
        {
            var rows = new List<SqlValue[]>();
            foreach (var type in MsSystemTypes)
            {
                if (onlySystem is { } only && only.Name != type.Name)
                    continue;
                rows.Add([
                    SqlValue.FromString(MsNameType, type.Name), type.VarLenMax is { } cap ? SqlValue.FromInt16(cap) : SqlValue.Null(SqlType.SmallInt),
                    SqlValue.FromInt32(type.AllowNulls), SqlValue.FromInt32(type.IsNumeric), SqlValue.FromInt32(type.AllowIdentity), SqlValue.FromBoolean(type.Variable),
                    SqlValue.FromInt16(type.MaxLen), type.Precision is { } precision ? SqlValue.FromInt16(precision) : SqlValue.Null(SqlType.SmallInt), type.Collated ? collation : nullName,
                ]);
            }
            yield return new SimulatedSqlResultSet(
                [MsNameType, SqlType.SmallInt, SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.Bit, SqlType.SmallInt, SqlType.SmallInt, MsNameType],
                ["SystemDatatypeName", "ifvarlen_max", "allownulls", "isnumeric", "allowidentity", "variablelength", "max_len", "prec_len", "collation"], rows)
            {
                ColumnNullability = [false, true, true, false, false, false, false, true, true],
            };
        }
        if (userSet)
        {
            yield return new SimulatedSqlResultSet(
                [MsNameType, MsNameType, MsNameType, MsNameType, MsNameType, SqlType.SmallInt, SqlType.Int32, SqlType.Bit, SqlType.SmallInt, SqlType.TinyInt,
                    SqlType.Int32, SqlType.Int32, SqlType.Bit, SqlType.SmallInt, MsNameType, MsNameType, MsNameType],
                ["UserDatatypeName", "owner", "basetypename", "defaultname", "rulename", "tid", "length", "nullable", "dt_prec", "dt_scale", "dt_flags",
                    "allowidentity", "variablelength", "maxlen", "defaultowner", "ruleowner", "collation"],
                onlyUser is not null ? [onlyUser] : userTypes)
            {
                ColumnNullability = [false, true, true, true, true, true, false, true, true, true, true, false, false, true, true, true, true],
            };
        }
    }

    /// <summary>Whether a system type name is the character or binary family the helpers flag with 1.</summary>
    private static bool IsMsCharacterType(string typeName) =>
        typeName is "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary";

    /// <summary>The user-defined types' <c>sp_MShelptype</c> rows, in name order.</summary>
    private static List<SqlValue[]> MsUserTypes(BatchContext batch)
    {
        var database = batch.CurrentDatabase;
        var objects = MsObjects(batch);
        var typeNames = new Dictionary<int, string>();
        var rows = new List<SqlValue[]>();
        var types = BuiltInResources.CatalogViewRows(batch, database, "sys.types", out var ordinal).ToList();
        foreach (var row in types)
            typeNames[row[ordinal("user_type_id")].AsInt32] = row[ordinal("name")].AsString;
        var schemas = new Dictionary<int, string>();
        foreach (var row in BuiltInResources.CatalogViewRows(batch, database, "sys.schemas", out var schemaOrdinal))
            schemas[row[schemaOrdinal("schema_id")].AsInt32] = row[schemaOrdinal("name")].AsString;
        SqlValue Name(string? value) => value is null ? SqlValue.Null(MsNameType) : SqlValue.FromString(MsNameType, value);
        foreach (var row in types)
        {
            if (!row[ordinal("is_user_defined")].CoerceTo(SqlType.Bit).AsBoolean)
                continue;
            var isTable = row[ordinal("is_table_type")].CoerceTo(SqlType.Bit).AsBoolean;
            var baseName = isTable ? null : typeNames.GetValueOrDefault(row[ordinal("system_type_id")].AsByte);
            var maxLength = row[ordinal("max_length")].CoerceTo(SqlType.Int32).AsInt32;
            var isDecimal = baseName is "decimal" or "numeric";
            var scale = row[ordinal("scale")].AsByte;
            MsObject? Bound(string column) => row[ordinal(column)].AsInt32 is var id and not 0 && objects.Find(candidate => candidate.ObjectId == id) is { Name: not null } found ? found : null;
            var defaultObject = Bound("default_object_id");
            var ruleObject = Bound("rule_object_id");
            rows.Add([
                Name(row[ordinal("name")].AsString), Name(schemas.GetValueOrDefault(row[ordinal("schema_id")].AsInt32)), Name(baseName),
                Name(defaultObject?.Name), Name(ruleObject?.Name), SqlValue.FromInt16((short)row[ordinal("user_type_id")].AsInt32),
                SqlValue.FromInt32(baseName is not null && IsMsCharacterType(baseName) ? (baseName is "nchar" or "nvarchar" ? maxLength / 2 : maxLength) : 0),
                SqlValue.FromBoolean(row[ordinal("is_nullable")].CoerceTo(SqlType.Bit).AsBoolean),
                isDecimal ? SqlValue.FromInt16(row[ordinal("precision")].AsByte) : SqlValue.Null(SqlType.SmallInt),
                isDecimal ? SqlValue.FromByte(scale) : SqlValue.Null(SqlType.TinyInt),
                SqlValue.FromInt32(baseName is not null && IsMsCharacterType(baseName) ? 1 : isDecimal ? 2 : 0),
                SqlValue.FromInt32(baseName is "tinyint" or "smallint" or "int" or "bigint" || (isDecimal && scale == 0) ? 1 : 0),
                SqlValue.FromBoolean(baseName is "varchar" or "nvarchar" or "varbinary"),
                SqlValue.FromInt16((short)maxLength), Name(defaultObject?.Schema), Name(ruleObject?.Schema),
                row[ordinal("collation_name")].IsNull ? SqlValue.Null(MsNameType) : Name(row[ordinal("collation_name")].AsString),
            ]);
        }
        rows.Sort((a, b) => database.Collation.Compare(a[0].AsString, b[0].AsString));
        return rows;
    }

    /// <summary>
    /// <c>sp_MShelpcolumns @tablename [, @flags [, @orderby [, @flags2]]]</c>: the
    /// columns of a table or view in <c>column_id</c> order (by name under
    /// <c>@orderby = 'name'</c>), CLR-typed ones left out: type and base type,
    /// length (characters for <c>nchar</c> / <c>nvarchar</c>), precision and
    /// scale for <c>decimal</c> / <c>numeric</c> — every numeric type's under
    /// <c>@flags2 = 1</c> — bound default and rule, nullability, identity seed and
    /// increment, a flags word (1 character or binary, 2 decimal, 4 key column,
    /// 8 <c>ROWGUIDCOL</c>), the default constraint and computed definitions,
    /// <c>NOT FOR REPLICATION</c>, full-text membership, ANSI padding, collation
    /// and whether the column is a plain stored one. A name that resolves to
    /// nothing is Msg 2786 from line 76.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpMsHelpColumns(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments("sp_MShelpcolumns", calledAs, arguments, MsHelpColumnsParameters);
        if (ResolveMsObject(batch, values[0].IsNull ? null : values[0].AsString) is not { Type: "U" or "V" } target)
            throw MsFormatMismatch(calledAs, 76);
        var database = batch.CurrentDatabase;
        var allPrecisions = !values[3].IsNull && (values[3].AsInt32 & 1) != 0;
        var byName = !values[2].IsNull && values[2].AsString.TrimEnd(' ').Equals("name", StringComparison.OrdinalIgnoreCase);

        var typeNames = new Dictionary<int, string>();
        foreach (var row in BuiltInResources.CatalogViewRows(batch, database, "sys.types", out var ordinal))
            typeNames[row[ordinal("user_type_id")].AsInt32] = row[ordinal("name")].AsString;
        var objects = MsObjects(batch);
        var defaultConstraints = new Dictionary<int, (string Name, string Definition)>();
        foreach (var row in BuiltInResources.CatalogViewRows(batch, database, "sys.default_constraints", out var ordinal))
            defaultConstraints[row[ordinal("object_id")].AsInt32] = (row[ordinal("name")].AsString, row[ordinal("definition")].AsString);
        var computed = new Dictionary<int, string>();
        var identities = new Dictionary<int, (string Seed, string Increment, bool NotForReplication)>();
        foreach (var row in BuiltInResources.CatalogViewRows(batch, database, "sys.computed_columns", out var ordinal))
        {
            if (row[ordinal("object_id")].AsInt32 == target.ObjectId)
                computed[row[ordinal("column_id")].AsInt32] = row[ordinal("definition")].AsString;
        }
        foreach (var row in BuiltInResources.CatalogViewRows(batch, database, "sys.identity_columns", out var ordinal))
        {
            if (row[ordinal("object_id")].AsInt32 == target.ObjectId)
            {
                identities[row[ordinal("column_id")].AsInt32] = (row[ordinal("seed_value")].CoerceTo(SqlType.NVarchar).AsString,
                    row[ordinal("increment_value")].CoerceTo(SqlType.NVarchar).AsString, row[ordinal("is_not_for_replication")].CoerceTo(SqlType.Bit).AsBoolean);
            }
        }
        var keyColumns = new HashSet<int>();
        foreach (var index in MsIndexes(batch, target.ObjectId))
        {
            if ((index.Status & 2048) == 0)
                continue;
            foreach (var row in BuiltInResources.CatalogViewRows(batch, database, "sys.index_columns", out var ordinal))
            {
                if (row[ordinal("object_id")].AsInt32 == target.ObjectId && row[ordinal("index_id")].AsInt32 == index.IndexId)
                    _ = keyColumns.Add(row[ordinal("column_id")].AsInt32);
            }
        }
        var fullText = new HashSet<int>();
        foreach (var row in BuiltInResources.CatalogViewRows(batch, database, "sys.fulltext_index_columns", out var ordinal))
        {
            if (row[ordinal("object_id")].AsInt32 == target.ObjectId)
                _ = fullText.Add(row[ordinal("column_id")].AsInt32);
        }

        var text = NVarcharSqlType.Get(4000, Collation.Catalog, Coercibility.Implicit);
        var qualified = NVarcharSqlType.Get(257, Collation.Catalog, Coercibility.Implicit);
        var seedType = NVarcharSqlType.Get(40, Collation.Catalog, Coercibility.Implicit);
        SqlValue Name(string? value) => value is null ? SqlValue.Null(MsNameType) : SqlValue.FromString(MsNameType, value);
        SqlValue Text(NVarcharSqlType type, string? value) => value is null ? SqlValue.Null(type) : SqlValue.FromString(type, value);
        var rows = new List<(string Name, SqlValue[] Row)>();
        foreach (var row in BuiltInResources.CatalogViewRows(batch, database, "sys.columns", out var ordinal))
        {
            if (row[ordinal("object_id")].AsInt32 != target.ObjectId)
                continue;
            var userTypeId = row[ordinal("user_type_id")].AsInt32;
            var systemTypeId = row[ordinal("system_type_id")].AsByte;
            // The CLR types — hierarchyid, geometry, geography and the user's
            // own — aren't listed.
            if (systemTypeId == 240)
                continue;
            var columnId = row[ordinal("column_id")].AsInt32;
            var name = row[ordinal("name")].AsString;
            var typeName = typeNames.GetValueOrDefault(userTypeId, "");
            var baseName = typeNames.GetValueOrDefault(systemTypeId, typeName);
            var maxLength = row[ordinal("max_length")].CoerceTo(SqlType.Int32).AsInt32;
            var precision = row[ordinal("precision")].AsByte;
            var scale = row[ordinal("scale")].AsByte;
            var isDecimal = baseName is "decimal" or "numeric";
            var isNumeric = isDecimal || baseName is "tinyint" or "smallint" or "int" or "bigint" or "money" or "smallmoney" or "real" or "float";
            var isComputed = computed.TryGetValue(columnId, out var computedText);
            var hasIdentity = identities.TryGetValue(columnId, out var identity);
            var defaultId = row[ordinal("default_object_id")].AsInt32;
            var ruleId = row[ordinal("rule_object_id")].AsInt32;
            var hasConstraint = defaultConstraints.TryGetValue(defaultId, out var constraint);
            var boundDefault = !hasConstraint && defaultId != 0 ? objects.Find(candidate => candidate.ObjectId == defaultId) : (MsObject?)null;
            var boundRule = ruleId != 0 ? objects.Find(candidate => candidate.ObjectId == ruleId) : (MsObject?)null;
            var isRowGuid = row[ordinal("is_rowguidcol")].CoerceTo(SqlType.Bit).AsBoolean;
            var isLarge = maxLength == -1 || baseName is "text" or "ntext" or "image" or "xml" or "json" || typeName == "vector";
            var flags = (typeName == baseName && IsMsCharacterType(baseName) ? 1 : 0) | (isDecimal ? 2 : 0) | (keyColumns.Contains(columnId) ? 4 : 0) | (isRowGuid ? 8 : 0);
            var collation = row[ordinal("collation_name")];
            rows.Add((name,
            [
                Name(name), SqlValue.FromInt32(columnId), Name(typeName), SqlValue.FromInt32(baseName is "nchar" or "nvarchar" ? maxLength / 2 : maxLength),
                isDecimal || (allPrecisions && isNumeric) ? SqlValue.FromInt32(precision) : SqlValue.Null(SqlType.Int32),
                isDecimal || (allPrecisions && isNumeric && baseName is not ("real" or "float")) ? SqlValue.FromInt32(scale) : SqlValue.Null(SqlType.Int32),
                Name(baseName),
                Text(qualified, boundDefault is { Name: not null } d ? d.Schema + "." + d.Name : null), Text(qualified, boundRule is { Name: not null } r ? r.Schema + "." + r.Name : null),
                SqlValue.FromBoolean(row[ordinal("is_nullable")].CoerceTo(SqlType.Bit).AsBoolean), SqlValue.FromBoolean(hasIdentity), SqlValue.FromInt32(flags),
                Text(seedType, hasIdentity ? identity.Seed : null), Text(seedType, hasIdentity ? identity.Increment : null),
                Name(hasConstraint ? constraint.Name : null), Text(text, hasConstraint ? constraint.Definition : null),
                SqlValue.FromInt32(isComputed ? 1 : 0), Text(text, computedText),
                SqlValue.FromBoolean(hasIdentity && identity.NotForReplication), SqlValue.FromBoolean(fullText.Contains(columnId)),
                baseName is "char" or "varchar" or "binary" or "varbinary" or "sql_variant" ? SqlValue.FromBoolean(true) : SqlValue.Null(SqlType.Bit),
                Name(boundDefault?.Schema), Name(boundDefault?.Name), Name(boundRule?.Schema), Name(boundRule?.Name),
                collation.IsNull ? SqlValue.Null(MsNameType) : Name(collation.AsString), SqlValue.Null(MsNameType),
                SqlValue.FromInt32(target.Type == "U" && !isComputed && !isLarge ? 1 : 0), SqlValue.FromInt32(0),
            ]));
        }
        rows.Sort((a, b) => byName ? database.Collation.Compare(a.Name, b.Name) : a.Row[1].AsInt32.CompareTo(b.Row[1].AsInt32));
        yield return new SimulatedSqlResultSet(
            [MsNameType, SqlType.Int32, MsNameType, SqlType.Int32, SqlType.Int32, SqlType.Int32, MsNameType, qualified, qualified, SqlType.Bit, SqlType.Bit, SqlType.Int32,
                seedType, seedType, MsNameType, text, SqlType.Int32, text, SqlType.Bit, SqlType.Bit, SqlType.Bit, MsNameType, MsNameType, MsNameType, MsNameType,
                MsNameType, MsNameType, SqlType.Int32, SqlType.Int32],
            ["col_name", "col_id", "col_typename", "col_len", "col_prec", "col_scale", "col_basetypename", "col_defname", "col_rulname", "col_null", "col_identity",
                "col_flags", "col_seed", "col_increment", "col_dridefname", "text", "col_iscomputed", "text", "col_NotForRepl", "col_fulltext", "col_AnsiPad",
                "col_DOwner", "col_DName", "col_ROwner", "col_RName", "collation", "ColType", "", ""],
            rows.ConvertAll(static row => row.Row))
        {
            ColumnNullability =
            [
                false, false, false, false, true, true, false, true, true, false, false, true, true, true, true, true, false, true, false, false, true,
                true, true, true, true, true, true, true, true,
            ],
        };
    }

    /// <summary>The SQL-DMO object type <c>sp_MSdependencies</c> reports for a <c>sys.objects</c> type code; 0 for one it doesn't list.</summary>
    private static int MsDependencyType(string typeCode) => typeCode switch
    {
        "D" => 64,
        "FN" or "FS" or "FT" or "IF" or "SN" or "SO" or "TF" => 1,
        "P" or "PC" => 16,
        "R" => 128,
        "TR" or "TA" => 256,
        "U" => 8,
        "V" => 4,
        _ => 0,
    };

    /// <summary>
    /// <c>sp_MSdependencies @objname [, @objtype [, @flags]]</c> for one named
    /// object: the objects it depends on, transitively — or, with the
    /// <c>0x40000</c> flag, the objects that depend on it — each with its SQL-DMO
    /// type (8 table, 4 view, 16 procedure, 1 function, sequence or synonym, 256
    /// trigger, 64 default, 128 rule) and the level it sits at in their creation
    /// order, by level, type and name. The flags' low word masks the types
    /// reported, and <c>0x80000</c> reports the object itself last among the
    /// objects it depends on. A name that resolves to nothing is Msg 2786 from
    /// line 152.
    /// </summary>
    /// <remarks>
    /// The whole-database listing (no <c>@objname</c>), <c>@objlist</c> and the
    /// paired-row form of flag <c>0x10000</c> raise <see cref="NotSupportedException"/>.
    /// </remarks>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpMsDependencies(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments("sp_MSdependencies", calledAs, arguments, MsDependenciesParameters);
        var flags = values[2].IsNull ? 0x1fd : values[2].AsInt32;
        var children = (flags & 0x40000) != 0;
        var self = (flags & 0x80000) != 0;
        if (values[0].IsNull || !values[3].IsNull || (flags & 0x10000) != 0 || (children && self))
            throw new NotSupportedException("sp_MSdependencies is modeled for one named object's dependencies or dependents; its whole-database listing, @objlist, the paired-row form (flag 0x10000) and a dependents listing that includes the object are not.");
        var objects = MsObjects(batch);
        if (ResolveMsObject(batch, values[0].AsString) is not { } anchor || MsDependencyType(anchor.Type) == 0)
            throw MsFormatMismatch(calledAs, 152);

        // What each object depends on: the objects its definition names, and a
        // trigger's table.
        var byId = objects.ToDictionary(static candidate => candidate.ObjectId);
        var parents = new Dictionary<int, HashSet<int>>();
        void Edge(int child, int parent)
        {
            if (child != parent && byId.ContainsKey(child) && byId.ContainsKey(parent))
            {
                if (!parents.TryGetValue(child, out var set))
                    parents[child] = set = [];
                _ = set.Add(parent);
            }
        }
        foreach (var entity in ModuleDependencies.Enumerate(batch.CurrentDatabase))
        {
            if (entity.ReferencingClass != ModuleDependencies.ObjectOrColumnClass)
                continue;
            foreach (var reference in entity.References)
            {
                if (reference.ReferencedId is { } referenced && reference.ReferencedClass == ModuleDependencies.ObjectOrColumnClass)
                    Edge(entity.ReferencingId, referenced);
            }
        }
        foreach (var row in BuiltInResources.CatalogViewRows(batch, batch.CurrentDatabase, "sys.triggers", out var ordinal))
        {
            if (!row[ordinal("parent_id")].IsNull)
                Edge(row[ordinal("object_id")].AsInt32, row[ordinal("parent_id")].AsInt32);
        }
        var dependents = new Dictionary<int, HashSet<int>>();
        foreach (var (child, set) in parents)
        {
            foreach (var parent in set)
            {
                if (!dependents.TryGetValue(parent, out var down))
                    dependents[parent] = down = [];
                _ = down.Add(child);
            }
        }

        // The closure on the asked side, then each member's level: one past the
        // deepest of its own parents inside the closure (the anchor at 0 when
        // listing dependents).
        var walk = children ? dependents : parents;
        var closure = new HashSet<int>();
        var pending = new Stack<int>([anchor.ObjectId]);
        while (pending.Count > 0)
        {
            if (!walk.TryGetValue(pending.Pop(), out var next))
                continue;
            foreach (var id in next)
            {
                if (id != anchor.ObjectId && closure.Add(id))
                    pending.Push(id);
            }
        }
        var levels = new Dictionary<int, int>();
        int Level(int id, HashSet<int> visiting)
        {
            if (levels.TryGetValue(id, out var known))
                return known;
            if (children && id == anchor.ObjectId)
                return 0;
            var level = 1;
            if (visiting.Add(id) && parents.TryGetValue(id, out var set))
            {
                foreach (var parent in set)
                {
                    if (closure.Contains(parent) || (children && parent == anchor.ObjectId))
                        level = Math.Max(level, Level(parent, visiting) + 1);
                }
            }
            _ = visiting.Remove(id);
            return levels[id] = level;
        }
        var reported = new List<(int Level, int Type, string Name, string Schema)>();
        var mask = flags & 0xffff;
        foreach (var id in closure)
        {
            var type = MsDependencyType(byId[id].Type);
            if ((type & mask) != 0)
                reported.Add((Level(id, []), type, byId[id].Name, byId[id].Schema));
        }
        if (self && (MsDependencyType(anchor.Type) & mask) != 0)
        {
            var top = 1;
            if (parents.TryGetValue(anchor.ObjectId, out var own))
            {
                foreach (var parent in own)
                    top = Math.Max(top, Level(parent, []) + 1);
            }
            reported.Add((top, MsDependencyType(anchor.Type), anchor.Name, anchor.Schema));
        }
        var collation = batch.CurrentDatabase.Collation;
        reported.Sort((a, b) =>
        {
            var order = a.Level.CompareTo(b.Level);
            if (order == 0)
                order = a.Type.CompareTo(b.Type);
            return order != 0 ? order : collation.Compare(a.Name, b.Name);
        });
        yield return new SimulatedSqlResultSet([SqlType.Int32, MsNameType, MsNameType, SqlType.SmallInt], ["oType", "oObjName", "oOwner", "oSequence"],
            reported.ConvertAll(static entry => new[] { SqlValue.FromInt32(entry.Type), SqlValue.FromString(MsNameType, entry.Name), SqlValue.FromString(MsNameType, entry.Schema), SqlValue.FromInt16((short)entry.Level) }))
        {
            ColumnNullability = [true, false, true, true],
        };
    }
}
