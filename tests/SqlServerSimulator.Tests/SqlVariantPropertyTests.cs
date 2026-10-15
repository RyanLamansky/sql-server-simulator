using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>SQL_VARIANT_PROPERTY(expression, property)</c>: reports one
/// facet (BaseType / Precision / Scale / MaxLength / TotalBytes / Collation) of
/// the sql_variant that would capture the expression. All expected values are
/// probe-confirmed against SQL Server 2025. Like real, the result is sql_variant
/// carrying a per-property inner base type (sysname for BaseType / Collation,
/// int for the numeric facets); each cell surfaces that inner CLR type.
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

    [TestMethod]
    public void BaseType_DecimalLiteral_ReturnsNumeric()
        => AreEqual("numeric", Scalar("select sql_variant_property(1.5, 'BaseType')"));

    /// <summary>
    /// A stored variant keeps the name its source carried: a decimal column or
    /// CAST reads decimal, a literal or numeric source numeric, and a table
    /// constructor's column takes its first decimal row's name (probed
    /// 2026-09-28 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void BaseType_StoredVariant_KeepsDecimalOrNumericName()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table s (d decimal(5, 2), n numeric(5, 2));
            insert s values (1, 2);
            create table t (id int, v sql_variant);
            insert t values (1, cast(1.5 as decimal(5, 2)));
            insert t values (2, 1.5);
            insert t select 3, d from s;
            insert t select 4, n from s;
            insert t values (5, cast(1 as numeric(3, 0))), (6, cast(1 as decimal(3, 0)));
            insert t values (7, null);
            update t set v = n from s where id = 7;
            insert t values (8, null);
            update t set v = (select d from s) where id = 8;
            insert t select 9, v from t where id = 1;
            """);
        AreEqual("decimal|numeric|decimal|numeric|numeric|numeric|numeric|decimal|decimal", simulation.ExecuteScalar(
            "select string_agg(cast(sql_variant_property(v, 'BaseType') as varchar(10)), '|') within group (order by id) from t"));
    }

    [TestMethod]
    [DataRow("declare @v sql_variant = cast(1.5 as decimal(5, 2)); select sql_variant_property(@v, 'BaseType')", "decimal")]
    [DataRow("declare @v sql_variant = cast(1.5 as numeric(5, 2)); select sql_variant_property(@v, 'BaseType')", "numeric")]
    [DataRow("declare @v sql_variant; set @v = cast(1 as decimal(3, 0)); select sql_variant_property(@v, 'BaseType')", "decimal")]
    [DataRow("declare @v sql_variant; select @v = cast(1 as decimal(3, 0)); select sql_variant_property(@v, 'BaseType')", "decimal")]
    [DataRow("select sql_variant_property(v, 'BaseType') from (select cast(cast(1 as decimal(3, 0)) as sql_variant) v) q", "decimal")]
    [DataRow("select sql_variant_property(convert(sql_variant, 1.5), 'BaseType')", "numeric")]
    public void BaseType_VariantFilledFromExpression_KeepsItsName(string sql, string expected)
        => AreEqual(expected, Scalar(sql));

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
    [DataRow("0.", 1, 1)]
    [DataRow("00.", 1, 1)]
    [DataRow("12.", 2, 0)]
    [DataRow("0.50", 2, 2)]
    public void DecimalLiteral_PrecisionFloorFallsOnTheFractionalSide(string literal, int precision, int scale)
    {
        AreEqual(precision, Scalar($"select sql_variant_property({literal}, 'Precision')"));
        AreEqual(scale, Scalar($"select sql_variant_property({literal}, 'Scale')"));
    }

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

    /// <summary>
    /// A <c>varbinary(n)</c> value reports its declared length wherever it
    /// came from — a CAST from any source, a variable, a column, a variant
    /// stored in a table or a table constructor, a union branch, a
    /// concatenation and the SID functions — while a cut CAST keeps its cut
    /// length and a hex literal its byte count (probed 2026-10-15 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("cast(0x01 as varbinary(85))", 85)]
    [DataRow("cast(1 as varbinary(85))", 85)]
    [DataRow("cast('ab' as varbinary(85))", 85)]
    [DataRow("cast(newid() as varbinary(20))", 20)]
    [DataRow("cast(1.5e0 as varbinary(20))", 20)]
    [DataRow("cast(1.5 as varbinary(20))", 20)]
    [DataRow("cast(sysdatetime() as varbinary(20))", 20)]
    [DataRow("convert(varbinary(30), '0xab', 1)", 30)]
    [DataRow("cast(cast(N'<a/>' as xml) as varbinary(30))", 30)]
    [DataRow("cast(0x010203 as varbinary(2))", 2)]
    [DataRow("isnull(cast(0x01 as varbinary(85)), 0x02)", 85)]
    [DataRow("cast(0x01 as varbinary(85)) + 0x02", 86)]
    [DataRow("cast(0x01 as varbinary(10)) + cast(0x02 as binary(3))", 13)]
    [DataRow("cast(cast(0x01 as varbinary(85)) as sql_variant)", 85)]
    [DataRow("suser_sid()", 85)]
    [DataRow("sid_binary('S-1-5-32-544')", 85)]
    [DataRow("0x0102", 2)]
    public void MaxLength_Varbinary_ReportsDeclaredLength(string expression, int expected)
        => AreEqual(expected, Scalar($"select sql_variant_property({expression}, 'MaxLength')"));

    [TestMethod]
    public void MaxLength_Varbinary_SurvivesVariablesColumnsAndRowsets()
        => AreEqual("85|85|20|9|85|85,10|10,10|85|85,7", Scalar("""
            declare @v varbinary(85) = 0x01, @s sql_variant, @b binary(7) = 0x01;
            set @s = @v;
            create table #t (c varbinary(20), b binary(9), v sql_variant, w sql_variant);
            insert #t (c, b) values (0x0102, 0x01);
            update #t set v = c, w = b;
            declare @tv table (v sql_variant);
            insert @tv values (cast(cast(0x01 as varbinary(85)) as sql_variant));
            create table #u (id int, v sql_variant);
            insert #u values (1, cast(cast(0x01 as varbinary(85)) as sql_variant)), (2, @b);
            select concat_ws('|',
                cast(sql_variant_property(@s, 'MaxLength') as int),
                cast(sql_variant_property(cast(@v as sql_variant), 'MaxLength') as int),
                (select cast(sql_variant_property(v, 'MaxLength') as int) from #t),
                (select cast(sql_variant_property(w, 'MaxLength') as int) from #t),
                (select cast(sql_variant_property(x, 'MaxLength') as int) from (values (cast(cast(0x01 as varbinary(85)) as sql_variant))) d(x)),
                (select string_agg(cast(sql_variant_property(x, 'MaxLength') as int), ',') from (select cast(cast(0x01 as varbinary(85)) as sql_variant) x union all select cast(cast(0x0102 as varbinary(10)) as sql_variant)) d),
                (select string_agg(cast(sql_variant_property(cast(x as sql_variant), 'MaxLength') as int), ',') from (select cast(0x01 as varbinary(5)) x union all select cast(0x0102 as binary(10))) d),
                (select cast(sql_variant_property(v, 'MaxLength') as int) from @tv),
                (select string_agg(cast(sql_variant_property(v, 'MaxLength') as int), ',') within group (order by id) from #u))
            """));

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
    // facet with the same inner base type as a literal (the result is always
    // sql_variant, whether or not the property argument is constant).
    [TestMethod]
    public void PropertyName_RuntimeExpression_Works()
        => AreEqual("int", Scalar("declare @p sysname = 'BaseType'; select sql_variant_property(1, @p)"));

    [TestMethod]
    public void PropertyName_RuntimeExpression_NumericFacetKeepsIntInner()
        => AreEqual(10, Scalar("declare @p sysname = 'Precision'; select sql_variant_property(1, @p)"));

    // --- Projection type (always sql_variant) with inner base type ---

    [TestMethod]
    public void BaseType_SurfacesAsVariantWithSysnameInner()
    {
        using var reader = new Simulation().ExecuteReader("select sql_variant_property(1, 'BaseType')");
        IsTrue(reader.Read());
        AreEqual("sql_variant", reader.GetDataTypeName(0));
        AreEqual("int", reader.GetValue(0));
    }

    [TestMethod]
    public void Precision_SurfacesAsVariantWithIntInner()
    {
        using var reader = new Simulation().ExecuteReader("select sql_variant_property(1, 'Precision')");
        IsTrue(reader.Read());
        AreEqual("sql_variant", reader.GetDataTypeName(0));
        _ = Assert.IsInstanceOfType<int>(reader.GetValue(0));
        AreEqual(10, reader.GetValue(0));
    }
}
