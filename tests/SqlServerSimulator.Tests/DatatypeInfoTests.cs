using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the <c>sp_datatype_info_100</c> system procedure — the ODBC
/// <c>SQLGetTypeInfo</c> backing proc a driver calls on connect to learn each
/// type's precision/scale. It returns a fixed 20-column result set whose rows
/// are selected by <c>@data_type</c> (single type or all) and version-tagged by
/// <c>@ODBCVer</c> (2 vs 3), the split driving the temporal <c>DATA_TYPE</c>
/// codes and float/real precision + radix. Values mirror the SQL Server 2025
/// reference (probe-captured).
/// </summary>
[TestClass]
public sealed class DatatypeInfoTests
{
    private static List<(string TypeName, short DataType, int Precision, object MaxScale, object Radix)> ReadRows(string sql)
    {
        using var reader = new Simulation().ExecuteReader(sql);
        return
        [
            .. reader.EnumerateRecords()
                .Select(r => (
                    TypeName: r.GetString(0),
                    DataType: r.GetInt16(1),
                    Precision: r.GetInt32(2),
                    MaxScale: r.IsDBNull(14) ? (object)DBNull.Value : r.GetInt16(14),
                    Radix: r.IsDBNull(17) ? (object)DBNull.Value : r.GetInt32(17))),
        ];
    }

    [TestMethod]
    public void DatatypeInfo_DefaultArgs_ReturnsThirtySevenRows()
        => HasCount(37, ReadRows("exec sys.sp_datatype_info_100"));

    [TestMethod]
    public void DatatypeInfo_ColumnShape()
    {
        using var reader = new Simulation().ExecuteReader("exec sys.sp_datatype_info_100");
        AreEqual(20, reader.FieldCount);
        string[] expected =
        [
            "TYPE_NAME", "DATA_TYPE", "PRECISION", "LITERAL_PREFIX", "LITERAL_SUFFIX",
            "CREATE_PARAMS", "NULLABLE", "CASE_SENSITIVE", "SEARCHABLE", "UNSIGNED_ATTRIBUTE",
            "MONEY", "AUTO_INCREMENT", "LOCAL_TYPE_NAME", "MINIMUM_SCALE", "MAXIMUM_SCALE",
            "SQL_DATA_TYPE", "SQL_DATETIME_SUB", "NUM_PREC_RADIX", "INTERVAL_PRECISION", "USERTYPE",
        ];
        for (var i = 0; i < expected.Length; i++)
            AreEqual(expected[i], reader.GetName(i));
        AreEqual(typeof(string), reader.GetFieldType(0));  // TYPE_NAME nvarchar(128)
        AreEqual(typeof(short), reader.GetFieldType(1));   // DATA_TYPE smallint
        AreEqual(typeof(int), reader.GetFieldType(2));     // PRECISION int
        AreEqual(typeof(string), reader.GetFieldType(3));  // LITERAL_PREFIX varchar(32)
        AreEqual(typeof(short), reader.GetFieldType(14));  // MAXIMUM_SCALE smallint
        AreEqual(typeof(int), reader.GetFieldType(17));    // NUM_PREC_RADIX int
    }

    [TestMethod]
    public void DatatypeInfo_DefaultOdbcVer_IsTwo_TemporalCodesAreConcise()
    {
        // Absent @ODBCVer defaults to 2, where datetime2/datetime/smalldatetime
        // share the concise DATA_TYPE code 11 (not the v3 verbatim 93).
        var codes = ReadRows("exec sys.sp_datatype_info_100")
            .Where(r => r.TypeName is "datetime2" or "datetime" or "smalldatetime")
            .Select(r => r.DataType)
            .Distinct()
            .ToList();
        CollectionAssert.AreEqual(new[] { (short)11 }, codes);
    }

