using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>a IS [NOT] DISTINCT FROM b</c> — SQL Server 2022 NULL-safe
/// comparison. Unlike <c>=</c>/<c>&lt;&gt;</c> (which return UNKNOWN when
/// either operand is NULL), the DISTINCT-FROM family treats NULL as a
/// comparable value: two NULLs are not distinct, one NULL plus a non-NULL is
/// distinct. Probed against SQL Server 2025 (2026-05-13).
/// </summary>
[TestClass]
public sealed class DistinctFromTests
{
    [TestMethod]
    [DataRow("5 is distinct from 5", 0)]
    [DataRow("5 is distinct from 6", 1)]
    [DataRow("cast(null as int) is distinct from 5", 1)]
    [DataRow("5 is distinct from cast(null as int)", 1)]
    [DataRow("cast(null as int) is distinct from cast(null as int)", 0)]
    [DataRow("5 is not distinct from 5", 1)]
    [DataRow("5 is not distinct from 6", 0)]
    [DataRow("cast(null as int) is not distinct from 5", 0)]
    [DataRow("cast(null as int) is not distinct from cast(null as int)", 1)]
    public void Basic(string predicate, int expectedCount) =>
        AreEqual(expectedCount,
            new Simulation().ExecuteReader($"select 1 where {predicate}").EnumerateRecords().Count());

    [TestMethod]
    public void TypePromotion_IntVsDecimal_NumericallyEqual()
    {
        // 5 (int) vs 5.0 (decimal) — values numerically equal; not distinct.
        AreEqual(0,
            new Simulation().ExecuteReader(
                "select 1 where 5 is distinct from cast(5.0 as decimal(5,2))").EnumerateRecords().Count());
        AreEqual(1,
            new Simulation().ExecuteReader(
                "select 1 where 5 is distinct from cast(5.5 as decimal(5,2))").EnumerateRecords().Count());
    }

    [TestMethod]
    public void TypePromotion_VarcharVsNvarchar_Equal()
    {
        AreEqual(0,
            new Simulation().ExecuteReader(
                "select 1 where 'hello' is distinct from cast('hello' as nvarchar(10))").EnumerateRecords().Count());
    }

    [TestMethod]
    public void CaseWhenContext()
    {
        // Probe-confirmed: works in CASE-WHEN predicate position.
        AreEqual("yes",
            new Simulation().ExecuteScalar(
                "select case when 5 is distinct from null then 'yes' else 'no' end"));
    }

    [TestMethod]
    public void MultiRowWhere_DistinctFromPicksUpNullEdges()
    {
        using var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("""
            create table dt (id int, a int, b int);
            insert dt values (1, 5, 5), (2, 5, null), (3, null, 5), (4, null, null), (5, 5, 6)
            """).ExecuteNonQuery();
        using var reader = conn.CreateCommand(
            "select id from dt where a is distinct from b order by id").ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        // Distinct rows: (5,null), (null,5), (5,6). Not-distinct: (5,5), (null,null).
        CollectionAssert.AreEqual(new int[] { 2, 3, 5 }, ids);
    }

    [TestMethod]
    public void MultiRowWhere_NotDistinctFromIncludesBothNullRow()
    {
        using var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("""
            create table dt (id int, a int, b int);
            insert dt values (1, 5, 5), (2, 5, null), (3, null, 5), (4, null, null), (5, 5, 6)
            """).ExecuteNonQuery();
        using var reader = conn.CreateCommand(
            "select id from dt where a is not distinct from b order by id").ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        // Probe-confirmed: NULL=NULL counts as a match here (the key
        // difference from regular `=`).
        CollectionAssert.AreEqual(new int[] { 1, 4 }, ids);
    }

    [TestMethod]
    public void JoinOn_NullSafeJoin()
    {
        using var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("""
            create table j1 (id int, v int);
            create table j2 (id int, v int);
            insert j1 values (1, 5), (2, null), (3, 10);
            insert j2 values (1, 5), (2, null), (3, 11)
            """).ExecuteNonQuery();
        using var reader = conn.CreateCommand(
            "select j1.id from j1 inner join j2 on j1.v is not distinct from j2.v order by j1.id").ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        // Probe-confirmed: NULL-safe join matches rows where both v are NULL;
        // regular `=` would exclude that pair.
        CollectionAssert.AreEqual(new int[] { 1, 2 }, ids);
    }

    [TestMethod]
    public void TypeMismatch_SurfacesUnderlyingConversionError()
    {
        // Promotion routes through CompareValuesPromoted; string→int conversion
        // fails with Msg 245 (same as regular `=` would).
        var ex = Throws<DbException>(() =>
            new Simulation().ExecuteScalar("select 1 where 'hello' is distinct from 5"));
        AreEqual("245", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void MissingFromKeyword_SyntaxError()
    {
        // `IS DISTINCT rhs` without FROM → syntax error at rhs.
        var ex = Throws<DbException>(() =>
            new Simulation().ExecuteScalar("select 1 where 5 is distinct 5"));
        AreEqual("102", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void RegressionIsNullStillWorks()
    {
        // ParseIsSuffix took over from ParseIsNullSuffix; verify the IS NULL /
        // IS NOT NULL branches still route correctly. Existing IsNullExpression
        // tests in other suites cover broader semantics — this is a smoke
        // test for the refactor.
        AreEqual(1, new Simulation().ExecuteReader(
            "select 1 where cast(null as int) is null").EnumerateRecords().Count());
        AreEqual(0, new Simulation().ExecuteReader(
            "select 1 where 5 is null").EnumerateRecords().Count());
        AreEqual(1, new Simulation().ExecuteReader(
            "select 1 where 5 is not null").EnumerateRecords().Count());
        AreEqual(0, new Simulation().ExecuteReader(
            "select 1 where cast(null as int) is not null").EnumerateRecords().Count());
    }
}
