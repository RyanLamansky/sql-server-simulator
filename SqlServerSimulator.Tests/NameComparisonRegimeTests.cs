using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Captures the three name-comparison regimes real SQL Server uses, all
/// probed against SQL Server 2025 (2026-05-18):
/// <list type="bullet">
///   <item>
///     Regime 1 — identifier resolution under the database collation
///     (CI + width-insensitive, accent-sensitive). Fullwidth Latin
///     characters fold to halfwidth. Applies to catalog views, schema
///     names, type-prefixes in static calls, system-proc names, proc
///     parameter names, the INSERTED/DELETED pseudo-tables, OBJECT_ID's
///     typeFilter argument, extended-property level-type / level-name
///     arguments.
///   </item>
///   <item>
///     Regime 2 — T-SQL grammar tokens (parser-level, ASCII-only
///     case-insensitive). Fullwidth Latin REJECTED. Applies to reserved
///     keywords, table hints, SET option keywords, schema-element
///     keywords (CONTENT, COLLECTION, USING, CASCADE, MEMBER, CLEAR,
///     ACCENT_SENSITIVITY, BOUNDING_BOX/GRIDS/LOW, etc.).
///   </item>
///   <item>
///     Regime 3 — CLR reflection (strictly ordinal, case-SENSITIVE).
///     Applies to instance + static method dispatch on hierarchyid /
///     geography / geometry / xml. <c>getlevel</c>, <c>VALUE</c>,
///     <c>starea</c> all raise method-not-found errors on real SQL Server.
///   </item>
/// </list>
/// Tests exercise bare-identifier fullwidth Latin where the regime is
/// identifier resolution, and string-literal fullwidth where the input is
/// a quoted argument value. Supplementary-plane characters (surrogate
/// pairs) are rejected at the tokenizer entry in both real SQL Server
/// (Msg 102 near <c>0xd835</c>) and the simulator — by design.
/// </summary>
[TestClass]
public sealed class NameComparisonRegimeTests
{
    // ===== Regime 1: identifier resolution — fullwidth ACCEPTED =====

    [TestMethod]
    public void Regime1_CatalogView_FullwidthSchema_Resolves()
        => IsGreaterThanOrEqualTo(0, (int)new Simulation().ExecuteScalar(
            "select count(*) from ｓys.tables")!);

    [TestMethod]
    public void Regime1_CatalogView_FullwidthObject_Resolves()
        => IsGreaterThanOrEqualTo(0, (int)new Simulation().ExecuteScalar(
            "select count(*) from sys.ｔables")!);

    [TestMethod]
    public void Regime1_InformationSchema_FullwidthSchema_Resolves()
        => IsGreaterThanOrEqualTo(0, (int)new Simulation().ExecuteScalar(
            "select count(*) from ｉnformation_schema.tables")!);