    [TestMethod]
    public void DatatypeInfo_OdbcVer3_DataType93_ReturnsThreeTemporalRowsWithScales()
    {
        var rows = ReadRows("exec sys.sp_datatype_info_100 @data_type=93, @ODBCVer=3");
        CollectionAssert.AreEqual(
            new[] { "datetime2", "datetime", "smalldatetime" },
            rows.Select(r => r.TypeName).ToList());
        CollectionAssert.AreEqual(
            new object[] { (short)7, (short)3, (short)0 },
            rows.Select(r => r.MaxScale).ToList());
    }

    [TestMethod]
    public void DatatypeInfo_Datetime2_CodeShiftsAcrossVersions()
    {
        AreEqual((short)11, ReadRows("exec sys.sp_datatype_info_100 0, 2").Single(r => r.TypeName == "datetime2").DataType);
        AreEqual((short)93, ReadRows("exec sys.sp_datatype_info_100 0, 3").Single(r => r.TypeName == "datetime2").DataType);
    }

    [TestMethod]
    public void DatatypeInfo_Float_PrecisionAndRadixShiftAcrossVersions()
    {
        var (_, _, precision2, _, radix2) = ReadRows("exec sys.sp_datatype_info_100 6, 2").Single();
        AreEqual(15, precision2);
        AreEqual(10, radix2);

        var (_, _, precision3, _, radix3) = ReadRows("exec sys.sp_datatype_info_100 6, 3").Single();
        AreEqual(53, precision3);
        AreEqual(2, radix3);
    }

    [TestMethod]
    public void DatatypeInfo_Real_PrecisionAndRadixShiftAcrossVersions()
    {
        var (_, _, precision2, _, radix2) = ReadRows("exec sys.sp_datatype_info_100 7, 2").Single();
        AreEqual(7, precision2);
        AreEqual(10, radix2);

        var (_, _, precision3, _, radix3) = ReadRows("exec sys.sp_datatype_info_100 7, 3").Single();
        AreEqual(24, precision3);
        AreEqual(2, radix3);
    }

    [TestMethod]
    public void DatatypeInfo_Varchar_HasDataType12()
    {
        var (typeName, dataType, precision, _, _) = ReadRows("exec sys.sp_datatype_info_100 12, 3").Single();
        AreEqual("varchar", typeName);
        AreEqual((short)12, dataType);
        AreEqual(8000, precision);
    }

    [TestMethod]
    public void DatatypeInfo_RowsAreOrderedByDataTypeAscending()
    {
        var codes = ReadRows("exec sys.sp_datatype_info_100").Select(r => (int)r.DataType).ToList();
        CollectionAssert.AreEqual(codes.OrderBy(c => c).ToList(), codes);
    }

    [TestMethod]
    public void DatatypeInfo_OdbcVer2And3_HaveEqualRowCounts()
    {
        HasCount(37, ReadRows("exec sys.sp_datatype_info_100 0, 2"));
        HasCount(37, ReadRows("exec sys.sp_datatype_info_100 0, 3"));
    }

    [TestMethod]
    public void DatatypeInfo_PositionalDataType_SelectsSingleCode()
    {
        var rows = ReadRows("exec sys.sp_datatype_info_100 -5");
        CollectionAssert.AreEqual(new[] { "bigint", "bigint identity" }, rows.Select(r => r.TypeName).ToList());
    }

    [TestMethod]
    public void DatatypeInfo_NamedArgumentsInEitherOrder_Resolve()
    {
        var rows = ReadRows("exec sys.sp_datatype_info_100 @ODBCVer=3, @data_type=93");
        HasCount(3, rows);
        AreEqual("datetime2", rows[0].TypeName);
    }

    // ODBC's SQLGetTypeInfo issues the RPC as `sys.sp_datatype_info_100`; the
    // in-process EXEC path re-enters the same handler, so the fully-qualified
    // name resolves identically to the bare leaf.
    [TestMethod]
    public void DatatypeInfo_CallableThroughBareLeaf()
        => HasCount(37, ReadRows("exec sp_datatype_info_100"));
}
