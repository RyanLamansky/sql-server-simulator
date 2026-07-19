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
        _ = simulation.ExecuteNonQuery("""
            create table t (id int, v int);
            insert t values (1, 10), (2, 20), (3, 30)
            """);

        AreEqual(1, simulation.ExecuteNonQuery("delete from t where id = 2"));
        CollectionAssert.AreEqual(new[] { 1, 3 }, ReadInts(simulation.CreateCommand("select id from t order by id")));
    }

    [TestMethod]
    public void Delete_NoWhere_RemovesAllRows()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int);
            insert t values (1), (2), (3), (4)
            """);

        AreEqual(4, simulation.ExecuteNonQuery("delete from t"));
        IsEmpty(ReadInts(simulation.CreateCommand("select id from t")));
    }

    [TestMethod]
    public void Delete_WhereNoMatch_ZeroAffected()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int);
            insert t values (1), (2)
            """);

        AreEqual(0, simulation.ExecuteNonQuery("delete from t where id = 999"));
        CollectionAssert.AreEqual(new[] { 1, 2 }, ReadInts(simulation.CreateCommand("select id from t order by id")));
    }

    [TestMethod]
    public void Delete_OptionalFromKeyword_StillWorks()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int);
            insert t values (1), (2);
            delete t where id = 1;
            select count(*) from t
            """));

    [TestMethod]
    public void Delete_NonexistentTable_RaisesMsg208()
        => _ = new Simulation().AssertSqlError("delete from no_such", 208);

    [TestMethod]
    public void Delete_ThenInsert_NewRowVisibleAfterTombstone()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int, v varchar(20));
            insert t values (1, 'a'), (2, 'b');
            delete from t where id = 1;
            insert t values (3, 'c')
            """);

        CollectionAssert.AreEqual(new[] { 2, 3 }, ReadInts(simulation.CreateCommand("select id from t order by id")));
    }

    [TestMethod]
    public void Delete_AllThenInsert_HeapStillFunctional()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int);
            insert t values (1), (2), (3);
            delete from t;
            insert t values (4), (5)
            """);

        CollectionAssert.AreEqual(new[] { 4, 5 }, ReadInts(simulation.CreateCommand("select id from t order by id")));
    }

    [TestMethod]
    public void Delete_OutputLiteralOne_YieldsOneRowPerAffected()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int);
            insert t values (1), (2), (3)
            """);

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("delete from t output 1 where id <= 2").ExecuteReader();
        var ones = new List<int>();
        while (reader.Read())
            ones.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1, 1 }, ones);
    }

    [TestMethod]
    public void Delete_OutputDeletedColumn_YieldsRemovedValue()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int);
            insert t values (1);
            delete from t output deleted.id where id = 1
            """));

    // INSERTED isn't valid in DELETE OUTPUT.
    [TestMethod]
    public void Delete_OutputInsertedColumn_RaisesMsg4104()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int);
            insert t values (1);
            delete from t output inserted.id where id = 1
            """, 4104);

    [TestMethod]
    public void Delete_PrimaryKeyTable_AllowsReinsertAfterDelete()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (k int primary key, v int);
            insert t values (1, 10);
            delete from t where k = 1;
            insert t values (1, 20)
            """);

        CollectionAssert.AreEqual(new[] { 20 }, ReadInts(simulation.CreateCommand("select v from t")));
    }

    [TestMethod]
    public void Delete_MultiTableSyntax_AcceptsAliasFormWithFromClause()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int);
            insert t values (1), (2), (3)
            """);

        AreEqual(1, simulation.ExecuteNonQuery("delete from [a] from t as [a] where [a].[id] = 2"));
        CollectionAssert.AreEqual(new[] { 1, 3 }, ReadInts(simulation.CreateCommand("select id from t order by id")));
    }

    [TestMethod]
    public void Delete_MultiTableSyntax_NoWhereClause_DeletesAllRows()
    {
        // Setup separate from DELETE so the rows-affected assert isolates the DELETE's count
        // (ExecuteNonQuery returns the SUM across statements in a batch).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int);
            insert t values (1), (2), (3)
            """);
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
        _ = simulation.ExecuteNonQuery("""
            create table customers (id int primary key, status varchar(20));
            create table orders (id int primary key, customerId int, total decimal(10, 2));
            insert customers values (1, 'A'), (2, 'B'), (3, 'C');
            insert orders values (10, 1, 50), (11, 1, 200), (12, 2, 150), (13, 2, 250)
            """);

        AreEqual(2, simulation.ExecuteNonQuery(
            "delete c from customers c inner join orders o on o.customerId = c.id where o.total > 100"));

        var statuses = new List<string>();
        using (var reader = simulation.CreateCommand("select status from customers order by id").ExecuteReader())
            while (reader.Read()) statuses.Add(reader.GetString(0));
        CollectionAssert.AreEqual(new[] { "C" }, statuses);
    }

    // DELETE FROM <alias> FROM ... JOIN ... — both forms accepted (EF Core 10 emits the DELETE alias form).
    [TestMethod]
    public void Delete_JoinedFromClause_DeleteFromAliasSyntax_AlsoWorks()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table customers (id int primary key, status varchar(20));
            create table orders (id int primary key, customerId int, total decimal(10, 2));
            insert customers values (1, 'A'), (2, 'B');
            insert orders values (10, 1, 200)
            """);

        AreEqual(1, simulation.ExecuteNonQuery(
            "delete from c from customers c inner join orders o on o.customerId = c.id where o.total > 100"));
    }

    [TestMethod]
    public void Delete_JoinedFromClause_AliasNotInFrom_RaisesMsg208()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int);
            create table u (id int)
            """);
        var ex = Throws<DbException>(() =>
            _ = simulation.ExecuteNonQuery("delete [x] from t as [a] inner join u as [b] on [a].[id] = [b].[id]"));
        Contains("Invalid object name", ex.Message);
    }

    // Target alias on right side of join (delete orders, not customers).
    [TestMethod]
    public void Delete_JoinedFromClause_TargetOnRightSide_DeletesFromTarget()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table customers (id int primary key, status varchar(20));
            create table orders (id int primary key, customerId int, total decimal(10, 2));
            insert customers values (1, 'X');
            insert orders values (10, 1, 50), (11, 1, 200)
            """);

        AreEqual(1, simulation.ExecuteNonQuery(
            "delete o from customers c inner join orders o on o.customerId = c.id where o.total > 100"));

        var totals = new List<decimal>();
        using var reader = simulation.CreateCommand("select total from orders order by id").ExecuteReader();
        while (reader.Read()) totals.Add(reader.GetDecimal(0));
        CollectionAssert.AreEqual(new[] { 50m }, totals);
    }
}
