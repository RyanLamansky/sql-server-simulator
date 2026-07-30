using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for DML AFTER triggers — <c>CREATE TRIGGER</c>,
/// <c>DROP TRIGGER</c>, <c>DISABLE / ENABLE TRIGGER</c>, <c>ALTER TRIGGER</c>,
/// <c>CREATE OR ALTER TRIGGER</c>, the <c>INSERTED</c> / <c>DELETED</c>
/// pseudo-tables, multi-action triggers, multiple triggers per table,
/// trigger-error rollback, recursion suppression, and
/// <c>TRIGGER_NESTLEVEL()</c>. INSTEAD OF triggers and DDL triggers are
/// out of scope for this bundle. All behaviors probe-confirmed against
/// SQL Server 2025.
/// </summary>
[TestClass]
public sealed class TriggerTests
{
    private static DbConnection Seeded()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t_target (id int primary key, v int);
            create table audit_log (action varchar(10), id int, oldv int, newv int);
            """).ExecuteNonQuery();
        return connection;
    }

    private static List<(string Action, int Id, int? OldV, int? NewV)> ReadAuditLog(DbConnection connection)
    {
        using var reader = connection.CreateCommand("select action, id, oldv, newv from audit_log order by id, action").ExecuteReader();
        var rows = new List<(string, int, int?, int?)>();
        while (reader.Read())
        {
            rows.Add((
                reader.GetString(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3)));
        }
        return rows;
    }

    // === Trigger-body result sets ===

    /// <summary>
    /// A body <c>SELECT</c> is the firing statement's result set on real, so
    /// it reaches the client rather than being drained at the call site.
    /// </summary>
    [TestMethod]
    public void BodySelect_SurfacesAsTheStatementsResultSet()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("create trigger tr on t_target after insert as begin select 'from-trigger' as src, id from inserted; end").ExecuteNonQuery();
        using var reader = connection.CreateCommand("insert t_target values (1, 10)").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("from-trigger", reader.GetString(0));
        AreEqual(1, reader.GetInt32(1));
        IsFalse(reader.Read());
    }

    /// <summary>Several body SELECTs arrive in body order.</summary>
    [TestMethod]
    public void SeveralBodySelects_ArriveInOrder()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("create trigger tr on t_target after insert as begin select 'one' as a; select 'two' as b; end").ExecuteNonQuery();
        using var reader = connection.CreateCommand("insert t_target values (1, 10)").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("one", reader.GetString(0));
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual("two", reader.GetString(0));
        IsFalse(reader.NextResult());
    }

    /// <summary>
    /// Every firing trigger contributes its result sets. Which one goes first
    /// is deliberately not asserted — SQL Server leaves multi-trigger order
    /// unspecified without <c>sp_settriggerorder</c>, which isn't modeled.
    /// </summary>
    [TestMethod]
    public void SeveralTriggers_EachContributeAResultSet()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("create trigger tr_a on t_target after insert as begin select 'first' as a; end").ExecuteNonQuery();
        _ = connection.CreateCommand("create trigger tr_b on t_target after insert as begin select 'second' as a; end").ExecuteNonQuery();
        using var reader = connection.CreateCommand("insert t_target values (1, 10)").ExecuteReader();
        var seen = new List<string>();
        do
        {
            while (reader.Read())
                seen.Add(reader.GetString(0));
        } while (reader.NextResult());
        seen.Sort(StringComparer.Ordinal);
        CollectionAssert.AreEqual(new[] { "first", "second" }, seen);
    }

    /// <summary>An INSTEAD OF body's SELECT surfaces the same way.</summary>
    [TestMethod]
    public void InsteadOfBodySelect_Surfaces()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("create trigger tr on t_target instead of insert as begin select 'instead' as a; end").ExecuteNonQuery();
        using var reader = connection.CreateCommand("insert t_target values (1, 10)").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("instead", reader.GetString(0));
    }

    /// <summary>
    /// The body's own DML contributes no result set and — importantly — no
    /// rows-affected: forwarding those would inflate the firing statement's
    /// reported total, which is what an ORM reads back.
    /// </summary>
    [TestMethod]
    public void BodyDml_ContributesNoResultSetAndNoRowCount()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("create trigger tr on t_target after insert as begin insert audit_log values ('ins', 1, null, null); end").ExecuteNonQuery();
        AreEqual(1, connection.CreateCommand("insert t_target values (1, 10)").ExecuteNonQuery());
        HasCount(1, ReadAuditLog(connection));
    }

    // === AFTER INSERT ===

    [TestMethod]
    public void AfterInsert_FiresAndSeesInserted()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("""
            create trigger tr_t on t_target after insert
            as
                insert audit_log(action, id, oldv, newv)
                select 'I', id, null, v from inserted
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("insert t_target values (1, 100), (2, 200)").ExecuteNonQuery();
        var log = ReadAuditLog(connection);
        HasCount(2, log);
        AssertRow(("I", 1, null, 100), log[0]);
        AssertRow(("I", 2, null, 200), log[1]);
    }

    private static void AssertRow((string Action, int Id, int? OldV, int? NewV) expected, (string Action, int Id, int? OldV, int? NewV) actual)
    {
        AreEqual(expected.Action, actual.Action);
        AreEqual(expected.Id, actual.Id);
        AreEqual(expected.OldV, actual.OldV);
        AreEqual(expected.NewV, actual.NewV);
    }

    [TestMethod]
    public void For_IsSynonymForAfter()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("""
            create trigger tr_t on t_target for insert
            as
                insert audit_log(action, id, oldv, newv)
                select 'F', id, null, v from inserted
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("insert t_target values (5, 50)").ExecuteNonQuery();
        var log = ReadAuditLog(connection);
        HasCount(1, log);
        AreEqual("F", log[0].Action);
    }

    // === AFTER UPDATE ===

    [TestMethod]
    public void AfterUpdate_SeesBothInsertedAndDeleted()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("insert t_target values (1, 10), (2, 20)").ExecuteNonQuery();
        _ = connection.CreateCommand("""
            create trigger tr_t on t_target after update
            as
                insert audit_log(action, id, oldv, newv)
                select 'U', i.id, d.v, i.v
                from inserted i join deleted d on i.id = d.id
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("update t_target set v = v + 1").ExecuteNonQuery();
        var log = ReadAuditLog(connection);
        HasCount(2, log);
        AssertRow(("U", 1, 10, 11), log[0]);
        AssertRow(("U", 2, 20, 21), log[1]);
    }

    // === AFTER DELETE ===

    [TestMethod]
    public void AfterDelete_SeesDeleted()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("insert t_target values (1, 10), (2, 20)").ExecuteNonQuery();
        _ = connection.CreateCommand("""
            create trigger tr_t on t_target after delete
            as
                insert audit_log(action, id, oldv, newv)
                select 'D', id, v, null from deleted
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("delete from t_target where id = 1").ExecuteNonQuery();
        var log = ReadAuditLog(connection);
        HasCount(1, log);
        AssertRow(("D", 1, 10, null), log[0]);
    }

    // === Multi-action trigger ===

    [TestMethod]
    public void MultiAction_TriggerHandlesInsertAndUpdate()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("""
            create trigger tr_t on t_target after insert, update
            as
                insert audit_log(action, id, oldv, newv)
                select case when d.id is null then 'I' else 'U' end, i.id, d.v, i.v
                from inserted i left join deleted d on i.id = d.id
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("insert t_target values (1, 10); update t_target set v = 99 where id = 1").ExecuteNonQuery();
        var log = ReadAuditLog(connection);
        HasCount(2, log);
        AssertRow(("I", 1, null, 10), log[0]);
        AssertRow(("U", 1, 10, 99), log[1]);
    }

    // === Multiple triggers, all fire ===

    [TestMethod]
    public void MultipleTriggers_AllFire()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("""
            create trigger tr_a on t_target after insert
            as insert audit_log values('A', 0, null, null);
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("""
            create trigger tr_b on t_target after insert
            as insert audit_log values('B', 0, null, null);
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("insert t_target values (1, 10)").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select action from audit_log order by action").ExecuteReader();
        var actions = new List<string>();
        while (reader.Read())
            actions.Add(reader.GetString(0));
        CollectionAssert.AreEqual(new[] { "A", "B" }, actions);
    }

    // === Trigger-error rollback ===

    [TestMethod]
    public void TriggerThrow_RollsBackDml()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("""
            create trigger tr_t on t_target after insert
            as
            begin
                if exists (select 1 from inserted where v < 0)
                    throw 50001, 'negative not allowed', 1;
            end
            """).ExecuteNonQuery();
        var ex = Throws<DbException>(() =>
            _ = connection.CreateCommand("insert t_target values (5, -1)").ExecuteNonQuery());
        AreEqual("50001", ex.Data["HelpLink.EvtID"]);
        // Row must NOT exist — trigger throw rolls back the DML.
        using var reader = connection.CreateCommand("select count(*) from t_target where id = 5").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(0, reader.GetInt32(0));
    }

    // === DISABLE / ENABLE TRIGGER ===

    [TestMethod]
    public void DisableTrigger_SuppressesFiring()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("""
            create trigger tr_t on t_target after insert
            as insert audit_log values('I', 0, null, null);
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("disable trigger tr_t on t_target").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t_target values (1, 10)").ExecuteNonQuery();
        var log = ReadAuditLog(connection);
        IsEmpty(log);

        _ = connection.CreateCommand("enable trigger tr_t on t_target").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t_target values (2, 20)").ExecuteNonQuery();
        log = ReadAuditLog(connection);
        HasCount(1, log);
    }

    [TestMethod]
    public void DisableTriggerAll_SuppressesEveryTriggerOnTable()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("""
            create trigger tr_a on t_target after insert as insert audit_log values('A', 0, null, null);
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("""
            create trigger tr_b on t_target after insert as insert audit_log values('B', 0, null, null);
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("disable trigger all on t_target").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t_target values (1, 10)").ExecuteNonQuery();
        IsEmpty(ReadAuditLog(connection));
    }

    // === DROP TRIGGER ===

    [TestMethod]
    public void DropTrigger_RemovesIt()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("""
            create trigger tr_t on t_target after insert
            as insert audit_log values('I', 0, null, null);
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("drop trigger tr_t").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t_target values (1, 10)").ExecuteNonQuery();
        IsEmpty(ReadAuditLog(connection));
    }

    [TestMethod]
    public void DropTrigger_Missing_Raises3701()
    {
        using var connection = Seeded();
        var ex = Throws<DbException>(() =>
            _ = connection.CreateCommand("drop trigger tr_nonexistent").ExecuteNonQuery());
        AreEqual("3701", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void DropTriggerIfExists_Missing_Silent()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("drop trigger if exists tr_nonexistent").ExecuteNonQuery();
    }

    // === CREATE TRIGGER errors ===

    [TestMethod]
    public void CreateTrigger_OnMissingTable_Raises8197()
    {
        using var connection = Seeded();
        var ex = Throws<DbException>(() =>
            _ = connection.CreateCommand(
                "create trigger tr_missing on no_such_table after insert as select 1").ExecuteNonQuery());
        AreEqual("8197", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void CreateTrigger_DuplicateName_Raises2714()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("create trigger tr_t on t_target after insert as select 1").ExecuteNonQuery();
        var ex = Throws<DbException>(() =>
            _ = connection.CreateCommand("create trigger tr_t on t_target after insert as select 1").ExecuteNonQuery());
        AreEqual("2714", ex.Data["HelpLink.EvtID"]);
    }

    // === ALTER TRIGGER ===

    [TestMethod]
    public void AlterTrigger_ReplacesBody()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("""
            create trigger tr_t on t_target after insert
            as insert audit_log values('OLD', 0, null, null);
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("""
            alter trigger tr_t on t_target after insert
            as insert audit_log values('NEW', 0, null, null);
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("insert t_target values (1, 10)").ExecuteNonQuery();
        var log = ReadAuditLog(connection);
        HasCount(1, log);
        AreEqual("NEW", log[0].Action);
    }

    [TestMethod]
    public void CreateOrAlterTrigger_UpsertsBody()
    {
        using var connection = Seeded();
        // First time: creates.
        _ = connection.CreateCommand("""
            create or alter trigger tr_t on t_target after insert
            as insert audit_log values('V1', 0, null, null);
            """).ExecuteNonQuery();
        // Second time: replaces.
        _ = connection.CreateCommand("""
            create or alter trigger tr_t on t_target after insert
            as insert audit_log values('V2', 0, null, null);
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("insert t_target values (1, 10)").ExecuteNonQuery();
        var log = ReadAuditLog(connection);
        HasCount(1, log);
        AreEqual("V2", log[0].Action);
    }

    // === Recursion guard ===

    [TestMethod]
    public void DirectRecursion_Suppressed()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("insert t_target values (1, 10)").ExecuteNonQuery();
        // Trigger updates self → without recursion-guard would infinite-loop.
        _ = connection.CreateCommand("""
            create trigger tr_t on t_target after update
            as
                update t_target set v = v + 100 where id = (select top 1 id from inserted)
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("update t_target set v = v + 1 where id = 1").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select v from t_target where id = 1").ExecuteReader();
        IsTrue(reader.Read());
        // Initial 10 → updated to 11 → trigger fires once, adds 100 → 111.
        // Trigger's own update does NOT re-fire the trigger.
        AreEqual(111, reader.GetInt32(0));
    }

    // === TRIGGER_NESTLEVEL() ===

    [TestMethod]
    public void TriggerNestLevel_IsOneAtTopLevelTrigger()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("""
            create trigger tr_t on t_target after insert
            as
                insert audit_log(action, id, oldv, newv)
                values ('N', trigger_nestlevel(), null, null)
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("insert t_target values (1, 10)").ExecuteNonQuery();
        var log = ReadAuditLog(connection);
        HasCount(1, log);
        AssertRow(("N", 1, null, null), log[0]);
    }

    [TestMethod]
    public void TriggerNestLevel_IsZeroOutsideTrigger()
    {
        using var connection = Seeded();
        using var reader = connection.CreateCommand("select trigger_nestlevel()").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(0, reader.GetInt32(0));
    }

    // === Inserted/Deleted resolution outside trigger ===

    [TestMethod]
    public void Inserted_OutsideTrigger_Raises208()
    {
        using var connection = Seeded();
        var ex = Throws<DbException>(() =>
            _ = connection.CreateCommand("select * from inserted").ExecuteScalar());
        AreEqual("208", ex.Data["HelpLink.EvtID"]);
    }

    // === MERGE INSERT branch fires AFTER INSERT trigger ===

    [TestMethod]
    public void Merge_InsertBranch_FiresAfterInsertTrigger()
    {
        // EF Core 10's batched-SaveChanges emit shape is MERGE…USING (VALUES …)
        // ON 1=0 WHEN NOT MATCHED THEN INSERT … OUTPUT INSERTED. Verify the
        // trigger dispatch wired into Simulation.Merge.cs fires under that
        // shape (the regular-INSERT path is covered separately).
        using var connection = Seeded();
        _ = connection.CreateCommand("""
            create trigger tr_t on t_target after insert
            as
                insert audit_log(action, id, oldv, newv)
                select 'M', id, null, v from inserted
            """).ExecuteNonQuery();
        using (var reader = connection.CreateCommand("""
            merge t_target using (values (1, 100), (2, 200)) as src (id, v) on 1 = 0
            when not matched then insert (id, v) values (src.id, src.v)
            output inserted.id;
            """).ExecuteReader())
        {
            var inserted = new List<int>();
            while (reader.Read())
                inserted.Add(reader.GetInt32(0));
            HasCount(2, inserted);
        }

        var log = ReadAuditLog(connection);
        HasCount(2, log);
        AssertRow(("M", 1, null, 100), log[0]);
        AssertRow(("M", 2, null, 200), log[1]);
    }

    // === sys.triggers / sys.objects integration ===

    [TestMethod]
    public void SysTriggers_ListsTrigger()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("create trigger tr_t on t_target after insert as select 1").ExecuteNonQuery();
        using var reader = connection.CreateCommand(
            "select name, type, type_desc, is_disabled, is_instead_of_trigger from sys.triggers where name = 'tr_t'").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("tr_t", reader.GetString(0));
        AreEqual("TR", reader.GetString(1));
        AreEqual("SQL_TRIGGER", reader.GetString(2));
        IsFalse(reader.GetBoolean(3));
        IsFalse(reader.GetBoolean(4));
    }

    [TestMethod]
    public void SysObjects_TriggerType_IsTR()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("create trigger tr_t on t_target after insert as select 1").ExecuteNonQuery();
        using var reader = connection.CreateCommand(
            "select type, type_desc from sys.objects where name = 'tr_t'").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("TR", reader.GetString(0));
        AreEqual("SQL_TRIGGER", reader.GetString(1));
    }

    [TestMethod]
    public void SysTriggers_Disabled_ReportsTrue()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("create trigger tr_t on t_target after insert as select 1").ExecuteNonQuery();
        _ = connection.CreateCommand("disable trigger tr_t on t_target").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select is_disabled from sys.triggers where name = 'tr_t'").ExecuteReader();
        IsTrue(reader.Read());
        IsTrue(reader.GetBoolean(0));
    }

    // === @@ROWCOUNT inside trigger ===

    [TestMethod]
    public void Rowcount_InsideTrigger_ReflectsFiringDml()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("""
            create trigger tr_t on t_target after insert
            as
                insert audit_log(action, id, oldv, newv) values ('R', @@rowcount, null, null)
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("insert t_target values (1, 10), (2, 20)").ExecuteNonQuery();
        var log = ReadAuditLog(connection);
        HasCount(1, log);
        AreEqual(2, log[0].Id);  // @@ROWCOUNT was 2 (matched the INSERT's count).
    }
}
