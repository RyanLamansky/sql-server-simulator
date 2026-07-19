using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Exercises the <c>ROWGUIDCOL</c> column marker: a uniqueidentifier-only,
/// one-per-table metadata annotation with no runtime effect (the simulator
/// doesn't model the <c>$ROWGUID</c> pseudo-column). It round-trips through
/// <c>sys.columns.is_rowguidcol</c> / <c>COLUMNPROPERTY(…, 'IsRowGuidCol')</c>
/// for BACPAC parity. Errors probed against SQL Server 2025 (2026-07-17):
/// Msg 2761 (non-uniqueidentifier) and Msg 8196 (duplicate) — both compile-time.
/// </summary>
[TestClass]
public sealed class RowGuidColTests
{
    [TestMethod]
    public void RowGuidCol_SetsSysColumnsFlag()
        => IsTrue((bool)new Simulation().ExecuteScalar("""
            create table t (id uniqueidentifier rowguidcol default newid(), x int);
            select is_rowguidcol from sys.columns where object_id = object_id('t') and name = 'id'
            """)!);

    [TestMethod]
    public void RowGuidCol_ColumnProperty_ReportsOne()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id uniqueidentifier rowguidcol, x int);
            select COLUMNPROPERTY(object_id('t'), 'id', 'IsRowGuidCol')
            """));

    [TestMethod]
    public void NonRowGuidColumn_FlagIsZero()
        => IsFalse((bool)new Simulation().ExecuteScalar("""
            create table t (id uniqueidentifier, x int);
            select is_rowguidcol from sys.columns where object_id = object_id('t') and name = 'id'
            """)!);

    [TestMethod]
    public void RowGuidCol_OnNonUniqueIdentifier_RaisesMsg2761()
    {
        var ex = new Simulation().AssertSqlError(
            "create table t (id int rowguidcol)", 2761);
        Assert.Contains("uniqueidentifier data type", ex.Message);
    }

    [TestMethod]
    public void DuplicateRowGuidCol_RaisesMsg8196()
    {
        var ex = new Simulation().AssertSqlError(
            "create table t (a uniqueidentifier rowguidcol, b uniqueidentifier rowguidcol)", 8196);
        Assert.Contains("Duplicate column specified as ROWGUIDCOL", ex.Message);
    }
}
