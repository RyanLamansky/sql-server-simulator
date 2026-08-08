using SqlServerSimulator.Bacpac;
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
    /// The session's own database, read-only, carrying one table with an index,
    /// a second schema, a principal and a full-text catalog — everything the
    /// non-table statements below need a target for.
    /// </summary>
    private static Simulation WithReadOnlySelf()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create schema s1",
            """
            create table dbo.t (id int not null constraint pk_t primary key, label nvarchar(20));
            create index ix_t on dbo.t (label);
            create fulltext catalog ftc0;
            create user u1 without login;
            create role r1
            """,
            "alter database current set read_only");
        return simulation;
    }

    private const string SelfRefusalMessage = "Failed to update database \"simulated\" because the database is read-only.";

    /// <summary>
    /// Every remaining catalog-writing statement carries the refusal too — the
    /// permission family, <c>sp_rename</c>, the extended properties, schema
    /// transfer, index <c>ALTER</c> / <c>DROP</c>, and the database-scoped
    /// principal DDL. All at state 1 (probe-confirmed 2026-08-08).
    /// </summary>
    [TestMethod]
    [DataRow("grant select on dbo.t to u1")]
    [DataRow("revoke select on dbo.t from u1")]
    [DataRow("deny select on dbo.t to u1")]
    [DataRow("grant select on schema::dbo to u1")]
    [DataRow("exec sp_rename 'dbo.t', 'tt'")]
    [DataRow("exec sp_rename 'dbo.t.label', 'lbl', 'COLUMN'")]
    [DataRow("exec sp_rename 'dbo.t.ix_t', 'ix2', 'INDEX'")]
    [DataRow("exec sp_addextendedproperty @name = N'X', @value = N'Y', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N't'")]
    [DataRow("alter schema s1 transfer dbo.t")]
    [DataRow("alter index ix_t on dbo.t disable")]
    [DataRow("alter index ix_t on dbo.t rebuild")]
    [DataRow("alter index all on dbo.t rebuild")]
    [DataRow("drop index ix_t on dbo.t")]
    [DataRow("create user u2 without login")]
    [DataRow("drop user u1")]
    [DataRow("create role r2")]
    [DataRow("drop role r1")]
    [DataRow("alter role r1 add member u1")]
    [DataRow("create application role ar with password = 'Pa$$w0rd!23'")]
    [DataRow("drop assembly nosuchassembly")]
    public void CatalogWritingStatements_OnAReadOnlyDatabase_RaiseMsg3906(string statement)
    {
        var ex = WithReadOnlySelf().AssertSqlError(statement, 3906);
        AreEqual(SelfRefusalMessage, ex.Message);
        AreEqual(16, ex.Class);
        AreEqual(1, ex.State);
    }

    /// <summary>
    /// <c>CREATE SCHEMA</c> has to open its own batch, so it can't ride the
    /// DataRow list above.
    /// </summary>
    [TestMethod]
    public void CreateSchema_OnAReadOnlyDatabase_RaisesMsg3906()
        => WithReadOnlySelf().AssertSqlError("create schema s2", 3906, SelfRefusalMessage);

    /// <summary>
    /// <c>ALTER TABLE</c> is the one statement whose state is not 1 — every
    /// sub-action reports state 12 (probe-confirmed).
    /// </summary>
    [TestMethod]
    [DataRow("alter table dbo.t add extra int null")]
    [DataRow("alter table dbo.t drop column label")]
    [DataRow("alter table dbo.t alter column label nvarchar(40)")]
    [DataRow("alter table dbo.t add constraint ck_t check (id > 0)")]
    [DataRow("alter table dbo.t drop constraint pk_t")]
    [DataRow("alter table dbo.t nocheck constraint all")]
    [DataRow("alter table dbo.t rebuild")]
    public void AlterTable_OnAReadOnlyDatabase_RaisesMsg3906AtState12(string statement)
    {
        var ex = WithReadOnlySelf().AssertSqlError(statement, 3906);
        AreEqual(SelfRefusalMessage, ex.Message);
        AreEqual(12, ex.State);
    }

    /// <summary>
    /// The full-text statements report the subsystem's own <strong>Msg 7690</strong>
    /// rather than Msg 3906, at a state per statement (probe-confirmed).
    /// </summary>
    [TestMethod]
    [DataRow("create fulltext catalog ftc1", 100)]
    [DataRow("drop fulltext catalog ftc0", 102)]
    [DataRow("create fulltext index on dbo.t (label) key index pk_t", 103)]
    [DataRow("drop fulltext index on dbo.t", 105)]
    public void FullTextStatements_OnAReadOnlyDatabase_RaiseMsg7690(string statement, int state)
    {
        var ex = WithReadOnlySelf().AssertSqlError(statement, 7690);
        AreEqual("Full-text operation failed because database is read only.", ex.Message);
        AreEqual(16, ex.Class);
        AreEqual(state, ex.State);
    }

    /// <summary>
    /// Where the refusal sits relative to name resolution differs per statement,
    /// and real's order is what the simulator follows: <c>ALTER INDEX</c> reports
    /// a missing table first and a missing index second, <c>DROP INDEX</c>
    /// reports both of its own misses first, and <c>sp_rename</c> /
    /// <c>sp_addextendedproperty</c> resolve their target before refusing.
    /// </summary>
    [TestMethod]
    [DataRow("alter index ix_t on dbo.nosuch disable", 1088)]
    [DataRow("drop index ix_t on dbo.nosuch", 3701)]
    [DataRow("drop index nosuch on dbo.t", 3701)]
    [DataRow("exec sp_rename 'dbo.nosuch', 'x'", 15225)]
    [DataRow("exec sp_rename 'dbo.t.nosuch', 'x', 'COLUMN'", 15248)]
    [DataRow("exec sp_addextendedproperty @name = N'X', @value = N'Y', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'nosuch'", 15135)]
    [DataRow("alter table dbo.nosuch add extra int null", 4902)]
    public void ResolutionErrorsThatOutrankTheRefusal(string statement, int expected)
        => _ = WithReadOnlySelf().AssertSqlError(statement, expected);

    /// <summary>
    /// And the ones that don't: real refuses these before the name is looked at
    /// all, so a target that doesn't exist still reports the read-only error.
    /// </summary>
    [TestMethod]
    [DataRow("grant select on dbo.nosuch to u1")]
    [DataRow("grant select on dbo.t to nosuchuser")]
    [DataRow("alter schema s1 transfer dbo.nosuch")]
    [DataRow("alter index nosuch on dbo.t disable")]
    [DataRow("create user u1 without login")]
    public void TheRefusalThatOutranksResolution(string statement)
        => WithReadOnlySelf().AssertSqlError(statement, 3906, SelfRefusalMessage);

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
    /// A bacpac declaring <c>IsReadOnly=True</c> imports read-only — DacFx does
    /// emit the property (probe-confirmed by exporting a database SET
    /// READ_ONLY), so dropping it would leave a database writable that real's
    /// own import refuses every write to.
    /// </summary>
    /// <remarks>
    /// The access mode can't be applied during the model walk: that runs in
    /// phase 1, before the schema and data load, so a READ_ONLY set there
    /// would make the database refuse the rest of its own import. It's
    /// deferred until every row has landed, which is what the row-count
    /// assertion here pins — the flag must arrive *after* the data, not
    /// instead of it.
    /// </remarks>
    [TestMethod]
    public void BacpacImport_DeclaringReadOnly_LandsReadOnlyWithItsDataIntact()
    {
        using var bacpac = BacpacBuilder.Create()
            .DatabaseOption("IsReadOnly", "True")
            .Table("dbo", "T", t => t.Column("Id", "int").Row(1).Row(2))
            .Build();

        var simulation = new Simulation();
        simulation.ImportBacpac(bacpac, out var diag, new BacpacImportOptions { DatabaseName = "imported" });

        IsEmpty(diag.Skipped);
        AreEqual(2, simulation.ExecuteScalar("select count(*) from dbo.T"));
        IsTrue((bool)simulation.ExecuteScalar("select is_read_only from sys.databases where name = 'imported'")!);
        _ = simulation.AssertSqlError("insert dbo.T values (3)", 3906);
    }

    /// <summary>
    /// A bacpac's <c>RecoveryMode</c> uses DacFx's own encoding, which is not
    /// <c>sys.databases.recovery_model</c>'s: the property is omitted for the
    /// FULL default and written as 1 for SIMPLE / 2 for BULK_LOGGED
    /// (probe-confirmed by exporting a database at each setting).
    /// </summary>
    [TestMethod]
    [DataRow(null, "FULL")]
    [DataRow("1", "SIMPLE")]
    [DataRow("2", "BULK_LOGGED")]
    public void BacpacImport_CarriesTheRecoveryModel(string? recoveryMode, string expected)
    {
        var builder = BacpacBuilder.Create().Table("dbo", "T", t => t.Column("Id", "int").Row(1));
        if (recoveryMode is not null)
            builder = builder.DatabaseOption("RecoveryMode", recoveryMode);
        using var bacpac = builder.Build();

        var simulation = new Simulation();
        simulation.ImportBacpac(bacpac, out _, new BacpacImportOptions { DatabaseName = "imported" });

        AreEqual(expected, simulation.ExecuteScalar(
            "select recovery_model_desc from sys.databases where name = 'imported'"));
    }

    /// <summary>
    /// The compatibility level comes from the model's own
    /// <c>CompatibilityMode</c> property when it carries one. The root
    /// <c>DspName</c> names the schema provider the model was written against,
    /// which is the exporting tool's version rather than the database's level —
    /// the two coincide often enough to look interchangeable, but a database
    /// set below its server's level carries the property and it has to win.
    /// </summary>
    [TestMethod]
    public void BacpacImport_CompatibilityModeBeatsTheProviderVersion()
    {
        // A Sql170-provider model whose database sits at 160 — the shape that
        // separates the two sources.
        using var bacpac = BacpacBuilder.Create()
            .CompatibilityLevel(170)
            .DatabaseOption("CompatibilityMode", "160")
            .Table("dbo", "T", t => t.Column("Id", "int").Row(1))
            .Build();

        var simulation = new Simulation();
        simulation.ImportBacpac(bacpac, out _, new BacpacImportOptions { DatabaseName = "imported" });

        AreEqual((byte)160, simulation.ExecuteScalar(
            "select compatibility_level from sys.databases where name = 'imported'"));
    }

    /// <summary>
    /// With no <c>CompatibilityMode</c> property the provider version is the
    /// only signal, and stays the fallback.
    /// </summary>
    [TestMethod]
    public void BacpacImport_WithoutCompatibilityMode_FallsBackToTheProviderVersion()
    {
        using var bacpac = BacpacBuilder.Create()
            .CompatibilityLevel(130)
            .Table("dbo", "T", t => t.Column("Id", "int").Row(1))
            .Build();

        var simulation = new Simulation();
        simulation.ImportBacpac(bacpac, out _, new BacpacImportOptions { DatabaseName = "imported" });

        AreEqual((byte)130, simulation.ExecuteScalar(
            "select compatibility_level from sys.databases where name = 'imported'"));
    }

    /// <summary>
    /// <c>IsAllowSnapshotIsolation</c> is load-bearing: without it a SNAPSHOT
    /// transaction that runs on the source database raises Msg 3952 here.
    /// </summary>
    [TestMethod]
    public void BacpacImport_CarriesAllowSnapshotIsolation()
    {
        using var bacpac = BacpacBuilder.Create()
            .DatabaseOption("IsAllowSnapshotIsolation", "True")
            .Table("dbo", "T", t => t.Column("Id", "int").Row(1))
            .Build();

        var simulation = new Simulation();
        simulation.ImportBacpac(bacpac, out _, new BacpacImportOptions { DatabaseName = "imported" });

        AreEqual("ON", simulation.ExecuteScalar(
            "select snapshot_isolation_state_desc from sys.databases where name = 'imported'"));
    }
}
