using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// An index's <c>FILLFACTOR</c> / <c>PAD_INDEX</c>, recorded from every
/// declaring form and reported through <c>sys.indexes</c> and
/// <c>INDEXPROPERTY</c>. Every expectation probed 2026-09-26 against SQL
/// Server 2025.
/// </summary>
[TestClass]
public sealed class IndexFillFactorTests
{
    private static string Report(string ddl)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int not null, a int, b int)");
        _ = simulation.ExecuteNonQuery(ddl);
        return (string)simulation.ExecuteScalar("""
            select string_agg(concat(name, ':', fill_factor, is_padded, indexproperty(object_id, name, 'IndexFillFactor'), indexproperty(object_id, name, 'IsPadIndex')), ',') within group (order by name)
            from sys.indexes where object_id = object_id('t') and index_id > 0
            """)!;
    }

    [TestMethod]
    [DataRow("create index ix on t (a)", "ix:0000")]
    [DataRow("create index ix on t (a) with (fillfactor = 80)", "ix:800800")]
    [DataRow("create index ix on t (a) with (fillfactor = 80, pad_index = on)", "ix:801801")]
    [DataRow("create index ix on t (a) with (pad_index = on)", "ix:0101")]
    [DataRow("create index ix on t (a) with fillfactor = 70", "ix:700700")]
    [DataRow("alter table t add constraint pk primary key (id) with (fillfactor = 90, pad_index = on)", "pk:901901")]
    [DataRow("alter table t add constraint uq unique (a) with fillfactor = 60", "uq:600600")]
    [DataRow("create index ix on t (a); alter index ix on t rebuild with (fillfactor = 50)", "ix:500500")]
    [DataRow("create index ix on t (a) with (fillfactor = 80); alter index ix on t rebuild", "ix:800800")]
    [DataRow("create index ix on t (a) with (fillfactor = 80); alter index ix on t rebuild with (pad_index = on)", "ix:801801")]
    [DataRow("create index ix on t (a) with (fillfactor = 80); alter index all on t rebuild with (fillfactor = 30)", "ix:300300")]
    public void FillFactor_IsRecorded(string ddl, string expected)
        => AreEqual(expected, Report(ddl));

    [TestMethod]
    public void CreateTable_RecordsInlineFillFactors()
        => AreEqual("ix:651,pk:750", new Simulation().ExecuteScalar("""
            create table t (id int not null constraint pk primary key with (fillfactor = 75), a int, index ix (a) with (fillfactor = 65, pad_index = on));
            select string_agg(concat(name, ':', fill_factor, is_padded), ',') within group (order by name) from sys.indexes where object_id = object_id('t')
            """));

    [TestMethod]
    public void Rollback_RestoresFillFactor()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int); create index ix on t (a) with (fillfactor = 80)").ExecuteNonQuery();
        _ = connection.CreateCommand("begin tran; alter index ix on t rebuild with (fillfactor = 20); rollback").ExecuteNonQuery();
        AreEqual((byte)80, connection.CreateCommand("select fill_factor from sys.indexes where name = 'ix'").ExecuteScalar());
    }

    [TestMethod]
    [DataRow("create index ix on t (a) with (fillfactor = 0)", 129, "Fillfactor 0 is not a valid percentage; fillfactor must be between 1 and 100.")]
    [DataRow("create index ix on t (a) with (fillfactor = 101)", 129, "Fillfactor 101 is not a valid percentage; fillfactor must be between 1 and 100.")]
    [DataRow("create index ix on t (a) with (fillfactor = -1)", 129, "Fillfactor -1 is not a valid percentage; fillfactor must be between 1 and 100.")]
    [DataRow("create index ix on t (a); alter index ix on t set (fillfactor = 30)", 155, "'fillfactor' is not a recognized ALTER INDEX SET option.")]
    [DataRow("create index ix on t (a); alter index ix on t set (online = on)", 155, "'online' is not a recognized ALTER INDEX SET option.")]
    [DataRow("create index ix on t (a); alter index ix on t set (nope = on)", 155, "'nope' is not a recognized ALTER INDEX option.")]
    public void Refusals_MatchReal(string sql, int number, string message)
        => new Simulation().AssertSqlError($"create table t (id int not null, a int); {sql}", number, message);

    /// <summary>
    /// Each statement refuses a WITH option name it doesn't take, naming itself
    /// (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("create index ix on t (a) with (nope = on)", 155, "'nope' is not a recognized CREATE INDEX option.")]
    [DataRow("create index ix on t (a); alter index ix on t rebuild with (nope = on)", 155, "'nope' is not a recognized ALTER INDEX option.")]
    [DataRow("create index ix on t (a); alter index ix on t rebuild with (drop_existing = on)", 155, "'drop_existing' is not a recognized ALTER INDEX REBUILD option.")]
    [DataRow("create index ix on t (a); alter index ix on t rebuild with (optimize_for_sequential_key = off)", 155, "'optimize_for_sequential_key' is not a recognized ALTER INDEX REBUILD option.")]
    [DataRow("alter table t add constraint pk primary key (id) with (nope = on)", 155, "'nope' is not a recognized ALTER TABLE option.")]
    [DataRow("alter table t add constraint pk primary key (id) with (drop_existing = off)", 155, "'drop_existing' is not a recognized ALTER TABLE option.")]
    [DataRow("create index ix on t (a) with (compression_delay = 5)", 122, "The COMPRESSION_DELAY option is allowed only with CREATE or ALTER COLUMNSTORE INDEX syntax.")]
    [DataRow("create index ix on t (a) with (max_duration = 1)", 11431, "The MAX_DURATION option is not permitted as the RESUMABLE option is not turned 'ON'.")]
    public void UnknownOptions_AreRefused(string sql, int number, string message)
        => new Simulation().AssertSqlError($"create table t (id int not null, a int); {sql}", number, message);

    [TestMethod]
    public void EveryKnownOption_IsAcceptedByCreateIndex()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int not null, a int);
            create index ix on t (a) with (pad_index = off, statistics_norecompute = off, sort_in_tempdb = off, drop_existing = off, online = off,
                allow_row_locks = on, allow_page_locks = on, optimize_for_sequential_key = off, maxdop = 1, data_compression = none,
                statistics_incremental = off, ignore_dup_key = off, fillfactor = 90, resumable = off, xml_compression = off);
            select count(*) from sys.indexes where name = 'ix'
            """));

    /// <summary>
    /// A key or inline index declared with its table takes its options by the
    /// declaring statement's list: CREATE TABLE (a table variable's and a
    /// function's return table too) refuses an unknown name and the four a
    /// build on its own takes, CREATE TYPE every name but IGNORE_DUP_KEY, and
    /// ALTER TABLE ADD an unknown name and DROP_EXISTING — each as the batch
    /// compiles (probed 2026-10-07 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("create table x (a int primary key with (nope = on))", "'nope' is not a recognized CREATE TABLE option.")]
    [DataRow("create table x (a int not null, constraint pk primary key (a) with (sort_in_tempdb = on))", "'sort_in_tempdb' is not a recognized CREATE TABLE option.")]
    [DataRow("create table x (a int index ix with (online = on))", "'online' is not a recognized CREATE TABLE option.")]
    [DataRow("create table x (a int, index ix (a) with (maxdop = 1))", "'maxdop' is not a recognized CREATE TABLE option.")]
    [DataRow("create table x (a int unique with (drop_existing = off))", "'drop_existing' is not a recognized CREATE TABLE option.")]
    [DataRow("create table #x (a int primary key with (nope = on))", "'nope' is not a recognized CREATE TABLE option.")]
    [DataRow("declare @x table (a int primary key with (nope = on))", "'nope' is not a recognized CREATE TABLE option.")]
    [DataRow("create type x as table (a int primary key with (nope = on))", "'nope' is not a recognized CREATE TYPE option.")]
    [DataRow("create type x as table (a int primary key with (FillFactor = 50))", "'fillfactor' is not a recognized CREATE TYPE option.")]
    [DataRow("create type x as table (a int primary key with (Data_Compression = page))", "'data_compression' is not a recognized CREATE TYPE option.")]
    [DataRow("create type x as table (a int primary key with (Pad_Index = on))", "'Pad_Index' is not a recognized CREATE TYPE option.")]
    [DataRow("alter table t add b int constraint u unique with (nope = on)", "'nope' is not a recognized ALTER TABLE option.")]
    [DataRow("alter table t add b int constraint u unique with (drop_existing = off)", "'drop_existing' is not a recognized ALTER TABLE option.")]
    public void ColumnClauseOptions_AreTheDeclaringStatements(string sql, string message)
    {
        var simulation = new Simulation();
        var ex = simulation.AssertSqlError($"create table t (id int not null, a int); {sql}", 155);
        AreEqual(message, ex.Errors[0].Message);
        // A compile error: the CREATE TABLE ahead of it never ran.
        _ = IsInstanceOfType<DBNull>(simulation.ExecuteScalar("select object_id('t')"));
    }

    [TestMethod]
    [DataRow("create table x (a int primary key with (nope = 1))", 155, 153, "CREATE TABLE")]
    [DataRow("create type x as table (a int primary key with (nope = 1))", 155, 153, "CREATE TYPE")]
    [DataRow("alter table t add constraint u unique (a) with (nope = 1)", 155, 153, "ALTER TABLE")]
    [DataRow("create table x (a int primary key with (statistics_only = 0))", 102, 155, "CREATE TABLE")]
    [DataRow("alter table t add constraint u unique (a) with (statistics_only = 0)", 102, 155, "ALTER TABLE")]
    [DataRow("create type x as table (a int primary key with (compression_delay = 0))", 122, 155, "CREATE TYPE")]
    public void ColumnClauseOptions_PairedRefusals(string sql, int first, int second, string statement)
    {
        var ex = new Simulation().AssertSqlError($"create table t (id int not null, a int); {sql}", first);
        AreEqual(second, ex.Errors[1].Number);
        IsTrue(ex.Errors[first == 155 ? 0 : 1].Message.EndsWith($"is not a recognized {statement} option.", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("create table x (a int primary key with (compression_delay = 0))", 122)]
    [DataRow("create table x (a int primary key with (max_duration = 1))", 11431)]
    [DataRow("create table x (a int primary key with (resumable = on))", 11438)]
    [DataRow("create table x (a int primary key with (Bucket_Count = 8))", 10790)]
    [DataRow("create type x as table (a int primary key with (bucket_count = 8))", 10790)]
    [DataRow("alter table t add constraint u unique (a) with (bucket_count = 8)", 10790)]
    [DataRow("alter table t add b int constraint u unique with (online = on)", 1758)]
    [DataRow("alter table t add b int, constraint u unique (a) with (online = on)", 1758)]
    [DataRow("alter table t add constraint u unique (a) with (online = on), constraint v unique (id)", 1758)]
    public void ColumnClauseOptions_OtherRefusals(string sql, int number)
    {
        var ex = new Simulation().AssertSqlError($"create table t (id int not null, a int); {sql}", number);
        if (number == 10790)
            Contains(sql.Contains("Bucket_Count", StringComparison.Ordinal) ? "'Bucket_Count'" : "'bucket_count'", ex.Errors[0].Message);
    }

    [TestMethod]
    public void ColumnClauseOptions_TheDeclaringStatementTakes_AreAccepted()
        => AreEqual((byte)90, new Simulation().ExecuteScalar("""
            create table t (id int not null, a int);
            create table x (a int primary key with (fillfactor = 90, pad_index = on, ignore_dup_key = off, data_compression = page, resumable = off,
                xml_compression = off, optimize_for_sequential_key = on, statistics_incremental = off, allow_row_locks = on, allow_page_locks = on,
                statistics_norecompute = on), b int unique with (ignore_dup_key = on), index ix (b) with (data_compression = page));
            create type tt as table (a int primary key with (ignore_dup_key = on));
            declare @v table (a int primary key with (fillfactor = 70));
            alter table t add b int constraint u unique with (sort_in_tempdb = on, maxdop = 1, online = off);
            alter table t add constraint pk primary key (id) with (online = on);
            select fill_factor from sys.indexes where object_id = object_id('x') and is_primary_key = 1
            """));
}
