using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Where the simulator's <c>decimal</c> backing type shows through. SQL Server
/// carries 38 significant digits and .NET's <see cref="decimal"/> carries
/// 28-29, so a value real represents happily can have no representation here —
/// a gap, not a SQL Server behavior, which is why every path that reaches it
/// names the ceiling rather than claiming real refused the statement.
/// <para>
/// The split is probed (SQL Server 2025, 2026-08-05): real converts a 30-digit
/// text into <c>decimal(38, 0)</c> and multiplies its way to 29 digits, and it
/// is SqlClient that then refuses to hand the value to a .NET caller; a
/// 40-digit text is real's own Msg 8115, and a 30-digit one into
/// <c>decimal(20, 0)</c> likewise.
/// </para>
/// </summary>
[TestClass]
public sealed class DecimalCeilingTests
{
    private static NotSupportedException Ceiling(string commandText)
    {
        var ex = Throws<NotSupportedException>(() => new Simulation().ExecuteScalar(commandText));
        Assert.Contains("28 significant digits", ex.Message);
        return ex;
    }

    [TestMethod]
    public void LiteralPastTheCeiling_NamesIt()
        => Assert.Contains("reading the literal", Ceiling("select 123456789012345678901234567890").Message);

    [TestMethod]
    public void StringConversionPastTheCeiling_NamesIt()
        => Assert.Contains(
            "converting the string",
            Ceiling("select cast('123456789012345678901234567890' as decimal(38, 0))").Message);

    [TestMethod]
    public void TryCastPastTheCeiling_RaisesRatherThanReturningNull()
        => Ceiling("select try_cast('123456789012345678901234567890' as decimal(38, 0))");

    [TestMethod]
    public void ArithmeticPastTheCeiling_NamesTheComputation()
        => Assert.Contains(
            "computing",
            Ceiling("select cast(10000000000000000000000000000 as decimal(38, 0)) * 10").Message);

    [TestMethod]
    public void ScaledStoragePastTheCeiling_NamesTheStore()
        => Assert.Contains(
            "storing",
            Ceiling("select cast('1234567890123456789012345678.9' as decimal(38, 10))").Message);

    [TestMethod]
    public void RunningTotalPastTheCeiling_NamesTheAccumulation()
        => Assert.Contains("accumulating", Ceiling("""
            create table s1 (v decimal(38, 0));
            insert s1 values (cast(50000000000000000000000000000 as decimal(38, 0))),
                             (cast(50000000000000000000000000000 as decimal(38, 0)));
            select sum(v) from s1
            """).Message);

    // --- What real refuses on its own terms, and still does ---

    /// <summary>Wider than <c>numeric</c>'s own 38 digits is real's Msg 1007.</summary>
    [TestMethod]
    public void LiteralPastNumericsOwnMaximum_Msg1007()
        => new Simulation().AssertSqlError("select 1234567890123456789012345678901234567890", 1007);

    [TestMethod]
    public void StringPastNumericsOwnMaximum_Msg8115()
        => new Simulation().AssertSqlError(
            "select cast('1234567890123456789012345678901234567890' as decimal(38, 0))",
            8115,
            "Arithmetic overflow error converting varchar to data type numeric.");

    [TestMethod]
    public void StringWiderThanTheDeclaredTarget_Msg8115()
        => new Simulation().AssertSqlError(
            "select cast('123456789012345678901234567890' as decimal(20, 0))",
            8115,
            "Arithmetic overflow error converting varchar to data type numeric.");

    /// <summary>Scientific notation real can't read is Msg 8114, not an overflow.</summary>
    [TestMethod]
    public void ScientificNotationPastRange_Msg8114()
        => new Simulation().AssertSqlError("select cast('1e40' as decimal(38, 0))", 8114);

    [TestMethod]
    public void NonNumericText_Msg8114()
        => new Simulation().AssertSqlError("select cast('abc' as decimal(38, 0))", 8114);

    // --- Below the ceiling, everything still converts ---

    [TestMethod]
    public void TwentyNineDigitsAtItsNaturalScale_Converts()
        => AreEqual(
            1234567890123456789012345678.9m,
            new Simulation().ExecuteScalar("select cast('1234567890123456789012345678.9' as decimal(38, 1))"));

    [TestMethod]
    public void WideDeclaredScale_KeepsTheValue()
        => AreEqual(1.50000000000000000000m, new Simulation().ExecuteScalar("select cast(1.5 as decimal(38, 20))"));
}
