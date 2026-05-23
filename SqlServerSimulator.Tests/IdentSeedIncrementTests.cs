using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>IDENT_SEED('table')</c> and <c>IDENT_INCR('table')</c>:
/// expose the declared <c>IDENTITY(seed, increment)</c> values for a
/// table's identity column. Both return <c>numeric(38, 0)</c>; both
/// return NULL for tables without an identity column or unknown tables.
/// Sibling of <c>IDENT_CURRENT</c>.
/// </summary>
[TestClass]
public sealed class IdentSeedIncrementTests
{
    [TestMethod]
    public void IdentSeed_DefaultIdentity_ReturnsOne()
        => AreEqual(1m, new Simulation().ExecuteScalar("create table t (id int identity primary key); select ident_seed('t')"));

    [TestMethod]
    public void IdentSeed_ExplicitSeed_ReturnsThat()
        => AreEqual(100m, new Simulation().ExecuteScalar("create table t (id int identity(100, 5) primary key); select ident_seed('t')"));

    [TestMethod]
    public void IdentIncr_DefaultIdentity_ReturnsOne()
        => AreEqual(1m, new Simulation().ExecuteScalar("create table t (id int identity primary key); select ident_incr('t')"));

    [TestMethod]
    public void IdentIncr_ExplicitIncrement_ReturnsThat()
        => AreEqual(5m, new Simulation().ExecuteScalar("create table t (id int identity(100, 5) primary key); select ident_incr('t')"));

    [TestMethod]
    public void IdentSeed_NoIdentityColumn_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("create table t (id int primary key); select ident_seed('t')"));

    [TestMethod]
    public void IdentIncr_NoIdentityColumn_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("create table t (id int primary key); select ident_incr('t')"));

    [TestMethod]
    public void IdentSeed_UnknownTable_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select ident_seed('no_such_table')"));

    [TestMethod]
    public void IdentSeed_SchemaQualified_Works()
        => AreEqual(50m, new Simulation().ExecuteScalar("create schema audit; create table audit.events (id int identity(50, 10) primary key); select ident_seed('audit.events')"));

    [TestMethod]
    public void IdentSeed_NegativeIncrement_PreservesSign()
        => AreEqual(-2m, new Simulation().ExecuteScalar("create table t (id int identity(1000, -2) primary key); select ident_incr('t')"));
}
