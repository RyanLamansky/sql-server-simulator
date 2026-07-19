using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the <c>CHOOSE(index, val1, val2, ...)</c> scalar function — the
/// 1-based variadic picker. Result type is the joint promotion of the value
/// arms (CASE-style branch-type unification); out-of-range and NULL index
/// both return typed NULL.
/// </summary>
[TestClass]
public sealed class ChooseTests
{
    [TestMethod]
    public void Choose_FirstValue_ReturnsFirst()
        => AreEqual("a", new Simulation().ExecuteScalar("select choose(1, 'a', 'b', 'c')"));

    [TestMethod]
    public void Choose_MiddleValue_ReturnsThat()
        => AreEqual("b", new Simulation().ExecuteScalar("select choose(2, 'a', 'b', 'c')"));

    [TestMethod]
    public void Choose_LastValue_ReturnsLast()
        => AreEqual("c", new Simulation().ExecuteScalar("select choose(3, 'a', 'b', 'c')"));

    [TestMethod]
    public void Choose_IndexZero_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select choose(0, 'a', 'b', 'c')"));

    [TestMethod]
    public void Choose_IndexNegative_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select choose(-1, 'a', 'b', 'c')"));

    [TestMethod]
    public void Choose_IndexBeyondList_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select choose(4, 'a', 'b', 'c')"));

    [TestMethod]
    public void Choose_NullIndex_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select choose(cast(null as int), 'a', 'b', 'c')"));

    [TestMethod]
    public void Choose_PickedValueIsNull_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select choose(2, 'a', cast(null as varchar(10)), 'c')"));

    [TestMethod]
    public void Choose_IntegerValues_ReturnsInt()
        => AreEqual(20, new Simulation().ExecuteScalar("select choose(2, 10, 20, 30)"));

    [TestMethod]
    public void Choose_MixedTypes_PromotesToHigher()
        => AreEqual(20m, new Simulation().ExecuteScalar("select choose(2, cast(10 as decimal(10, 2)), 20, 30)"));

    [TestMethod]
    public void Choose_OnlyIndexNoValues_RaisesMsg174()
        => new Simulation().AssertSqlError("select choose(1)", 174);

    [TestMethod]
    public void Choose_FromTableValues_RoundTrips()
        => AreEqual("medium", new Simulation().ExecuteScalar("""
            create table sizes (id int);
            insert sizes values (2);
            select choose(id, 'small', 'medium', 'large') from sizes
            """));

    [TestMethod]
    public void Choose_StringIndexCoerced_Works()
        => AreEqual("b", new Simulation().ExecuteScalar("select choose('2', 'a', 'b', 'c')"));
}
