using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sys.dm_db_index_usage_stats</c> and
/// <c>sys.dm_db_index_operational_stats</c> count what statements do with each
/// index — a use of each kind once per statement execution, row-level work per
/// row — as SQL Server 2025 counts it (probed 2026-10-16).
/// </summary>
[TestClass]
public sealed class IndexUsageStatsTests
{
    private const string Schema = """
        create table t (id int not null constraint pk_t primary key, a int, b int, c varchar(10));
        create index ix_a on t (a);
        create index ix_b on t (b) include (c);
        """;

    private static string Usage(Simulation sim, string index) =>
        (string)sim.ExecuteScalar($"""
            select concat(u.user_seeks, '/', u.user_scans, '/', u.user_lookups, '/', u.user_updates)
            from sys.dm_db_index_usage_stats u join sys.indexes i on i.object_id = u.object_id and i.index_id = u.index_id
            where u.database_id = db_id() and i.name = '{index}'
            """)!;

    private static string Operations(Simulation sim, string index) =>
        (string)sim.ExecuteScalar($"""
            select concat(o.leaf_insert_count, '/', o.leaf_update_count, '/', o.leaf_ghost_count, '/', o.range_scan_count, '/', o.singleton_lookup_count)
            from sys.dm_db_index_operational_stats(db_id(), null, null, null) o join sys.indexes i on i.object_id = o.object_id and i.index_id = o.index_id
            where i.name = '{index}'
            """)!;

