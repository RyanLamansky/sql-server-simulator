using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>uniqueidentifier</c>: 16-byte fixed-length GUID
/// storage, NEWID(), CAST round-trips through string and varbinary, the
/// SQL-Server-specific sort order, parameter binding, and rejected conversions.
/// </summary>
[TestClass]
public sealed class UniqueIdentifierTests
{
    private const string Sample = "AABBCCDD-EEFF-0011-2233-445566778899";

    private static Simulation SeededOneSample()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"""
            create table t (id uniqueidentifier);
            insert t (id) values (cast('{Sample}' as uniqueidentifier))
            """);
        return simulation;
    }

    [TestMethod]
    public void NewId_ReturnsUniqueidentifier() => _ = IsInstanceOfType<Guid>(ExecuteScalar("select newid()"));

    [TestMethod]
    public void NewId_ProducesDistinctValuesAcrossCalls()
    {
        var simulation = new Simulation();
        AreNotEqual(simulation.ExecuteScalar<Guid>("select newid()"), simulation.ExecuteScalar<Guid>("select newid()"));
    }

    [TestMethod]
    [DataRow("'aabbccdd-eeff-0011-2233-445566778899'")]
    [DataRow("'AABBCCDD-EEFF-0011-2233-445566778899'")]
    [DataRow("'{aabbccdd-eeff-0011-2233-445566778899}'")]
    [DataRow("'aabbccdd-eeff-0011-2233-445566778899   '")]    // trailing whitespace accepted
    [DataRow("N'aabbccdd-eeff-0011-2233-445566778899'")]
    public void Cast_StringToUniqueIdentifier_AcceptsValidForms(string literal)
        => AreEqual(Guid.Parse("aabbccdd-eeff-0011-2233-445566778899"),
            ExecuteScalar<Guid>($"select cast({literal} as uniqueidentifier)"));

    [TestMethod]
    [DataRow("'not-a-guid'")]
    [DataRow("'aabbccdd-eeff-0011-2233-44556677889'")]    // wrong length
    [DataRow("'(aabbccdd-eeff-0011-2233-445566778899)'")] // parens not accepted (only `{}`)
    [DataRow("'aabbccddeeff00112233445566778899'")]       // no-dashes form rejected
    [DataRow("' aabbccdd-eeff-0011-2233-445566778899'")]  // leading whitespace rejected
    public void Cast_StringToUniqueIdentifier_BadFormatRaisesMsg8169(string literal)
        => AssertSqlMessage($"select cast({literal} as uniqueidentifier)",
            "Conversion failed when converting from a character string to uniqueidentifier.");

    [TestMethod]
    public void Cast_UniqueIdentifierToVarchar_EmitsUppercaseDashedForm()
        => AreEqual(Sample, ExecuteScalar($"select cast(cast('{Sample}' as uniqueidentifier) as varchar(64))"));

    [TestMethod]
    public void Cast_UniqueIdentifierToNVarchar_EmitsUppercaseDashedForm()
        => AreEqual(Sample, ExecuteScalar($"select cast(cast('{Sample}' as uniqueidentifier) as nvarchar(64))"));

    [TestMethod]
    public void Cast_UniqueIdentifierToVarcharBelow36_RaisesMsg8170()
        => AssertSqlMessage($"select cast(cast('{Sample}' as uniqueidentifier) as varchar(35))",
            "Insufficient result space to convert uniqueidentifier value to char.");

    // SQL Server uses generic arithmetic-overflow for nchar/nvarchar (not the
    // dedicated 8170 used for char/varchar).
    [TestMethod]
    public void Cast_UniqueIdentifierToNVarcharBelow36_RaisesMsg8115()
        => AssertSqlMessage($"select cast(cast('{Sample}' as uniqueidentifier) as nvarchar(35))",
            "Arithmetic overflow error converting expression to data type nvarchar.");

    // A MAX-length target has unbounded width, so it holds the 36-char dashed
    // form rather than tripping the too-narrow Msg 8115/8170 — the length
    // sentinel (-1) must stay out of the "< 36" check. Regression for a
    // tiberius-surfaced bug where nvarchar(max) wrongly raised Msg 8115.
    [TestMethod]
    public void Cast_UniqueIdentifierToVarcharMax_EmitsUppercaseDashedForm()
        => AreEqual(Sample, ExecuteScalar($"select cast(cast('{Sample}' as uniqueidentifier) as varchar(max))"));

    [TestMethod]
    public void Cast_UniqueIdentifierToNVarcharMax_EmitsUppercaseDashedForm()
        => AreEqual(Sample, ExecuteScalar($"select cast(cast('{Sample}' as uniqueidentifier) as nvarchar(max))"));

    // Length check is value-conditional: NULL doesn't fire either Msg 8170 or 8115.
    [TestMethod]
    public void Cast_NullUniqueIdentifierToTooNarrowVarchar_PassesThroughAsDBNull()
        => _ = IsInstanceOfType<DBNull>(ExecuteScalar(
            "select cast(cast(cast(null as varchar(36)) as uniqueidentifier) as varchar(35))"));

    // SQL Server's on-disk byte layout matches Guid.ToByteArray(): first three
    // groups reversed, last group raw.
    [TestMethod]
    public void Cast_VarbinaryToUniqueIdentifier_RoundTripsByteOrder()
        => AreEqual(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            ExecuteScalar<Guid>("select cast(0x33221100554477668899AABBCCDDEEFF as uniqueidentifier)"));

    [TestMethod]
    public void Cast_UniqueIdentifierToVarbinary_EmitsSqlServerByteOrder()
    {
        var bytes = (byte[])ExecuteScalar(
            $"select cast(cast('00112233-4455-6677-8899-aabbccddeeff' as uniqueidentifier) as varbinary(16))")!;
        CollectionAssert.AreEqual(
            new byte[] { 0x33, 0x22, 0x11, 0x00, 0x55, 0x44, 0x77, 0x66, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF },
            bytes);
    }

    // 15-byte varbinary becomes byte 15 = 0 in the GUID. SQL Server is lenient
    // here — no error.
    [TestMethod]
    public void Cast_VarbinaryShorterThan16_PadsRightWithZeros()
        => AreEqual(Guid.Parse("ddccbbaa-ffee-1100-2233-445566778800"),
            ExecuteScalar<Guid>("select cast(0xaabbccddeeff001122334455667788 as uniqueidentifier)"));

    [TestMethod]
    public void Cast_VarbinaryLongerThan16_TruncatesFromTheRight()
        => AreEqual(Guid.Parse("ddccbbaa-ffee-1100-2233-445566778899"),
            ExecuteScalar<Guid>("select cast(0xaabbccddeeff0011223344556677889900 as uniqueidentifier)"));

    [TestMethod]
    [DataRow("int")]
    [DataRow("bigint")]
    [DataRow("bit")]
    [DataRow("date")]
    public void Cast_DisallowedSourceToUniqueIdentifier_RaisesMsg529(string sourceTypeSql)
    {
        var literal = sourceTypeSql == "date" ? "'2024-01-01'" : "0";
        var ex = AssertSqlError($"select cast(cast({literal} as {sourceTypeSql}) as uniqueidentifier)", 529);
        StartsWith($"Explicit conversion from data type {sourceTypeSql} to uniqueidentifier is not allowed.", ex.Message);
    }

    [TestMethod]
    public void Cast_UniqueIdentifierToDisallowedTarget_RaisesMsg529()
        => AssertSqlMessage($"select cast(cast('{Sample}' as uniqueidentifier) as int)",
            "Explicit conversion from data type uniqueidentifier to int is not allowed.");

    [TestMethod]
    public void UniqueIdentifier_EqualityIsCaseInsensitiveOnHex_ViaWhere()
        => AreEqual(Guid.Parse("aabbccdd-eeff-0011-2233-445566778899"), new Simulation().ExecuteScalar<Guid>("""
            create table t (id uniqueidentifier);
            insert t (id) values (cast('aabbccdd-eeff-0011-2233-445566778899' as uniqueidentifier));
            select id from t where id = cast('AABBCCDD-EEFF-0011-2233-445566778899' as uniqueidentifier)
            """));

    [TestMethod]
    public void UniqueIdentifier_StringOnRightOfEquality_PromotesToUniqueIdentifier()
        => AreEqual(Guid.Parse(Sample),
            SeededOneSample().ExecuteScalar<Guid>($"select id from t where id = '{Sample}'"));

    [TestMethod]
    public void UniqueIdentifier_StringOnLeftOfEquality_PromotesToUniqueIdentifier()
        => AreEqual(Guid.Parse(Sample),
            SeededOneSample().ExecuteScalar<Guid>($"select id from t where '{Sample}' = id"));

    [TestMethod]
    public void UniqueIdentifier_NVarcharLiteral_PromotesToUniqueIdentifier()
        => AreEqual(Guid.Parse(Sample),
            SeededOneSample().ExecuteScalar<Guid>($"select id from t where id = N'{Sample}'"));

    [TestMethod]
    public void UniqueIdentifier_StringInequality_PromotesAndCompares()
    {
        var simulation = SeededOneSample();
        _ = simulation.ExecuteNonQuery("insert t (id) values (cast('00000000-0000-0000-0000-000000000001' as uniqueidentifier))");
        AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000001"), simulation.ExecuteScalar<Guid>($"select id from t where id <> '{Sample}'"));
    }

    [TestMethod]
    public void UniqueIdentifier_BadStringInComparison_RaisesMsg8169()
        => SeededOneSample().AssertSqlError("select id from t where id = 'not-a-guid'", 8169,
            "Conversion failed when converting from a character string to uniqueidentifier.");

    [TestMethod]
    public void Insert_StringLiteralIntoUniqueIdentifierColumn_ParsesAndStores()
        => AreEqual(Guid.Parse(Sample), new Simulation().ExecuteScalar<Guid>($"""
            create table t (id uniqueidentifier);
            insert t (id) values ('{Sample}');
            select id from t
            """));

    [TestMethod]
    public void Insert_BadStringIntoUniqueIdentifierColumn_RaisesMsg8169()
        => new Simulation().AssertSqlError("""
            create table t (id uniqueidentifier);
            insert t (id) values ('not-a-guid')
            """, 8169,
            "Conversion failed when converting from a character string to uniqueidentifier.");

    [TestMethod]
    public void Cast_NullToUniqueIdentifier_ReturnsDBNull()
        => _ = IsInstanceOfType<DBNull>(ExecuteScalar("select cast(cast(null as varchar(36)) as uniqueidentifier)"));

    [TestMethod]
    public void Cast_NullUniqueIdentifierToVarchar_ReturnsDBNull()
        => _ = IsInstanceOfType<DBNull>(ExecuteScalar(
            "select cast(cast(cast(null as varchar(36)) as uniqueidentifier) as varchar(64))"));

    [TestMethod]
    public void UniqueIdentifier_WidthSpecifierInColumnDeclarationRaisesMsg2716()
    {
        var ex = new Simulation().AssertSqlError("create table t (id uniqueidentifier(16))", 2716);
        Contains("Cannot specify a column width on data type uniqueidentifier", ex.Message);
    }

    // SQL Server sort: bytes 10..15 most significant. Inverse of .NET's natural
    // Guid.CompareTo on string-form.
    [TestMethod]
    public void UniqueIdentifier_OrderBy_UsesSqlServerSortOrder()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id uniqueidentifier);
            insert t (id) values
                (cast('00000000-0000-0000-0000-000000000001' as uniqueidentifier)),
                (cast('00000000-0000-0000-0000-000000000100' as uniqueidentifier)),
                (cast('00000000-0000-0000-0000-010000000000' as uniqueidentifier)),
                (cast('00000000-0000-0001-0000-000000000000' as uniqueidentifier)),
                (cast('00000001-0000-0000-0000-000000000000' as uniqueidentifier)),
                (cast('01000000-0000-0000-0000-000000000000' as uniqueidentifier))
            """);

        using var reader = simulation.CreateCommand("select id from t order by id").ExecuteReader();
        var ordered = new List<Guid>();
        while (reader.Read())
            ordered.Add(reader.GetGuid(0));

        CollectionAssert.AreEqual(
            new[]
            {
                Guid.Parse("01000000-0000-0000-0000-000000000000"),
                Guid.Parse("00000001-0000-0000-0000-000000000000"),
                Guid.Parse("00000000-0000-0001-0000-000000000000"),
                Guid.Parse("00000000-0000-0000-0000-000000000001"),
                Guid.Parse("00000000-0000-0000-0000-000000000100"),
                Guid.Parse("00000000-0000-0000-0000-010000000000"),
            },
            ordered);
    }

    [TestMethod]
    public void Parameter_GuidValueRoundTripsAsUniqueIdentifier()
    {
        var expected = Guid.Parse(Sample);
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual(expected, connection.CreateCommand("select @p", ("@p", expected)).ExecuteScalar());
    }

    [TestMethod]
    public void GetGuid_ReturnsTheValue()
    {
        var expected = Guid.Parse(Sample);
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand("select @p", ("@p", expected)).ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(expected, reader.GetGuid(0));
    }
}
