using System.Data.Common;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for column <c>DEFAULT</c> clauses on <c>CREATE TABLE</c>:
/// the default expression fires only when the column is omitted from the
/// INSERT's destination column list, and <c>NEWSEQUENTIALID()</c> is
/// restricted to that grammar position.
/// </summary>
[TestClass]
public sealed class DefaultClauseTests
{
    [TestMethod]
    public void Default_LiteralFiresWhenColumnOmitted()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, label nvarchar(20) not null default N'unset')");
        _ = simulation.ExecuteNonQuery("insert into t (id) values (1)");
        Assert.AreEqual("unset", simulation.ExecuteScalar("select label from t"));
    }

    [TestMethod]
    public void Default_DoesNotFireWhenColumnIsListed()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, label nvarchar(20) not null default N'unset')");
        _ = simulation.ExecuteNonQuery("insert into t (id, label) values (1, N'explicit')");
        Assert.AreEqual("explicit", simulation.ExecuteScalar("select label from t"));
    }

    [TestMethod]
    public void Default_ExplicitNullOverridesDefault()
    {
        // SQL Server treats an explicit NULL in the VALUES list as a real
        // value, not an "omit and use default" — defaults only fire when the
        // column itself is absent from the column list.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, label nvarchar(20) null default N'unset')");
        _ = simulation.ExecuteNonQuery("insert into t (id, label) values (1, null)");
        Assert.AreEqual(DBNull.Value, simulation.ExecuteScalar("select label from t"));
    }

    [TestMethod]
    public void Default_OrderingFlexible_NotNullThenDefault()
    {
        // Real SQL Server accepts column constraints in any order; common
        // EF Core output is `NOT NULL DEFAULT (...)`, so this ordering must
        // parse cleanly.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, label nvarchar(20) not null default N'a')");
        _ = simulation.ExecuteNonQuery("insert into t (id) values (1)");
        Assert.AreEqual("a", simulation.ExecuteScalar("select label from t"));
    }

    [TestMethod]
    public void Default_OrderingFlexible_DefaultThenNotNull()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, label nvarchar(20) default N'a' not null)");
        _ = simulation.ExecuteNonQuery("insert into t (id) values (1)");
        Assert.AreEqual("a", simulation.ExecuteScalar("select label from t"));
    }

    [TestMethod]
    public void Default_ParenthesizedExpression()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, n int not null default (1 + 2))");
        _ = simulation.ExecuteNonQuery("insert into t (id) values (1)");
        Assert.AreEqual(3, simulation.ExecuteScalar("select n from t"));
    }

    [TestMethod]
    public void Default_NewIdProducesDistinctValues()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, g uniqueidentifier not null default newid())");
        _ = simulation.ExecuteNonQuery("insert into t (id) values (1)");
        _ = simulation.ExecuteNonQuery("insert into t (id) values (2)");
        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("select g from t order by id").ExecuteReader();
        Assert.IsTrue(reader.Read());
        var first = reader.GetGuid(0);
        Assert.IsTrue(reader.Read());
        var second = reader.GetGuid(0);
        Assert.AreNotEqual(first, second);
    }

    [TestMethod]
    public void NewSequentialId_DefaultFiresMonotonicGuids()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, g uniqueidentifier not null default newsequentialid())");
        for (var i = 0; i < 10; i++)
            _ = simulation.ExecuteNonQuery($"insert into t (id) values ({i})");

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("select g from t order by id").ExecuteReader();
        var guids = new List<Guid>();
        while (reader.Read())
            guids.Add(reader.GetGuid(0));

        Assert.HasCount(10, guids);
        for (var i = 1; i < guids.Count; i++)
            Assert.IsGreaterThan(guids[i - 1], guids[i], $"Guid at {i} was not greater than its predecessor");
    }

    [TestMethod]
    public void NewSequentialId_DoesNotAdvanceWhenColumnExplicitlyProvided()
    {
        // If the user supplies an explicit value for the GUID column, the
        // default expression must not fire — otherwise NEWSEQUENTIALID would
        // burn a counter slot per ignored row.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, g uniqueidentifier not null default newsequentialid())");
        var explicitGuid = Guid.NewGuid();

        using var connection = simulation.CreateOpenConnection();
        using (var insert = connection.CreateCommand("insert into t (id, g) values (1, @g)", ("@g", explicitGuid)))
            _ = insert.ExecuteNonQuery();
        _ = simulation.ExecuteNonQuery("insert into t (id) values (2)");
        _ = simulation.ExecuteNonQuery("insert into t (id) values (3)");

        using var reader = connection.CreateCommand("select g from t order by id").ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(explicitGuid, reader.GetGuid(0));
        Assert.IsTrue(reader.Read());
        var second = reader.GetGuid(0);
        Assert.IsTrue(reader.Read());
        var third = reader.GetGuid(0);
        Assert.IsGreaterThan(second, third);
    }

    [TestMethod]
    public void NewSequentialId_BareSelect_RaisesMsg302()
    {
        var simulation = new Simulation();
        var ex = Assert.Throws<DbException>(() => simulation.ExecuteScalar("select newsequentialid()"));
        Assert.AreEqual("The newsequentialid() built-in function can only be used in a DEFAULT expression for a column of type 'uniqueidentifier' in a CREATE TABLE or ALTER TABLE statement. It cannot be combined with other operators to form a complex scalar expression.", ex.Message);
        Assert.AreEqual("302", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void NewSequentialId_InsertValuesList_RaisesMsg302()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (g uniqueidentifier not null)");
        var ex = Assert.Throws<DbException>(() => simulation.ExecuteScalar("insert into t (g) values (newsequentialid())"));
        Assert.AreEqual("302", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void NewSequentialId_WithArguments_RaisesMsg302()
    {
        var simulation = new Simulation();
        var ex = Assert.Throws<DbException>(() =>
            simulation.ExecuteNonQuery("create table t (g uniqueidentifier not null default newsequentialid(1))"));
        Assert.AreEqual("302", ex.Data["HelpLink.EvtID"]);
    }
}