    [TestMethod]
    public void AnIndexIsListedFromItsFirstUse_ItsOperationsFromItsCreation()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Schema);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.dm_db_index_usage_stats where database_id = db_id()"));
        AreEqual(3, sim.ExecuteScalar("select count(*) from sys.dm_db_index_operational_stats(db_id(), object_id('t'), null, null)"));
        AreEqual("0/0/0/0/0", Operations(sim, "ix_a"));
    }

    [TestMethod]
    public void AnInsert_UpdatesEveryIndexOnce_AndInsertsALeafRowPerRow()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Schema + "insert t values (1, 10, 100, 'a'), (2, 20, 200, 'b'), (3, 30, 300, 'c'); insert t values (4, 40, 400, 'd')");
        AreEqual("0/0/0/2", Usage(sim, "pk_t"));
        AreEqual("0/0/0/2", Usage(sim, "ix_b"));
        AreEqual("4/0/0/0/0", Operations(sim, "ix_a"));
    }

    [TestMethod]
    public void AReadSeeksOrScans_TheIndexItReads()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Schema + "insert t values (1, 10, 100, 'a'), (2, 20, 200, 'b')");
        _ = sim.ExecuteNonQuery("select * from t");
        _ = sim.ExecuteNonQuery("select * from t where id = 2");
        _ = sim.ExecuteNonQuery("select id from t where a = 20");
        _ = sim.ExecuteNonQuery("select * from t where id between 1 and 2");
        AreEqual("2/1/0/1", Usage(sim, "pk_t"));
        AreEqual("2/0/0/2/1", Operations(sim, "pk_t"));
        AreEqual("1/0/0/1", Usage(sim, "ix_a"));
        AreEqual("2/0/0/1/0", Operations(sim, "ix_a"));
        IsFalse(sim.ExecuteScalar("select last_user_seek from sys.dm_db_index_usage_stats where database_id = db_id() and index_id = 1") is DBNull);
    }

    /// <summary>A positioned update seeks the row its cursor sits on (probed 2026-10-25 against SQL Server 2025).</summary>
    [TestMethod]
    public void APositionedUpdate_SeeksItsRow() =>
        AreEqual("2/3", new Simulation().ExecuteScalar("""
            create table p (id int not null constraint pk_p primary key, x int); insert p values (1, 1), (2, 2), (3, 3);
            declare @x int; declare c cursor local for select x from p; open c; fetch c into @x; fetch c into @x;
            update p set x = x + 100 where current of c; update p set x = x + 100 where current of c;
            close c; deallocate c;
            select concat(user_seeks, '/', user_updates) from sys.dm_db_index_usage_stats where database_id = db_id() and object_id = object_id('p')
            """));

    [TestMethod]
    public void EachStatementOfABatch_CountsItsUse_WhenThePlanCacheReplaysIt()
    {
        // The second run of the text replays its cached plans, each statement
        // counting its seek as the parsed run's did.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Schema + "insert t values (1, 10, 100, 'a'), (2, 20, 200, 'b')");
        for (var run = 1; run <= 3; run++)
        {
            _ = sim.ExecuteNonQuery("select * from t where id = 1; select * from t where id = 2");
            AreEqual($"{2 * run}/0/0/1", Usage(sim, "pk_t"));
        }
    }

    [TestMethod]
    public void AnUpdate_MaintainsTheIndexesHoldingWhatItSets()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Schema + "insert t values (1, 10, 100, 'a')");
        _ = sim.ExecuteNonQuery("update t set c = 'z' where id = 1");
        AreEqual("1/0/0/2", Usage(sim, "pk_t"));
        AreEqual("0/0/0/1", Usage(sim, "ix_a"));
        AreEqual("1/1/0/0/0", Operations(sim, "ix_b"));
        _ = sim.ExecuteNonQuery("update t set a = 11 where id = 1");
        AreEqual("0/0/0/2", Usage(sim, "ix_a"));
        AreEqual("2/0/1/0/0", Operations(sim, "ix_a"));
        AreEqual("1/2/0/0/2", Operations(sim, "pk_t"));
    }

    [TestMethod]
    public void AWriteMatchingNoRow_StillUpdatesItsIndexes()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Schema);
        _ = sim.ExecuteNonQuery("delete t where id = 9; update t set c = 'q' where id = 9");
        AreEqual("2/0/0/2", Usage(sim, "pk_t"));
        AreEqual("0/0/0/2", Usage(sim, "ix_b"));
        AreEqual("0/0/0/1", Usage(sim, "ix_a"));
    }

    [TestMethod]
    public void ADelete_GhostsEachRowsEntries_AHeapDeletesThem()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Schema + "insert t values (1, 10, 100, 'a'), (2, 20, 200, 'b'); create table h (x int); insert h values (1), (2)");
        _ = sim.ExecuteNonQuery("delete t; delete h where x = 1");
        AreEqual("2/0/2/0/0", Operations(sim, "ix_b"));
        AreEqual("0/1/0/2", (string)sim.ExecuteScalar("""
            select concat(user_seeks, '/', user_scans, '/', user_lookups, '/', user_updates) from sys.dm_db_index_usage_stats
            where database_id = db_id() and object_id = object_id('h')
            """)!);
        AreEqual(1L, sim.ExecuteScalar("select leaf_delete_count from sys.dm_db_index_operational_stats(db_id(), object_id('h'), 0, 1)"));
    }

    [TestMethod]
    public void AForeignKey_SeeksTheReferencedKeyOncePerStatement_AndScansAnUnindexedChild()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (pid int not null constraint pk_p primary key);
            create table ch (cid int not null constraint pk_ch primary key, pid int constraint fk_ch references p (pid));
            insert p values (1), (2);
            insert ch values (1, 1), (2, 1), (3, 2);
            """);
        AreEqual("1/0/0/1", Usage(sim, "pk_p"));
        AreEqual("2/0/0/0/3", Operations(sim, "pk_p"));
        _ = sim.AssertSqlError("delete p where pid = 2", 547);
        AreEqual("0/1/0/1", Usage(sim, "pk_ch"));
    }

    [TestMethod]
    public void AnUpdateLeavingAForeignKeysColumnsAlone_ChecksNothing()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table p (id int primary key);
            create table c (id int primary key, pid int constraint fk references p (id), v int);
            alter table c nocheck constraint fk;
            insert c values (1, 99, 0);
            alter table c with nocheck check constraint fk;
            update c set v = 1 where id = 1;
            select v from c
            """));

    [TestMethod]
    public void AnIndexBuild_ScansTheTableOnTheEnginesAccount()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key, a int); insert t values (1, 1); create index ix on t (a)");
        AreEqual(1L, sim.ExecuteScalar("select system_scans from sys.dm_db_index_usage_stats where database_id = db_id() and index_id = 1"));
        _ = sim.ExecuteNonQuery("select * from t where a = 1; drop index ix on t");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.dm_db_index_usage_stats where database_id = db_id()"));
    }

    [TestMethod]
    public void ANullDatabase_NamesEveryDatabase()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create database zz", "use zz; create table t (a int)");
        IsGreaterThan(0, sim.ExecuteScalar<int>("use master; select count(*) from sys.dm_db_index_operational_stats(null, null, null, null) where database_id = db_id('zz')"));
        IsGreaterThan(0, sim.ExecuteScalar<int>("use master; select count(*) from sys.dm_db_index_physical_stats(0, null, null, null, null) where database_id = db_id('zz')"));
    }

    [TestMethod]
    public void TheStatisticsFunctions_DeclareRealsKeyColumnsNotNull()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key); insert t values (1)");
        using var reader = sim.ExecuteReader("select h.step_number, h.range_high_key, o.leaf_insert_count from sys.dm_db_stats_histogram(object_id('t'), 1) h cross join sys.dm_db_index_operational_stats(db_id(), object_id('t'), 1, 1) o");
        var schema = reader.GetColumnSchema();
        IsFalse(schema[0].AllowDBNull);
        IsTrue(schema[1].AllowDBNull);
        IsFalse(schema[2].AllowDBNull);
    }
}
