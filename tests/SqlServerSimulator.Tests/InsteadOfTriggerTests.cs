using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for DML INSTEAD OF triggers — the trigger body
/// replaces the firing DML's heap-write phase. INSTEAD OF attaches to
/// heap tables (any of INSERT / UPDATE / DELETE) and to views (the
/// primary real-world use case — makes a non-updatable view writable).
/// At most one INSTEAD OF trigger per action per target (Msg 2111
/// probe-confirmed). AFTER triggers don't fire when an INSTEAD OF
/// replaces the DML on the same action. All behaviors probe-confirmed
/// against SQL Server 2025 (2026-05-13).
/// </summary>
[TestClass]
public sealed class InsteadOfTriggerTests
{
    private static DbConnection Seeded()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (id int identity(1,1) primary key, v int);
            create table audit_log (action varchar(10), seen_id int null, seen_v int null);
            """).ExecuteNonQuery();
        return connection;
    }

    private static List<(string Action, int? SeenId, int? SeenV)> ReadAuditLog(DbConnection connection)
    {
        using var reader = connection.CreateCommand("select action, seen_id, seen_v from audit_log").ExecuteReader();
        var rows = new List<(string, int?, int?)>();
        while (reader.Read())
        {
            rows.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2)));
        }
        rows.Sort((a, b) => (a.Item2 ?? -1).CompareTo(b.Item2 ?? -1));
        return rows;
    }

    private static int CountRows(DbConnection connection, string table) =>
        (int)connection.CreateCommand($"select count(*) from {table}").ExecuteScalar()!;

    // === INSTEAD OF INSERT on a table ===

    [TestMethod]
    public void InsteadOfInsert_SkipsHeapWrite_FiresTrigger()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("""
            create trigger tr_t on t instead of insert
            as
                insert audit_log(action, seen_id, seen_v) select 'I', id, v from inserted;
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("insert t (v) values (100), (200)").ExecuteNonQuery();

        // Heap not written — INSTEAD OF replaced the DML.
        AreEqual(0, CountRows(connection, "t"));

