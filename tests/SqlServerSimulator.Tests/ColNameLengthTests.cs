using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>COL_NAME(table_id, col_id)</c> and
/// <c>COL_LENGTH(table_name, col_name)</c>: column-level metadata
/// lookups used by introspection queries and EF Core's schema
/// migration scaffolding.
/// </summary>
[TestClass]
public sealed class ColNameLengthTests
{
    [TestMethod]
    public void ColName_FirstColumn_ReturnsName()
        => AreEqual("id", new Simulation().ExecuteScalar("create table t (id int, name varchar(50)); select col_name(object_id('t'), 1)"));

    [TestMethod]
    public void ColName_SecondColumn_ReturnsName()
        => AreEqual("name", new Simulation().ExecuteScalar("create table t (id int, name varchar(50)); select col_name(object_id('t'), 2)"));

    [TestMethod]
    public void ColName_OutOfRange_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("create table t (id int); select col_name(object_id('t'), 99)"));

    [TestMethod]
    public void ColName_UnknownTable_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select col_name(99999, 1)"));

    [TestMethod]
    public void ColName_NullTableId_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select col_name(null, 1)"));

    [TestMethod]
    public void ColLength_Int_Returns4()
        => AreEqual((short)4, new Simulation().ExecuteScalar("create table t (id int); select col_length('t', 'id')"));

    [TestMethod]
    public void ColLength_Bigint_Returns8()
        => AreEqual((short)8, new Simulation().ExecuteScalar("create table t (id bigint); select col_length('t', 'id')"));

    [TestMethod]
    public void ColLength_Varchar50_Returns50()
        => AreEqual((short)50, new Simulation().ExecuteScalar("create table t (name varchar(50)); select col_length('t', 'name')"));

    [TestMethod]
    public void ColLength_Nvarchar50_Returns100Bytes()
        => AreEqual((short)100, new Simulation().ExecuteScalar("create table t (name nvarchar(50)); select col_length('t', 'name')"));

    [TestMethod]
    public void ColLength_UnknownColumn_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("create table t (id int); select col_length('t', 'missing')"));

    [TestMethod]
    public void ColLength_NullArg_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select col_length(null, 'col')"));
}
