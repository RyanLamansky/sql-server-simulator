using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the <c>uniqueidentifier</c> type: 16-byte fixed-length
/// GUID storage, NEWID(), CAST round-trips through string and varbinary, the
/// SQL-Server-specific sort order, parameter binding, and the rejected
/// conversions.
/// </summary>
[TestClass]
public sealed class UniqueIdentifierTests
{
    private const string Sample = "AABBCCDD-EEFF-0011-2233-445566778899";

    [TestMethod]
    public void NewId_ReturnsUniqueidentifier()
    {
        var value = ExecuteScalar("select newid()");
        _ = IsInstanceOfType<Guid>(value);
    }

    [TestMethod]
    public void NewId_ProducesDistinctValuesAcrossCalls()
    {
        var simulation = new Simulation();
        var first = simulation.ExecuteScalar<Guid>("select newid()");
        var second = simulation.ExecuteScalar<Guid>("select newid()");
        AreNotEqual(first, second);
    }

    [TestMethod]
    [DataRow("'aabbccdd-eeff-0011-2233-445566778899'")]
    [DataRow("'AABBCCDD-EEFF-0011-2233-445566778899'")]
    [DataRow("'{aabbccdd-eeff-0011-2233-445566778899}'")]
    // Trailing whitespace is accepted by SQL Server; leading is not.
    [DataRow("'aabbccdd-eeff-0011-2233-445566778899   '")]
    [DataRow("N'aabbccdd-eeff-0011-2233-445566778899'")]
    public void Cast_StringToUniqueIdentifier_AcceptsValidForms(string literal)
    {
        var value = ExecuteScalar<Guid>($"select cast({literal} as uniqueidentifier)");
        AreEqual(Guid.Parse("aabbccdd-eeff-0011-2233-445566778899"), value);
    }

    [TestMethod]
    [DataRow("'not-a-guid'")]
    // Wrong length (one digit short).
    [DataRow("'aabbccdd-eeff-0011-2233-44556677889'")]
    // SQL Server rejects parens-as-braces; only `{}` is accepted.
    [DataRow("'(aabbccdd-eeff-0011-2233-445566778899)'")]
    // No-dashes form (.NET's `N` format) is rejected.
    [DataRow("'aabbccddeeff00112233445566778899'")]
    // Leading whitespace is rejected.
    [DataRow("' aabbccdd-eeff-0011-2233-445566778899'")]
    public void Cast_StringToUniqueIdentifier_BadFormatRaisesMsg8169(string literal)
    {
        var ex = Throws<DbException>(() => ExecuteScalar($"select cast({literal} as uniqueidentifier)"));
        AreEqual("Conversion failed when converting from a character string to uniqueidentifier.", ex.Message);
    }

    [TestMethod]
    public void Cast_UniqueIdentifierToVarchar_EmitsUppercaseDashedForm()
    {
        var value = ExecuteScalar($"select cast(cast('{Sample}' as uniqueidentifier) as varchar(64))");
        AreEqual(Sample, value);
    }

    [TestMethod]
    public void Cast_UniqueIdentifierToNVarchar_EmitsUppercaseDashedForm()
    {
        var value = ExecuteScalar($"select cast(cast('{Sample}' as uniqueidentifier) as nvarchar(64))");
        AreEqual(Sample, value);
    }

    [TestMethod]
    public void Cast_UniqueIdentifierToVarcharBelow36_RaisesMsg8170()
    {
        var ex = Throws<DbException>(() => ExecuteScalar(
            $"select cast(cast('{Sample}' as uniqueidentifier) as varchar(35))"));
        AreEqual("Insufficient result space to convert uniqueidentifier value to char.", ex.Message);
    }

    [TestMethod]
    public void Cast_UniqueIdentifierToNVarcharBelow36_RaisesMsg8115()
    {
        // SQL Server uses the generic arithmetic-overflow message for
        // nchar/nvarchar, not the dedicated 8170 text it uses for char/varchar.
        var ex = Throws<DbException>(() => ExecuteScalar(
            $"select cast(cast('{Sample}' as uniqueidentifier) as nvarchar(35))"));
        AreEqual("Arithmetic overflow error converting expression to data type nvarchar.", ex.Message);
    }

    [TestMethod]
    public void Cast_NullUniqueIdentifierToTooNarrowVarchar_PassesThroughAsDBNull()
    {
        // SQL Server's length check is value-conditional: NULL doesn't fire
        // either Msg 8170 or 8115.
        _ = IsInstanceOfType<DBNull>(ExecuteScalar(
            "select cast(cast(cast(null as varchar(36)) as uniqueidentifier) as varchar(35))"));
    }

