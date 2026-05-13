using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the <c>SCHEMA_NAME([id])</c>, <c>OBJECT_NAME(object_id [, db_id])</c>,
/// and <c>OBJECT_SCHEMA_NAME(object_id [, db_id])</c> scalar functions — the
/// id-to-name inverses of <c>SCHEMA_ID</c> / <c>OBJECT_ID</c>. Probed against
/// SQL Server 2025 (2026-05-13): result type is <c>sysname</c>; NULL argument
/// or unknown id returns NULL; <c>SCHEMA_NAME()</c> no-arg form returns the
/// caller's default schema (<c>dbo</c>); the optional <c>database_id</c>
/// argument on <c>OBJECT_*</c> is accepted-and-ignored (single-database
/// simulator).
/// </summary>
[TestClass]
public sealed class SchemaNameTests
{
    [TestMethod]
    public void SchemaName_Dbo_ReturnsDbo()
        => AreEqual("dbo", new Simulation().ExecuteScalar("select schema_name(1)"));

    [TestMethod]
    public void SchemaName_Sys_ReturnsSys()
        => AreEqual("sys", new Simulation().ExecuteScalar("select schema_name(4)"));

    [TestMethod]
    public void SchemaName_InformationSchema_ReturnsInformationSchema()
        => AreEqual("INFORMATION_SCHEMA", new Simulation().ExecuteScalar("select schema_name(3)"));

    [TestMethod]
    public void SchemaName_UserSchema_RoundTripsThroughSchemaId()
        => AreEqual("audit", new Simulation().ExecuteScalar("create schema audit; select schema_name(schema_id('audit'))"));

    [TestMethod]
    public void SchemaName_NoArg_ReturnsDbo()
        => AreEqual("dbo", new Simulation().ExecuteScalar("select schema_name()"));

    [TestMethod]
    public void SchemaName_NullArg_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select schema_name(NULL)"));

    [TestMethod]
    public void SchemaName_MissingId_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select schema_name(99999)"));

    [TestMethod]
    public void SchemaName_NegativeId_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select schema_name(-1)"));

    [TestMethod]
    public void ObjectName_ExistingTable_ReturnsLeafName()
        => AreEqual("foo", new Simulation().ExecuteScalar("create table foo (id int); select object_name(object_id('foo'))"));

    [TestMethod]
    public void ObjectName_NullArg_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select object_name(NULL)"));

    [TestMethod]
    public void ObjectName_MissingId_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select object_name(99999)"));

    [TestMethod]
    public void ObjectName_WithDbIdArg_IgnoresArg()
        => AreEqual("foo", new Simulation().ExecuteScalar("create table foo (id int); select object_name(object_id('foo'), 1)"));

    [TestMethod]
    public void ObjectName_View_Works()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (id int)", "create view v as select id from t");
        AreEqual("v", simulation.ExecuteScalar("select object_name(object_id('v'))"));
    }

    [TestMethod]
    public void ObjectName_TableType_ResolvableViaTypeTableObjectId()
        => AreEqual("MyType", new Simulation().ExecuteScalar("create type MyType as table (id int); select object_name(type_table_object_id) from sys.table_types where name = 'MyType'"));

    [TestMethod]
    public void ObjectSchemaName_DefaultSchemaTable_ReturnsDbo()
        => AreEqual("dbo", new Simulation().ExecuteScalar("create table foo (id int); select object_schema_name(object_id('foo'))"));

    [TestMethod]
    public void ObjectSchemaName_QualifiedSchemaTable_ReturnsSchema()
        => AreEqual("audit", new Simulation().ExecuteScalar("create schema audit; create table audit.t (id int); select object_schema_name(object_id('audit.t'))"));

    [TestMethod]
    public void ObjectSchemaName_NullArg_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select object_schema_name(NULL)"));

    [TestMethod]
    public void ObjectSchemaName_MissingId_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select object_schema_name(99999)"));

    [TestMethod]
    public void ObjectSchemaName_WithDbIdArg_IgnoresArg()
        => AreEqual("dbo", new Simulation().ExecuteScalar("create table foo (id int); select object_schema_name(object_id('foo'), 1)"));

    [TestMethod]
    public void RowConstructorIn_RejectedWithMatchingMsg4145()
        => new Simulation().AssertSqlError(
            "create table t (a int, b int); select * from t where (a, b) in ((1, 2))",
            4145,
            "An expression of non-boolean type specified in a context where a condition is expected, near ','.");
}
