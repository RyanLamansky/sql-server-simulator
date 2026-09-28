using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Placing tables, indexes, keys and LOB data on a filegroup, and what a
/// read-only or file-less filegroup refuses (probed 2026-09-28 against SQL
/// Server 2025).
/// </summary>
[TestClass]
public sealed class FilegroupPlacementTests
{
    private const string Filegroups = """
        alter database current add filegroup fg1;
        alter database current add filegroup fg2;
        alter database current add filegroup fgempty;
        alter database current add file (name = f1, filename = '/var/opt/mssql/data/f1.ndf', size = 1MB) to filegroup fg1;
        alter database current add file (name = f2, filename = '/var/opt/mssql/data/f2.ndf', size = 1MB) to filegroup fg2;
        """;

    private const string IndexSpaces = """
        select string_agg(concat(object_name(i.object_id), '.', i.index_id, ':', ds.name), ',') within group (order by object_name(i.object_id), i.index_id)
        from sys.indexes i join sys.data_spaces ds on ds.data_space_id = i.data_space_id
        where objectproperty(i.object_id, 'IsMsShipped') = 0
        """;

    private const string LobSpaces = """
        select string_agg(concat(name, ':', lob_data_space_id), ',') within group (order by name) from sys.tables
        """;

    private static Simulation WithFilegroups()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Filegroups);
        return sim;
    }

    [TestMethod]
    public void Tables_Indexes_AndKeys_ReportWhereTheyWerePlaced()
    {
        var sim = WithFilegroups();
        _ = sim.ExecuteNonQuery("""
            create table t1 (a int) on fg1;
            create table t2 (a int, b varchar(max)) on fg1;
            create table t3 (a int, b varchar(max)) on fg1 textimage_on fg2;
            create table t4 (a int, b varchar(max));
            create table t5 (a int primary key nonclustered, b int) on fg1;
            create table t6 (a int primary key on fg2, b int) on fg1;
            create table t7 (a int, b nvarchar(max)) on [default];
            create table t8 (a int, b text) textimage_on fg2;
            create index ix on t1 (a);
            create index ix2 on t1 (a) on fg2;
            create clustered index cx on t2 (a);
            create clustered index cx on t4 (a) on fg2
            """);
        AreEqual(
            "t1.0:fg1,t1.2:fg1,t1.3:fg2,t2.1:fg1,t3.0:fg1,t4.1:fg2,t5.0:fg1,t5.2:fg1,t6.1:fg2,t7.0:PRIMARY,t8.0:PRIMARY",
            sim.ExecuteScalar(IndexSpaces));
        AreEqual("t1:0,t2:2,t3:3,t4:1,t5:0,t6:0,t7:1,t8:3", sim.ExecuteScalar(LobSpaces));
    }

    [TestMethod]
    public void WithoutOn_ATableLandsOnTheDefaultFilegroup()
    {
        var sim = WithFilegroups();
        _ = sim.ExecuteNonQuery("""
            alter database current modify filegroup fg1 default;
            create table t1 (a int, b varchar(max));
            create table t2 (a int) on [primary];
            create index ix on t2 (a);
            select 1 a into t3
            """);
        AreEqual("t1.0:fg1,t2.0:PRIMARY,t2.2:PRIMARY,t3.0:fg1", sim.ExecuteScalar(IndexSpaces));
        AreEqual("t1:2,t2:0,t3:0", sim.ExecuteScalar(LobSpaces));
    }

    [TestMethod]
    public void ClusteredIndex_MovesTheRows_AndTheHeapStaysWhenItIsDropped()
    {
        var sim = WithFilegroups();
        _ = sim.ExecuteNonQuery("create table t (a int not null, b int); create clustered index cx on t (a) on fg1; create index nx on t (b)");
        AreEqual("t.1:fg1,t.2:fg1", sim.ExecuteScalar(IndexSpaces));
        _ = sim.ExecuteNonQuery("drop index cx on t");
        AreEqual("t.0:fg1,t.2:fg1", sim.ExecuteScalar(IndexSpaces));
        _ = sim.ExecuteNonQuery("alter table t add constraint pk primary key (a) on fg2; alter table t add constraint uq unique (b)");
        AreEqual("t.1:fg2,t.2:fg1,t.3:fg2", sim.ExecuteScalar(IndexSpaces));
    }

    [TestMethod]
    public void AllocationUnits_AndSpHelp_NameTheFilegroups()
    {
        var sim = WithFilegroups();
        _ = sim.ExecuteNonQuery("create table t (a int, b varchar(max)) on fg1 textimage_on fg2; create index ix on t (a) on fg2; insert t values (1, replicate(cast('x' as varchar(max)), 20000))");
        AreEqual("IN_ROW_DATA:fg1,LOB_DATA:fg2,IN_ROW_DATA:fg2", sim.ExecuteScalar("""
            select string_agg(concat(au.type_desc, ':', fg.name) collate database_default, ',') within group (order by p.index_id, au.type)
            from sys.allocation_units au join sys.partitions p on p.partition_id = au.container_id join sys.filegroups fg on fg.data_space_id = au.data_space_id
            where p.object_id = object_id('t')
            """));
        using var reader = sim.ExecuteReader("exec sp_help 't'");
        var filegroup = "";
        var description = "";
        do
        {
            if (reader.GetName(0) == "Data_located_on_filegroup" && reader.Read())
                filegroup = reader.GetString(0);
            if (reader.GetName(0) == "index_name" && reader.Read())
                description = reader.GetString(1);
        } while (reader.NextResult());
        AreEqual(("fg1", "nonclustered located on fg2"), (filegroup, description));
    }

    [TestMethod]
    [DataRow("create table t (a int) on nofg", 1921)]
    [DataRow("create table t (a int, b varchar(max)) textimage_on nofg", 1921)]
    [DataRow("create table t (a int) textimage_on fg1", 1709)]
    [DataRow("create table t (a int, b varchar(max)) on scheme_less textimage_on fg1", 1921)]
    public void Refusals(string statement, int number)
        => _ = WithFilegroups().AssertSqlError(statement, number);

    [TestMethod]
    public void AFilegroupWithoutFiles_TakesTablesButNotRows()
    {
        var sim = WithFilegroups();
        _ = sim.ExecuteNonQuery("create table t (a int) on fgempty; create table w (a int); insert w values (1)");
        AreEqual(3, sim.AssertSqlError("insert t values (1)", 622).State);
        var ex = sim.AssertSqlError("create index ix on w (a) on fgempty", 622);
        Contains("The statement has been terminated.", ex.Message);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.indexes where name = 'ix'"));
    }

    [TestMethod]
    public void AReadOnlyFilegroup_RefusesWritesAndNewObjects()
    {
        var sim = WithFilegroups();
        _ = sim.ExecuteNonQuery("""
            create table t (a int, b int) on fg1;
            create table u (a int, b int);
            create index ix on u (b) on fg1;
            insert t values (1, 1); insert u values (1, 1);
            alter database current modify filegroup fg1 read_only
            """);
        StartsWith("The index \"\" for table \"dbo.t\" (RowsetId ", sim.AssertSqlError("insert t values (2, 2)", 652).Errors[0].Message);
        _ = sim.AssertSqlError("update t set a = 3", 652);
        _ = sim.AssertSqlError("delete t", 652);
        Contains("The index \"ix\" for table \"dbo.u\"", sim.AssertSqlError("update u set b = 5", 652).Errors[0].Message);
        AreEqual(1, sim.ExecuteNonQuery("update u set a = 5"));
        AreEqual((1924, (byte)2), (sim.AssertSqlError("create table v (a int) on fg1", 1924).Number, sim.AssertSqlError("create index ix2 on u (a) on fg1", 1924).State));
        AreEqual(1, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void Removal_WaitsForTheObjects()
    {
        var sim = WithFilegroups();
        _ = sim.ExecuteNonQuery("create table t (a int) on fg1; insert t values (1)");
        _ = sim.AssertSqlError("alter database current remove file f1", 5042);
        _ = sim.ExecuteNonQuery("delete t");
        _ = sim.AssertSqlError("alter database current remove file f1", 5042);
        _ = sim.ExecuteNonQuery("drop table t; create table t (a int) on fg1; alter database current remove file f1");
        AreEqual(8, sim.AssertSqlError("alter database current remove filegroup fg1", 5042).State);
        _ = sim.ExecuteNonQuery("drop table t; alter database current remove filegroup fg1");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.filegroups where name = 'fg1'"));
    }

    [TestMethod]
    public void RolledBack_ClusteredMove_RestoresThePlacement()
    {
        var sim = WithFilegroups();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int) on fg1").ExecuteNonQuery();
        _ = connection.CreateCommand("begin tran; create clustered index cx on t (a) on fg2; rollback").ExecuteNonQuery();
        AreEqual("t.0:fg1", connection.CreateCommand(IndexSpaces).ExecuteScalar());
    }
}
