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

    // ----- Constraint object ids -----

    /// <summary>
    /// Seeds a table carrying one constraint of each family and returns the
    /// simulation; each constraint's object id is reachable through
    /// <c>sys.objects</c>'s <c>parent_object_id</c> link.
    /// </summary>
    private static Simulation Constrained()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (a int not null primary key);
            create table t (
                k int not null constraint pk_t primary key,
                u int not null constraint uq_t unique,
                c int null constraint ck_t check (c > 0),
                d int null constraint df_t default 1,
                f int null constraint fk_t references p (a))
            """);
        return sim;
    }

    /// <summary>
    /// Reads <paramref name="property"/> for the named constraint, taking its
    /// id from <paramref name="catalogView"/> — <c>OBJECT_ID</c> doesn't
    /// resolve a constraint name, and a DEFAULT constraint's id lives in
    /// <c>sys.default_constraints</c> rather than <c>sys.objects</c>.
    /// </summary>
    private static string ConstraintProperty(string constraintName, string property, string catalogView = "sys.objects") =>
        $"select objectproperty((select object_id from {catalogView} where name = '{constraintName}'), '{property}')";

    /// <summary>
    /// A CHECK or DEFAULT constraint answers a constant 0 — not the creating
    /// session's setting, which is 0 even for one created under
    /// <c>QUOTED_IDENTIFIER</c> ON (probe-confirmed both ways against SQL
    /// Server 2025, and uniformly 0 across msdb's shipped constraints).
    /// </summary>
    [TestMethod]
    public void IsQuotedIdentOn_OnCheckConstraint_Returns0()
        => AreEqual(0, Constrained().ExecuteScalar(ConstraintProperty("ck_t", "IsQuotedIdentOn")));

    [TestMethod]
    public void IsQuotedIdentOn_OnDefaultConstraint_Returns0()
        => AreEqual(0, Constrained().ExecuteScalar(ConstraintProperty("df_t", "IsQuotedIdentOn", "sys.default_constraints")));

    /// <summary>The key and foreign-key families answer NULL instead.</summary>
    [TestMethod]
    [DataRow("pk_t")]
    [DataRow("uq_t")]
    [DataRow("fk_t")]
    public void IsQuotedIdentOn_OnKeyOrForeignKeyConstraint_ReturnsNull(string constraintName)
        => AreEqual(DBNull.Value, Constrained().ExecuteScalar(ConstraintProperty(constraintName, "IsQuotedIdentOn")));

    /// <summary>
    /// The capture is constant, so a constraint created under
    /// <c>QUOTED_IDENTIFIER OFF</c> answers the same 0.
    /// </summary>
    [TestMethod]
    public void IsQuotedIdentOn_OnCheckConstraintCreatedUnderOff_Returns0()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("set quoted_identifier off; create table t (c int constraint ck_t check (c > 0))");
        AreEqual(0, sim.ExecuteScalar(ConstraintProperty("ck_t", "IsQuotedIdentOn")));
    }

    /// <summary>
    /// Every object-kind discriminator answers 0 for a constraint — it is a
    /// resolvable object, just none of those kinds — as do IsMSShipped,
    /// IsEncrypted and IsSystemTable (probe-confirmed).
    /// </summary>
    [TestMethod]
    [DataRow("IsTable")]
    [DataRow("IsView")]
    [DataRow("IsProcedure")]
    [DataRow("IsUserTable")]
    [DataRow("IsTrigger")]
    [DataRow("IsScalarFunction")]
    [DataRow("IsEncrypted")]
    [DataRow("IsMSShipped")]
    [DataRow("IsSystemTable")]
    public void ObjectKindProperties_OnConstraint_Return0(string property)
        => AreEqual(0, Constrained().ExecuteScalar(ConstraintProperty("ck_t", property)));

    /// <summary>The module-scoped and table-scoped names answer NULL.</summary>
    [TestMethod]
    [DataRow("IsAnsiNullsOn")]
    [DataRow("IsSchemaBound")]
    [DataRow("IsDeterministic")]
    [DataRow("ExecIsQuotedIdentOn")]
    [DataRow("TableHasPrimaryKey")]
    public void ModuleAndTableProperties_OnConstraint_ReturnNull(string property)
        => AreEqual(DBNull.Value, Constrained().ExecuteScalar(ConstraintProperty("ck_t", property)));

    /// <summary>
    /// <c>OBJECTPROPERTYEX</c> answers a constraint the same way the plain form
    /// does (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void ObjectPropertyEx_OnCheckConstraint_MatchesThePlainForm()
        => AreEqual(0, Constrained().ExecuteScalar(
            "select objectpropertyex((select object_id from sys.objects where name = 'ck_t'), 'IsQuotedIdentOn')"));
}
