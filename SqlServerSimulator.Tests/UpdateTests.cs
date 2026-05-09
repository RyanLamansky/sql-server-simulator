using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for single-table <c>UPDATE table SET col = expr [, col = expr]*
/// [WHERE pred]</c>: row-count return, multi-column SET pre-update snapshot,
/// identity / computed column rejection, NOT NULL / CHECK / PK re-validation
/// with UPDATE-verbed messages, scalar-subquery RHS, oversize-row off-row push,
/// and the EF7+ multi-table-syntax form.
/// </summary>
[TestClass]
public sealed class UpdateTests
{
    private static List<int> ReadInts(DbCommand command)
    {
        using var reader = command.ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        return values;
    }

    private static List<string> ReadStrings(DbCommand command)
    {
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
            values.Add(reader.GetString(0));
        return values;
    }

    [TestMethod]
    public void Update_BasicWhere_OneRow()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 10), (2, 20), (3, 30)");

        AreEqual(1, simulation.ExecuteNonQuery("update t set v = 99 where id = 2"));
        CollectionAssert.AreEqual(new[] { 10, 99, 30 }, ReadInts(simulation.CreateCommand("select v from t order by id")));
    }

    [TestMethod]
    public void Update_NoWhere_TouchesAllRows()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 10), (2, 20), (3, 30)");

        AreEqual(3, simulation.ExecuteNonQuery("update t set v = v + 1"));
        CollectionAssert.AreEqual(new[] { 11, 21, 31 }, ReadInts(simulation.CreateCommand("select v from t order by id")));
    }

    [TestMethod]
    public void Update_WhereMatchesNothing_ZeroAffected()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 10)");

        AreEqual(0, simulation.ExecuteNonQuery("update t set v = 99 where id = 999"));
    }

    [TestMethod]
    public void Update_MultipleSetClauses()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, name varchar(20), age int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 'alice', 30)");

        AreEqual(1, simulation.ExecuteNonQuery("update t set name = 'ALICE', age = 31 where id = 1"));

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("select name, age from t").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("ALICE", reader.GetString(0));
        AreEqual(31, reader.GetInt32(1));
    }

    [TestMethod]
    public void Update_MultipleSet_RhsReadsPreUpdateSnapshot()
    {
        // `set a = 100, b = a + 1` over (a=10, b=20) → (a=100, b=11) — b reads pre-update a.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, a int, b int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 10, 20)");

        _ = simulation.ExecuteNonQuery("update t set a = 100, b = a + 1 where id = 1");

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("select a, b from t").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(100, reader.GetInt32(0));
        AreEqual(11, reader.GetInt32(1));
    }

    [TestMethod]
    public void Update_ScalarSubqueryRhs_SeesPreUpdateState()
    {
        // max(v) is computed pre-update: row 1 (v=10) becomes max(v)=20.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 10), (2, 20)");

        _ = simulation.ExecuteNonQuery("update t set v = (select max(v) from t) where id = 1");

        CollectionAssert.AreEqual(new[] { 20, 20 }, ReadInts(simulation.CreateCommand("select v from t order by id")));
    }

    [TestMethod]
    public void Update_IdentityColumn_RaisesMsg8102()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int identity(1,1), v int)");
        _ = simulation.ExecuteNonQuery("insert into t (v) values (10)");

        simulation.AssertSqlError("update t set id = 99", 8102, "Cannot update identity column 'id'.");
    }

    [TestMethod]
    public void Update_ComputedColumn_RaisesMsg271()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, b as a + 1)");
        _ = simulation.ExecuteNonQuery("insert into t (a) values (10)");

        _ = simulation.AssertSqlError("update t set b = 99", 271);
    }

    [TestMethod]
    public void Update_NotNullViolation_RaisesMsg515WithUpdateVerb()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, label varchar(20) not null)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 'a')");

        var ex = simulation.AssertSqlError("update t set label = null where id = 1", 515);
        Contains("UPDATE fails.", ex.Message);
    }

    [TestMethod]
    public void Update_CheckViolation_RaisesMsg547WithUpdateStatement()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, age int, check (age >= 0))");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 30)");

        var ex = simulation.AssertSqlError("update t set age = -1 where id = 1", 547);
        Contains("UPDATE statement", ex.Message);
    }

    [TestMethod]
    public void Update_PrimaryKeyDuplicate_RaisesMsg2627()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (k int primary key, v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 10), (2, 20)");

        _ = simulation.AssertSqlError("update t set k = 2 where k = 1", 2627);
    }

    [TestMethod]
    public void Update_PrimaryKeyToSameValue_NoSelfCollision()
    {
        // Updating PK to its own value is no-op key-wise; affected-set exclusion in EnforceKeyConstraintsForUpdate.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (k int primary key, v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 10), (2, 20)");

        AreEqual(1, simulation.ExecuteNonQuery("update t set k = 1, v = 99 where k = 1"));
    }

    [TestMethod]
    public void Update_TypeCoercion_StringToInt()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 10)");

        _ = simulation.ExecuteNonQuery("update t set v = '99' where id = 1");

        CollectionAssert.AreEqual(new[] { 99 }, ReadInts(simulation.CreateCommand("select v from t")));
    }

    [TestMethod]
    public void Update_BadCoercion_RaisesMsg245()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 10)");

        _ = simulation.AssertSqlError("update t set v = 'abc' where id = 1", 245);
    }

    [TestMethod]
    public void Update_NonexistentTable_RaisesMsg208()
        => _ = new Simulation().AssertSqlError("update no_such set v = 1", 208);

    [TestMethod]
    public void Update_NonexistentColumn_RaisesMsg207()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");
        _ = simulation.AssertSqlError("update t set no_such = 1", 207);
    }

    [TestMethod]
    public void Update_ComputedColumnRecomputedAfterDependencyChange()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, a int, b as a + 100)");
        _ = simulation.ExecuteNonQuery("insert into t (id, a) values (1, 5)");

        _ = simulation.ExecuteNonQuery("update t set a = 50 where id = 1");

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("select a, b from t").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(50, reader.GetInt32(0));
        AreEqual(150, reader.GetInt32(1));
    }

    [TestMethod]
    public void Update_GrowingVarcharPastInline_ReencodesWithOffRowPush()
    {
        // varchar(8000) starting at 'short' (5 bytes) grows to 8000 bytes — encoder pushes off-row when row > 8060.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, big varchar(8000))");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 'short')");

        using var connection = simulation.CreateOpenConnection();
        using var update = connection.CreateCommand("update t set big = @big where id = 1");
        var bigParam = update.CreateParameter();
        bigParam.ParameterName = "@big";
        bigParam.Value = new string('x', 8000);
        _ = update.Parameters.Add(bigParam);
        _ = update.ExecuteNonQuery();

        using var reader = connection.CreateCommand("select big from t").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(8000, reader.GetString(0).Length);
    }

    [TestMethod]
    public void Update_OutputLiteralOne_YieldsOneRowPerAffected()
    {
        // EF Core 8 emits `UPDATE ... OUTPUT 1 WHERE ...` as a rows-affected detector.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 10), (2, 20), (3, 30)");

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("update t set v = v + 100 output 1 where id <= 2").ExecuteReader();
        var ones = new List<int>();
        while (reader.Read())
            ones.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1, 1 }, ones);
    }

    [TestMethod]
    public void Update_OutputInsertedColumn_YieldsNewValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 10)");

        AreEqual(99, simulation.ExecuteScalar("update t set v = 99 output inserted.v where id = 1"));
    }

    [TestMethod]
    public void Update_OutputDeletedColumn_YieldsOldValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 10)");

        AreEqual(10, simulation.ExecuteScalar("update t set v = 99 output deleted.v where id = 1"));
    }

    [TestMethod]
    public void Update_OutputBothInsertedAndDeleted_YieldsBoth()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 10)");

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("update t set v = v * 5 output deleted.v as old_v, inserted.v as new_v where id = 1").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(10, reader.GetInt32(0));
        AreEqual(50, reader.GetInt32(1));
    }

    [TestMethod]
    public void Update_OutputBareColumnRef_RaisesMsg207()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 10)");
        _ = simulation.AssertSqlError("update t set v = 99 output v where id = 1", 207);
    }

    [TestMethod]
    public void Update_OutputZeroAffected_EmptyResultSet()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, v int)");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 10)");

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("update t set v = 99 output 1 where id = 999").ExecuteReader();
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Update_ThenSelect_SeesNewState()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, name varchar(30))");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 'old')");

        _ = simulation.ExecuteNonQuery("update t set name = 'new' where id = 1");
        CollectionAssert.AreEqual(new[] { "new" }, ReadStrings(simulation.CreateCommand("select name from t")));

        _ = simulation.ExecuteNonQuery("update t set name = 'newer' where id = 1");
        CollectionAssert.AreEqual(new[] { "newer" }, ReadStrings(simulation.CreateCommand("select name from t")));
    }

    [TestMethod]
    public void Update_MultiTableSyntax_AcceptsAliasFormWithFromClause()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, name varchar(30))");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 'a'), (2, 'b'), (3, 'c')");

        AreEqual(1, simulation.ExecuteNonQuery(
            "update [a] set [a].[name] = upper([a].[name]) from t as [a] where [a].[id] = 2"));

        CollectionAssert.AreEqual(new[] { "a", "B", "c" }, ReadStrings(simulation.CreateCommand("select name from t order by id")));
    }

    [TestMethod]
    public void Update_MultiTableSyntax_NoWhereClause_UpdatesAllRows()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, name varchar(30))");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 'a'), (2, 'b')");

        AreEqual(2, simulation.ExecuteNonQuery("update [a] set [a].[name] = 'X' from t as [a]"));

        CollectionAssert.AreEqual(new[] { "X", "X" }, ReadStrings(simulation.CreateCommand("select name from t order by id")));
    }

    [TestMethod]
    public void Update_MultiTableSyntax_AliasUnknownAndNoFromClause_RaisesInvalidObject()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");

        var ex = Throws<DbException>(() => simulation.ExecuteNonQuery("update [unknown] set [unknown].[id] = 1"));
        Contains("Invalid object name", ex.Message);
    }

    [TestMethod]
    public void Update_MultiTableSyntax_JoinedFromClause_UpdatesEachTargetOnce()
    {
        // Joined-source UPDATE: targets matching multiple join rows update exactly once (probe-confirmed).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table customers (id int primary key, status varchar(20))");
        _ = simulation.ExecuteNonQuery("create table orders (id int primary key, customerId int, total decimal(10, 2))");
        _ = simulation.ExecuteNonQuery("insert customers values (1, 'New'), (2, 'New'), (3, 'New')");
        _ = simulation.ExecuteNonQuery("insert orders values (10, 1, 50), (11, 1, 200), (12, 2, 150), (13, 2, 250)");

        AreEqual(2, simulation.ExecuteNonQuery(
            "update c set c.status = 'Active' from customers c inner join orders o on o.customerId = c.id where o.total > 100"));

        CollectionAssert.AreEqual(new[] { "Active", "Active", "New" },
            ReadStrings(simulation.CreateCommand("select status from customers order by id")));
    }

    [TestMethod]
    public void Update_JoinedFromClause_AliasNotInFrom_RaisesMsg208()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");
        _ = simulation.ExecuteNonQuery("create table u (id int)");
        var ex = Throws<DbException>(() =>
            _ = simulation.ExecuteNonQuery("update [x] set [x].[id] = 1 from t as [a] inner join u as [b] on [a].[id] = [b].[id]"));
        Contains("Invalid object name", ex.Message);
    }

    [TestMethod]
    public void Update_JoinedFromClause_LeftJoinNullRight_StillUpdatesTarget()
    {
        // LEFT JOIN with no right match: target rows still update; SET RHS sees NULL for right-side columns.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table customers (id int primary key, status varchar(20))");
        _ = simulation.ExecuteNonQuery("create table orders (id int primary key, customerId int, total decimal(10, 2))");
        _ = simulation.ExecuteNonQuery("insert customers values (1, 'New'), (2, 'New')");
        _ = simulation.ExecuteNonQuery("insert orders values (10, 1, 50)");

        AreEqual(2, simulation.ExecuteNonQuery(
            "update c set c.status = 'Touched' from customers c left join orders o on o.customerId = c.id and o.total > 1000"));

        CollectionAssert.AreEqual(new[] { "Touched", "Touched" },
            ReadStrings(simulation.CreateCommand("select status from customers order by id")));
    }

    [TestMethod]
    public void Update_JoinedFromClause_SetRhsFromOtherSource_UsesFirstMatch()
    {
        // Multi-match SET RHS: target matching multiple rows uses FIRST match's value (heap-scan order).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table customers (id int primary key, status varchar(20))");
        _ = simulation.ExecuteNonQuery("create table orders (id int primary key, customerId int, code varchar(10))");
        _ = simulation.ExecuteNonQuery("insert customers values (1, 'New')");
        _ = simulation.ExecuteNonQuery("insert orders values (10, 1, 'A'), (11, 1, 'B')");

        AreEqual(1, simulation.ExecuteNonQuery(
            "update c set c.status = o.code from customers c inner join orders o on o.customerId = c.id"));

        CollectionAssert.AreEqual(new[] { "A" }, ReadStrings(simulation.CreateCommand("select status from customers")));
    }
}