    [TestMethod]
    public void Regime1_ObjectIdTypeFilter_FullwidthU_Resolves()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.regime1_obj (id int)");
        AreNotEqual(DBNull.Value, sim.ExecuteScalar("select object_id('dbo.regime1_obj', 'Ｕ')"));
    }

    [TestMethod]
    public void Regime1_ObjectIdTypeFilter_FullwidthUWidthFoldsToU()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.regime1_obj2 (id int)");
        AreEqual(
            sim.ExecuteScalar("select object_id('dbo.regime1_obj2', 'U')"),
            sim.ExecuteScalar("select object_id('dbo.regime1_obj2', 'Ｕ')"));
    }

    [TestMethod]
    public void Regime1_ReservedSchemaName_FullwidthSys_RejectedAsReserved()
        => new Simulation().AssertSqlError("create schema ｓys", 2760);

    [TestMethod]
    public void Regime1_TriggerBody_FullwidthInsertedReference_Resolves()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.regime1_t (id int)",
            "create trigger dbo.regime1_tr on dbo.regime1_t after insert as " +
            "insert into dbo.regime1_t (id) select id + 1000 from ｉnserted");
        _ = sim.ExecuteNonQuery("insert dbo.regime1_t values (1)");
        AreEqual(2, sim.ExecuteScalar("select count(*) from dbo.regime1_t"));
        AreEqual(1001, sim.ExecuteScalar("select id from dbo.regime1_t where id >= 1000"));
    }

    [TestMethod]
    public void Regime1_ExtendedProperty_FullwidthSchemaLevelType_TargetsSchema()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(
            "exec sp_addextendedproperty @name=N'p', @value=N'v', " +
            "@level0type=N'ｓchema', @level0name=N'dbo'");
        AreEqual("v", sim.ExecuteScalar(
            "select cast(value as nvarchar(100)) from sys.extended_properties where name='p'"));
    }

    [TestMethod]
    public void Regime1_SystemProcName_FullwidthSpAddextendedproperty_Dispatches()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(
            "exec ｓp_addextendedproperty @name=N'p2', @value=N'v2', " +
            "@level0type=N'SCHEMA', @level0name=N'dbo'");
        AreEqual("v2", sim.ExecuteScalar(
            "select cast(value as nvarchar(100)) from sys.extended_properties where name='p2'"));
    }

    [TestMethod]
    public void Regime1_OutputClause_FullwidthInsertedQualifier_Resolves()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.regime1_o (id int, v int)");
        AreEqual(42, sim.ExecuteScalar(
            "insert dbo.regime1_o output ｉnserted.v values (1, 42)"));
    }

    // ===== Regime 2: parser grammar tokens — fullwidth REJECTED =====

    [TestMethod]
    public void Regime2_TableHint_UnknownName_UnrecognizedHint()
        => new Simulation().AssertSqlError(
            "select * from sys.tables with (BOGUS)", 321);

    [TestMethod]
    public void Regime2_TableHint_FullwidthNolock_UnrecognizedHint()
        => new Simulation().AssertSqlError(
            "select * from sys.tables with (Ｎolock)", 321);

    [TestMethod]
    public void Regime2_SetLockTimeout_UnknownOption_UnrecognizedSetOption()
        => new Simulation().AssertSqlError("set BOGUS_OPTION 1000", 195);

    [TestMethod]
    public void Regime2_SetLockTimeout_FullwidthOption_UnrecognizedSetOption()
        => new Simulation().AssertSqlError("set ｌock_timeout 1000", 195);

    // ===== Regime 3: CLR reflection — case-SENSITIVE =====

    [TestMethod]
    public void Regime3_HierarchyIdMethod_LowercaseGetlevel_NotResolved()
        => _ = Throws<Exception>(() => new Simulation().ExecuteScalar(
            "select hierarchyid::GetRoot().getlevel()"));

    [TestMethod]
    public void Regime3_HierarchyIdMethod_UppercaseGETLEVEL_NotResolved()
        => _ = Throws<Exception>(() => new Simulation().ExecuteScalar(
            "select hierarchyid::GetRoot().GETLEVEL()"));

    [TestMethod]
    public void Regime3_HierarchyIdMethod_CanonicalGetLevel_Resolves()
        => AreEqual((short)0, new Simulation().ExecuteScalar(
            "select hierarchyid::GetRoot().GetLevel()"));

    [TestMethod]
    public void Regime3_HierarchyIdStatic_LowercaseGetroot_NotResolved()
        => _ = Throws<Exception>(() => new Simulation().ExecuteScalar(
            "select hierarchyid::getroot()"));

    [TestMethod]
    public void Regime3_SpatialMethod_CanonicalToString_Resolves()
        => AreEqual("POINT (0 0)", new Simulation().ExecuteScalar(
            "select geography::Point(0, 0, 4326).ToString()"));

    [TestMethod]
    public void Regime3_SpatialMethod_LowercaseTostring_NotResolved()
        => _ = Throws<Exception>(() => new Simulation().ExecuteScalar(
            "select geography::Point(0, 0, 4326).tostring()"));

    [TestMethod]
    public void Regime3_SpatialStatic_LowercasePoint_NotResolved()
        => _ = Throws<Exception>(() => new Simulation().ExecuteScalar(
            "select cast(geography::point(0, 0, 4326) as varchar(50))"));
}
