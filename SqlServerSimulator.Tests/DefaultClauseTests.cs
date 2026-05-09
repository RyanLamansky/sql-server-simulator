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
        => Assert.AreEqual("unset", new Simulation().ExecuteScalar("""
            create table t (id int, label nvarchar(20) not null default N'unset');
            insert t (id) values (1);
            select label from t
            """));

    [TestMethod]
    public void Default_DoesNotFireWhenColumnIsListed()
        => Assert.AreEqual("explicit", new Simulation().ExecuteScalar("""
            create table t (id int, label nvarchar(20) not null default N'unset');
            insert t (id, label) values (1, N'explicit');
            select label from t
            """));

    // SQL Server treats an explicit NULL in the VALUES list as a real value, not an
    // "omit and use default" — defaults only fire when the column itself is absent
    // from the column list.
    [TestMethod]
    public void Default_ExplicitNullOverridesDefault()
        => Assert.AreEqual(DBNull.Value, new Simulation().ExecuteScalar("""
            create table t (id int, label nvarchar(20) null default N'unset');
            insert t (id, label) values (1, null);
            select label from t
            """));

    // Real SQL Server accepts column constraints in any order; common EF Core output
    // is `NOT NULL DEFAULT (...)`, so this ordering must parse cleanly.
    [TestMethod]
    public void Default_OrderingFlexible_NotNullThenDefault()
        => Assert.AreEqual("a", new Simulation().ExecuteScalar("""
            create table t (id int, label nvarchar(20) not null default N'a');
            insert t (id) values (1);
            select label from t
            """));

    [TestMethod]
    public void Default_OrderingFlexible_DefaultThenNotNull()
        => Assert.AreEqual("a", new Simulation().ExecuteScalar("""
            create table t (id int, label nvarchar(20) default N'a' not null);
            insert t (id) values (1);
            select label from t
            """));

    [TestMethod]
    public void Default_ParenthesizedExpression()
        => Assert.AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int, n int not null default (1 + 2));
            insert t (id) values (1);
            select n from t
            """));

    [TestMethod]
    public void Default_NewIdProducesDistinctValues()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int, g uniqueidentifier not null default newid());
            insert t (id) values (1);
            insert t (id) values (2)
            """);
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
        _ = simulation.ExecuteNonQuery("""
            create table t (id int, g uniqueidentifier not null default newsequentialid());
            insert t (id) values (0), (1), (2), (3), (4), (5), (6), (7), (8), (9)
            """);

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("select g from t order by id").ExecuteReader();
        var guids = new List<Guid>();
        while (reader.Read())
            guids.Add(reader.GetGuid(0));

        Assert.HasCount(10, guids);
        for (var i = 1; i < guids.Count; i++)
            Assert.IsGreaterThan(guids[i - 1], guids[i], $"Guid at {i} was not greater than its predecessor");
    }

    // If the user supplies an explicit value for the GUID column, the default
    // expression must not fire — otherwise NEWSEQUENTIALID would burn a counter
    // slot per ignored row.
    [TestMethod]
    public void NewSequentialId_DoesNotAdvanceWhenColumnExplicitlyProvided()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, g uniqueidentifier not null default newsequentialid())");
        var explicitGuid = Guid.NewGuid();

        using var connection = simulation.CreateOpenConnection();
        using (var insert = connection.CreateCommand("insert t (id, g) values (1, @g)", ("@g", explicitGuid)))
            _ = insert.ExecuteNonQuery();
        _ = simulation.ExecuteNonQuery("""
            insert t (id) values (2);
            insert t (id) values (3)
            """);

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
        var ex = Assert.Throws<DbException>(() => new Simulation().ExecuteScalar("select newsequentialid()"));
        Assert.AreEqual("The newsequentialid() built-in function can only be used in a DEFAULT expression for a column of type 'uniqueidentifier' in a CREATE TABLE or ALTER TABLE statement. It cannot be combined with other operators to form a complex scalar expression.", ex.Message);
        Assert.AreEqual("302", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void NewSequentialId_InsertValuesList_RaisesMsg302()
        => _ = new Simulation().AssertSqlError("""
            create table t (g uniqueidentifier not null);
            insert t (g) values (newsequentialid())
            """, 302);

    [TestMethod]
    public void NewSequentialId_WithArguments_RaisesMsg302()
        => _ = new Simulation().AssertSqlError(
            "create table t (g uniqueidentifier not null default newsequentialid(1))", 302);
}
