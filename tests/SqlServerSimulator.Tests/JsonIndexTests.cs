using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// SQL Server 2025's <c>CREATE JSON INDEX</c>: the statement's grammar and
/// refusals, the index ids it takes, and the catalog surfaces that report it.
/// Every expectation was probed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class JsonIndexTests
{
    private const string Table = "create table t (id int primary key, d json, e json, n nvarchar(max));";

    [TestMethod]
    public void DefaultPathIsTheWholeDocument() =>
        AreEqual("ji|1216000|9|JSON|$", new Simulation().ExecuteScalar($"""
            {Table}
            create json index ji on t(d);
            select concat(i.name, '|', i.index_id, '|', i.type, '|', cast(i.type_desc as varchar(10)) collate database_default, '|', cast(p.path as varchar(10)) collate database_default)
            from sys.indexes i join sys.json_index_paths p on p.object_id = i.object_id and p.index_id = i.index_id
            where i.object_id = object_id('t') and i.type = 9
            """));

    [TestMethod]
    public void PathsAreKeptAsWritten() =>
        AreEqual(" $.d |$.\"e f\"|$.a|lax $.b|strict $.c", new Simulation().ExecuteScalar($"""
            {Table}
            create json index ji on t(d) for ('$.a', N'lax $.b', 'strict $.c', ' $.d ', '$."e f"');
            select string_agg(path, '|') within group (order by path) from sys.json_index_paths
            """));

    [TestMethod]
    public void JsonIndexesRow() =>
        AreEqual("ji|9|JSON|80|1|0|0|1|1|0", new Simulation().ExecuteScalar($"""
            {Table}
            create json index ji on t(d) for ('$.a') with (fillfactor = 80, pad_index = on, allow_row_locks = off, optimize_for_array_search = on, data_compression = page, maxdop = 1);
            select concat(name, '|', type, '|', cast(type_desc as varchar(10)) collate database_default, '|', fill_factor, '|', cast(is_padded as int), '|', cast(is_disabled as int), '|', cast(allow_row_locks as int), '|', cast(allow_page_locks as int), '|', cast(optimize_for_array_search as int), '|', cast(is_unique as int))
            from sys.json_indexes
            """));

    [TestMethod]
    public void IndexIdsTakeTheJsonRange() =>
        AreEqual("1216001:jk|1216002:jl|1216000:ji2", new Simulation().ExecuteScalar("""
            create table t (id int primary key, d json, e json);
            create table u (id int primary key, d json);
            create json index ji on t(d); create json index jk on t(e);
            drop index ji on t; create json index jl on t(d);
            create json index ji2 on u(d);
            select string_agg(concat(index_id, ':', name), '|') within group (order by object_id, index_id) from sys.json_indexes
            """));

    [TestMethod]
    public void IndexColumnAndProperties() =>
        AreEqual("2|1|0|1216000|0|d", new Simulation().ExecuteScalar($"""
            {Table}
            create json index ji on t(d);
            select concat(column_id, '|', index_column_id, '|', key_ordinal, '|', indexproperty(object_id('t'), 'ji', 'IndexId'), '|', indexproperty(object_id('t'), 'ji', 'IsClustered'), '|', index_col('t', 1216000, 1))
            from sys.index_columns where object_id = object_id('t') and index_id = 1216000
            """));

    [TestMethod]
    public void DisableRebuildRenameDrop()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"{Table} create json index ji on t(d); alter index ji on t disable");
        IsTrue((bool)sim.ExecuteScalar("select is_disabled from sys.indexes where object_id = object_id('t') and name = 'ji'")!);
        _ = sim.ExecuteNonQuery("alter index ji on t rebuild; exec sp_rename 't.ji', 'jk', 'INDEX'");
        AreEqual("jk|0", sim.ExecuteScalar("select concat(name, '|', is_disabled) from sys.json_indexes"));
        _ = sim.ExecuteNonQuery("drop index jk on t");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.json_indexes"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.json_index_paths"));
    }

    [TestMethod]
    public void DropExistingReplaces() =>
        AreEqual("1216000|$.b", new Simulation().ExecuteScalar($"""
            {Table}
            create json index ji on t(d) for ('$.a');
            create json index ji on t(d) for ('$.b') with (drop_existing = on);
            select concat(index_id, '|', path) from sys.json_index_paths
            """));

    [TestMethod]
    public void DroppingAnEarlierColumnKeepsTheIndexOnItsColumn() =>
        AreEqual("d", new Simulation().ExecuteScalar("""
            create table t (x int, id int primary key, d json);
            create json index ji on t(d);
            alter table t drop column x;
            select c.name from sys.index_columns ic join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id where ic.index_id = 1216000
            """));

    [TestMethod]
    public void RowsWriteThroughTheIndexedTable() =>
        AreEqual("""{"x":1}""", new Simulation().ExecuteScalar("""
            create table t (id int primary key, d json); create json index ji on t(d) for ('$.a');
            insert t values (1, '{"a":1}'), (2, '{"b":2}');
            update t set d = '{"x":1}' where id = 2; delete t where id = 1;
            select cast(d as nvarchar(max)) from t
            """));

    [TestMethod]
    [DataRow("create table u (id int, d json); create json index ji on u(d)", 13672, (byte)1, "Table 'u' needs to have a clustered primary key with less than 32 columns in it in order to create a JSON index on it.")]
    [DataRow("create table u (id int not null, d json, constraint pk primary key nonclustered (id)); create json index ji on u(d)", 13672, (byte)1, "Table 'u' needs to have a clustered primary key with less than 32 columns in it in order to create a JSON index on it.")]
    [DataRow("create json index ji on t(n)", 13680, (byte)1, "Column 'n' on table 't' is not of JSON data type, which is required to create a JSON index on it.")]
    [DataRow("create json index ji on t(id)", 13680, (byte)1, "Column 'id' on table 't' is not of JSON data type, which is required to create a JSON index on it.")]
    [DataRow("create json index ji on t(zz)", 1911, (byte)7, "Column name 'zz' does not exist in the target table, index or view.")]
    [DataRow("create json index ji on nosuch(d)", 1088, (byte)3, "Cannot find the object \"nosuch\" because it does not exist or you do not have permissions.")]
    [DataRow("create table #u (id int primary key, d json); create json index ji on #u(d)", 13675, (byte)1, "Cannot create a JSON index on temp objects. '#u' is identified as a temp object.")]
    [DataRow("create json index ji on t(d); create json index j2 on t(d)", 13681, (byte)1, "A JSON index 'ji' already exists on column 'd' on table 't', and multiple JSON indexes per column are not allowed.")]
    [DataRow("create json index ji on t(d); create json index ji on t(e)", 1913, (byte)3, "The operation failed because an index or statistics with name 'ji' already exists on table 't'.")]
    [DataRow("create index ji on t(id); create json index ji on t(d)", 1913, (byte)3, "The operation failed because an index or statistics with name 'ji' already exists on table 't'.")]
    [DataRow("create json index ji on t(d); create index ji on t(id)", 1913, (byte)1, "The operation failed because an index or statistics with name 'ji' already exists on table 't'.")]
    [DataRow("create json index ji on t(d) with (drop_existing = on)", 13685, (byte)2, "A JSON index 'ji' cannot be found on column 'd' on table 't'.")]
    [DataRow("create json index ji on t(d); create json index j2 on t(d) with (drop_existing = on)", 13685, (byte)2, "A JSON index 'j2' cannot be found on column 'd' on table 't'.")]
    [DataRow("create json index ji on t(d) with (fillfactor = 0)", 129, (byte)1, "Fillfactor 0 is not a valid percentage; fillfactor must be between 1 and 100.")]
    [DataRow("create json index ji on t(d); drop index t.ji", 3766, (byte)4, "Cannot drop JSON index 't.ji' using old 'Table.Index' syntax, use 'Index ON Table' syntax instead.")]
    [DataRow("create json index ji on t(d); alter table t drop column d", 5074, (byte)1, "The index 'ji' is dependent on column 'd'.")]
    [DataRow("create json index ji on t(d); alter table t alter column d nvarchar(max)", 5074, (byte)1, "The index 'ji' is dependent on column 'd'.")]
    [DataRow("create json index ji on t(d) for ('xx')", 13607, (byte)22, "JSON path is not properly formatted. Unexpected character 'x' is found at position 0.")]
    [DataRow("create json index ji on t(d) for ('append $.a')", 13607, (byte)14, "JSON path is not properly formatted. Unexpected character 'a' is found at position 0.")]
    public void Refusals(string statement, int number, byte state, string message) =>
        AssertSqlError($"{Table} {statement}", number, state, message);

    [TestMethod]
    public void DroppingThePrimaryKeyIsRefused()
    {
        var ex = new Simulation().AssertSqlError("create table t (id int, d json, constraint pk primary key (id)); create json index ji on t(d); alter table t drop constraint pk", 3767);
        AreEqual("Could not drop the primary key constraint 'pk' because the table has a JSON index.", ex.Errors[0].Message);
        AreEqual(3727, ex.Errors[1].Number);
    }

    [TestMethod]
    [DataRow("'$.a', '$.a'", (byte)1)]
    [DataRow("'$.a', '$.a.b'", (byte)1)]
    [DataRow("'$.a.b', '$.a'", (byte)1)]
    [DataRow("'$.\"a\"', '$.a'", (byte)1)]
    [DataRow("'$.A', '$.a'", (byte)1)]
    [DataRow("'lax $.a', 'strict $.a'", (byte)1)]
    [DataRow("'$', '$.a'", (byte)1)]
    [DataRow("'$.a[0]', '$.a'", (byte)1)]
    [DataRow("'$.a[*]'", (byte)2)]
    [DataRow("'$.a[*]', '$.a'", (byte)2)]
    [DataRow("'$.a', '$.a[*]'", (byte)2)]
    [DataRow("'$.a[last]'", (byte)3)]
    [DataRow("'$.a[0 to 1]'", (byte)3)]
    [DataRow("'$.*'", (byte)3)]
    public void InvalidPaths(string paths, byte state) =>
        AssertSqlError($"{Table} create json index ji on t(d) for ({paths})", 13683, state, "Invalid JSON paths in JSON index.");

    [TestMethod]
    [DataRow("'$.ab', '$.a'")]
    [DataRow("'$.a[0]', '$.a[1]'")]
    public void DistinctPaths(string paths) =>
        AreEqual(2, new Simulation().ExecuteScalar($"{Table} create json index ji on t(d) for ({paths}); select count(*) from sys.json_index_paths"));

    [TestMethod]
    [DataRow("online")]
    [DataRow("sort_in_tempdb")]
    [DataRow("ignore_dup_key")]
    [DataRow("statistics_norecompute")]
    [DataRow("resumable")]
    [DataRow("xml_compression")]
    [DataRow("optimize_for_sequential_key")]
    [DataRow("statistics_incremental")]
    public void OptionsTheStatementDoesNotTake(string option) =>
        AssertSqlError($"{Table} create json index ji on t(d) with ({option} = on)", 153, 35, $"Invalid usage of the option {option} in the CREATE JSON INDEX statement.");

    [TestMethod]
    public void UnknownOption()
    {
        var ex = new Simulation().AssertSqlError($"{Table} create json index ji on t(d) with (bogus = on)", 155);
        AreEqual("'bogus' is not a recognized CREATE JSON INDEX option.", ex.Errors[0].Message);
        AreEqual("Invalid usage of the option bogus in the CREATE JSON INDEX statement.", ex.Errors[1].Message);
    }

    [TestMethod]
    [DataRow("create unique json index ji on t(d)", "json")]
    [DataRow("create clustered json index ji on t(d)", "json")]
    [DataRow("create json index ji on t(d, d)", ",")]
    [DataRow("create json index ji on t(d) for ()", ")")]
    [DataRow("create json index ji on t(d) for ('$.a',)", ")")]
    [DataRow("create json index ji on t(d) for ('$.a' '$.b')", "$.b")]
    public void Grammar(string statement, string near) =>
        new Simulation().ValidateSyntaxError($"{Table} {statement}", near);

    [TestMethod]
    [DataRow("create json index ji on t(d) on [primary]", 156)]
    [DataRow("create json index ji on t(d) where id > 1", 156)]
    [DataRow("create json index ji on t(d) with (fillfactor = 80) for ('$.a')", 156)]
    [DataRow("create json index ji on t(d) for (null)", 156)]
    [DataRow("declare @p nvarchar(10) = '$.a'; create json index ji on t(d) for (@p)", 102)]
    public void GrammarKeywords(string statement, int number) =>
        new Simulation().AssertSqlError($"{Table} {statement}", number);

    private const string Indexed = Table + " create json index ji on t(d);";

    /// <summary>ALTER INDEX forms and options a JSON index refuses (probed 2026-09-30 against SQL Server 2025).</summary>
    [TestMethod]
    [DataRow("alter index ji on t set (allow_row_locks = off)", 13688, 1)]
    [DataRow("alter index ji on t set (ignore_dup_key = on)", 13688, 1)]
    [DataRow("alter index all on t set (allow_page_locks = off)", 13688, 1)]
    [DataRow("alter index ji on t pause", 13688, 1)]
    [DataRow("alter index ji on t resume", 13688, 1)]
    [DataRow("alter index ji on t abort", 13688, 1)]
    [DataRow("alter index ji on t rebuild partition = 1", 7731, 5)]
    [DataRow("alter index ji on t reorganize partition = 1", 7731, 5)]
    [DataRow("alter index ji on t reorganize with (compress_all_row_groups = on)", 35375, 3)]
    [DataRow("alter index ji on t rebuild with (online = on)", 153, 37)]
    [DataRow("alter index ji on t rebuild with (ignore_dup_key = on)", 153, 36)]
    [DataRow("alter index ji on t rebuild with (statistics_incremental = on)", 153, 40)]
    [DataRow("alter index ji on t rebuild with (sort_in_tempdb = on)", 153, 42)]
    [DataRow("alter index ji on t rebuild with (statistics_norecompute = on)", 153, 43)]
    [DataRow("alter index ji on t rebuild with (xml_compression = on)", 153, 45)]
    [DataRow("alter index ji on t disable; alter index ji on t set (allow_row_locks = off)", 1973, 1)]
    [DataRow("alter index ji on t disable; alter index ji on t reorganize", 1973, 1)]
    public void AlterIndex_RefusesWhatAJsonIndexDoesntTake(string statement, int number, int state)
    {
        var ex = new Simulation().AssertSqlError($"{Indexed} {statement}", number);
        AreEqual((byte)state, ex.Errors[0].State);
    }

    [TestMethod]
    public void AlterIndex_RebuildOptionNamesAreReportedInTheirRealCase()
    {
        var ex = new Simulation().AssertSqlError($"{Indexed} alter index ji on t rebuild with (Ignore_Dup_Key = On, Online = On)", 153);
        AreEqual("Invalid usage of the option ignore_dup_key in the ALTER INDEX REBUILD statement.", ex.Errors[0].Message);
        ex = new Simulation().AssertSqlError($"{Indexed} alter index ji on t rebuild with (online = on)", 153);
        AreEqual("Invalid usage of the option ONLINE in the ALTER INDEX REBUILD statement.", ex.Errors[0].Message);
    }

    [TestMethod]
    [DataRow("alter index ji on t rebuild with (online = off, ignore_dup_key = off, sort_in_tempdb = off, xml_compression = off, statistics_norecompute = off)")]
    [DataRow("alter index ji on t rebuild with (fillfactor = 80, maxdop = 1, data_compression = columnstore)")]
    [DataRow("alter index ji on t reorganize with (compress_all_row_groups = off, lob_compaction = on)")]
    [DataRow("alter index ji on t rebuild partition = all")]
    [DataRow("alter index all on t rebuild")]
    public void AlterIndex_AcceptsWhatAJsonIndexTakes(string statement)
        => AreEqual(1, new Simulation().ExecuteScalar($"{Indexed} {statement}; select 1"));
}
