using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>SQL_VARIANT_PROPERTY(expression, property)</c>: reports one
/// facet (BaseType / Precision / Scale / MaxLength / TotalBytes / Collation) of
/// the sql_variant that would capture the expression. All expected values are
/// probe-confirmed against SQL Server 2025. The simulator has no sql_variant,
/// so it surfaces the inner base type per the SERVERPROPERTY convention.
/// </summary>
[TestClass]
public sealed class SqlVariantPropertyTests
{
    private static object? Scalar(string sql) => new Simulation().ExecuteScalar(sql);

    // --- BaseType ---

    [TestMethod]
    public void BaseType_IntLiteral_ReturnsInt()
        => AreEqual("int", Scalar("select sql_variant_property(1, 'BaseType')"));

    [TestMethod]
    public void BaseType_VarcharLiteral_ReturnsVarchar()
        => AreEqual("varchar", Scalar("select sql_variant_property('abc', 'BaseType')"));

    [TestMethod]
    public void BaseType_NVarcharLiteral_ReturnsNVarchar()
        => AreEqual("nvarchar", Scalar("select sql_variant_property(N'abc', 'BaseType')"));

    // A decimal literal reports numeric — matches real's literal inference. The
    // simulator has one decimal family, so CAST(... AS decimal) also reports
    // numeric here (real would say decimal); documented divergence.
    [TestMethod]
    public void BaseType_DecimalLiteral_ReturnsNumeric()
        => AreEqual("numeric", Scalar("select sql_variant_property(1.5, 'BaseType')"));

    [TestMethod]
    public void BaseType_Bit_ReturnsBit()
        => AreEqual("bit", Scalar("select sql_variant_property(cast(1 as bit), 'BaseType')"));

    [TestMethod]
    public void BaseType_GetDate_ReturnsDatetime()
        => AreEqual("datetime", Scalar("select sql_variant_property(getdate(), 'BaseType')"));

    [TestMethod]
    public void BaseType_Float_ReturnsFloat()
        => AreEqual("float", Scalar("select sql_variant_property(cast(1 as float), 'BaseType')"));

    [TestMethod]
    public void BaseType_Money_ReturnsMoney()
        => AreEqual("money", Scalar("select sql_variant_property(cast(1 as money), 'BaseType')"));

    [TestMethod]
    public void BaseType_Guid_ReturnsUniqueidentifier()
        => AreEqual("uniqueidentifier", Scalar("select sql_variant_property(newid(), 'BaseType')"));

    [TestMethod]
    public void BaseType_Date_ReturnsDate()
        => AreEqual("date", Scalar("select sql_variant_property(cast('2020-01-01' as date), 'BaseType')"));

    // --- Precision / Scale ---

    [TestMethod]
    public void Precision_Decimal_ReturnsDeclaredPrecision()
        => AreEqual(3, Scalar("select sql_variant_property(1.25, 'Precision')"));

    [TestMethod]
    public void Scale_Decimal_ReturnsDeclaredScale()
        => AreEqual(2, Scalar("select sql_variant_property(1.25, 'Scale')"));

    [TestMethod]
    public void Precision_Int_ReturnsTen()
        => AreEqual(10, Scalar("select sql_variant_property(1, 'Precision')"));

    [TestMethod]
    public void Scale_Int_ReturnsZero()
        => AreEqual(0, Scalar("select sql_variant_property(1, 'Scale')"));

    [TestMethod]
    public void Precision_Datetime_Returns23()
        => AreEqual(23, Scalar("select sql_variant_property(getdate(), 'Precision')"));

    [TestMethod]
    public void Scale_Datetime_Returns3()
        => AreEqual(3, Scalar("select sql_variant_property(getdate(), 'Scale')"));

    [TestMethod]
    public void Precision_Bit_ReturnsOne()
        => AreEqual(1, Scalar("select sql_variant_property(cast(1 as bit), 'Precision')"));

    [TestMethod]
    public void Precision_BigInt_Returns19()
        => AreEqual(19, Scalar("select sql_variant_property(cast(1 as bigint), 'Precision')"));

    [TestMethod]
    public void Precision_Time7_Returns16()
        => AreEqual(16, Scalar("select sql_variant_property(cast('12:00' as time(7)), 'Precision')"));

    [TestMethod]
    public void Precision_Money_Returns19()
        => AreEqual(19, Scalar("select sql_variant_property(cast(1 as money), 'Precision')"));

    // --- MaxLength (declared container byte width) ---

    [TestMethod]
    public void MaxLength_VarcharLiteral_ReturnsValueByteLength()
        => AreEqual(3, Scalar("select sql_variant_property('abc', 'MaxLength')"));

    [TestMethod]
    public void MaxLength_NVarcharLiteral_ReturnsDoubleValueByteLength()
        => AreEqual(6, Scalar("select sql_variant_property(N'abc', 'MaxLength')"));

    // A wider container reports its declared width, not the value's length.
    [TestMethod]
    public void MaxLength_WiderVarchar_ReturnsDeclaredWidth()
        => AreEqual(10, Scalar("select sql_variant_property(cast('ab' as varchar(10)), 'MaxLength')"));

