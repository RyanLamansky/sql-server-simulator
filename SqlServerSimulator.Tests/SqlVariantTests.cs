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
}
