using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A statement replacing a table's rows wholesale — an <c>ALTER TABLE</c>
/// rewriting them into a new heap, a <c>TRUNCATE</c>, either side of a
/// <c>SWITCH</c> — leaves no version chain describing the rows it replaced for
/// a later versioned read to resolve against the rows written after it, and
/// a rollback of the statement brings the old chains back with the old rows.
/// </summary>
[TestClass]
public sealed class VersionChainRewriteTests
{
    // Rows 1..10 under READ_COMMITTED_SNAPSHOT, half of them updated and one
    // deleted, so the table carries version chains for both shapes.
    private const string VersionedTable = """
        alter database simulated set allow_snapshot_isolation on;
        alter database simulated set read_committed_snapshot on;
        create table t (id int primary key, v int);
        insert t select value, 1 from generate_series(1, 10);
        update t set v = 2 where id % 2 = 0;
        delete t where id = 3;
        """;

    [TestMethod]
    [DataRow("alter table t add c int null")]
    [DataRow("alter table t add c int null; alter table t drop column c")]
    [DataRow("alter table t alter column v bigint")]
    public void VersionedRead_AfterARewritingAlter_ReadsEveryRow(string alter)
        => AreEqual("9:14", new Simulation().ExecuteScalar($"""
            {VersionedTable}
            {alter};
            select concat(count(*), ':', sum(v)) from t
            """));

    [TestMethod]
    public void VersionedRead_AfterTruncateAndRefill_ReadsTheNewRows()
        => AreEqual("10:50 10:50", new Simulation().ExecuteScalar($"""
            {VersionedTable}
            truncate table t;
            insert t select value, 5 from generate_series(1, 10);
            declare @rcsi varchar(10) = (select concat(count(*), ':', sum(v)) from t);
            set transaction isolation level snapshot;
            begin tran;
            declare @snapshot varchar(10) = (select concat(count(*), ':', sum(v)) from t);
            commit;
            select concat(@rcsi, ' ', @snapshot)
            """));

    [TestMethod]
    public void VersionedRead_AfterARolledBackTruncate_ReadsTheRestoredRows()
        => AreEqual("9:14", new Simulation().ExecuteScalar($"""
            {VersionedTable}
            begin tran;
            truncate table t;
            insert t values (1, 7);
            rollback;
            select concat(count(*), ':', sum(v)) from t
            """));

    [TestMethod]
    public void VersionedRead_AfterASwitch_ReadsBothSides()
        => AreEqual("0: 9:14 1:9", new Simulation().ExecuteScalar($"""
            {VersionedTable}
            create table d (id int primary key, v int);
            insert d values (50, 1);
            update d set v = 2;
            delete d;
            alter table t switch to d;
            declare @source varchar(10) = (select concat(count(*), ':', sum(v)) from t);
            declare @target varchar(10) = (select concat(count(*), ':', sum(v)) from d);
            insert t values (3, 9);
            select concat(@source, ' ', @target, ' ', (select concat(count(*), ':', sum(v)) from t))
            """));

    /// <summary>
    /// The emptiness a <c>SWITCH</c> requires of its target is of rows, not
    /// slots: a target whose rows were all deleted takes the switch (probed
    /// 2026-10-03 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void Switch_IntoATargetWhoseRowsWereDeleted_Succeeds()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table s (id int primary key, v int);
            create table d (id int primary key, v int);
            insert s values (1, 1), (2, 2);
            insert d values (5, 1);
            delete d;
            alter table s switch to d;
            select count(*) from d
            """));
}
