using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>ASCII</c> / <c>UNICODE</c> / <c>CHAR</c> / <c>NCHAR</c> char-code
/// scalars. Probe-confirmed verbatim against SQL Server 2025 (2026-05-14);
/// every test below corresponds to a probe entry.
/// </summary>
[TestClass]
public sealed class CharScalarTests
{
    [TestMethod]
    public void Ascii_Letter_ReturnsCode()
        => AreEqual(65, new Simulation().ExecuteScalar("select ASCII('A')"));

    [TestMethod]
    public void Ascii_MultiChar_ReturnsFirstByte()
        => AreEqual(65, new Simulation().ExecuteScalar("select ASCII('Abc')"));

    [TestMethod]
    public void Ascii_EmptyString_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select ASCII('')"));

    [TestMethod]
    public void Ascii_Null_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select ASCII(NULL)"));

    [TestMethod]
    public void Ascii_Space_Returns32()
        => AreEqual(32, new Simulation().ExecuteScalar("select ASCII(' ')"));

    /// <summary>
    /// N'€' is U+20AC, representable in CP1252 as 0x80 = 128.
    /// </summary>
    [TestMethod]
    public void Ascii_Cp1252Representable_Returns128()
        => AreEqual(128, new Simulation().ExecuteScalar("select ASCII(N'€')"));

    /// <summary>
    /// 'é' is U+00E9 = 233 in CP1252.
    /// </summary>
    [TestMethod]
    public void Ascii_LatinE_Returns233()
        => AreEqual(233, new Simulation().ExecuteScalar("select ASCII('é')"));

    /// <summary>
    /// ASCII(65) stringifies 65 to "65" first, then returns ASCII of '6' = 54.
    /// </summary>
    [TestMethod]
    public void Ascii_IntInput_ImplicitStringifies()
        => AreEqual(54, new Simulation().ExecuteScalar("select ASCII(65)"));

    [TestMethod]
    public void Unicode_Letter_Returns65()
        => AreEqual(65, new Simulation().ExecuteScalar("select UNICODE(N'A')"));

    [TestMethod]
    public void Unicode_EmptyString_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select UNICODE(N'')"));

    [TestMethod]
    public void Unicode_Null_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select UNICODE(NULL)"));

    [TestMethod]
    public void Unicode_EuroSign_Returns8364()
        => AreEqual(8364, new Simulation().ExecuteScalar("select UNICODE(N'€')"));

    /// <summary>
    /// 😀 (U+1F600) is the high surrogate 0xD83D = 55357 + low surrogate 0xDE00.
    /// Non-SC default collation returns the high surrogate, not the full code point.
    /// </summary>
    [TestMethod]
    public void Unicode_Supplementary_ReturnsHighSurrogate()
        => AreEqual(55357, new Simulation().ExecuteScalar("select UNICODE(N'😀')"));

    [TestMethod]
    public void Unicode_IntInput_ImplicitStringifies()
        => AreEqual(54, new Simulation().ExecuteScalar("select UNICODE(65)"));

    [TestMethod]
    public void Char_65_ReturnsA()
        => AreEqual("A", new Simulation().ExecuteScalar("select CHAR(65)"));

    [TestMethod]
    public void Char_0_ReturnsNulByte()
    {
        // CHAR(0) is a valid character — the NUL byte — not NULL. DATALENGTH
        // confirms a single byte; ASCII round-trips back to 0.
        var sim = new Simulation();
        AreEqual(1, sim.ExecuteScalar("select datalength(CHAR(0))"));
        AreEqual(0, sim.ExecuteScalar("select ASCII(CHAR(0))"));
    }

    [TestMethod]
    public void Char_256_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select CHAR(256)"));

    [TestMethod]
    public void Char_Negative_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select CHAR(-1)"));

    [TestMethod]
    public void Char_Null_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select CHAR(NULL)"));

    [TestMethod]
    public void Char_DecimalInput_TruncatesToInt()
        => AreEqual("A", new Simulation().ExecuteScalar("select CHAR(65.7)"));

    [TestMethod]
    public void Char_StringInput_ParsesAsInt()
        => AreEqual("A", new Simulation().ExecuteScalar("select CHAR('65')"));

    /// <summary>
    /// CHAR(128) maps to € under CP1252.
    /// </summary>
    [TestMethod]
    public void Char_Cp1252EuroByte_Returns()
        => AreEqual("€", new Simulation().ExecuteScalar("select CHAR(128)"));

    [TestMethod]
    public void NChar_65_ReturnsA()
        => AreEqual("A", new Simulation().ExecuteScalar("select NCHAR(65)"));

    [TestMethod]
    public void NChar_EuroCodepoint_ReturnsEuro()
        => AreEqual("€", new Simulation().ExecuteScalar("select NCHAR(8364)"));

    /// <summary>
    /// Default non-SC collation: NCHAR > 65535 returns NULL rather than
    /// emitting a surrogate pair. Documented in CLAUDE.md as a deliberate
    /// alignment with the simulator's default-collation-only stance.
    /// </summary>
    [TestMethod]
    public void NChar_Supplementary_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select NCHAR(65536)"));

    [TestMethod]
    public void NChar_Emoji_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select NCHAR(128512)"));

    [TestMethod]
    public void NChar_Negative_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select NCHAR(-1)"));

    [TestMethod]
    public void NChar_Null_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select NCHAR(NULL)"));

    [TestMethod]
    public void NChar_StringInput_ParsesAsInt()
        => AreEqual("A", new Simulation().ExecuteScalar("select NCHAR('65')"));

    [TestMethod]
    public void Char_Width_IsOneByte()
        => AreEqual(1, new Simulation().ExecuteScalar("select datalength(CHAR(65))"));

    [TestMethod]
    public void NChar_Width_IsTwoBytes()
        => AreEqual(2, new Simulation().ExecuteScalar("select datalength(NCHAR(65))"));

    [TestMethod]
    public void Char13Plus10_EmbedsCRLF()
    {
        // The PRINT-bundle observation that drove this feature: CHAR(13) +
        // CHAR(10) is the idiomatic way to embed CR+LF in T-SQL string
        // literals.
        var sim = new Simulation();
        AreEqual(2, sim.ExecuteScalar("select len(CHAR(13)+CHAR(10))"));
        AreEqual("a\nb", sim.ExecuteScalar("select 'a'+CHAR(10)+'b'"));
    }
}
