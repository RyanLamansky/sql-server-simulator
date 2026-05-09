using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>DELETE FROM table [WHERE pred]</c> (FROM is optional in T-SQL).
/// Covers row-count return, no-WHERE bulk delete, no-match zero-affected, post-delete enumeration
/// (tombstoned slots invisible), post-delete INSERT, and the EF7+ multi-table-syntax form.
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

        AreEqual(1, simulation.ExecuteNonQuery("delete from t where id = 2"));
        CollectionAssert.AreEqual(new[] { 1, 3 }, ReadInts(simulation.CreateCommand("select id from t order by id")));
    }

    [TestMethod]
    public void Delete_NoWhere_RemovesAllRows()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1), (2), (3), (4)");

        AreEqual(4, simulation.ExecuteNonQuery("delete from t"));
        IsEmpty(ReadInts(simulation.CreateCommand("select id from t")));
    }

    [TestMethod]
    public void Delete_WhereNoMatch_ZeroAffected()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1), (2)");

        AreEqual(0, simulation.ExecuteNonQuery("delete from t where id = 999"));
        CollectionAssert.AreEqual(new[] { 1, 2 }, ReadInts(simulation.CreateCommand("select id from t order by id")));
    }

    [TestMethod]
    public void Delete_OptionalFromKeyword_StillWorks()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1), (2)");
        AreEqual(1, simulation.ExecuteNonQuery("delete t where id = 1"));
    }

    [TestMethod]
    public void Delete_NonexistentTable_RaisesMsg208()
        => _ = new Simulation().AssertSqlError("delete from no_such", 208);

    [TestMethod]
    public void Delete_ThenInsert_NewRowVisibleAfterTombstone()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, v varchar(20))");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 'a'), (2, 'b')");
        _ = simulation.ExecuteNonQuery("delete from t where id = 1");
        _ = simulation.ExecuteNonQuery("insert into t values (3, 'c')");

        CollectionAssert.AreEqual(new[] { 2, 3 }, ReadInts(simulation.CreateCommand("select id from t order by id")));
    }

    [TestMethod]
    public void Delete_AllThenInsert_HeapStillFunctional()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1), (2), (3)");
        _ = simulation.ExecuteNonQuery("delete from t");
        _ = simulation.ExecuteNonQuery("insert into t values (4), (5)");

        CollectionAssert.AreEqual(new[] { 4, 5 }, ReadInts(simulation.CreateCommand("select id from t order by id")));
    }

    [TestMethod]
    public void Delete_OutputLiteralOne_YieldsOneRowPerAffected()
    {
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
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1)");
        AreEqual(1, simulation.ExecuteScalar("delete from t output deleted.id where id = 1"));
    }

    [TestMethod]
    public void Delete_OutputInsertedColumn_RaisesMsg4104()
    {
        // INSERTED isn't valid in DELETE OUTPUT.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1)");
        _ = simulation.AssertSqlError("delete from t output inserted.id where id = 1", 4104);
    }

    [TestMethod]
    public void Delete_PrimaryKeyTable_AllowsReinsertAfterDelete()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (k int primary key, v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 10)");
        _ = simulation.ExecuteNonQuery("delete from t where k = 1");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 20)");

        CollectionAssert.AreEqual(new[] { 20 }, ReadInts(simulation.CreateCommand("select v from t")));
    }

    [TestMethod]
    public void Delete_MultiTableSyntax_AcceptsAliasFormWithFromClause()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1), (2), (3)");

        AreEqual(1, simulation.ExecuteNonQuery("delete from [a] from t as [a] where [a].[id] = 2"));
        CollectionAssert.AreEqual(new[] { 1, 3 }, ReadInts(simulation.CreateCommand("select id from t order by id")));
    }

    [TestMethod]
    public void Delete_MultiTableSyntax_NoWhereClause_DeletesAllRows()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1), (2), (3)");
        AreEqual(3, simulation.ExecuteNonQuery("delete from [a] from t as [a]"));
    }

    [TestMethod]
    public void Delete_MultiTableSyntax_AliasUnknownAndNoFromClause_RaisesInvalidObject()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");
        var ex = Throws<DbException>(() => simulation.ExecuteNonQuery("delete from [unknown]"));
        Contains("Invalid object name", ex.Message);
    }

    [TestMethod]
    public void Delete_MultiTableSyntax_JoinedFromClause_DeletesEachTargetOnce()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table customers (id int primary key, status varchar(20))");
        _ = simulation.ExecuteNonQuery("create table orders (id int primary key, customerId int, total decimal(10, 2))");
        _ = simulation.ExecuteNonQuery("insert customers values (1, 'A'), (2, 'B'), (3, 'C')");
        _ = simulation.ExecuteNonQuery("insert orders values (10, 1, 50), (11, 1, 200), (12, 2, 150), (13, 2, 250)");

        AreEqual(2, simulation.ExecuteNonQuery(
            "delete c from customers c inner join orders o on o.customerId = c.id where o.total > 100"));

        var statuses = new List<string>();
        using (var reader = simulation.CreateCommand("select status from customers order by id").ExecuteReader())
            while (reader.Read()) statuses.Add(reader.GetString(0));
        CollectionAssert.AreEqual(new[] { "C" }, statuses);
    }

    [TestMethod]
    public void Delete_JoinedFromClause_DeleteFromAliasSyntax_AlsoWorks()
    {
        // DELETE FROM <alias> FROM ... JOIN ... — both forms accepted (EF Core 10 emits the DELETE alias form).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table customers (id int primary key, status varchar(20))");
        _ = simulation.ExecuteNonQuery("create table orders (id int primary key, customerId int, total decimal(10, 2))");
        _ = simulation.ExecuteNonQuery("insert customers values (1, 'A'), (2, 'B')");
        _ = simulation.ExecuteNonQuery("insert orders values (10, 1, 200)");

        AreEqual(1, simulation.ExecuteNonQuery(
            "delete from c from customers c inner join orders o on o.customerId = c.id where o.total > 100"));
    }

    [TestMethod]
    public void Delete_JoinedFromClause_AliasNotInFrom_RaisesMsg208()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");
        _ = simulation.ExecuteNonQuery("create table u (id int)");
        var ex = Throws<DbException>(() =>
            _ = simulation.ExecuteNonQuery("delete [x] from t as [a] inner join u as [b] on [a].[id] = [b].[id]"));
        Contains("Invalid object name", ex.Message);
    }

    [TestMethod]
    public void Delete_JoinedFromClause_TargetOnRightSide_DeletesFromTarget()
    {
        // Target alias on right side of join (delete orders, not customers).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table customers (id int primary key, status varchar(20))");
        _ = simulation.ExecuteNonQuery("create table orders (id int primary key, customerId int, total decimal(10, 2))");
        _ = simulation.ExecuteNonQuery("insert customers values (1, 'X')");
        _ = simulation.ExecuteNonQuery("insert orders values (10, 1, 50), (11, 1, 200)");

        AreEqual(1, simulation.ExecuteNonQuery(
            "delete o from customers c inner join orders o on o.customerId = c.id where o.total > 100"));

        var totals = new List<decimal>();
        using var reader = simulation.CreateCommand("select total from orders order by id").ExecuteReader();
        while (reader.Read()) totals.Add(reader.GetDecimal(0));
        CollectionAssert.AreEqual(new[] { 50m }, totals);
    }
}