        // Trigger fired; INSERTED's identity column shows the typed
        // default (0 for int) because identity isn't allocated for
        // INSTEAD OF INSERT (probe-confirmed).
        var log = ReadAuditLog(connection);
        HasCount(2, log);
        AreEqual(0, log[0].SeenId);
        AreEqual(100, log[0].SeenV);
        AreEqual(0, log[1].SeenId);
        AreEqual(200, log[1].SeenV);
    }

    [TestMethod]
    public void InsteadOfInsert_IdentityCounterDoesNotAdvance()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("""
            create trigger tr_t on t instead of insert as select 1;
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("insert t (v) values (1), (2), (3)").ExecuteNonQuery();

        // Drop the trigger; a regular INSERT now allocates from seed 1
        // (identity wasn't burned by the prior INSTEAD OF INSERTs).
        _ = connection.CreateCommand("drop trigger tr_t; insert t (v) values (999)").ExecuteNonQuery();
        var firstId = (int)connection.CreateCommand("select max(id) from t").ExecuteScalar()!;
        AreEqual(1, firstId);
    }

    [TestMethod]
    public void InsteadOfInsert_DefaultsAndComputedRunForInserted()
    {
        // INSERTED carries DEFAULT-clause results and computed-column
        // values; only identity is skipped (probe-confirmed). Schema
        // has a default on v and a computed c = v * 2; the INSERT
        // supplies only id, so v picks up its DEFAULT (99) and c
        // computes as 198.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (id int primary key, v int default 99, c as v * 2);
            create table capture (cap_v int, cap_c int);
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("""
            create trigger tr_t on t instead of insert
            as
                insert capture(cap_v, cap_c) select v, c from inserted
            """).ExecuteNonQuery();

        _ = connection.CreateCommand("insert t (id) values (1)").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select cap_v, cap_c from capture").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(99, reader.GetInt32(0));     // DEFAULT ran
        AreEqual(198, reader.GetInt32(1));    // computed evaluated
    }

    [TestMethod]
    public void InsteadOfInsert_AfterInsertOnSameAction_DoesNotFire()
    {
        // INSTEAD OF replaces the DML; AFTER on the same action is bypassed.
        using var connection = Seeded();
        _ = connection.CreateCommand("""
            create trigger tr_t_io on t instead of insert
            as insert audit_log(action) values ('IO')
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("""
            create trigger tr_t_after on t after insert
            as insert audit_log(action) values ('AFTER')
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("insert t (v) values (1)").ExecuteNonQuery();

        var log = ReadAuditLog(connection);
        HasCount(1, log);
        AreEqual("IO", log[0].Action);
    }

    [TestMethod]
    public void InsteadOfInsert_BodyCanInsertIntoSameTarget_NoRecursion()
    {
        // Direct-recursion guard: the body's own INSERT against the same
        // target doesn't re-fire the INSTEAD OF trigger; the nested
        // INSERT reaches the heap.
        using var connection = Seeded();
        _ = connection.CreateCommand("""
            create trigger tr_t on t instead of insert
            as
                insert audit_log(action, seen_v) select 'I', v from inserted;
                insert t (v) select v + 1000 from inserted;
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("insert t (v) values (42)").ExecuteNonQuery();

        var heap = new List<int>();
        using var reader = connection.CreateCommand("select v from t").ExecuteReader();
        while (reader.Read()) heap.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1042 }, heap);

        // Only one audit row — the nested INSERT didn't re-fire INSTEAD OF.
        var log = ReadAuditLog(connection);
        HasCount(1, log);
        AreEqual(42, log[0].SeenV);
    }

    [TestMethod]
    public void InsteadOfInsert_BodyThrow_PropagatesError()
    {
        // A throw from inside the INSTEAD OF body surfaces to the
        // caller. Note: per the documented multi-statement-body atomicity
        // gap (see docs/claude/triggers.md), prior body statements'
        // writes don't roll back when a later body statement throws —
        // so this test asserts the throw propagates but doesn't rely on
        // the rolled-back audit_log invariant.
        using var connection = Seeded();
        _ = connection.CreateCommand("""
            create trigger tr_t on t instead of insert
            as
                throw 50000, 'bail', 1
            """).ExecuteNonQuery();
        _ = Throws<SimulatedSqlException>(() =>
            _ = connection.CreateCommand("insert t (v) values (1)").ExecuteNonQuery());

        // Heap untouched (it would have been anyway with INSTEAD OF).
        AreEqual(0, CountRows(connection, "t"));
    }

    // === INSTEAD OF UPDATE on a table ===

    [TestMethod]
    public void InsteadOfUpdate_SkipsHeapWrite_FiresTrigger()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("insert t (v) values (10), (20), (30)").ExecuteNonQuery();
        _ = connection.CreateCommand("""
            create trigger tr_t on t instead of update
            as
                insert audit_log(action, seen_id, seen_v)
                select 'U_NEW', i.id, i.v from inserted i;
                insert audit_log(action, seen_id, seen_v)
                select 'U_OLD', d.id, d.v from deleted d;
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("update t set v = v + 1000").ExecuteNonQuery();

        // Heap untouched.
        var heap = new List<(int, int)>();
        using var reader = connection.CreateCommand("select id, v from t order by id").ExecuteReader();
        while (reader.Read()) heap.Add((reader.GetInt32(0), reader.GetInt32(1)));
        CollectionAssert.AreEqual(new[] { (1, 10), (2, 20), (3, 30) }, heap);

        // INSERTED has new (v + 1000), DELETED has old (v).
        var log = new List<(string Action, int? Id, int? V)>();
        using var r2 = connection.CreateCommand("select action, seen_id, seen_v from audit_log order by seen_id, action").ExecuteReader();
        while (r2.Read())
            log.Add((r2.GetString(0), r2.IsDBNull(1) ? null : r2.GetInt32(1), r2.IsDBNull(2) ? null : r2.GetInt32(2)));
        HasCount(6, log);
        Assert.Contains(("U_NEW", 1, 1010), log);
        Assert.Contains(("U_OLD", 1, 10), log);
        Assert.Contains(("U_NEW", 2, 1020), log);
        Assert.Contains(("U_OLD", 2, 20), log);
    }

    // === INSTEAD OF DELETE on a table ===

    [TestMethod]
    public void InsteadOfDelete_SkipsHeapWrite_FiresTrigger()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("insert t (v) values (10), (20), (30)").ExecuteNonQuery();
        _ = connection.CreateCommand("""
            create trigger tr_t on t instead of delete
            as
                insert audit_log(action, seen_id, seen_v)
                select 'D', id, v from deleted;
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("delete from t where v >= 20").ExecuteNonQuery();

        // Heap untouched.
        AreEqual(3, CountRows(connection, "t"));

        var log = ReadAuditLog(connection);
        HasCount(2, log);
        AreEqual(2, log[0].SeenId);
        AreEqual(20, log[0].SeenV);
        AreEqual(3, log[1].SeenId);
        AreEqual(30, log[1].SeenV);
    }

    // === Max-one-per-action enforcement (Msg 2111) ===

    [TestMethod]
    public void SecondInsteadOfInsertOnTable_Raises2111()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("create trigger tr_t1 on t instead of insert as select 1").ExecuteNonQuery();
        var ex = Throws<SimulatedSqlException>(() =>
            _ = connection.CreateCommand("create trigger tr_t2 on t instead of insert as select 1").ExecuteNonQuery());
        AreEqual(2111, ex.Number);
        Assert.Contains("on table 't'", ex.Message);
        Assert.Contains("INSTEAD OF INSERT", ex.Message);
    }

    [TestMethod]
    public void SecondInsteadOfWithOverlappingAction_Raises2111()
    {
        // Per-action overlap: a (INSERT, UPDATE) trigger followed by an
        // INSERT-only trigger collides on INSERT.
        using var connection = Seeded();
        _ = connection.CreateCommand("create trigger tr_t1 on t instead of insert, update as select 1").ExecuteNonQuery();
        var ex = Throws<SimulatedSqlException>(() =>
            _ = connection.CreateCommand("create trigger tr_t2 on t instead of insert as select 1").ExecuteNonQuery());
        AreEqual(2111, ex.Number);
    }

    [TestMethod]
    public void InsteadOfDifferentActions_BothCoexist()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("create trigger tr_t_io_ins on t instead of insert as select 1").ExecuteNonQuery();
        _ = connection.CreateCommand("create trigger tr_t_io_del on t instead of delete as select 1").ExecuteNonQuery();
        // Both succeed — different actions, no overlap.
    }

    [TestMethod]
    public void AfterTriggerOnView_Raises8197()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("create view v_t as select id, v from t").ExecuteNonQuery();
        var ex = Throws<SimulatedSqlException>(() =>
            _ = connection.CreateCommand("create trigger tr_v on v_t after insert as select 1").ExecuteNonQuery());
        AreEqual(8197, ex.Number);
    }

    // === INSTEAD OF on a view ===

    [TestMethod]
    public void InsteadOfInsertOnView_FiresTrigger()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("create view v_t as select id, v from t").ExecuteNonQuery();
        _ = connection.CreateCommand("""
            create trigger tr_v on v_t instead of insert
            as
                insert audit_log(action, seen_id, seen_v)
                select 'V_INS', id, v from inserted;
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("insert v_t (id, v) values (99, 99)").ExecuteNonQuery();

        // Heap untouched; trigger fired with user-supplied values.
        AreEqual(0, CountRows(connection, "t"));
        var log = ReadAuditLog(connection);
        HasCount(1, log);
        AreEqual(99, log[0].SeenId);
        AreEqual(99, log[0].SeenV);
    }

    [TestMethod]
    public void InsteadOfInsertOnNonUpdatableView_Works()
    {
        // Join views are non-updatable without INSTEAD OF; with INSTEAD OF
        // INSERT they accept INSERT statements (the trigger body is
        // responsible for figuring out what to write to the bases).
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t1 (id int primary key, v int);
            create table t2 (id int primary key, label nvarchar(10));
            create table capture (v int, label nvarchar(10));
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("create view v_join as select t1.id, t1.v, t2.label from t1 join t2 on t1.id = t2.id").ExecuteNonQuery();
        _ = connection.CreateCommand("""
            create trigger tr_vj on v_join instead of insert
            as insert capture(v, label) select v, label from inserted
            """).ExecuteNonQuery();

        _ = connection.CreateCommand("insert v_join (id, v, label) values (1, 100, 'one')").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select v, label from capture").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(100, reader.GetInt32(0));
        AreEqual("one", reader.GetString(1));
    }

    [TestMethod]
    public void InsteadOfInsertOnView_SecondInsteadOfRaises2111()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("create view v_t as select id, v from t").ExecuteNonQuery();
        _ = connection.CreateCommand("create trigger tr_v1 on v_t instead of insert as select 1").ExecuteNonQuery();
        var ex = Throws<SimulatedSqlException>(() =>
            _ = connection.CreateCommand("create trigger tr_v2 on v_t instead of insert as select 1").ExecuteNonQuery());
        AreEqual(2111, ex.Number);
        Assert.Contains("on view 'v_t'", ex.Message);
    }

    [TestMethod]
    public void InsteadOfUpdateOnJoinView_SpanningBothTables_RoutesToTheTrigger()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t1 (id int primary key, v int);
            create table t2 (id int primary key, label nvarchar(10));
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("create view v_join as select t1.id, t1.v, t2.label from t1 join t2 on t1.id = t2.id").ExecuteNonQuery();
        _ = connection.CreateCommand("create trigger tr_vj on v_join instead of update as select i.v, i.label, d.v from inserted i join deleted d on d.id = i.id").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t1 values (1, 10); insert t2 values (1, 'a')").ExecuteNonQuery();
        using var reader = connection.CreateCommand("update v_join set v = 99, label = 'b'").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(99, reader.GetInt32(0));
        AreEqual("b", reader.GetString(1));
        AreEqual(10, reader.GetInt32(2));
    }

    // === MERGE routing through INSTEAD OF ===

    [TestMethod]
    public void Merge_NotMatchedInsert_RoutesThroughInsteadOf()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("""
            create trigger tr_t on t instead of insert
            as
                insert audit_log(action, seen_id, seen_v)
                select 'M_INS', id, v from inserted;
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("""
            merge t using (values (1, 100), (2, 200)) as s(id, v) on 1 = 0
            when not matched then insert (v) values (s.v);
            """).ExecuteNonQuery();

        AreEqual(0, CountRows(connection, "t"));
        var log = ReadAuditLog(connection);
        HasCount(2, log);
        AreEqual(100, log[0].SeenV);
        AreEqual(200, log[1].SeenV);
    }

    [TestMethod]
    public void Merge_InsteadOfOnSomeActionsButNotAll_Msg5316()
    {
        // INSERT has INSTEAD OF, UPDATE doesn't: real refuses the MERGE while
        // compiling rather than routing each action its own way.
        using var connection = Seeded();
        _ = connection.CreateCommand("insert t (v) values (10)").ExecuteNonQuery();
        _ = connection.CreateCommand("create trigger tr_t_io on t instead of insert as insert audit_log(action) values ('IO_INS')").ExecuteNonQuery();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("""
            if 1 = 0
                merge t using (values (1, 999), (2, 888)) as s(id, v) on t.id = s.id
                when matched then update set v = s.v
                when not matched then insert (v) values (s.v);
            """).ExecuteNonQuery());
        AreEqual(5316, ex.Number);
        Assert.StartsWith("The target 't' of the MERGE statement has an INSTEAD OF trigger on some, but not all,", ex.Message);
        AreEqual(10, connection.CreateCommand("select v from t").ExecuteScalar());
    }

    [TestMethod]
    public void Merge_InsteadOfCoversOnlyActionsUsed_Runs()
        => AreEqual("ins", new Simulation().ExecuteBatchesScalar(
            "create table t (id int primary key, v int)",
            "create trigger tr on t instead of insert as select 'ins'",
            "merge t using (values (1, 5)) as s(id, v) on t.id = s.id when not matched then insert values (s.id, s.v);"));

    // === INSTEAD OF UPDATE / DELETE on views real can't write through ===

    [TestMethod]
    public void InsteadOfUpdateOnAggregateView_PseudoTablesAreTheViewsRows()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (g int, a int); insert t values (1, 1), (1, 2), (2, 5)",
            "create view v as select g, sum(a) as s, count(*) as c from t group by g",
            "create trigger tr on v instead of update as select d.s, i.s, i.c, @@rowcount from deleted d join inserted i on i.g = d.g");
        using var reader = sim.ExecuteReader("update v set s = s + 100 where g = 1");
        IsTrue(reader.Read());
        AreEqual(3, reader.GetInt32(0));
        AreEqual(103, reader.GetInt32(1));
        AreEqual(2, reader.GetInt32(2));
        AreEqual(1, reader.GetInt32(3));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void InsteadOfUpdateOnAView_FiresWhenNoRowQualifies()
        => AreEqual(0, new Simulation().ExecuteBatchesScalar(
            "create table t (g int, a int); insert t values (1, 1)",
            "create view v as select g, sum(a) as s from t group by g",
            "create trigger tr on v instead of update as select count(*) from inserted",
            "update v set s = 0 where g = 99"));

    [TestMethod]
    public void InsteadOfUpdateOnADistinctView_UpdatesEachDistinctRow()
        => AreEqual(30, new Simulation().ExecuteBatchesScalar(
            "create table t (a int); insert t values (1), (1), (2)",
            "create view v as select distinct a from t",
            "create trigger tr on v instead of update as select sum(a) from inserted",
            "update v set a = a * 10"));

    [TestMethod]
    public void InsteadOfUpdate_VariableAssignmentInTheSetList()
        => AreEqual(8, new Simulation().ExecuteBatchesScalar(
            "create table t (g int, a int); insert t values (1, 1)",
            "create view v as select g, sum(a) as s from t group by g",
            "create trigger tr on v instead of update as declare @unused int",
            "declare @x int = 7; update v set @x = s = @x + 1; select @x"));

    [TestMethod]
    public void InsteadOfUpdate_UpdateFunctionReadsTheViewsColumns()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int primary key, a int); insert t values (1, 3)",
            "create view v as select id, a, a * 2 as dbl from t",
            "create trigger tr on v instead of update as select case when update(dbl) then 1 else 0 end, case when update(a) then 1 else 0 end, columns_updated()");
        using var reader = sim.ExecuteReader("update v set dbl = 100");
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual(0, reader.GetInt32(1));
        CollectionAssert.AreEqual(new byte[] { 4 }, (byte[])reader.GetValue(2));
    }

    [TestMethod]
    public void InsteadOfUpdate_WhereAndSetMayNameADerivedColumn()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int primary key, a int); insert t values (1, 3), (2, 4)",
            "create view v as select id, a, a * 2 as dbl from t",
            "create trigger tr on v instead of update as select id, a, dbl from inserted");
        using var reader = sim.ExecuteReader("update v set dbl = 100 where dbl = 8");
        IsTrue(reader.Read());
        AreEqual(2, reader.GetInt32(0));
        AreEqual(4, reader.GetInt32(1));
        AreEqual(100, reader.GetInt32(2));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void InsteadOfUpdate_SetLeavesADerivedColumnAtItsOldValue()
        => AreEqual(6, new Simulation().ExecuteBatchesScalar(
            "create table t (id int primary key, a int); insert t values (1, 3)",
            "create view v as select id, a, a * 2 as dbl from t",
            "create trigger tr on v instead of update as select dbl from inserted",
            "update v set a = 5"));

    [TestMethod]
    public void InsteadOfUpdate_JoinedFrom_Msg414()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (g int, a int); create table k (g int)",
            "create view v as select g, sum(a) as s from t group by g",
            "create trigger tr on v instead of update as select 1");
        sim.AssertSqlError("update v set s = 9 from v join k on v.g = k.g", 414,
            "UPDATE is not allowed because the statement updates view \"v\" which participates in a join and has an INSTEAD OF UPDATE trigger.");
    }

    [TestMethod]
    public void InsteadOfUpdate_UnknownSetColumn_Msg207()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (g int, a int)",
            "create view v as select g, sum(a) as s from t group by g",
            "create trigger tr on v instead of update as select 1");
        _ = sim.AssertSqlError("update v set zz = 1", 207);
    }

    [TestMethod]
    public void InsteadOfDeleteOnAggregateView_DeletedIsTheViewsRows()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (g int, a int); insert t values (1, 1), (1, 2), (2, 5)",
            "create view v as select g, sum(a) as s from t group by g",
            "create trigger tr on v instead of delete as delete t from t join deleted d on t.g = d.g");
        _ = sim.ExecuteNonQuery("delete v where s > 2");
        AreEqual(0, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void InsteadOfDeleteOnAUnionView_OutputInto()
        => AreEqual(1, new Simulation().ExecuteBatchesScalar(
            "create table t (a int); create table u (a int); create table log (a int); insert t values (1); insert u values (2)",
            "create view v as select a from t union all select a from u",
            "create trigger tr on v instead of delete as declare @unused int",
            "delete v output deleted.a into log where a = 1; select a from log"));

    [TestMethod]
    public void InsteadOfDeleteOnAJoinView_DeletedCarriesBothTablesColumns()
        => AreEqual("one", new Simulation().ExecuteBatchesScalar(
            "create table a (id int primary key, n varchar(10)); create table b (id int primary key, aid int, q int); insert a values (1, 'one'); insert b values (10, 1, 5), (11, 1, 6)",
            "create view jv as select b.id, a.n, b.q from a join b on a.id = b.aid",
            "create trigger tr on jv instead of delete as select n from deleted where q = 6",
            "delete jv where q = 6"));

    // === DROP cascade ===

    [TestMethod]
    public void DropTable_CascadesTriggers()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("create trigger tr_t on t instead of insert as select 1").ExecuteNonQuery();
        _ = connection.CreateCommand("drop table t").ExecuteNonQuery();

        // Trigger removed from sys.triggers when its parent went away.
        var count = (int)connection.CreateCommand("select count(*) from sys.triggers where name = 'tr_t'").ExecuteScalar()!;
        AreEqual(0, count);
    }

    [TestMethod]
    public void DropView_CascadesTriggers()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("create view v_t as select id, v from t").ExecuteNonQuery();
        _ = connection.CreateCommand("create trigger tr_v on v_t instead of insert as select 1").ExecuteNonQuery();
        _ = connection.CreateCommand("drop view v_t").ExecuteNonQuery();

        var count = (int)connection.CreateCommand("select count(*) from sys.triggers where name = 'tr_v'").ExecuteScalar()!;
        AreEqual(0, count);
    }

    // === sys.triggers exposure ===

    [TestMethod]
    public void SysTriggers_IsInsteadOfTrigger_SetForInsteadOfOnly()
    {
        // Each CREATE TRIGGER goes in its own batch — the body parser
        // captures source to end-of-command, so multiple CREATE TRIGGERs
        // in one batch would nest inside the first trigger's body.
        using var connection = Seeded();
        _ = connection.CreateCommand("create view v_t as select id, v from t").ExecuteNonQuery();
        _ = connection.CreateCommand("create trigger tr_after on t after insert as select 1").ExecuteNonQuery();
        _ = connection.CreateCommand("create trigger tr_io_table on t instead of update as select 1").ExecuteNonQuery();
        _ = connection.CreateCommand("create trigger tr_io_view on v_t instead of insert as select 1").ExecuteNonQuery();

        var rows = new Dictionary<string, bool>();
        using var reader = connection.CreateCommand(
            "select name, is_instead_of_trigger from sys.triggers order by name").ExecuteReader();
        while (reader.Read()) rows[reader.GetString(0)] = reader.GetBoolean(1);

        IsFalse(rows["tr_after"]);
        IsTrue(rows["tr_io_table"]);
        IsTrue(rows["tr_io_view"]);
    }
}
