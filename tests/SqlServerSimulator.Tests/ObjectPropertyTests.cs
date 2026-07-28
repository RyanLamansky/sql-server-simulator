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

    /// <summary>
    /// IsEncrypted is module-scoped: 0 for a module, NULL for a table
    /// (probe-confirmed). DacFx's encrypted-procedure enumeration filters
    /// `IsEncrypted = 1 OR IsEncrypted IS NULL`, so the NULL-for-unknown
    /// fallback enrolled every procedure as encrypted and failed bacpac
    /// export with SQL71564 on all 42 WWI procedures.
    /// </summary>
    [TestMethod]
    public void IsEncrypted_ModuleReturns0_TableReturnsNull()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table t (id int)", "create procedure p as select 1", "create view v as select 1 x");
        AreEqual(0, sim.ExecuteScalar("select objectproperty(object_id('p'), 'IsEncrypted')"));
        AreEqual(0, sim.ExecuteScalar("select objectproperty(object_id('v'), 'IsEncrypted')"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select objectproperty(object_id('t'), 'IsEncrypted')"));
    }

    /// <summary>
    /// The module SET-option snapshot pair returns 1 for modules (every
    /// simulator module is created under QUOTED_IDENTIFIER / ANSI_NULLS ON)
    /// and NULL for non-modules — probe-confirmed (view → 1/1, table → NULL).
    /// DacFx's view reverse-engineering reads CONVERT(bit, ...) over both.
    /// </summary>
    [TestMethod]
    public void ExecIsOptions_ModuleReturns1_TableReturnsNull()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table t (id int)", "create view v as select 1 x");
        AreEqual(1, sim.ExecuteScalar("select objectproperty(object_id('v'), 'ExecIsQuotedIdentOn')"));
        AreEqual(1, sim.ExecuteScalar("select objectproperty(object_id('v'), 'ExecIsAnsiNullsOn')"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select objectproperty(object_id('t'), 'ExecIsQuotedIdentOn')"));
    }

    [TestMethod]
    public void IsSchemaBound_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar("create view v as select 1 x; select objectproperty(object_id('v'), 'IsSchemaBound')"));

    /// <summary>
    /// IsSystemTable is 0 for every resolvable object and NULL for an
    /// unknown id — probe-confirmed against SQL Server 2025 (table / view /
    /// proc / even sys.tables → 0). DacFx's default-constraint populator
    /// filters on <c>OBJECTPROPERTY(parent_object_id, 'IsSystemTable') = 0</c>,
    /// so a NULL here silently drops every DEFAULT constraint from a bacpac
    /// export.
    /// </summary>
    [TestMethod]
    public void IsSystemTable_ResolvableReturns0_UnknownReturnsNull()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table t (id int default 5)", "create view v as select 1 x");
        AreEqual(0, sim.ExecuteScalar("select objectproperty(object_id('t'), 'IsSystemTable')"));
        AreEqual(0, sim.ExecuteScalar("select objectproperty(object_id('v'), 'IsSystemTable')"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select objectproperty(12345678, 'IsSystemTable')"));
        AreEqual(1, sim.ExecuteScalar(
            "select count(*) from sys.default_constraints d where objectproperty(d.parent_object_id, 'IsSystemTable') = 0"));
    }

    /// <summary>
    /// Real answers the whole <c>TableHas*</c> family from the plain
    /// <c>OBJECTPROPERTY</c>, not just <c>OBJECTPROPERTYEX</c> — probed
    /// against SQL Server 2025 on this exact table shape, which returns
    /// identity 0, primary key 1, clustered 1, index 1, unique 1, check 1,
    /// foreign key 0, foreign ref 0, rowguidcol 1.
    /// </summary>
    [TestMethod]
    [DataRow("TableHasIdentity", 0)]
    [DataRow("TableHasPrimaryKey", 1)]
    [DataRow("TableHasClustIndex", 1)]
    [DataRow("TableHasIndex", 1)]
    [DataRow("TableHasUniqueCnst", 1)]
    [DataRow("TableHasCheckCnst", 1)]
    [DataRow("TableHasForeignKey", 0)]
    [DataRow("TableHasForeignRef", 0)]
    [DataRow("TableHasRowGuidCol", 1)]
    public void TableFlags_AnsweredByBothEntryPoints(string property, int expected)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table f9 (
                id int not null primary key, g uniqueidentifier rowguidcol,
                u int unique, c int check (c > 0))
            """);
        AreEqual(expected, sim.ExecuteScalar($"select objectproperty(object_id('f9'), '{property}')"));
        AreEqual(expected, sim.ExecuteScalar($"select convert(int, objectpropertyex(object_id('f9'), '{property}'))"));
    }

    /// <summary>
    /// <c>Cardinality</c> and <c>BaseType</c> are the genuinely EX-only pair:
    /// the plain form returns NULL for both on real (probe-confirmed), since
    /// neither is integer-valued.
    /// </summary>
    [TestMethod]
    [DataRow("Cardinality")]
    [DataRow("BaseType")]
    public void ExtendedOnlyProperties_AreNullFromThePlainForm(string property)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        AreEqual(DBNull.Value, sim.ExecuteScalar($"select objectproperty(object_id('t'), '{property}')"));
        AreNotEqual(DBNull.Value, sim.ExecuteScalar($"select objectpropertyex(object_id('t'), '{property}')"));
    }

    /// <summary>
    /// A non-table object answers NULL for the table family, matching real.
    /// </summary>
    [TestMethod]
    public void TableFlags_NonTableReturnsNull()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create view v as select 1 x");
        AreEqual(DBNull.Value, sim.ExecuteScalar("select objectproperty(object_id('v'), 'TableHasPrimaryKey')"));
    }
}