    [TestMethod]
    public void Cast_VarbinaryToUniqueIdentifier_RoundTripsByteOrder()
    {
        // Real SQL Server's on-disk byte layout matches Guid.ToByteArray():
        // first three groups are reversed, last group is raw bytes.
        // 0x33221100554477668899AABBCCDDEEFF ↔ 00112233-4455-6677-8899-aabbccddeeff
        var value = ExecuteScalar<Guid>(
            "select cast(0x33221100554477668899AABBCCDDEEFF as uniqueidentifier)");
        AreEqual(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), value);
    }

    [TestMethod]
    public void Cast_UniqueIdentifierToVarbinary_EmitsSqlServerByteOrder()
    {
        var bytes = (byte[])ExecuteScalar(
            $"select cast(cast('00112233-4455-6677-8899-aabbccddeeff' as uniqueidentifier) as varbinary(16))")!;
        CollectionAssert.AreEqual(
            new byte[] { 0x33, 0x22, 0x11, 0x00, 0x55, 0x44, 0x77, 0x66, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF },
            bytes);
    }

    [TestMethod]
    public void Cast_VarbinaryShorterThan16_PadsRightWithZeros()
    {
        // 15-byte varbinary becomes byte 15 = 0 in the GUID. SQL Server is
        // lenient about length here — no error.
        var value = ExecuteScalar<Guid>(
            "select cast(0xaabbccddeeff001122334455667788 as uniqueidentifier)");
        // Bytes [aa,bb,cc,dd, ee,ff, 00,11, 22,33,44,55,66,77,88, 00] →
        // ddccbbaa-ffee-1100-2233-445566778800
        AreEqual(Guid.Parse("ddccbbaa-ffee-1100-2233-445566778800"), value);
    }

    [TestMethod]
    public void Cast_VarbinaryLongerThan16_TruncatesFromTheRight()
    {
        var value = ExecuteScalar<Guid>(
            "select cast(0xaabbccddeeff0011223344556677889900 as uniqueidentifier)");
        AreEqual(Guid.Parse("ddccbbaa-ffee-1100-2233-445566778899"), value);
    }

    [TestMethod]
    [DataRow("int")]
    [DataRow("bigint")]
    [DataRow("bit")]
    [DataRow("date")]
    public void Cast_DisallowedSourceToUniqueIdentifier_RaisesMsg529(string sourceTypeSql)
    {
        // Use a same-type literal: cast 0 (or '2024-01-01') first to source,
        // then to uniqueidentifier.
        var literal = sourceTypeSql == "date" ? "'2024-01-01'" : "0";
        var ex = Throws<DbException>(() => ExecuteScalar(
            $"select cast(cast({literal} as {sourceTypeSql}) as uniqueidentifier)"));
        StringAssert.StartsWith(ex.Message, $"Explicit conversion from data type {sourceTypeSql} to uniqueidentifier is not allowed.");
    }

    [TestMethod]
    public void Cast_UniqueIdentifierToDisallowedTarget_RaisesMsg529()
    {
        var ex = Throws<DbException>(() => ExecuteScalar(
            $"select cast(cast('{Sample}' as uniqueidentifier) as int)"));
        AreEqual("Explicit conversion from data type uniqueidentifier to int is not allowed.", ex.Message);
    }

    [TestMethod]
    public void UniqueIdentifier_EqualityIsCaseInsensitiveOnHex_ViaWhere()
    {
        // Round-trip through a heap table with a WHERE filter: case-insensitive
        // hex parsing means rows inserted via lowercase compare equal to the
        // uppercase literal in the WHERE clause.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id uniqueidentifier)");
        _ = simulation.ExecuteNonQuery("insert into t (id) values (cast('aabbccdd-eeff-0011-2233-445566778899' as uniqueidentifier))");
        var match = simulation.ExecuteScalar<Guid>(
            "select id from t where id = cast('AABBCCDD-EEFF-0011-2233-445566778899' as uniqueidentifier)");
        AreEqual(Guid.Parse("aabbccdd-eeff-0011-2233-445566778899"), match);
    }

    [TestMethod]
    public void UniqueIdentifier_StringOnRightOfEquality_PromotesToUniqueIdentifier()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id uniqueidentifier)");
        _ = simulation.ExecuteNonQuery($"insert into t (id) values (cast('{Sample}' as uniqueidentifier))");
        var match = simulation.ExecuteScalar<Guid>($"select id from t where id = '{Sample}'");
        AreEqual(Guid.Parse(Sample), match);
    }

    [TestMethod]
    public void UniqueIdentifier_StringOnLeftOfEquality_PromotesToUniqueIdentifier()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id uniqueidentifier)");
        _ = simulation.ExecuteNonQuery($"insert into t (id) values (cast('{Sample}' as uniqueidentifier))");
        var match = simulation.ExecuteScalar<Guid>($"select id from t where '{Sample}' = id");
        AreEqual(Guid.Parse(Sample), match);
    }

    [TestMethod]
    public void UniqueIdentifier_NVarcharLiteral_PromotesToUniqueIdentifier()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id uniqueidentifier)");
        _ = simulation.ExecuteNonQuery($"insert into t (id) values (cast('{Sample}' as uniqueidentifier))");
        var match = simulation.ExecuteScalar<Guid>($"select id from t where id = N'{Sample}'");
        AreEqual(Guid.Parse(Sample), match);
    }

    [TestMethod]
    public void UniqueIdentifier_StringInequality_PromotesAndCompares()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id uniqueidentifier)");
        _ = simulation.ExecuteNonQuery($"insert into t (id) values (cast('{Sample}' as uniqueidentifier))");
        _ = simulation.ExecuteNonQuery("insert into t (id) values (cast('00000000-0000-0000-0000-000000000001' as uniqueidentifier))");
        // Filter out one row by string-form inequality; one row remains.
        var match = simulation.ExecuteScalar<Guid>($"select id from t where id <> '{Sample}'");
        AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000001"), match);
    }

    [TestMethod]
    public void UniqueIdentifier_BadStringInComparison_RaisesMsg8169()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id uniqueidentifier)");
        _ = simulation.ExecuteNonQuery($"insert into t (id) values (cast('{Sample}' as uniqueidentifier))");
        var ex = Throws<DbException>(() => simulation.ExecuteScalar("select id from t where id = 'not-a-guid'"));
        AreEqual("Conversion failed when converting from a character string to uniqueidentifier.", ex.Message);
    }

    [TestMethod]
    public void Insert_StringLiteralIntoUniqueIdentifierColumn_ParsesAndStores()
    {
        // Direct INSERT path: the string literal coerces through the uid
        // type-converter just like in CAST. Should round-trip identically.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id uniqueidentifier)");
        _ = simulation.ExecuteNonQuery($"insert into t (id) values ('{Sample}')");
        var stored = simulation.ExecuteScalar<Guid>("select id from t");
        AreEqual(Guid.Parse(Sample), stored);
    }

    [TestMethod]
    public void Insert_BadStringIntoUniqueIdentifierColumn_RaisesMsg8169()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id uniqueidentifier)");
        var ex = Throws<DbException>(() => simulation.ExecuteNonQuery("insert into t (id) values ('not-a-guid')"));
        AreEqual("Conversion failed when converting from a character string to uniqueidentifier.", ex.Message);
    }

    [TestMethod]
    public void Cast_NullToUniqueIdentifier_ReturnsDBNull()
    {
        _ = IsInstanceOfType<DBNull>(ExecuteScalar("select cast(cast(null as varchar(36)) as uniqueidentifier)"));
    }

    [TestMethod]
    public void Cast_NullUniqueIdentifierToVarchar_ReturnsDBNull()
    {
        _ = IsInstanceOfType<DBNull>(ExecuteScalar("select cast(cast(cast(null as varchar(36)) as uniqueidentifier) as varchar(64))"));
    }

    [TestMethod]
    public void UniqueIdentifier_WidthSpecifierInColumnDeclarationRaisesMsg2716()
    {
        // Fixed-length type rejects (N) on a column declaration via the
        // existing Msg 2716 path.
        var simulation = new Simulation();
        var ex = Throws<DbException>(() => simulation.ExecuteNonQuery("create table t (id uniqueidentifier(16))"));
        StringAssert.Contains(ex.Message, "Cannot specify a column width on data type uniqueidentifier");
    }

    [TestMethod]
    public void UniqueIdentifier_OrderBy_UsesSqlServerSortOrder()
    {
        // SQL Server's quirky sort: bytes 10..15 most significant. The
        // string-form orderings here would be reversed under .NET's
        // natural Guid.CompareTo.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id uniqueidentifier)");
        string[] inserts =
        [
            "00000000-0000-0000-0000-000000000001",
            "00000000-0000-0000-0000-000000000100",
            "00000000-0000-0000-0000-010000000000",
            "00000000-0000-0001-0000-000000000000",
            "00000001-0000-0000-0000-000000000000",
            "01000000-0000-0000-0000-000000000000",
        ];
        foreach (var s in inserts)
            _ = simulation.ExecuteNonQuery($"insert into t (id) values (cast('{s}' as uniqueidentifier))");

        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand("select id from t order by id");
        using var reader = command.ExecuteReader();
        var ordered = new List<Guid>();
        while (reader.Read())
            ordered.Add(reader.GetGuid(0));

        // Expected order, verified against SQL Server 2025.
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
        using var command = connection.CreateCommand("select @p", ("@p", expected));
        var actual = command.ExecuteScalar();
        AreEqual(expected, actual);
    }

    [TestMethod]
    public void GetGuid_ReturnsTheValue()
    {
        var expected = Guid.Parse(Sample);
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("select @p", ("@p", expected));
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(expected, reader.GetGuid(0));
    }
}
