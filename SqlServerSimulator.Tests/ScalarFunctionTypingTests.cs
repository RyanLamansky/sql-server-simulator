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
}
