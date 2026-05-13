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
            as insert audit_log(action) values ('IO');
            create trigger tr_t_after on t after insert
            as insert audit_log(action) values ('AFTER');
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
        _ = Throws<DbException>(() =>
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
        var ex = Throws<DbException>(() =>
            _ = connection.CreateCommand("create trigger tr_t2 on t instead of insert as select 1").ExecuteNonQuery());
        AreEqual("2111", ex.Data["HelpLink.EvtID"]);
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
        var ex = Throws<DbException>(() =>
            _ = connection.CreateCommand("create trigger tr_t2 on t instead of insert as select 1").ExecuteNonQuery());
        AreEqual("2111", ex.Data["HelpLink.EvtID"]);
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
        var ex = Throws<DbException>(() =>
            _ = connection.CreateCommand("create trigger tr_v on v_t after insert as select 1").ExecuteNonQuery());
        AreEqual("8197", ex.Data["HelpLink.EvtID"]);
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
            create view v_join as select t1.id, t1.v, t2.label from t1 join t2 on t1.id = t2.id;
            create trigger tr_vj on v_join instead of insert
            as insert capture(v, label) select v, label from inserted;
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
        var ex = Throws<DbException>(() =>
            _ = connection.CreateCommand("create trigger tr_v2 on v_t instead of insert as select 1").ExecuteNonQuery());
        AreEqual("2111", ex.Data["HelpLink.EvtID"]);
        Assert.Contains("on view 'v_t'", ex.Message);
    }

    [TestMethod]
    public void InsteadOfUpdateOnNonUpdatableView_RaisesNotSupported()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t1 (id int primary key, v int);
            create table t2 (id int primary key, label nvarchar(10));
            create view v_join as select t1.id, t1.v, t2.label from t1 join t2 on t1.id = t2.id;
            create trigger tr_vj on v_join instead of update as select 1;
            insert t1 values (1, 10); insert t2 values (1, 'a');
            """).ExecuteNonQuery();
        _ = Throws<NotSupportedException>(() =>
            _ = connection.CreateCommand("update v_join set v = 99").ExecuteNonQuery());
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
    public void Merge_MixedInsteadOfAndAfter_BothRouteCorrectly()
    {
        // INSERT has INSTEAD OF; UPDATE has AFTER. Mixed MERGE routes
        // each action independently — INSERT through trigger, UPDATE
        // through heap + AFTER trigger.
        using var connection = Seeded();
        _ = connection.CreateCommand("insert t (v) values (10)").ExecuteNonQuery();
        _ = connection.CreateCommand("""
            create trigger tr_t_io on t instead of insert
            as insert audit_log(action) values ('IO_INS');
            create trigger tr_t_after on t after update
            as insert audit_log(action) values ('AFTER_UPD');
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("""
            merge t using (values (1, 999), (2, 888)) as s(id, v) on t.id = s.id
            when matched then update set v = s.v
            when not matched then insert (v) values (s.v);
            """).ExecuteNonQuery();

        // The (id=2) source row INSERTs → INSTEAD OF (no heap write).
        // The (id=1) source row matches → real UPDATE (heap written) → AFTER fires.
        var actions = new List<string>();
        using var reader = connection.CreateCommand("select action from audit_log").ExecuteReader();
        while (reader.Read()) actions.Add(reader.GetString(0));
        Assert.Contains("IO_INS", actions);
        Assert.Contains("AFTER_UPD", actions);

        // Heap reflects only the UPDATE.
        var ids = new List<int>();
        using var r2 = connection.CreateCommand("select id from t order by id").ExecuteReader();
        while (r2.Read()) ids.Add(r2.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1 }, ids);
        var newV = (int)connection.CreateCommand("select v from t").ExecuteScalar()!;
        AreEqual(999, newV);
    }

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
