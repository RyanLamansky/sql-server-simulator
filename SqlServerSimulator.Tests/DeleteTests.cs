using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>DELETE FROM table [WHERE pred]</c> (the
/// <c>FROM</c> keyword is optional in T-SQL). Covers row-count return,
/// no-WHERE bulk delete, no-match zero-affected, post-delete enumeration
/// (tombstoned slots invisible), and post-delete INSERT (slot directory
/// continues from current count). LOB chain reclamation isn't part of
/// this bundle — see CLAUDE.md for the leak quirk.
/// </summary>
[TestClass]
public sealed class DeleteTests
{
    private static List<int> ReadInts(DbCommand command)
    {
        using var reader = command.ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        return values;
    }

    [TestMethod]
    public void Delete_BasicWhere_RemovesOneRow()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 10), (2, 20), (3, 30)");

        var affected = simulation.ExecuteNonQuery("delete from t where id = 2");
        AreEqual(1, affected);

        var values = ReadInts(simulation.CreateCommand("select id from t order by id"));
        CollectionAssert.AreEqual(new[] { 1, 3 }, values);
    }

    [TestMethod]
    public void Delete_NoWhere_RemovesAllRows()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1), (2), (3), (4)");

        var affected = simulation.ExecuteNonQuery("delete from t");
        AreEqual(4, affected);

        var values = ReadInts(simulation.CreateCommand("select id from t"));
        IsEmpty(values);
    }

    [TestMethod]
    public void Delete_WhereNoMatch_ZeroAffected()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1), (2)");

        var affected = simulation.ExecuteNonQuery("delete from t where id = 999");
        AreEqual(0, affected);

        var values = ReadInts(simulation.CreateCommand("select id from t order by id"));
        CollectionAssert.AreEqual(new[] { 1, 2 }, values);
    }

    [TestMethod]
    public void Delete_OptionalFromKeyword_StillWorks()
    {
        // T-SQL allows DELETE without FROM (FROM is optional in single-table form).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1), (2)");

        var affected = simulation.ExecuteNonQuery("delete t where id = 1");
        AreEqual(1, affected);
    }

    [TestMethod]
    public void Delete_NonexistentTable_RaisesMsg208()
    {
        var ex = Throws<DbException>(() => _ = new Simulation().ExecuteNonQuery("delete from no_such"));
        AreEqual("208", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Delete_ThenInsert_NewRowVisibleAfterTombstone()
    {
        // Verifies tombstoned slots don't block subsequent INSERTs from
        // creating new visible rows in the heap.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, v varchar(20))");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 'a'), (2, 'b')");
        _ = simulation.ExecuteNonQuery("delete from t where id = 1");
        _ = simulation.ExecuteNonQuery("insert into t values (3, 'c')");

        var values = ReadInts(simulation.CreateCommand("select id from t order by id"));
        CollectionAssert.AreEqual(new[] { 2, 3 }, values);
    }

    [TestMethod]
    public void Delete_AllThenInsert_HeapStillFunctional()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1), (2), (3)");
        _ = simulation.ExecuteNonQuery("delete from t");
        _ = simulation.ExecuteNonQuery("insert into t values (4), (5)");

        var values = ReadInts(simulation.CreateCommand("select id from t order by id"));
        CollectionAssert.AreEqual(new[] { 4, 5 }, values);
    }

    // === OUTPUT clause (literal-only) ===

    [TestMethod]
    public void Delete_OutputLiteralOne_YieldsOneRowPerAffected()
    {
        // EF Core emits `DELETE FROM ... OUTPUT 1 WHERE ...` on
        // SaveChanges-Remove. Verifies the OUTPUT projection runs once per
        // deleted row.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1), (2), (3)");

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("delete from t output 1 where id <= 2").ExecuteReader();
        var ones = new List<int>();
        while (reader.Read())
            ones.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1, 1 }, ones);
    }

    [TestMethod]
    public void Delete_OutputDeletedColumn_YieldsRemovedValue()
    {
        // DELETED.<col> on DELETE returns the pre-delete value (probe-confirmed).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1)");

        var deletedId = simulation.ExecuteScalar("delete from t output deleted.id where id = 1");
        AreEqual(1, deletedId);
    }

    [TestMethod]
    public void Delete_OutputInsertedColumn_RaisesMsg4104()
    {
        // INSERTED isn't a valid qualifier in DELETE OUTPUT (probe-confirmed Msg 4104).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1)");

        var ex = Throws<DbException>(() =>
            _ = simulation.ExecuteScalar("delete from t output inserted.id where id = 1"));
        AreEqual("4104", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Delete_PrimaryKeyTable_AllowsReinsertAfterDelete()
    {
        // After DELETE removes the row with k=1, a new INSERT of k=1
        // must succeed (no PK collision against tombstoned data).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (k int primary key, v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 10)");
        _ = simulation.ExecuteNonQuery("delete from t where k = 1");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 20)");

        var values = ReadInts(simulation.CreateCommand("select v from t"));
        CollectionAssert.AreEqual(new[] { 20 }, values);
    }
}
