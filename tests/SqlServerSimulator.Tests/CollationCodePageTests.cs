using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Public-surface coverage for the ANSI code page a collation stores
/// <c>varchar</c> / <c>char</c> data in: storage bytes, the byte-counted
/// <c>varchar(N)</c> budget (which is not a character count under UTF-8 or a
/// DBCS code page), and the Msg 459 rejection of the Unicode-only collations.
/// Every expectation here is byte-for-byte what SQL Server 2025 produced for
/// the same statement.
/// </summary>
/// <remarks>
/// Counterpart to <see cref="CollationDeclaredColumnTests"/> (compare / sort
/// semantics) and <see cref="CollationMetadataTests"/> (catalog reporting) —
/// this class is about what lands in the bytes.
/// </remarks>
[TestClass]
public sealed class CollationCodePageTests
{
    private static string Hex(Simulation simulation, string commandText) =>
        Convert.ToHexString((byte[])simulation.ExecuteScalar(commandText)!);

    private static string Text(Simulation simulation, string commandText) =>
        (string)simulation.ExecuteScalar(commandText)!;

    /// <summary>
    /// Each collation family stores through its own ANSI code page, so text
    /// outside CP1252 survives instead of collapsing to <c>?</c>. Bytes,
    /// <c>DATALENGTH</c>, and <c>LEN</c> all match the reference server.
    /// </summary>
    [TestMethod]
    [DataRow("Turkish_CI_AS", "Ğğİış", "D0F0DDFDFE", 5, 5)]
    [DataRow("Greek_CI_AS", "Καλημέρα", "CAE1EBE7ECDDF1E1", 8, 8)]
    [DataRow("Cyrillic_General_CI_AS", "Привет", "CFF0E8E2E5F2", 6, 6)]
    [DataRow("SQL_Latin1_General_CP850_CI_AS", "é", "82", 1, 1)]
    [DataRow("Japanese_XJIS_140_CI_AS", "こんにちは", "82B182F182C982BF82CD", 10, 5)]
    [DataRow("Chinese_PRC_CI_AS", "上海", "C9CFBAA3", 4, 2)]
    public void VarcharStorage_UsesCollationCodePage(string collation, string text, string expectedHex, int expectedDataLength, int expectedLength)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            $"create table t (v varchar(40) collate {collation})",
            $"insert t values (N'{text}')");

        AreEqual(expectedHex, Hex(sim, "select convert(varbinary(40), v) from t"));
        AreEqual(expectedDataLength, sim.ExecuteScalar<int>("select datalength(v) from t"));
        AreEqual(expectedLength, sim.ExecuteScalar<int>("select len(v) from t"));
        // The round trip back to Unicode is lossless, which is the whole point:
        // CP1252 storage would have replaced every character with '?'.
        AreEqual(text, Text(sim, "select cast(v as nvarchar(40)) from t"));
    }

    /// <summary>
    /// <c>CAST(varchar AS varbinary)</c> renders the bytes the value actually
    /// stores. The CP1252 0x80-0x9F range is the visible case on the default
    /// collation: ISO-8859-1 has no such characters and best-fit-folds them
    /// (<c>Š</c> to <c>S</c>, <c>—</c> to <c>-</c>), which real never does.
    /// </summary>
    [TestMethod]
    [DataRow("€", "80")]
    [DataRow("Š", "8A")]
    [DataRow("“", "93")]
    [DataRow("—", "97")]
    [DataRow("é", "E9")]
    public void CastToVarbinary_UsesCodePageNotIso8859(string text, string expectedHex) =>
        AreEqual(expectedHex, Hex(new Simulation(), $"select convert(varbinary(10), cast(N'{text}' as varchar(10)))"));

    /// <summary>
    /// The varbinary rendering agrees with <c>DATALENGTH</c> over the same
    /// expression — they read one storage encoding, not two.
    /// </summary>
    [TestMethod]
    public void CastToVarbinary_AgreesWithDataLength()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (v varchar(20) collate Latin1_General_100_CI_AS_SC_UTF8)",
            "insert t values (N'ééééé')");

        AreEqual("C3A9C3A9C3A9C3A9C3A9", Hex(sim, "select convert(varbinary(20), v) from t"));
        AreEqual(10, sim.ExecuteScalar<int>("select datalength(v) from t"));
    }

    /// <summary>
    /// <c>ASCII</c> reads the first byte of the argument's own code page, so a
    /// character CP1252 cannot represent still returns its real byte. Under a
    /// DBCS code page the result is the lead byte of the two-byte form.
    /// </summary>
    [TestMethod]
    public void Ascii_ReadsArgumentCodePage()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (tr varchar(20) collate Turkish_CI_AS, ja varchar(20) collate Japanese_XJIS_140_CI_AS)",
            "insert t values (N'Ğğ', N'こんにちは')");

        AreEqual(208, sim.ExecuteScalar<int>("select ascii(tr) from t"));
        AreEqual(130, sim.ExecuteScalar<int>("select ascii(ja) from t"));
        // UNICODE is codepoint-based and so is code-page-independent.
        AreEqual(286, sim.ExecuteScalar<int>("select unicode(tr) from t"));
    }

    /// <summary>
    /// <c>varchar(N)</c> budgets N bytes, so a CAST clips to the longest
    /// character prefix that fits rather than to N characters — and never
    /// splits a multi-byte character. Both the DBCS and the UTF-8 cases yield
    /// two characters from a five-character source.
    /// </summary>
    [TestMethod]
    [DataRow("Japanese_XJIS_140_CI_AS", "こんにちは", 5, "82B182F1", 4, 2)]
    [DataRow("Japanese_XJIS_140_CI_AS", "こんにちは", 4, "82B182F1", 4, 2)]
    [DataRow("Latin1_General_100_CI_AS_SC_UTF8", "ééééé", 5, "C3A9C3A9", 4, 2)]
    public void Cast_ToNarrowerVarchar_ClipsToByteBudgetOnCharacterBoundary(
        string collation, string text, int targetLength, string expectedHex, int expectedDataLength, int expectedLength)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            $"create table t (v varchar(40) collate {collation})",
            $"insert t values (N'{text}')");

        AreEqual(expectedHex, Hex(sim, $"select convert(varbinary(40), cast(v as varchar({targetLength}))) from t"));
        AreEqual(expectedDataLength, sim.ExecuteScalar<int>($"select datalength(cast(v as varchar({targetLength}))) from t"));
        AreEqual(expectedLength, sim.ExecuteScalar<int>($"select len(cast(v as varchar({targetLength}))) from t"));
    }

    /// <summary>
    /// The string functions stay character-based while the budget is byte-based
    /// — <c>LEFT(v, 2)</c> is two kana (four CP932 bytes), not two bytes.
    /// </summary>
    [TestMethod]
    public void StringFunctions_StayCharacterBasedUnderDbcs()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (v varchar(40) collate Japanese_XJIS_140_CI_AS)",
            "insert t values (N'こんにちは')");

        AreEqual(2, sim.ExecuteScalar<int>("select len(left(v, 2)) from t"));
        AreEqual(4, sim.ExecuteScalar<int>("select datalength(left(v, 2)) from t"));
        AreEqual(4, sim.ExecuteScalar<int>("select datalength(substring(v, 2, 2)) from t"));
        AreEqual(20, sim.ExecuteScalar<int>("select datalength(v + v) from t"));
    }

    /// <summary>
    /// Overflowing a DBCS column reports the truncated value clipped to the
    /// byte budget on a character boundary: five kana into <c>varchar(5)</c>
    /// reports two, because a third would need a sixth byte.
    /// </summary>
    [TestMethod]
    public void Insert_OverflowingDbcsColumn_ReportsCharacterBoundaryPrefix()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (v varchar(5) collate Japanese_XJIS_140_CI_AS)");
        sim.AssertSqlError(
            "insert t values (N'こんにちは')",
            2628,
            "String or binary data would be truncated in table 't', column 'v'. Truncated value: 'こん'.");
    }

    /// <summary>
    /// Two kana fit a <c>varchar(5)</c> budget, so the row inserts and stores
    /// its four bytes — the guard is on bytes, not on the character count.
    /// </summary>
    [TestMethod]
    public void Insert_DbcsColumn_AcceptsWhatFitsTheByteBudget()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (v varchar(5) collate Japanese_XJIS_140_CI_AS)",
            "insert t values (N'こん')");

        AreEqual("82B182F1", Hex(sim, "select convert(varbinary(10), v) from t"));
        AreEqual(4, sim.ExecuteScalar<int>("select datalength(v) from t"));
    }

    /// <summary>
    /// The Windows collations with no ANSI code page are Unicode-only: pinning
    /// one on a char-family type raises Msg 459, while nvarchar accepts it.
    /// </summary>
    [TestMethod]
    [DataRow("varchar(10)")]
    [DataRow("char(10)")]
    [DataRow("text")]
    public void UnicodeOnlyCollation_OnCharFamily_RaisesMsg459(string typeName)
    {
        var sim = new Simulation();
        var error = sim.AssertSqlError($"create table t (v {typeName} collate Assamese_100_CI_AS)", 459);
        AreEqual(
            "Collation 'Assamese_100_CI_AS' is supported on Unicode data types only and cannot be applied to char, varchar or text data types.",
            error.Message);
        AreEqual((byte)2, error.State);
    }

    /// <summary>
    /// The same collation on an nvarchar column is accepted, and its
    /// <c>COLLATIONPROPERTY</c> code page reports 0 rather than defaulting to
    /// 1252 — that zero is what makes the char-family pairing illegal.
    /// </summary>
    [TestMethod]
    public void UnicodeOnlyCollation_OnNvarchar_IsAcceptedAndReportsCodePageZero()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (v nvarchar(10) collate Assamese_100_CI_AS)");
        AreEqual(0, sim.ExecuteScalar<int>("select collationproperty('Assamese_100_CI_AS', 'CodePage')"));
        AreEqual(1254, sim.ExecuteScalar<int>("select collationproperty('Turkish_CI_AS', 'CodePage')"));
        AreEqual(932, sim.ExecuteScalar<int>("select collationproperty('Japanese_XJIS_140_CI_AS', 'CodePage')"));
        AreEqual(850, sim.ExecuteScalar<int>("select collationproperty('SQL_Latin1_General_CP850_CI_AS', 'CodePage')"));
    }

    /// <summary>
    /// Narrowing an existing DBCS column measures the stored rows against the
    /// new byte budget, so a value that fits on characters but not on bytes
    /// still blocks the ALTER.
    /// </summary>
    [TestMethod]
    public void AlterColumn_NarrowingDbcsColumn_MeasuresByteBudget()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (v varchar(40) collate Japanese_XJIS_140_CI_AS)",
            "insert t values (N'こんにちは')");

        _ = sim.AssertSqlError("alter table t alter column v varchar(5) collate Japanese_XJIS_140_CI_AS", 2628);
        // Ten bytes is exactly the stored width, so the same ALTER succeeds.
        _ = sim.ExecuteNonQuery("alter table t alter column v varchar(10) collate Japanese_XJIS_140_CI_AS");
        AreEqual(10, sim.ExecuteScalar<int>("select datalength(v) from t"));
    }

    /// <summary>
    /// The reverse direction reads bytes back through the same code page, so
    /// the CP1252 0x80-0x9F range decodes to its real characters rather than
    /// the C1 control codes ISO-8859-1 would give.
    /// </summary>
    [TestMethod]
    [DataRow("0x80", 8364)]
    [DataRow("0x97", 8212)]
    [DataRow("0x93", 8220)]
    [DataRow("0xE9", 233)]
    public void CastFromVarbinary_DecodesThroughCodePage(string literal, int expectedCodePoint)
    {
        var sim = new Simulation();
        AreEqual(expectedCodePoint, sim.ExecuteScalar<int>($"select unicode(cast({literal} as varchar(10)))"));
        // The explicit style-0 CONVERT takes the same path and round-trips.
        AreEqual(expectedCodePoint, sim.ExecuteScalar<int>($"select unicode(convert(varchar(10), {literal}, 0))"));
        AreEqual(literal[2..], Hex(sim, $"select convert(varbinary(10), convert(varchar(10), {literal}, 0), 0)"));
    }

    /// <summary>
    /// <c>HASHBYTES</c> digests the bytes the column stores, so hashing a
    /// Turkish column equals hashing the same CP1254 bytes as a varbinary
    /// literal — the identity real exhibits.
    /// </summary>
    [TestMethod]
    public void HashBytes_DigestsCollationCodePageBytes()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (v varchar(40) collate Turkish_CI_AS)",
            "insert t values (N'Ğğ')");

        AreEqual(
            Hex(sim, "select hashbytes('SHA2_256', cast(0xD0F0 as varbinary(10)))"),
            Hex(sim, "select hashbytes('SHA2_256', v) from t"));
    }

    /// <summary>
    /// <c>COMPRESS</c> likewise compresses the stored bytes: two CP1254 bytes
    /// for the Turkish value, four CP932 bytes for the Japanese one. The
    /// round trip reads back through the database collation, so the Turkish
    /// bytes surface as their CP1252 characters.
    /// </summary>
    [TestMethod]
    public void Compress_CompressesCollationCodePageBytes()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (v varchar(40) collate Turkish_CI_AS, j varchar(40) collate Japanese_XJIS_140_CI_AS)",
            "insert t values (N'Ğğ', N'こん')");

        // DATALENGTH over COMPRESS's varbinary(MAX) result is bigint.
        AreEqual(22L, sim.ExecuteScalar<long>("select datalength(compress(v)) from t"));
        AreEqual(24L, sim.ExecuteScalar<long>("select datalength(compress(j)) from t"));
        AreEqual("Ðð", Text(sim, "select cast(decompress(compress(v)) as varchar(40)) from t"));
    }

    /// <summary>
    /// A binary collation on a varchar column byte-compares in its own code
    /// page, so its ordering follows the stored bytes rather than UTF-16.
    /// </summary>
    [TestMethod]
    public void BinaryCollation_OnVarchar_StoresItsOwnCodePage()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (v varchar(20) collate Japanese_BIN2)",
            "insert t values (N'こんにちは')");

        AreEqual("82B182F182C982BF82CD", Hex(sim, "select convert(varbinary(20), v) from t"));
        AreEqual(10, sim.ExecuteScalar<int>("select datalength(v) from t"));
    }

    /// <summary>
    /// A searching scalar compares under the collation its arguments resolve
    /// to, so an explicit <c>COLLATE</c> on <em>any</em> argument decides the
    /// whole call. This is how an ORM forces a case-sensitive REPLACE on a
    /// case-insensitive database, and the simulator previously hardcoded a
    /// case-insensitive comparison.
    /// </summary>
    [TestMethod]
    [DataRow("replace('George R. R. Martin', 'r. r.', '')", "George  Martin")]
    [DataRow("replace('George R. R. Martin', 'r. r.', '' collate SQL_Latin1_General_CP1_CS_AS)", "George R. R. Martin")]
    [DataRow("replace('George R. R. Martin', 'r. r.' collate SQL_Latin1_General_CP1_CS_AS, '')", "George R. R. Martin")]
    [DataRow("replace('George R. R. Martin' collate SQL_Latin1_General_CP1_CS_AS, 'r. r.', '')", "George R. R. Martin")]
    public void Replace_ComparesUnderTheResolvedCollation(string expression, string expected) =>
        AreEqual(expected, Text(new Simulation(), $"select {expression}"));

    /// <summary>
    /// <c>CHARINDEX</c> follows the same rule.
    /// </summary>
    [TestMethod]
    public void CharIndex_ComparesUnderTheResolvedCollation()
    {
        var sim = new Simulation();
        AreEqual(8, sim.ExecuteScalar<int>("select charindex('r. r.', 'George R. R. Martin')"));
        AreEqual(0, sim.ExecuteScalar<int>("select charindex('r. r.' collate SQL_Latin1_General_CP1_CS_AS, 'George R. R. Martin')"));
    }
}
