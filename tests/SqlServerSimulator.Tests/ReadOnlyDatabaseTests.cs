using SqlServerSimulator.Bacpac;
using SqlServerSimulator.Storage.Bacpac;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>ALTER DATABASE … SET { READ_ONLY | READ_WRITE }</c> and the write refusal
/// it arms: <strong>Msg 3906</strong> (<c>Failed to update database "&lt;n&gt;"
/// because the database is read-only.</c>), class 16 state 1, identical wording
/// for DML and DDL. Every number, message and allowance below was probed against
/// SQL Server 2025 on 2026-08-04 (a scratch database toggled read-only, plus the
/// system-database rules re-probed with sysadmin).
/// </summary>
[TestClass]
public sealed class ReadOnlyDatabaseTests
{
    private const string RefusalMessage = "Failed to update database \"other\" because the database is read-only.";

    /// <summary>A second database with one populated table, then set read-only.</summary>
    private static Simulation WithReadOnlyOther()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create database other",
            "create table other.dbo.t (id int not null primary key, label varchar(20))",
            "insert other.dbo.t values (1, 'a'), (2, 'b')",
            "alter database other set read_only");
        return simulation;
    }

    [TestMethod]
    public void ReadOnly_IsProjectedBySysDatabases_AndRoundTrips()
    {
        var simulation = WithReadOnlyOther();
        IsTrue((bool)simulation.ExecuteScalar("select is_read_only from sys.databases where name = 'other'")!);
        AreEqual("READ_ONLY", simulation.ExecuteScalar("select databasepropertyex('other', 'Updateability')"));

        _ = simulation.ExecuteNonQuery("alter database other set read_write");
        IsFalse((bool)simulation.ExecuteScalar("select is_read_only from sys.databases where name = 'other'")!);
        AreEqual("READ_WRITE", simulation.ExecuteScalar("select databasepropertyex('other', 'Updateability')"));

        // The write the flag was refusing lands once it is cleared.
        AreEqual(1, simulation.ExecuteNonQuery("insert other.dbo.t values (3, 'c')"));
    }

    /// <summary>A fresh database is writable, and every seeded one reads back 0.</summary>
    [TestMethod]
    public void FreshDatabases_AreWritable()
        => AreEqual(0, new Simulation().ExecuteScalar("select count(*) from sys.databases where is_read_only = 1"));

    [TestMethod]
    public void Reads_AreUnaffected()
        => AreEqual(2, WithReadOnlyOther().ExecuteScalar("select count(*) from other.dbo.t"));

    /// <summary>The whole DML family, each writing through a three-part name.</summary>
    [TestMethod]
    [DataRow("insert other.dbo.t values (3, 'c')")]
    [DataRow("update other.dbo.t set label = 'z'")]
    [DataRow("delete other.dbo.t")]
    [DataRow("merge other.dbo.t as d using (select 9 as id) as s on d.id = s.id when not matched then insert (id, label) values (s.id, 'new');")]
    public void CrossDatabaseWrite_AgainstReadOnlyTarget_RaisesMsg3906(string write)
    {
        var ex = WithReadOnlyOther().AssertSqlError(write, 3906);
        AreEqual(RefusalMessage, ex.Message);
        AreEqual(16, ex.Class);
        AreEqual(1, ex.State);
    }

    /// <summary>
    /// Real raises only once a write is actually due: an UPDATE / DELETE matching
    /// no row, an INSERT … SELECT producing none, and a MERGE whose actions all
    /// decline all complete quietly on a read-only database (probe-confirmed).
    /// </summary>
    [TestMethod]
    [DataRow("update other.dbo.t set label = 'z' where 1 = 0")]
    [DataRow("delete other.dbo.t where 1 = 0")]
    [DataRow("insert other.dbo.t select id, label from other.dbo.t where 1 = 0")]
    [DataRow("merge other.dbo.t as d using (select 1 as id) as s on d.id = s.id when not matched then insert (id, label) values (s.id, 'new');")]
    public void WritesAffectingNoRow_AreAccepted(string write)
        => AreEqual(0, WithReadOnlyOther().ExecuteNonQuery(write));

    /// <summary>DDL carries the same refusal, with the same wording.</summary>
    [TestMethod]
    [DataRow("create table other.dbo.t2 (id int)")]
    [DataRow("select id into other.dbo.t3 from other.dbo.t")]
    [DataRow("select id into other.dbo.t3 from other.dbo.t where 1 = 0")]
    [DataRow("truncate table other.dbo.t")]
    [DataRow("drop table other.dbo.t")]
    [DataRow("alter table other.dbo.t add extra int")]
    [DataRow("create index ix_t on other.dbo.t (label)")]
    [DataRow("create sequence other.dbo.s as int start with 1")]
    [DataRow("create type other.dbo.ty from int")]
    [DataRow("create synonym other.dbo.syn for other.dbo.t")]
    public void CrossDatabaseDdl_AgainstReadOnlyTarget_RaisesMsg3906(string ddl)
        => WithReadOnlyOther().AssertSqlError(ddl, 3906, RefusalMessage);

    /// <summary>
    /// A module `CREATE` is refused in its own database, the batch-first rule
    /// meaning each needs its own command. Real attributes the module name too;
    /// the number and wording are what the simulator pins here.
    /// </summary>
    [TestMethod]
    [DataRow("create view v as select 1 as x")]
    [DataRow("create procedure p as select 1")]
    [DataRow("create function fn() returns int as begin return 1 end")]
    [DataRow("create trigger tr on dbo.t after insert as select 1")]
    public void ModuleCreate_OnAReadOnlyDatabase_RaisesMsg3906(string create)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table dbo.t (id int)",
            "alter database current set read_only");
        var ex = simulation.AssertSqlError(create, 3906);
        AreEqual("Failed to update database \"simulated\" because the database is read-only.", ex.Message);
    }

    /// <summary>
    /// Writes to a table that belongs to no database — a <c>#temp</c> table, a
    /// table variable — are unaffected however the session's own database is
    /// set: real serves those from tempdb (probe-confirmed, including
    /// <c>SELECT … INTO #t</c> reading a read-only table).
    /// </summary>
    [TestMethod]
    public void TempTableAndTableVariableWrites_AreUnaffected()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table dbo.t (id int)");
        _ = simulation.ExecuteNonQuery("insert dbo.t values (1), (2)");
        _ = simulation.ExecuteNonQuery("alter database current set read_only");

        AreEqual(2, simulation.ExecuteScalar("""
            create table #t (id int);
            insert #t values (1), (2);
            select count(*) from #t
            """));
        AreEqual(2, simulation.ExecuteScalar("""
            select id into #into from dbo.t;
            select count(*) from #into
            """));
        AreEqual(2, simulation.ExecuteScalar("""
            declare @v table (id int);
            insert @v select id from dbo.t;
            select count(*) from @v
            """));
    }

    /// <summary>
    /// <c>master</c> and <c>tempdb</c> pin the option — <strong>Msg 5058</strong>
    /// at their own states (5 and 4), for either value asked for — while
    /// <c>model</c> and <c>msdb</c> accept it (all probe-confirmed).
    /// </summary>
    [TestMethod]
    [DataRow("master", "READ_ONLY", 5)]
    [DataRow("master", "READ_WRITE", 5)]
    [DataRow("tempdb", "READ_ONLY", 4)]
    [DataRow("tempdb", "READ_WRITE", 4)]
    public void PinnedSystemDatabases_RaiseMsg5058(string database, string option, int state)
    {
        var ex = new Simulation().AssertSqlError($"alter database {database} set {option}", 5058);
        AreEqual($"Option '{option}' cannot be set in database '{database}'.", ex.Message);
        AreEqual(16, ex.Class);
        AreEqual(state, ex.State);
    }

    [TestMethod]
    [DataRow("model")]
    [DataRow("msdb")]
    public void UnpinnedSystemDatabases_AcceptTheOption(string database)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"alter database {database} set read_only");
        IsTrue((bool)simulation.ExecuteScalar($"select is_read_only from sys.databases where name = '{database}'")!);
        _ = simulation.ExecuteNonQuery($"alter database {database} set read_write");
        IsFalse((bool)simulation.ExecuteScalar($"select is_read_only from sys.databases where name = '{database}'")!);
    }

    /// <summary>
    /// <c>COMPATIBILITY_LEVEL</c> is the one SET option a read-only database
    /// refuses; the rest — and <c>READ_WRITE</c> itself — move freely
    /// (probe-confirmed for RECURSIVE_TRIGGERS / ALLOW_SNAPSHOT_ISOLATION /
    /// READ_COMMITTED_SNAPSHOT / ANSI_NULLS / RECOVERY).
    /// </summary>
    [TestMethod]
    public void CompatibilityLevel_IsRefused_WhileOtherOptionsMove()
    {
        var simulation = WithReadOnlyOther();
        simulation.AssertSqlError("alter database other set compatibility_level = 160", 3906, RefusalMessage);

        _ = simulation.ExecuteNonQuery("alter database other set recursive_triggers on");
        IsTrue((bool)simulation.ExecuteScalar("select is_recursive_triggers_on from sys.databases where name = 'other'")!);
        _ = simulation.ExecuteNonQuery("alter database other set allow_snapshot_isolation on");
        _ = simulation.ExecuteNonQuery("alter database other set ansi_nulls on");
        _ = simulation.ExecuteNonQuery("alter database other set recovery simple");
    }

    /// <summary>The access-mode grammar takes the same optional termination clause the other bare-state options do.</summary>
    [TestMethod]
    [DataRow("alter database other set read_only")]
    [DataRow("alter database other set read_only with rollback immediate")]
    [DataRow("alter database other set read_only with no_wait")]
    [DataRow("alter database other set read_only with rollback after 5 seconds")]
    public void AccessModeTerminationClause_Parses(string statement)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create database other", statement);
        IsTrue((bool)simulation.ExecuteScalar("select is_read_only from sys.databases where name = 'other'")!);
    }

    /// <summary>
    /// A bacpac import lands writable. DacFx omits the access mode from
    /// <c>SqlDatabaseOptions</c> (verified against the WideWorldImporters and
    /// AdventureWorks models), and an <c>IsReadOnly</c> property is deliberately
    /// not translated: the element is read in phase 1, before the schema and
    /// data load, so a READ_ONLY set there would refuse the rest of its own
    /// import.
    /// </summary>
    [TestMethod]
    public void BacpacImport_LandsWritable_EvenWithAnIsReadOnlyProperty()
    {
        using var bacpac = BacpacBuilder.Create()
            .DatabaseOption("IsReadOnly", "True")
            .Table("dbo", "T", t => t.Column("Id", "int").Row(1))
            .Build();

        var simulation = new Simulation();
        simulation.ImportBacpac(bacpac, out _, new BacpacImportOptions { DatabaseName = "imported" });

        IsFalse((bool)simulation.ExecuteScalar("select is_read_only from sys.databases where name = 'imported'")!);
        AreEqual(1, simulation.ExecuteNonQuery("insert dbo.T values (2)"));
    }
}
