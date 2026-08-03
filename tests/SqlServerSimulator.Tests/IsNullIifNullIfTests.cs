using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

[TestClass]
public sealed class IsNullIifNullIfTests
{
    [TestMethod]
    public void IsNull_NonNullFirst_ReturnsFirst()
    {
        AreEqual(5, ExecuteScalar<int>("select isnull(5, 99)"));
        AreEqual("hello", ExecuteScalar("select isnull('hello', 'world')"));
    }

    [TestMethod]
    public void IsNull_NullFirst_ReturnsSecondCoercedToFirstType()
    {
        AreEqual(42, ExecuteScalar<int>("select isnull(cast(null as int), '42')"));
        AreEqual(99, ExecuteScalar<int>("select isnull(cast(null as int), 99)"));
    }

    [TestMethod]
    public void IsNull_NullFirst_NonNumericString_RaisesMsg245()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select isnull(cast(null as int), 'abc')"));
        StartsWith("Conversion failed when converting the varchar value 'abc'", ex.Message);
    }

    [TestMethod]
    public void IsNull_BothNull_ReturnsTypedNull()
    {
        AreEqual(DBNull.Value, ExecuteScalar("select isnull(cast(null as int), cast(null as bigint))"));
        AreEqual(DBNull.Value, ExecuteScalar("select isnull(null, null)"));
    }

    [TestMethod]
    public void IsNull_OneArg_RaisesMsg174()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select isnull(5)"));
        AreEqual("The isnull function requires 2 argument(s).", ex.Message);
        AreEqual("174", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void IsNull_ThreeArgs_RaisesMsg174()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select isnull(null, null, 1)"));
        AreEqual("The isnull function requires 2 argument(s).", ex.Message);
        AreEqual("174", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void IsNull_FirstWins_NoCoercionOfFallback()
    {
        // 5 is non-null, so the second arg's parse is skipped — even though
        // 'invalid' would fail to convert to int, it's never reached.
        AreEqual(5, ExecuteScalar<int>("select isnull(cast(5 as int), cast('invalid' as varchar(20)))"));
    }

    // EF Core emits this shape constantly: SUM over an empty filter returns
    // NULL; ISNULL substitutes a default.
    [TestMethod]
    public void IsNull_AggregateFallback()
        => AreEqual(0, new Simulation().ExecuteScalar<int>("""
            create table t (v int);
            select isnull(sum(v), 0) from t where 1=0
            """));

    [TestMethod]
    public void IsNull_Nested()
    {
        AreEqual(5, ExecuteScalar<int>("select isnull(isnull(cast(null as int), cast(null as int)), 5)"));
    }

    [TestMethod]
    public void Iif_TrueBranch()
    {
        AreEqual("yes", ExecuteScalar("select iif(1=1, 'yes', 'no')"));
        AreEqual(5, ExecuteScalar<int>("select iif(1=1, 5, 99)"));
    }

    [TestMethod]
    public void Iif_FalseBranch()
    {
        AreEqual("no", ExecuteScalar("select iif(1=2, 'yes', 'no')"));
        AreEqual(99, ExecuteScalar<int>("select iif(1=2, 5, 99)"));
    }

    [TestMethod]
    public void Iif_UnknownConditionRoutesToFalse()
    {
        // UNKNOWN-condition CASE-equivalent semantics: not-true → ELSE arm.
        AreEqual("else", ExecuteScalar("select iif(null=1, 'eq', 'else')"));
        AreEqual("b", ExecuteScalar("select iif(cast(null as bit) = 1, 'a', 'b')"));
    }

    [TestMethod]
    public void Iif_TypePromotion()
    {
        // int + decimal arms → decimal result.
        var result = ExecuteScalar("select iif(1=1, cast(5 as int), cast(5.5 as decimal(10,2)))");
        AreEqual(5m, result);
        var resultFalse = ExecuteScalar("select iif(1=2, cast(5 as int), cast(5.5 as decimal(10,2)))");
        AreEqual(5.5m, resultFalse);
    }

    [TestMethod]
    public void Iif_NullArm_PropagatesNull()
    {
        AreEqual(DBNull.Value, ExecuteScalar("select iif(1=1, null, 5)"));
        AreEqual(5, ExecuteScalar<int>("select iif(1=2, null, 5)"));
    }

    [TestMethod]
    public void Iif_FromTableRow()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, name varchar(20));
            insert t (id, name) values (1, 'a'), (-1, 'b')
            """);
        using var reader = sim.ExecuteReader("select iif(id > 0, name, 'none') from t order by id");
        IsTrue(reader.Read());
        AreEqual("none", reader.GetString(0));
        IsTrue(reader.Read());
        AreEqual("a", reader.GetString(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Iif_TwoArgs_RaisesSyntax()
    {
        _ = Throws<DbException>(() => ExecuteScalar("select iif(1=1, 'a')"));
    }

    [TestMethod]
    public void Iif_FourArgs_RaisesSyntax()
    {
        _ = Throws<DbException>(() => ExecuteScalar("select iif(1=1, 'a', 'b', 'c')"));
    }

    [TestMethod]
    public void NullIf_Equal_ReturnsNull()
    {
        AreEqual(DBNull.Value, ExecuteScalar("select nullif(5, 5)"));
        AreEqual(DBNull.Value, ExecuteScalar("select nullif('abc', 'abc')"));
    }

    [TestMethod]
    public void NullIf_NotEqual_ReturnsFirst()
    {
        // The int literal narrows to tinyint — NULLIF's own rule, covered in
        // NullIfLiteralNarrowingTests.
        AreEqual((byte)5, ExecuteScalar<byte>("select nullif(5, 3)"));
        AreEqual("abc", ExecuteScalar("select nullif('abc', 'def')"));
    }

    [TestMethod]
    public void NullIf_FirstNull_ReturnsNull()
    {
        // a is NULL → equality is UNKNOWN → ELSE branch returns a (NULL).
        AreEqual(DBNull.Value, ExecuteScalar("select nullif(cast(null as int), 5)"));
    }

    [TestMethod]
    public void NullIf_SecondNull_ReturnsFirst()
    {
        // a non-null, b NULL → equality is UNKNOWN → ELSE branch returns a
        // (narrowed to tinyint, as any int literal in that slot is).
        AreEqual((byte)5, ExecuteScalar<byte>("select nullif(5, cast(null as int))"));
    }

    [TestMethod]
    public void NullIf_TypeMix_PromotedEquality_ReturnsNull()
    {
        // 5 (int) compared against 5.0 (decimal) — equality holds via promote;
        // result is NULL, typed as int (first-arg type).
        AreEqual(DBNull.Value, ExecuteScalar("select nullif(cast(5 as int), cast(5.0 as decimal(10,2)))"));
    }

    [TestMethod]
    public void NullIf_TypeMix_NotEqual_ReturnsFirstAtFirstType()
    {
        // 5 (int) != 5.5 (decimal) — returns 5 as int.
        AreEqual(5, ExecuteScalar<int>("select nullif(cast(5 as int), cast(5.5 as decimal(10,2)))"));
    }

    [TestMethod]
    public void NullIf_OneArg_RaisesSyntax()
    {
        _ = Throws<DbException>(() => ExecuteScalar("select nullif(5)"));
    }

    [TestMethod]
    public void NullIf_ThreeArgs_RaisesSyntax()
    {
        _ = Throws<DbException>(() => ExecuteScalar("select nullif(1, 2, 3)"));
    }

    // === Msg 8133 via IIF: IIF desugars to CASE so all-bare-NULL arms ===
    // share the CASE wording. Probed against SQL Server 2025 (2026-05-11).

    [TestMethod]
    public void Iif_BothArmsBareNull_Msg8133()
        => AssertSqlError("select iif(1=1, null, null)", 8133,
            "At least one of the result expressions in a CASE specification must be an expression other than the NULL constant.");

    [TestMethod]
    public void Iif_BothArmsBareNullParenWrapped_Msg8133()
        => AssertSqlError("select iif(1=1, (null), (null))", 8133);

    [TestMethod]
    public void Iif_OneArmTypedNull_Accepted()
    {
        // A typed NULL on one arm satisfies the rule.
        AreEqual(DBNull.Value, ExecuteScalar("select iif(1=1, null, cast(null as int))"));
    }

    [TestMethod]
    public void Iif_OneArmTyped_OneArmBareNull_Accepted()
    {
        // One typed arm satisfies Msg 8133. Int both sides because the
        // simulator currently types bare NULL as int (cross-family
        // varchar+null promotion is a pre-existing fidelity gap orthogonal
        // to Msg 8133).
        AreEqual(7, ExecuteScalar("select iif(1=1, 7, null)"));
    }

    // EF Core emits NULLIF for safe-divide: a / NULLIF(b, 0).
    [TestMethod]
    public void NullIf_FromTableRow_EfCoreSafeDividePattern()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (a int, b int);
            insert t (a, b) values (10, 2), (10, 0)
            """);
        using var reader = sim.ExecuteReader("select a / nullif(b, 0) from t order by b");
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
        IsTrue(reader.Read());
        AreEqual(5, reader.GetInt32(0));
        IsFalse(reader.Read());
    }
}
