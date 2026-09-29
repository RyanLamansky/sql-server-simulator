using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// DML remembers which triggers a table or view carries between statements, so
/// every change to that set must reach the next statement: each test runs a
/// write first (so the answer is remembered), changes the trigger set, and
/// checks the next write — the same statement text, which also replays a
/// cached DML plan — fires exactly the triggers that now exist.
/// </summary>
[TestClass]
public sealed class TriggerLookupInvalidationTests
{
    private static DbConnection Seeded(string extra = "")
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (id int identity primary key, v int); create table fired (n int)").ExecuteNonQuery();
        if (extra.Length != 0)
            _ = connection.CreateCommand(extra).ExecuteNonQuery();
        return connection;
    }

    private const string CreateAfterInsert = "create trigger tr_t on t after insert as insert fired values (1)";

    /// <summary>Runs <paramref name="insert"/> and returns how many trigger rows it added.</summary>
    private static int FiredBy(DbConnection connection, string insert = "insert t (v) values (1)")
    {
        var before = (int)connection.CreateCommand("select count(*) from fired").ExecuteScalar()!;
        _ = connection.CreateCommand(insert).ExecuteNonQuery();
        return (int)connection.CreateCommand("select count(*) from fired").ExecuteScalar()! - before;
    }

    [TestMethod]
    public void CreateTrigger_FiresOnTheNextWrite()
    {
        using var connection = Seeded();
        AreEqual(0, FiredBy(connection));
        _ = connection.CreateCommand(CreateAfterInsert).ExecuteNonQuery();
        AreEqual(1, FiredBy(connection));
    }

    [TestMethod]
    public void DropTrigger_StopsFiringOnTheNextWrite()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand(CreateAfterInsert).ExecuteNonQuery();
        AreEqual(1, FiredBy(connection));
        _ = connection.CreateCommand("drop trigger tr_t").ExecuteNonQuery();
        AreEqual(0, FiredBy(connection));
    }

    [TestMethod]
    public void RolledBackCreateTrigger_StopsFiring()
    {
        using var connection = Seeded();
        AreEqual(0, FiredBy(connection));
        _ = connection.CreateCommand("begin tran").ExecuteNonQuery();
        _ = connection.CreateCommand(CreateAfterInsert).ExecuteNonQuery();
        AreEqual(1, FiredBy(connection));
        _ = connection.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual(0, FiredBy(connection));
    }

    [TestMethod]
    public void RolledBackDropTrigger_FiresAgain()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand(CreateAfterInsert).ExecuteNonQuery();
        AreEqual(1, FiredBy(connection));
        _ = connection.CreateCommand("begin tran").ExecuteNonQuery();
        _ = connection.CreateCommand("drop trigger tr_t").ExecuteNonQuery();
        AreEqual(0, FiredBy(connection));
        _ = connection.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual(1, FiredBy(connection));
    }

    // The restored table is the same object, and its triggers come back with it.
    [TestMethod]
    public void RolledBackDropTable_FiresAgain()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand(CreateAfterInsert).ExecuteNonQuery();
        AreEqual(1, FiredBy(connection));
        _ = connection.CreateCommand("begin tran; drop table t; rollback").ExecuteNonQuery();
        AreEqual(1, FiredBy(connection));
    }

    [TestMethod]
    public void AlterTrigger_ChangesWhichActionFires()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand(CreateAfterInsert).ExecuteNonQuery();
        AreEqual(1, FiredBy(connection));
        _ = connection.CreateCommand("alter trigger tr_t on t after update as insert fired values (1)").ExecuteNonQuery();
        AreEqual(0, FiredBy(connection));
        AreEqual(1, FiredBy(connection, "update t set v = 2 where id = 1"));
    }

    [TestMethod]
    public void DisableAndEnable_TakeEffectOnTheNextWrite()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand(CreateAfterInsert).ExecuteNonQuery();
        AreEqual(1, FiredBy(connection));
        _ = connection.CreateCommand("disable trigger tr_t on t").ExecuteNonQuery();
        AreEqual(0, FiredBy(connection));
        _ = connection.CreateCommand("alter table t enable trigger all").ExecuteNonQuery();
        AreEqual(1, FiredBy(connection));
    }

    [TestMethod]
    public void TransferredTable_KeepsFiring()
    {
        using var connection = Seeded("create schema other");
        _ = connection.CreateCommand(CreateAfterInsert).ExecuteNonQuery();
        AreEqual(1, FiredBy(connection));
        _ = connection.CreateCommand("alter schema other transfer dbo.t").ExecuteNonQuery();
        AreEqual(1, FiredBy(connection, "insert other.t (v) values (1)"));
    }

    [TestMethod]
    public void InsteadOfTriggerOnAlteredView_FiresOnTheReplacement()
    {
        using var connection = Seeded("create view vt as select id, v from t");
        _ = connection.CreateCommand("create trigger tr_vt on vt instead of insert as insert fired values (1)").ExecuteNonQuery();
        AreEqual(1, FiredBy(connection, "insert vt (v) values (1)"));
        _ = connection.CreateCommand("alter view vt as select id, v from t where v > 0").ExecuteNonQuery();
        AreEqual(1, FiredBy(connection, "insert vt (v) values (1)"));
        _ = connection.CreateCommand("drop trigger tr_vt").ExecuteNonQuery();
        AreEqual(0, FiredBy(connection, "insert vt (v) values (1)"));
    }

    // Another session's trigger DDL reaches a session that already wrote the table.
    [TestMethod]
    public void CreateTriggerFromAnotherSession_Fires()
    {
        var simulation = new Simulation();
        using var writer = simulation.CreateOpenConnection();
        _ = writer.CreateCommand("create table t (id int identity primary key, v int); create table fired (n int)").ExecuteNonQuery();
        AreEqual(0, FiredBy(writer));
        using (var other = simulation.CreateOpenConnection())
            _ = other.CreateCommand(CreateAfterInsert).ExecuteNonQuery();
        AreEqual(1, FiredBy(writer));
    }

    // A DDL trigger that fails the CREATE TRIGGER after it wrote the slot: the
    // write's firing agrees with what the catalog says survived.
    [TestMethod]
    public void CreateTriggerFailedByDdlTrigger_FiringMatchesTheCatalog()
    {
        using var connection = Seeded("create trigger ddl_block on database for create_trigger as rollback");
        AreEqual(0, FiredBy(connection));
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand(CreateAfterInsert).ExecuteNonQuery());
        AreEqual(3609, ex.Number);
        var exists = (int)connection.CreateCommand("select count(*) from sys.triggers where name = 'tr_t'").ExecuteScalar()!;
        AreEqual(exists, FiredBy(connection));
    }
}
