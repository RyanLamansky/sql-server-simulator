using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>OBJECTPROPERTY(object_id, 'property')</c>: per-object
/// metadata flags. The simulator supports the common Is-X checks
/// (IsTable, IsView, IsProcedure, IsTrigger, IsScalarFunction,
/// IsTableFunction, IsInlineFunction, IsMSShipped, IsDeterministic,
/// IsSchemaBound). Unknown properties return NULL.
/// </summary>
[TestClass]
public sealed class ObjectPropertyTests
{
    [TestMethod]
    public void IsTable_OnTable_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar("create table t (id int); select objectproperty(object_id('t'), 'IsTable')"));

    [TestMethod]
    public void IsView_OnTable_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar("create table t (id int); select objectproperty(object_id('t'), 'IsView')"));

    [TestMethod]
    public void IsView_OnView_Returns1()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table t (id int)", "create view v as select id from t");
        AreEqual(1, sim.ExecuteScalar("select objectproperty(object_id('v'), 'IsView')"));
    }

    [TestMethod]
    public void IsProcedure_OnProcedure_Returns1()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure p as select 1");
        AreEqual(1, sim.ExecuteScalar("select objectproperty(object_id('p'), 'IsProcedure')"));
    }

    [TestMethod]
    public void UnknownObject_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select objectproperty(99999, 'IsTable')"));

    [TestMethod]
    public void UnknownProperty_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("create table t (id int); select objectproperty(object_id('t'), 'NotAProperty')"));

    [TestMethod]
    public void NullArg_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select objectproperty(null, 'IsTable')"));

    [TestMethod]
    public void IsMSShipped_OnUserTable_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar("create table t (id int); select objectproperty(object_id('t'), 'IsMSShipped')"));
}
