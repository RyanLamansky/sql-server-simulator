using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// SQL Server sizes an <c>int</c>-typed integer <b>literal</b> in
/// <c>NULLIF</c>'s first slot down to the narrowest integer type that holds
/// its value, so <c>NULLIF(60, 76)</c> is <c>tinyint</c> where a bare
/// <c>SELECT 60</c> is <c>int</c>. Probed against SQL Server 2025 through
/// <c>sys.dm_exec_describe_first_result_set</c> and through
/// <c>SELECT … INTO</c>, whose destination column is declared at the narrowed
/// type.
/// </summary>
/// <remarks>
/// The rule is <c>NULLIF</c>'s alone: the <c>CASE</c> it is defined as, and
/// every sibling value-selecting form, leave the same literal at <c>int</c> —
/// which is why this can't ride the shared branch-promotion seam. It reads the
/// first argument alone; the second contributes nothing at all.
/// </remarks>
[TestClass]
public sealed class NullIfLiteralNarrowingTests
{
    /// <remarks>
    /// The decimal-family rows read <c>decimal</c> because that is what this
    /// client surface answers for both names — the narrowing under test is the
    /// integer-width one, not the decimal-vs-numeric split.
    /// </remarks>
    private static string DeclaredType(string expression)
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand($"select {expression} as v").ExecuteReader();
        _ = reader.Read();
        return reader.GetDataTypeName(0);
    }

    [TestMethod]
    [DataRow("nullif(0, 300)", "tinyint")]
    [DataRow("nullif(1, 2)", "tinyint")]
    [DataRow("nullif(60, 76)", "tinyint")]
    [DataRow("nullif(128, 1)", "tinyint")]
    [DataRow("nullif(255, 1)", "tinyint")]
    [DataRow("nullif(-1, 1)", "smallint")]
    [DataRow("nullif(-3, 78)", "smallint")]
    [DataRow("nullif(-128, 1)", "smallint")]
    [DataRow("nullif(256, 1)", "smallint")]
    [DataRow("nullif(300, 4)", "smallint")]
    [DataRow("nullif(32767, 1)", "smallint")]
    [DataRow("nullif(-32768, 1)", "smallint")]
    [DataRow("nullif(32768, 1)", "int")]
    [DataRow("nullif(-32769, 1)", "int")]
    [DataRow("nullif(65535, 1)", "int")]
    [DataRow("nullif(99999999, 4)", "int")]
    [DataRow("nullif(2147483647, 1)", "int")]
    [DataRow("nullif(-2147483648, 1)", "int")]
    public void FirstArgumentIntegerLiteral_NarrowsToTheSmallestTypeHoldingIt(string expression, string expected) =>
        AreEqual(expected, DeclaredType(expression));

    [TestMethod]
    [DataRow("nullif(60, 76.0)")]
    [DataRow("nullif(60, null)")]
    [DataRow("nullif(1, 2147483648)")]
    [DataRow("nullif(1, 99999)")]
    [DataRow("nullif(60, cast(76 as int))")]
    [DataRow("nullif(60, cast(76 as bigint))")]
    public void SecondArgument_DoesNotParticipate(string expression) =>
        AreEqual("tinyint", DeclaredType(expression));

    [TestMethod]
    [DataRow("nullif((60), 76)")]
    [DataRow("nullif(+60, 76)")]
    [DataRow("nullif(-(-60), 76)")]
    [DataRow("nullif(007, 1)")]
    [DataRow("nullif(nullif(60, 76), 4)")]
    public void LiteralIsSeenThroughTheWrappersRealSeesThrough(string expression) =>
        AreEqual("tinyint", DeclaredType(expression));

    [TestMethod]
    [DataRow("nullif(cast(60 as int), 76)", "int")]
    [DataRow("nullif(60 + 0, 76)", "int")]
    [DataRow("nullif(cast(60 as tinyint), 76)", "tinyint")]
    [DataRow("nullif(cast(60 as bigint), 76)", "bigint")]
    [DataRow("nullif(cast(60 as real), 76)", "real")]
    [DataRow("nullif(60.0, 76)", "decimal")]
    [DataRow("nullif(2147483648, 1)", "decimal")]
    [DataRow("nullif(-2147483649, 1)", "decimal")]
    [DataRow("nullif('a', 'b')", "varchar")]
    public void OnlyAWrittenIntLiteralNarrows(string expression, string expected) =>
        AreEqual(expected, DeclaredType(expression));

    [TestMethod]
    [DataRow("coalesce(60, 76)")]
    [DataRow("isnull(60, 76)")]
    [DataRow("iif(1 = 2, null, 60)")]
    [DataRow("choose(1, 60)")]
    [DataRow("greatest(60, 1)")]
    [DataRow("least(60, 1)")]
    [DataRow("case when 60 = 76 then null else 60 end")]
    public void SiblingValueSelectingForms_LeaveTheLiteralAtInt(string expression) =>
        AreEqual("int", DeclaredType(expression));

    [TestMethod]
    public void NarrowedResult_CarriesTheNarrowedRuntimeValue()
    {
        AreEqual((byte)60, new Simulation().ExecuteScalar<byte>("select nullif(60, 76)"));
        AreEqual((short)-3, new Simulation().ExecuteScalar<short>("select nullif(-3, 78)"));
        AreEqual((short)300, new Simulation().ExecuteScalar<short>("select nullif(300, 4)"));
        AreEqual(99999999, new Simulation().ExecuteScalar<int>("select nullif(99999999, 4)"));
    }

    [TestMethod]
    public void EqualArguments_ReturnNullAtTheNarrowedType()
    {
        AreEqual("tinyint", DeclaredType("nullif(60, 60)"));
        AreEqual("smallint", DeclaredType("nullif(300, 300)"));
        AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select nullif(60, 60)"));
    }

    [TestMethod]
    public void NarrowedResult_ReachesSelectIntoAndTheRowEncoder()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("select nullif(60, 76) as v into d").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select v from d").ExecuteReader();
        _ = reader.Read();
        AreEqual("tinyint", reader.GetDataTypeName(0));
        AreEqual((byte)60, reader.GetValue(0));
    }

    [TestMethod]
    public void NarrowedResult_FlowsIntoArithmetic()
    {
        // tinyint + tinyint stays tinyint on real; against an int literal the
        // pair promotes to int as any tinyint would.
        AreEqual("tinyint", DeclaredType("nullif(60, 76) + nullif(60, 76)"));
        AreEqual("int", DeclaredType("nullif(60, 76) + 1"));
    }

    [TestMethod]
    public void NonLiteralFirstArgument_KeepsTheColumnType()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (100)").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select nullif(a, 60) as v from t").ExecuteReader();
        _ = reader.Read();
        AreEqual("int", reader.GetDataTypeName(0));
        AreEqual(100, reader.GetValue(0));
    }

    [TestMethod]
    public void LiteralFirstArgumentAgainstAColumn_StillNarrows()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (100)").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select nullif(60, a) as v from t").ExecuteReader();
        _ = reader.Read();
        AreEqual("tinyint", reader.GetDataTypeName(0));
        AreEqual((byte)60, reader.GetValue(0));
    }

    [TestMethod]
    public void NarrowedResult_SurvivesAnAggregate()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1)").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select max(nullif(60, 76)) as v from t").ExecuteReader();
        _ = reader.Read();
        AreEqual("tinyint", reader.GetDataTypeName(0));
    }

    [TestMethod]
    public void NarrowedTinyint_RejectsNothingTheColumnWouldAccept()
    {
        // A narrowed value is an ordinary tinyint downstream: it inserts into a
        // tinyint column without a conversion and compares against one.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a tinyint)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (nullif(60, 76))").ExecuteNonQuery();
        AreEqual((byte)60, connection.CreateCommand("select a from t").ExecuteScalar());
    }

    [TestMethod]
    public void NarrowedResult_ReportsThroughADerivedTable()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand("select v from (select nullif(300, 4) as v) s").ExecuteReader();
        _ = reader.Read();
        AreEqual("smallint", reader.GetDataTypeName(0));
    }
}
