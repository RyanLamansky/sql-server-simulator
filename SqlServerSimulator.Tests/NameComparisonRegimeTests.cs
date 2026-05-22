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

    // ===== CS-database fidelity: regime 1 sites flip case-sensitivity =====
    // Probe-confirmed (2026-05-21): under a case-sensitive database
    // collation (SQL_Latin1_General_CP1_CS_AS), reserved-name checks,
    // system-proc dispatch, and CLR-type-prefix lookups all follow the
    // database collation. Width-folding still applies (IgnoreWidth stays
    // on under CS_AS — no _WS_ suffix). True system tokens routed through
    // BuiltInToken (INSERTED / DELETED / OBJECT_ID type filter /
    // sp_addextendedproperty arg names) stay invariant — those have
    // dedicated regime-1 coverage in the section above.

    private static Simulation CsCollation()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("ALTER DATABASE simulated COLLATE SQL_Latin1_General_CP1_CS_AS");
        return sim;
    }

    [TestMethod]
    public void CsDatabase_CreateSchemaUppercaseDBO_PassesReservedCheck()
    {
        // The reserved-name check follows database collation: under CS,
        // `DBO` doesn't case-equal the reserved `dbo`, so the check
        // passes (the schema-creation attempt then proceeds). The
        // Schemas dict's construction-time CI comparer doesn't rebuild
        // on ALTER COLLATE (documented fidelity gap on
        // <see cref="Database.Schemas"/>), so the TryAdd collides with
        // the pre-seeded `dbo` and raises Msg 2714 "already exists"
        // instead. Surfacing Msg 2714 (rather than Msg 2760 "reserved")
        // confirms the reserved-name check now follows DB collation.
        var ex = CsCollation().AssertSqlError("CREATE SCHEMA DBO", 2714);
        Assert.Contains("already an object", ex.Message);
    }

    [TestMethod]
    public void CsDatabase_CreateSchemaCanonicalDbo_StillRejectedAsReserved()
        => _ = CsCollation().AssertSqlError("CREATE SCHEMA dbo", 2760);

    [TestMethod]
    public void CsDatabase_CreateSchemaFullwidthSys_StillRejectedAsReserved()
    {
        // Width-folding stays on under CS_AS (no _WS_ suffix), so ｓys
        // folds to sys and matches the reserved set.
        _ = CsCollation().AssertSqlError("CREATE SCHEMA ｓys", 2760);
    }

    /// <summary>
    ///`SP_EXECUTESQL` doesn't case-equal `sp_executesql` under CS, so
    ///the dispatch falls through to generic user-proc lookup which
    ///also misses → Msg 2812 "Could not find stored procedure".
    /// </summary>
    [TestMethod]
    public void CsDatabase_SystemProcSpExecuteSql_UppercaseCase_NotFound()
        => _ = CsCollation().AssertSqlError("EXEC SP_EXECUTESQL N'select 1'", 2812);

    /// <summary>
    /// Probe-confirmed: ｓp_executesql width-folds to sp_executesql
    /// under CS_AS (IgnoreWidth stays on), so dispatch still routes
    /// through the simulator's sp_executesql handler.
    /// </summary>
    [TestMethod]
    public void CsDatabase_SystemProcSpExecuteSql_FullwidthCase_Dispatches()
        => AreEqual(1, CsCollation().ExecuteScalar("EXEC ｓp_executesql N'select 1'"));

    /// <summary>
    /// hierarchyid:: is the canonical lowercase form; HIERARCHYID
    /// doesn't case-equal it under CS, so the static-call dispatch
    /// misses and the parser raises a syntax error.
    /// </summary>
    [TestMethod]
    public void CsDatabase_HierarchyIdTypePrefix_UppercaseHIERARCHYID_NotResolved()
        => _ = Throws<Exception>(() => CsCollation().ExecuteScalar(
            "SELECT HIERARCHYID::GetRoot()"));

    [TestMethod]
    public void CsDatabase_HierarchyIdTypePrefix_CanonicalCase_Resolves()
        => AreEqual("/", CsCollation().ExecuteScalar(
            "SELECT hierarchyid::GetRoot().ToString()"));

    // ===== Simulation.ServerCollationName seeds new-database collation =====
    // Mirrors SQL Server's model.collation role: setting it before the
    // first CreateDbConnection / ImportBacpac makes the lazy-seeded
    // "simulated" database (and any subsequent bacpac import that
    // doesn't declare its own collation) inherit the value. Equivalent
    // end state to ALTER DATABASE COLLATE for the simulated DB, but
    // chosen up-front rather than as a post-hoc adjustment.

    [TestMethod]
    public void ServerCollationName_DefaultIsClassicCi()
        => AreEqual("SQL_Latin1_General_CP1_CI_AS", new Simulation().ServerCollationName);

    [TestMethod]
    public void ServerCollationName_SetToRecognizedCs_Persists()
    {
        var sim = new Simulation { ServerCollationName = "SQL_Latin1_General_CP1_CS_AS" };
        AreEqual("SQL_Latin1_General_CP1_CS_AS", sim.ServerCollationName);
    }

    [TestMethod]
    public void ServerCollationName_Unrecognized_Throws()
    {
        var ex = Throws<ArgumentException>(() => new Simulation { ServerCollationName = "Not_A_Real_Collation" });
        Assert.Contains("not recognized", ex.Message);
    }

    [TestMethod]
    public void ServerCollationName_Null_Throws()
        => _ = Throws<ArgumentNullException>(() => new Simulation { ServerCollationName = null! });

    [TestMethod]
    public void ServerCollationName_SeedsLazySimulatedDatabase()
    {
        var sim = new Simulation { ServerCollationName = "SQL_Latin1_General_CP1_CS_AS" };
        AreEqual("SQL_Latin1_General_CP1_CS_AS",
            sim.ExecuteScalar("SELECT collation_name FROM sys.databases WHERE name = N'simulated'"));
    }

    [TestMethod]
    public void ServerCollationName_SeededDatabase_CreateSchemaDBO_CoexistsWithDbo()
    {
        // Probe-confirmed on real SQL Server CS database (2026-05-22):
        // CREATE SCHEMA DBO succeeds on a CS-collation DB — both `dbo`
        // and `DBO` end up in sys.schemas as distinct entries. The
        // ALTER-DATABASE-COLLATE variant (CsDatabase_CreateSchemaUppercaseDBO_…)
        // hits Msg 2714 instead because the Schemas dict's comparer
        // was built CI at construction time and doesn't rebuild on
        // ALTER (documented fidelity gap). With ServerCollationName
        // seeded up-front, the dict is CS from the start and the gap
        // doesn't apply — the simulator matches real SQL Server's
        // coexistence behavior.
        var sim = new Simulation { ServerCollationName = "SQL_Latin1_General_CP1_CS_AS" };
        _ = sim.ExecuteNonQuery("CREATE SCHEMA DBO");
        AreEqual(2, sim.ExecuteScalar("SELECT COUNT(*) FROM sys.schemas WHERE name IN (N'dbo', N'DBO')"));
    }

}
