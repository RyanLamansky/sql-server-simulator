using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Forces each scalar function's <c>GetSqlType</c> override to be invoked
/// by placing it inside a UNION branch — set-op type promotion calls
/// <c>SqlType.Promote</c> across all branches' projection types, which
/// reads <c>GetSqlType</c> on each. Without these queries, scalar
/// functions in plain <c>SELECT</c>s never have their static type read
/// (the runtime <c>Run</c> path just returns a typed value directly).
/// </summary>
[TestClass]
public sealed class ScalarFunctionTypingTests
{
    /// <summary>
    /// Each row is a SQL expression; pairing it in a UNION ALL with a
    /// typed-NULL of matching kind lights up the function's GetSqlType
    /// during set-op type promotion. The returned scalar (from the second
    /// branch's first row) is just used to assert the query parses + runs.
    /// </summary>
    [TestMethod]
    [DataRow("cast(null as int)", "charindex('b', 'abc')")]
    [DataRow("cast(null as int)", "convert(int, '42')")]
    [DataRow("cast(null as bigint)", "datalength('abc')")]
    [DataRow("cast(null as int)", "datepart(year, cast('2024-01-01' as date))")]
    [DataRow("cast(null as varchar(10))", "left('abcdef', 3)")]
    [DataRow("cast(null as varchar(10))", "right('abcdef', 3)")]
    [DataRow("cast(null as varchar(10))", "reverse('abc')")]
    [DataRow("cast(null as varchar(10))", "trim('  abc  ')")]
    public void ScalarFunctionInUnionBranch_TypePromotionRunsGetSqlType(string typedNull, string expression)
    {
        // Pair with a typed NULL in the first branch: Promote computes the
        // common type by calling GetSqlType on both sides; matching kinds
        // keeps the cross-category-promotion path out of scope.
        var sql = $"select {typedNull} union all select {expression}";
        var v = ExecuteScalar(sql);
        _ = IsInstanceOfType<DBNull>(v);
    }

    [TestMethod]
    public void NewId_InUnionBranch_TypePromotionRunsGetSqlType()
    {
        // newid() returns uniqueidentifier; pair with cast(null as uniqueidentifier).
        _ = IsInstanceOfType<DBNull>(ExecuteScalar(
            "select cast(null as uniqueidentifier) union all select newid()"));
    }

    [TestMethod]
    public void IdentCurrent_InUnionBranch_TypePromotionRunsGetSqlType()
    {
        // IDENT_CURRENT and SCOPE_IDENTITY both return numeric (decimal/int);
        // pair with a typed NULL of matching shape.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int identity, v int)");
        _ = sim.ExecuteNonQuery("insert t (v) values (1)");
        _ = IsInstanceOfType<DBNull>(sim.ExecuteScalar(
            "select cast(null as decimal(38,0)) union all select ident_current('t')"));
    }

    [TestMethod]
    public void ScopeIdentity_InUnionBranch_TypePromotionRunsGetSqlType()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int identity, v int)");
        _ = sim.ExecuteNonQuery("insert t (v) values (1)");
        _ = IsInstanceOfType<DBNull>(sim.ExecuteScalar(
            "select cast(null as decimal(38,0)) union all select scope_identity()"));
    }

    [TestMethod]
    public void TranCount_InUnionBranch_TypePromotionRunsGetSqlType() =>
        _ = IsInstanceOfType<DBNull>(ExecuteScalar(
            "select cast(null as int) union all select @@trancount"));

    /// <summary>
    /// `(expr)` wraps in Parenthesized; in a UNION branch its GetSqlType is read.
    /// </summary>
    [TestMethod]
    public void Parenthesized_InUnionBranch_TypePromotionRunsGetSqlType() =>
        _ = IsInstanceOfType<DBNull>(ExecuteScalar(
            "select cast(null as int) union all select (1 + 2)"));

    [TestMethod]
    public void Trim_IntegerOperand_RaisesMsg8116()
        => new Simulation().AssertSqlError(
            "select trim(cast(1 as int))",
            8116,
            "Argument data type int is invalid for argument 1 of Trim function.");

    [TestMethod]
    public void Substring_IntegerFirstArg_RaisesMsg8116()
        => new Simulation().AssertSqlError(
            "select substring(cast(12345 as int), 2, 2)",
            8116,
            "Argument data type int is invalid for argument 1 of substring function.");

    [TestMethod]
    public void Charindex_IntegerNeedle_RaisesMsg8116()
        => new Simulation().AssertSqlError(
            "select charindex(cast(2 as int), 'abc')",
            8116,
            "Argument data type int is invalid for argument 1 of charindex function.");

    /// <summary>
    /// Integer haystack implicit-coerces to varchar (probe-confirmed
    /// against real 2026-05-22: CHARINDEX('2', 12345) = 2). Needle stays
    /// strict — see Charindex_IntegerNeedle_RaisesMsg8116 above.
    /// </summary>
    [TestMethod]
    public void Charindex_IntegerHaystack_ImplicitCoercesToVarchar() =>
        AreEqual(2, ExecuteScalar("select charindex('2', cast(12345 as int))"));

    // Note: LEN(text) / LEN(ntext) Msg 8116 — the existing CLAUDE.md "Not modeled"
    // entry covers this. The simulator's IsStringCategory treats text/ntext as
    // strings so they slip past the LEN gate and return a length. Adding a
    // dedicated reject-list there is a separate bundle; this test exercises
    // the binary / image path that does flow through the elevation.
    [TestMethod]
    public void Len_ImageOperand_RaisesMsg8116()
        => new Simulation().AssertSqlError(
            "select len(cast(0x010203 as image))",
            8116,
            "Argument data type image is invalid for argument 1 of len function.");
}
