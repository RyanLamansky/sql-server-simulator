using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The first-class <c>sql_variant</c> type: per-row inner base types on
/// <c>sys.database_scoped_configurations</c>, CAST wrap / unwrap, ISNULL
/// fallback, and SQL_VARIANT_PROPERTY over a true variant. Probe-confirmed
/// against SQL Server 2025 (2026-07-16).
/// </summary>
[TestClass]
public sealed class SqlVariantTests
{
    private static object? Scalar(string sql) => new Simulation().ExecuteScalar(sql);

    // The value column projects as sql_variant with an object field type,
    // matching SqlClient's per-column metadata for a sql_variant column.
    [TestMethod]
    public void DscValueColumn_ReportsSqlVariantMetadata()
    {
        using var reader = new Simulation().ExecuteReader(
            "SELECT value FROM sys.database_scoped_configurations WHERE name = 'MAXDOP'");
        IsTrue(reader.Read());
        AreEqual("sql_variant", reader.GetDataTypeName(0));
        AreEqual(typeof(object), reader.GetFieldType(0));
    }

    // Each row's value surfaces its own CLR type: MAXDOP int, the bit-valued
    // knobs bool — the shape that lets DacFx's (bool)reader[value] unbox on
    // LEGACY_CARDINALITY_ESTIMATION succeed.
    [TestMethod]
    public void DscValues_SurfacePerRowClrTypes()
    {
        using var reader = new Simulation().ExecuteReader(
            "SELECT name, value FROM sys.database_scoped_configurations ORDER BY configuration_id");
        var byName = new Dictionary<string, object?>();
        while (reader.Read())
            byName[(string)reader.GetValue(0)] = reader.IsDBNull(1) ? null : reader.GetValue(1);

        AreEqual(0, byName["MAXDOP"]);
        IsFalse((bool)byName["LEGACY_CARDINALITY_ESTIMATION"]!);
        IsTrue((bool)byName["PARAMETER_SNIFFING"]!);
        IsFalse((bool)byName["QUERY_OPTIMIZER_HOTFIXES"]!);
    }

    // value_for_secondary is a variant NULL on every row (DBNull at the reader).
    [TestMethod]
    public void DscValueForSecondary_IsVariantNull()
        => IsInstanceOfType<DBNull>(Scalar("SELECT value_for_secondary FROM sys.database_scoped_configurations WHERE name = 'MAXDOP'"));

    [TestMethod]
    public void CastVariantToInt_UnwrapsInner()
        => AreEqual(0, Scalar("SELECT CAST(value AS int) FROM sys.database_scoped_configurations WHERE name = 'MAXDOP'"));

    [TestMethod]
    public void CastVariantToString_UnwrapsInner()
        => AreEqual("0", Scalar("SELECT CAST(value AS varchar(10)) FROM sys.database_scoped_configurations WHERE name = 'MAXDOP'"));

    // ISNULL(variant_null, 'PRIMARY') stays sql_variant (its first argument's
    // type) and reads back as the wrapped string — the SSMS projection shape.
    [TestMethod]
    public void IsNull_VariantNullSecondary_FallsBackToString()
        => AreEqual("PRIMARY", Scalar(
            "SELECT ISNULL(value_for_secondary, 'PRIMARY') FROM sys.database_scoped_configurations WHERE name = 'MAXDOP'"));

    [TestMethod]
    public void SqlVariantProperty_OverVariantColumn_ReportsInnerBaseType()
    {
        AreEqual("int", Scalar(
            "SELECT SQL_VARIANT_PROPERTY(value, 'BaseType') FROM sys.database_scoped_configurations WHERE name = 'MAXDOP'"));
        AreEqual("bit", Scalar(
            "SELECT SQL_VARIANT_PROPERTY(value, 'BaseType') FROM sys.database_scoped_configurations WHERE name = 'LEGACY_CARDINALITY_ESTIMATION'"));
    }

    // CAST(x AS sql_variant) wraps x; SQL_VARIANT_PROPERTY reads the inner type.
    [TestMethod]
    public void CastToSqlVariant_WrapsValue()
    {
        AreEqual("nvarchar", Scalar("SELECT SQL_VARIANT_PROPERTY(CAST(N'OFF' AS sql_variant), 'BaseType')"));
        AreEqual("smallint", Scalar("SELECT SQL_VARIANT_PROPERTY(CAST(CAST(5 AS smallint) AS sql_variant), 'BaseType')"));
    }

    // A variant column compared to an int literal unwraps and compares the
    // inner value numerically (variant int 0 = 0).
    [TestMethod]
    public void VariantComparedToIntLiteral_UnwrapsAndMatches()
        => AreEqual(1, Scalar(
            "SELECT COUNT(*) FROM sys.database_scoped_configurations WHERE value = 0 AND name = 'MAXDOP'"));

