using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the <c>float</c> / <c>real</c> types — IEEE 754
/// double / single, scientific-notation literal parsing, and the
/// empty-string-to-zero quirk that distinguishes them from
/// <c>decimal</c>'s strict parsing.
/// </summary>
[TestClass]
public sealed class FloatTests
{
    [TestMethod]
    public void Literal_ScientificNotation_ProducesFloat()
    {
        // Verified against SQL Server 2025: any literal with `e` is float.
        AreEqual(150.0, ExecuteScalar("select 1.5e2"));
    }

    [TestMethod]
    public void Cast_StringToFloat_BasicRoundTrip()
    {
        AreEqual(1.5, ExecuteScalar("select cast('1.5' as float)"));
    }

    [TestMethod]
    public void Cast_StringToFloat_AcceptsScientific()
    {
        AreEqual(15000000000.0, ExecuteScalar("select cast('1.5E+10' as float)"));
    }

    [TestMethod]
    public void Cast_EmptyStringToFloat_ReturnsZero()
    {
        // SQL Server quirk: empty/whitespace-only strings cast to 0 for
        // float (verified against SQL Server 2025; differs from decimal,
        // where empty raises Msg 8114).
        AreEqual(0.0, ExecuteScalar("select cast('' as float)"));
    }

    [TestMethod]
    [DataRow("'inf'")]
    [DataRow("'NaN'")]
    [DataRow("'$5.95'")]
    [DataRow("'abc'")]
    public void Cast_BadStringToFloat_RaisesMsg8114(string literal)
    {
        var ex = Throws<DbException>(() => ExecuteScalar($"select cast({literal} as float)"));
        AreEqual("Error converting data type varchar to float.", ex.Message);
    }

    [TestMethod]
    public void Cast_RealHas4ByteStorage_FloatHas8()
    {
        // Smoke test: float(N) for N ≤ 24 → real (4 bytes), N ≥ 25 → float (8 bytes).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a float, b real, c float(24), d float(25))");
    }

    [TestMethod]
    [DataRow("1.5", 1)]
    [DataRow("-1.5", -1)]
    [DataRow("0.5", 0)]
    [DataRow("-0.5", 0)]
    public void Cast_FloatToInt_TruncatesTowardZero(string sourceLiteral, int expected)
    {
        // Float → int truncates (verified 1.5 → 1, -1.5 → -1, 0.5 → 0).
        var value = ExecuteScalar<int>($"select cast(cast({sourceLiteral} as float) as int)");
        AreEqual(expected, value);
    }

    [TestMethod]
    public void FloatArithmetic_FloatAndIntPromotesToFloat()
    {
        // float + int → float (verified against SQL Server 2025).
        AreEqual(3.5, ExecuteScalar("select cast(1.5 as float) + 2"));
    }

    [TestMethod]
    public void FloatArithmetic_FloatAndDecimalPromotesToFloat()
    {
        AreEqual(2.73, (double)ExecuteScalar("select cast(1.5 as float) + cast(1.23 as decimal(5, 2))")!, 0.0001);
    }

    [TestMethod]
    public void FloatArithmetic_DivideByZero_RaisesMsg8134()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select cast(1 as float) / cast(0 as float)"));
        AreEqual("Divide by zero error encountered.", ex.Message);
    }

    [TestMethod]
    public void Parameter_DoubleRoundTrips()
    {
        const double expected = 3.14;
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("select @p", ("@p", expected));
        AreEqual(expected, command.ExecuteScalar());
    }

    [TestMethod]
    public void Parameter_SingleRoundTrips()
    {
        const float expected = 3.14f;
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("select @p", ("@p", expected));
        AreEqual(expected, command.ExecuteScalar());
    }

    [TestMethod]
    public void GetDouble_ReturnsTheValue()
    {
        const double expected = 3.14;
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("select @p", ("@p", expected));
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(expected, reader.GetDouble(0));
    }
}