    [TestMethod]
    public void MaxLength_Int_ReturnsFour()
        => AreEqual(4, Scalar("select sql_variant_property(1, 'MaxLength')"));

    [TestMethod]
    public void MaxLength_Decimal_ReturnsStorageWidth()
        => AreEqual(5, Scalar("select sql_variant_property(cast(1.25 as decimal(5,2)), 'MaxLength')"));

    [TestMethod]
    public void MaxLength_Guid_Returns16()
        => AreEqual(16, Scalar("select sql_variant_property(newid(), 'MaxLength')"));

    [TestMethod]
    public void MaxLength_CharPadsToDeclared()
        => AreEqual(5, Scalar("select sql_variant_property(cast('ab' as char(5)), 'MaxLength')"));

    // --- TotalBytes (actual value bytes + per-family overhead) ---

    [TestMethod]
    public void TotalBytes_Varchar_ValueBytesPlus8()
        => AreEqual(11, Scalar("select sql_variant_property('abc', 'TotalBytes')"));

    [TestMethod]
    public void TotalBytes_NVarchar_ValueBytesPlus8()
        => AreEqual(14, Scalar("select sql_variant_property(N'abc', 'TotalBytes')"));

    [TestMethod]
    public void TotalBytes_Int_ValueBytesPlus2()
        => AreEqual(6, Scalar("select sql_variant_property(1, 'TotalBytes')"));

    [TestMethod]
    public void TotalBytes_Bit_ValueBytesPlus2()
        => AreEqual(3, Scalar("select sql_variant_property(cast(1 as bit), 'TotalBytes')"));

    [TestMethod]
    public void TotalBytes_Datetime_ValueBytesPlus2()
        => AreEqual(10, Scalar("select sql_variant_property(getdate(), 'TotalBytes')"));

    [TestMethod]
    public void TotalBytes_Decimal_ValueBytesPlus4()
        => AreEqual(9, Scalar("select sql_variant_property(cast(1.25 as decimal(5,2)), 'TotalBytes')"));

    [TestMethod]
    public void TotalBytes_Char_ValuePaddedPlus8()
        => AreEqual(13, Scalar("select sql_variant_property(cast('ab' as char(5)), 'TotalBytes')"));

    [TestMethod]
    public void TotalBytes_Time7_ValueBytesPlus3()
        => AreEqual(8, Scalar("select sql_variant_property(cast('12:00' as time(7)), 'TotalBytes')"));

    // --- Collation ---

    [TestMethod]
    public void Collation_String_ReturnsCollationName()
        => AreEqual("SQL_Latin1_General_CP1_CI_AS", Scalar("select sql_variant_property(N'abc', 'Collation')"));

    [TestMethod]
    public void Collation_NonString_ReturnsNull()
        => AreEqual(DBNull.Value, Scalar("select sql_variant_property(1, 'Collation')"));

    // --- Edge cases ---

    [TestMethod]
    public void NullExpression_ReturnsNull()
        => AreEqual(DBNull.Value, Scalar("select sql_variant_property(cast(null as int), 'BaseType')"));

    [TestMethod]
    public void UnknownProperty_ReturnsNull()
        => AreEqual(DBNull.Value, Scalar("select sql_variant_property(1, 'Bogus')"));

    [TestMethod]
    public void NullProperty_ReturnsNull()
        => AreEqual(DBNull.Value, Scalar("select sql_variant_property(1, cast(null as sysname))"));

    [TestMethod]
    public void PropertyName_CaseInsensitive()
        => AreEqual("int", Scalar("select sql_variant_property(1, 'basetype')"));

    // The property argument can be a runtime expression; it resolves the same
    // facet, surfaced as nvarchar (the static-type fallback for a non-literal).
    [TestMethod]
    public void PropertyName_RuntimeExpression_Works()
        => AreEqual("int", Scalar("declare @p sysname = 'BaseType'; select sql_variant_property(1, @p)"));

    [TestMethod]
    public void PropertyName_RuntimeExpression_NumericFacetCoercedToNVarchar()
        => AreEqual("10", Scalar("declare @p sysname = 'Precision'; select sql_variant_property(1, @p)"));

    // --- Static projection type (string literal property) ---

    [TestMethod]
    public void BaseType_LiteralProperty_SurfacesAsSysname()
    {
        using var reader = new Simulation().ExecuteReader("select sql_variant_property(1, 'BaseType')");
        IsTrue(reader.Read());
        AreEqual("sysname", reader.GetDataTypeName(0));
        AreEqual("int", reader.GetValue(0));
    }

    [TestMethod]
    public void Precision_LiteralProperty_SurfacesAsInt()
    {
        using var reader = new Simulation().ExecuteReader("select sql_variant_property(1, 'Precision')");
        IsTrue(reader.Read());
        AreEqual("int", reader.GetDataTypeName(0));
        _ = Assert.IsInstanceOfType<int>(reader.GetValue(0));
        AreEqual(10, reader.GetValue(0));
    }
}