    // sql_variant has no concatenation behavior — Msg 402, matching real.
    [TestMethod]
    public void StringConcatWithVariant_RaisesMsg402()
    {
        var ex = new Simulation().AssertSqlError(
            "SELECT 'v=' + value FROM sys.database_scoped_configurations WHERE name = 'MAXDOP'", 402);
        Contains("sql_variant", ex.Message);
    }

    // Arithmetic between a variant and a numeric operand is Msg 257 (implicit
    // conversion forbidden); the non-variant side is the disallowed target.
    // Probe-confirmed against SQL Server 2025.
    [TestMethod]
    public void VariantPlusInt_RaisesMsg257()
    {
        var ex = new Simulation().AssertSqlError("declare @v sql_variant = 5; select @v + 1", 257);
        Contains("Implicit conversion from data type sql_variant to int is not allowed", ex.Message);
    }

    // Two variant operands in an arithmetic operator raise Msg 402.
    [TestMethod]
    public void VariantPlusVariant_RaisesMsg402()
        => new Simulation().AssertSqlError("declare @a sql_variant = 5, @b sql_variant = 3; select @a + @b", 402);

    // The runtime rejection matches the compile-time projection-schema
    // rejection — a no-FROM scalar SELECT (evaluated eagerly) reports the same
    // Msg 257 as a FROM-driven query (bound before scanning).
    [TestMethod]
    public void VariantArithmeticFromServerProperty_RaisesMsg257()
        => new Simulation().AssertSqlError("select serverproperty('EngineEdition') + 1", 257);

    // SELECT ... INTO from a sql_variant-producing built-in creates a
    // sql_variant column (probe-confirmed against SQL Server 2025), and the
    // stored inner value round-trips with its base type.
    [TestMethod]
    public void SelectIntoFromServerProperty_CreatesVariantColumn()
    {
        using var reader = new Simulation().ExecuteReader(
            "SELECT SERVERPROPERTY('Edition') AS e INTO #t; SELECT e, SQL_VARIANT_PROPERTY(e, 'BaseType') AS bt FROM #t");
        AreEqual("sql_variant", reader.GetDataTypeName(0));
        IsTrue(reader.Read());
        AreEqual("Developer Edition (64-bit)", reader.GetValue(0));
        AreEqual("nvarchar", reader.GetValue(1));
    }

    /// <summary>
    /// Converting a variant's temporal payload to text uses style 0, where the
    /// same base type converted directly uses its own ISO default — so a
    /// <c>time</c> reads <c>1:45PM</c> through a variant and
    /// <c>13:45:12.345</c> without one. Probe-confirmed against SQL Server
    /// 2025 (2026-07-30) for every temporal base type; <c>datetime</c> and
    /// <c>smalldatetime</c> already default to style 0, so both routes agree
    /// there.
    /// </summary>
    [TestMethod]
    [DataRow("cast('13:45:12.345' as time(3))", "13:45:12.345", "1:45PM")]
    [DataRow("cast('2024-03-15 13:45:12.345 +05:30' as datetimeoffset(3))", "2024-03-15 13:45:12.345 +05:30", "Mar 15 2024  1:45PM +05:30")]
    [DataRow("cast('2024-03-15 13:45:12.345' as datetime2(3))", "2024-03-15 13:45:12.345", "Mar 15 2024  1:45PM")]
    [DataRow("cast('2024-03-15' as date)", "2024-03-15", "Mar 15 2024")]
    [DataRow("cast('2024-03-15 13:45:12.345' as datetime)", "Mar 15 2024  1:45PM", "Mar 15 2024  1:45PM")]
    public void VariantToText_UsesStyleZeroForTemporalPayloads(string expression, string direct, string throughVariant)
    {
        var sim = new Simulation();
        AreEqual(direct, sim.ExecuteScalar($"select cast({expression} as nvarchar(60))"));
        AreEqual(throughVariant, sim.ExecuteScalar($"select cast(cast({expression} as sql_variant) as nvarchar(60))"));
    }

    /// <summary>
    /// An odd-length binary reinterpreted as <c>nvarchar</c> zero-pads its
    /// dangling byte into a final character, so the byte survives a round trip
    /// — real returns <c>0x01020300</c> for <c>0x010203</c>, where a Unicode
    /// replacement character would have destroyed it. Probe-confirmed.
    /// </summary>
    [TestMethod]
    public void OddLengthBinaryToNVarchar_ZeroPadsTheDanglingByte()
    {
        var sim = new Simulation();
        AreEqual(2, sim.ExecuteScalar("select len(cast(0x010203 as nvarchar(60)))"));
        CollectionAssert.AreEqual(
            new byte[] { 0x01, 0x02, 0x03, 0x00 },
            (byte[])sim.ExecuteScalar("select cast(cast(0x010203 as nvarchar(60)) as varbinary(60))")!);
        CollectionAssert.AreEqual(
            new byte[] { 0x01, 0x02, 0x03, 0x04 },
            (byte[])sim.ExecuteScalar("select cast(cast(0x01020304 as nvarchar(60)) as varbinary(60))")!);
    }
}
