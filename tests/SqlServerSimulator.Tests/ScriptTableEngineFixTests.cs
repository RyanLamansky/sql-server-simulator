using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Engine-behavior fixes the SSMS "Script Table as → CREATE To" barrage
/// surfaced against a non-baseline-collation database (WWI's
/// <c>Latin1_General_100_CI_AS</c>): CAST/CONVERT-to-string result collation,
/// two-coercible-default operand resolution, the <c>catalog_default</c> /
/// <c>database_default</c> pseudo-collations, and untyped-NULL CASE typing.
/// Behaviors probed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ScriptTableEngineFixTests
{
    // === CASE result-type resolution treats a bare NULL literal as typeless ===

    [TestMethod]
    public void Case_TypedThen_NullElse_YieldsTypedResult()
        => AreEqual("hello", new Simulation().ExecuteScalar("select case when 1 = 1 then N'hello' else null end"));

    [TestMethod]
    public void Case_NullThen_TypedElse_YieldsTypedResult()
        => AreEqual("x", new Simulation().ExecuteScalar("select case when 1 = 0 then null else 'x' end"));

    [TestMethod]
    public void Case_NullElse_DoesNotForceIntConversion()
        => AreEqual("hello", new Simulation().ExecuteScalar(
            "create table t (id int not null primary key); insert t values (1); select case when 1 = 1 then N'hello' else null end from t"));

    [TestMethod]
    public void Case_AllNullResults_StillRaisesMsg8133()
        => new Simulation().AssertSqlError("select case when 1 = 1 then null else null end", 8133);

    // === CAST / CONVERT to a character type carries the database collation ===
    // (non-baseline database so a bug's baseline result would raise Msg 457/468).

    [TestMethod]
    public void CastIntToVarchar_ConcatWithLiteral_NoCollationConflict()
        => AreEqual("a1", new Simulation { ServerCollationName = "Latin1_General_100_CI_AS" }
            .ExecuteScalar("select 'a' + cast(1 as varchar(10))"));

    [TestMethod]
    public void ConvertIntToVarchar_ComparedWithLiteral_NoCollationConflict()
        => AreEqual(1, new Simulation { ServerCollationName = "Latin1_General_100_CI_AS" }
            .ExecuteScalar<int>("select case when convert(varchar(10), 1) = '1' then 1 else 0 end"));

    // === Two coercible-default operands with different collations resolve to
    // the database default rather than raising Msg 468 — a baseline-collated
    // system-function result meeting a database-collation literal. ===

    [TestMethod]
    public void CoercibleDefaultOperands_DifferentCollations_NoConflict()
        => AreEqual(1, new Simulation { ServerCollationName = "Latin1_General_100_CI_AS" }
            .ExecuteScalar<int>("select case when DATABASEPROPERTYEX(db_name(), 'Updateability') = 'READ_WRITE' then 1 else 0 end"));

    // === catalog_default / database_default pseudo-collations ===

    [TestMethod]
    public void CatalogDefault_PseudoCollation_Resolves()
        => AreEqual(1, new Simulation().ExecuteScalar<int>("select case when 'a' collate catalog_default = 'A' then 1 else 0 end"));

    [TestMethod]
    public void DatabaseDefault_PseudoCollation_Resolves()
        => AreEqual(1, new Simulation().ExecuteScalar<int>("select case when 'a' collate database_default = 'A' then 1 else 0 end"));

    [TestMethod]
    public void UnknownCollation_StillRaisesMsg448()
        => new Simulation().AssertSqlError("select 'a' collate not_a_real_collation", 448);
}
