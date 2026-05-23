using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>SOUNDEX(s)</c>, <c>DIFFERENCE(a, b)</c>, and
/// <c>STR(num, [length, [decimals]])</c> — the remaining String-category
/// scalars. SOUNDEX is the standard 4-character English phonetic code;
/// DIFFERENCE returns 0-4 comparing two SOUNDEX codes; STR formats a
/// float right-aligned in a fixed-width string.
/// </summary>
[TestClass]
public sealed class SoundexStrTests
{
    [TestMethod]
    public void Soundex_Smith_S530()
        => AreEqual("S530", new Simulation().ExecuteScalar("select soundex('Smith')"));

    [TestMethod]
    public void Soundex_Smyth_S530()
        => AreEqual("S530", new Simulation().ExecuteScalar("select soundex('Smyth')"));

    [TestMethod]
    public void Soundex_Williams_W452()
        => AreEqual("W452", new Simulation().ExecuteScalar("select soundex('Williams')"));

    [TestMethod]
    public void Soundex_Empty_AllZeros()
        => AreEqual("0000", new Simulation().ExecuteScalar("select soundex('')"));

    [TestMethod]
    public void Soundex_Null_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select soundex(cast(null as varchar(10)))"));

    [TestMethod]
    public void Difference_SmithSmyth_Returns4()
        => AreEqual(4, new Simulation().ExecuteScalar("select difference('Smith', 'Smyth')"));

    [TestMethod]
    public void Difference_NullSide_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select difference(cast(null as varchar(10)), 'foo')"));

    [TestMethod]
    public void Str_DefaultArgs_Width10NoDecimals()
        => AreEqual("       123", new Simulation().ExecuteScalar("select str(123.456)"));

    [TestMethod]
    public void Str_WithDecimals_RoundsHalfUp()
        => AreEqual("123.46", new Simulation().ExecuteScalar("select str(123.456, 6, 2)"));

    [TestMethod]
    public void Str_WithZeroDecimals_RoundsToInt()
        => AreEqual("   123", new Simulation().ExecuteScalar("select str(123.456, 6, 0)"));

    [TestMethod]
    public void Str_Null_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select str(cast(null as float))"));
}
