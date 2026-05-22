using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

[TestClass]
public sealed class CastTests
{
    [TestMethod]
    [DataRow("cast(1 as int)", 1)]
    [DataRow("cast(1 as bigint)", 1L)]
    [DataRow("cast(1 as smallint)", (short)1)]
    [DataRow("cast(255 as tinyint)", (byte)255)]
    [DataRow("cast(1 as bit)", true)]
    [DataRow("cast(0 as bit)", false)]
    [DataRow("cast(-1 as int)", -1)]              // regression: unary minus inside CAST
    [DataRow("cast(-(2 + 3) as int)", -5)]        // unary minus + parenthesized
    public void Cast(string expression, object expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void Cast_NullPassesThroughWithRetargetedType() =>
        IsInstanceOfType<DBNull>(ExecuteScalar("select cast(null as int)"));

    [TestMethod]
    public void Cast_NarrowingOverflow_RaisesArithmeticOverflow()
        => Contains("Arithmetic overflow", AssertSqlError("select cast(300 as tinyint)", 8115).Message);

    [TestMethod]
    [DataRow("cast('42' as int)", 42)]
    [DataRow("cast('-42' as int)", -42)]
    [DataRow("cast('+42' as int)", 42)]
    [DataRow("cast('  42  ' as int)", 42)]      // whitespace tolerated
    [DataRow("cast('007' as int)", 7)]          // leading zeros fine
    [DataRow("cast('' as int)", 0)]             // empty → 0
    [DataRow("cast('   ' as int)", 0)]          // whitespace-only → 0
    public void Cast_StringToInt32(string expression, int expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    [DataRow("'abc'", "Conversion failed when converting the varchar value 'abc' to data type int.")]
    [DataRow("'42.5'", "Conversion failed when converting the varchar value '42.5' to data type int.")]
    [DataRow("'0x42'", "Conversion failed when converting the varchar value '0x42' to data type int.")]
    public void Cast_BadStringToInt32_RaisesMsg245(string source, string expected) =>
        AssertSqlMessage($"select cast({source} as int)", expected);

    [TestMethod]
    public void Cast_NVarcharToInt32_UsesNVarcharInErrorMessage() =>
        AssertSqlMessage("select cast(N'abc' as int)", "Conversion failed when converting the nvarchar value 'abc' to data type int.");

    [TestMethod]
    public void Cast_StringOverflow_TinyInt_RaisesMsg244WithINT1() =>
        AssertSqlMessage("select cast('300' as tinyint)", "The conversion of the varchar value '300' overflowed an INT1 column. Use a larger integer column.");

    [TestMethod]
    public void Cast_StringOverflow_SmallInt_RaisesMsg244WithINT2() =>
        AssertSqlMessage("select cast('99999' as smallint)", "The conversion of the varchar value '99999' overflowed an INT2 column. Use a larger integer column.");

    [TestMethod]
    public void Cast_StringOverflow_Int_RaisesMsg248() =>
        AssertSqlMessage("select cast('99999999999' as int)", "The conversion of the varchar value '99999999999' overflowed an int column.");

    [TestMethod]
    public void Cast_StringOverflow_BigInt_RaisesMsg8115()
    {
        var ex = AssertSqlError("select cast('99999999999999999999' as bigint)", 8115);
        Contains("Arithmetic overflow", ex.Message);
        Contains("bigint", ex.Message);
    }

    [TestMethod]
    [DataRow("'1'", true)]
    [DataRow("'0'", false)]
    [DataRow("'true'", true)]
    [DataRow("'TRUE'", true)]
    [DataRow("'false'", false)]
    [DataRow("'FALSE'", false)]
    [DataRow("'  true  '", true)]       // surrounding whitespace tolerated
    [DataRow("'2'", true)]              // any non-zero numeric → true
    [DataRow("'-1'", true)]
    [DataRow("'000'", false)]           // all-zero digit string → false
    [DataRow("''", false)]              // empty → false
    [DataRow("'   '", false)]           // whitespace-only → false
    [DataRow("'99999999999999999999'", true)]   // exceeds long but bit ignores magnitude
    public void Cast_StringToBit(string source, bool expected) =>
        AreEqual(expected, ExecuteScalar($"select cast({source} as bit)"));

    [TestMethod]
    [DataRow("'yes'")]
    [DataRow("'no'")]
    [DataRow("'t'")]
    [DataRow("'truex'")]
    [DataRow("'1.0'")]              // decimal point not accepted even for bit
    public void Cast_BadStringToBit_RaisesMsg245(string source)
    {
        var ex = Throws<System.Data.Common.DbException>(() => ExecuteScalar($"select cast({source} as bit)"));
        Contains("Conversion failed", ex.Message);
        Contains("to data type bit", ex.Message);
    }

    [TestMethod]
    [DataRow("cast(42 as varchar(10))", "42")]
    [DataRow("cast(-42 as varchar(10))", "-42")]
    [DataRow("cast(0 as varchar(10))", "0")]
    [DataRow("cast(cast(255 as tinyint) as varchar(5))", "255")]
    [DataRow("cast(cast(1 as bit) as varchar(5))", "1")]
    [DataRow("cast(cast(0 as bit) as varchar(5))", "0")]
    public void Cast_IntegerToString(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void Cast_BigIntToVarchar_PreservesFullRange()
    {
        // Numeric literal tokens are int-bounded; source bigint constructed via string parse.
        AreEqual("9223372036854775807", ExecuteScalar("select cast(cast('9223372036854775807' as bigint) as varchar(30))"));
    }

    [TestMethod]
    [DataRow("cast('2026-05-04' as date)", "2026-05-04")]
    [DataRow("cast('20260504' as date)", "2026-05-04")]
    [DataRow("cast('2026-05-04T13:45:30' as date)", "2026-05-04")]
    [DataRow("cast(N'2026-05-04' as date)", "2026-05-04")]
    public void Cast_StringToDate(string expression, string expectedIso)
    {
        var dt = IsInstanceOfType<DateTime>(ExecuteScalar($"select {expression}"));
        AreEqual(DateTime.Parse(expectedIso, System.Globalization.CultureInfo.InvariantCulture), dt);
    }

    [TestMethod]
    [DataRow("not a date")]
    [DataRow("2026-13-01")]
    public void Cast_BadStringToDate_RaisesConversionFailed(string text) =>
        AssertSqlMessage($"select cast('{text}' as date)", "Conversion failed when converting date and/or time from character string.");

    [TestMethod]
    [DataRow("cast(cast('2026-05-04' as date) as varchar(10))", "2026-05-04")]
    [DataRow("cast(cast('2026-05-04' as date) as nvarchar(10))", "2026-05-04")]
    public void Cast_DateToString(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void Cast_DateNullPassesThrough() =>
        IsInstanceOfType<DBNull>(ExecuteScalar("select cast(cast(null as date) as varchar(10))"));

    [TestMethod]
    [DataRow("cast('2026-05-04 13:45:30' as datetime2(0))", "2026-05-04T13:45:30")]
    [DataRow("cast('2026-05-04 13:45:30.123' as datetime2(3))", "2026-05-04T13:45:30.123")]
    [DataRow("cast('2026-05-04T13:45:30.1234567' as datetime2(7))", "2026-05-04T13:45:30.1234567")]
    public void Cast_StringToDateTime2(string expression, string expectedIso)
    {
        var dt = IsInstanceOfType<DateTime>(ExecuteScalar($"select {expression}"));
        AreEqual(DateTime.Parse(expectedIso, System.Globalization.CultureInfo.InvariantCulture), dt);
    }

    [TestMethod]
    [DataRow("cast(cast('2026-05-04 13:45:30' as datetime2(0)) as varchar(20))", "2026-05-04 13:45:30")]
    [DataRow("cast(cast('2026-05-04 13:45:30.123' as datetime2(3)) as varchar(25))", "2026-05-04 13:45:30.123")]
    [DataRow("cast(cast('2026-05-04 13:45:30.1234567' as datetime2(7)) as varchar(30))", "2026-05-04 13:45:30.1234567")]
    public void Cast_DateTime2ToString(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void Cast_DateTime2_NarrowingPrecisionRoundsHalfUp()
    {
        var dt = IsInstanceOfType<DateTime>(ExecuteScalar("select cast(cast('2026-05-04 13:45:30.5' as datetime2(7)) as datetime2(0))"));
        AreEqual(new DateTime(2026, 5, 4, 13, 45, 31), dt);
    }

    [TestMethod]
    public void Cast_DateToDateTime2_AppliesMidnight() =>
        AreEqual(new DateTime(2026, 5, 4), IsInstanceOfType<DateTime>(ExecuteScalar("select cast(cast('2026-05-04' as date) as datetime2(7))")));

    [TestMethod]
    public void Cast_DateTime2ToDate_DropsTimePortion() =>
        AreEqual(new DateTime(2026, 5, 4), IsInstanceOfType<DateTime>(ExecuteScalar("select cast(cast('2026-05-04 13:45:30' as datetime2(7)) as date)")));

    [TestMethod]
    public void Cast_DateTime2NullPassesThrough() =>
        IsInstanceOfType<DBNull>(ExecuteScalar("select cast(cast(null as datetime2(7)) as varchar(30))"));

    [TestMethod]
    [DataRow("cast('13:45:30' as time(0))", "13:45:30")]
    [DataRow("cast('13:45:30.123' as time(3))", "13:45:30.123")]
    [DataRow("cast('13:45:30.1234567' as time(7))", "13:45:30.1234567")]
    [DataRow("cast('00:00:00' as time(7))", "00:00:00")]
    public void Cast_StringToTime(string expression, string expected)
    {
        var ts = IsInstanceOfType<TimeSpan>(ExecuteScalar($"select {expression}"));
        AreEqual(TimeSpan.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), ts);
    }

    [TestMethod]
    [DataRow("not a time")]
    [DataRow("25:00:00")]                               // out of [0,24)
    [DataRow("13:45:99")]
    public void Cast_BadStringToTime_RaisesConversionFailed(string text) =>
        AssertSqlMessage($"select cast('{text}' as time(7))", "Conversion failed when converting date and/or time from character string.");

    [TestMethod]
    [DataRow("cast(cast('13:45:30' as time(0)) as varchar(20))", "13:45:30")]
    [DataRow("cast(cast('13:45:30.123' as time(3)) as varchar(20))", "13:45:30.123")]
    [DataRow("cast(cast('13:45:30.1234567' as time(7)) as varchar(20))", "13:45:30.1234567")]
    public void Cast_TimeToString(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void Cast_Time_NarrowingPrecisionRoundsHalfUp() =>
        AreEqual(new TimeSpan(13, 45, 31), IsInstanceOfType<TimeSpan>(ExecuteScalar("select cast(cast('13:45:30.5' as time(7)) as time(0))")));

    [TestMethod]
    public void Cast_DateTime2ToTime_DropsDatePortion() =>
        AreEqual(new TimeSpan(13, 45, 30), IsInstanceOfType<TimeSpan>(ExecuteScalar("select cast(cast('2026-05-04 13:45:30' as datetime2(7)) as time(7))")));

    [TestMethod]
    public void Cast_TimeToDateTime2_FillsLegacyDate()
    {
        // SQL Server fills date portion with 1900-01-01 for time → datetime2.
        AreEqual(new DateTime(1900, 1, 1, 13, 45, 30), IsInstanceOfType<DateTime>(ExecuteScalar("select cast(cast('13:45:30' as time(7)) as datetime2(7))")));
    }

    [TestMethod]
    public void Cast_TimeNullPassesThrough() =>
        IsInstanceOfType<DBNull>(ExecuteScalar("select cast(cast(null as time(7)) as varchar(20))"));

    [TestMethod]
    [DataRow("cast('2026-05-04 13:45:30 -07:00' as datetimeoffset(0))", "2026-05-04T13:45:30-07:00")]
    [DataRow("cast('2026-05-04 13:45:30.123 +03:00' as datetimeoffset(3))", "2026-05-04T13:45:30.123+03:00")]
    [DataRow("cast('2026-05-04 13:45:30.1234567 +00:00' as datetimeoffset(7))", "2026-05-04T13:45:30.1234567+00:00")]
    public void Cast_StringToDateTimeOffset(string expression, string expectedIso)
    {
        var dto = IsInstanceOfType<DateTimeOffset>(ExecuteScalar($"select {expression}"));
        AreEqual(DateTimeOffset.Parse(expectedIso, System.Globalization.CultureInfo.InvariantCulture), dto);
    }

    [TestMethod]
    public void Cast_StringWithoutOffsetToDateTimeOffset_AssumesUtc()
    {
        var dto = IsInstanceOfType<DateTimeOffset>(ExecuteScalar("select cast('2026-05-04 13:45:30' as datetimeoffset(0))"));
        AreEqual(new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.Zero), dto);
        AreEqual(TimeSpan.Zero, dto.Offset);
    }

    [TestMethod]
    [DataRow("not a datetimeoffset")]
    [DataRow("2026-13-01 13:45:30 +00:00")]
    public void Cast_BadStringToDateTimeOffset_RaisesConversionFailed(string text) =>
        AssertSqlMessage($"select cast('{text}' as datetimeoffset(7))", "Conversion failed when converting date and/or time from character string.");

    [TestMethod]
    [DataRow("cast(cast('2026-05-04 13:45:30 -07:00' as datetimeoffset(0)) as varchar(40))", "2026-05-04 13:45:30 -07:00")]
    [DataRow("cast(cast('2026-05-04 13:45:30.123 +03:00' as datetimeoffset(3)) as varchar(40))", "2026-05-04 13:45:30.123 +03:00")]
    public void Cast_DateTimeOffsetToString(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void Cast_DateTimeOffset_NarrowingPrecisionRoundsHalfUp()
    {
        var dto = IsInstanceOfType<DateTimeOffset>(ExecuteScalar("select cast(cast('2026-05-04 13:45:30.5 -07:00' as datetimeoffset(7)) as datetimeoffset(0))"));
        AreEqual(new DateTimeOffset(2026, 5, 4, 13, 45, 31, TimeSpan.FromHours(-7)), dto);
    }

    [TestMethod]
    public void Cast_DateTime2ToDateTimeOffset_AssumesZeroOffset()
    {
        var dto = IsInstanceOfType<DateTimeOffset>(ExecuteScalar("select cast(cast('2026-05-04 13:45:30' as datetime2(7)) as datetimeoffset(7))"));
        AreEqual(new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.Zero), dto);
    }

    [TestMethod]
    public void Cast_DateTimeOffsetToDateTime2_DropsOffset()
    {
        // datetimeoffset → datetime2 returns the LOCAL wall-clock and discards the offset.
        AreEqual(new DateTime(2026, 5, 4, 13, 45, 30), IsInstanceOfType<DateTime>(ExecuteScalar("select cast(cast('2026-05-04 13:45:30 -07:00' as datetimeoffset(7)) as datetime2(7))")));
    }

    [TestMethod]
    public void Cast_DateToDateTimeOffset_AppliesMidnightAndZeroOffset() =>
        AreEqual(new DateTimeOffset(2026, 5, 4, 0, 0, 0, TimeSpan.Zero), IsInstanceOfType<DateTimeOffset>(ExecuteScalar("select cast(cast('2026-05-04' as date) as datetimeoffset(7))")));

    [TestMethod]
    public void Cast_DateTimeOffsetToDate_DropsTimeAndOffset()
    {
        // Local date, not the UTC-shifted one.
        AreEqual(new DateTime(2026, 5, 4), IsInstanceOfType<DateTime>(ExecuteScalar("select cast(cast('2026-05-04 23:45:30 -07:00' as datetimeoffset(7)) as date)")));
    }

    [TestMethod]
    public void Cast_TimeToDateTimeOffset_FillsLegacyDateAndZeroOffset() =>
        AreEqual(new DateTimeOffset(1900, 1, 1, 13, 45, 30, TimeSpan.Zero), IsInstanceOfType<DateTimeOffset>(ExecuteScalar("select cast(cast('13:45:30' as time(7)) as datetimeoffset(7))")));

    [TestMethod]
    public void Cast_DateTimeOffsetToTime_DropsDateAndOffset() =>
        AreEqual(new TimeSpan(13, 45, 30), IsInstanceOfType<TimeSpan>(ExecuteScalar("select cast(cast('2026-05-04 13:45:30 -07:00' as datetimeoffset(7)) as time(7))")));

    [TestMethod]
    public void Cast_DateTimeOffsetNullPassesThrough() =>
        IsInstanceOfType<DBNull>(ExecuteScalar("select cast(cast(null as datetimeoffset(7)) as varchar(40))"));

    [TestMethod]
    public void Cast_UnknownTypeName_RaisesMsg243() =>
        AssertSqlMessage("select cast('x' as intz)", "Type intz is not a defined system type.");

    [TestMethod]
    public void Cast_LengthSpecifierOnFixedType_RaisesMsg291()
    {
        // Note: SQL Server's Msg 291 message lacks a trailing period.
        AssertSqlMessage("select cast('x' as int(4))", "CAST or CONVERT: invalid attributes specified for type 'int'");
    }

    [TestMethod]
    public void Cast_VarcharSizeExceedsMaximum_UsesTypeWording()
    {
        // CAST form of Msg 131 says "given to the type 'T'" rather than column-context "given to the column 'V'".
        AssertSqlMessage("select cast('x' as varchar(8001))", "The size (8001) given to the type 'varchar' exceeds the maximum allowed for any data type (8000).");
    }

    [TestMethod]
    public void Cast_VarbinarySizeExceedsMaximum_UsesTypeWording() =>
        AssertSqlMessage("select cast(0xAB as varbinary(8001))", "The size (8001) given to the type 'varbinary' exceeds the maximum allowed for any data type (8000).");

    [TestMethod]
    public void Cast_NVarcharSizeExceedsMaximum_UsesConvertSpecificationWording() =>
        AssertSqlMessage("select cast(N'x' as nvarchar(4001))", "The size (4001) given to the convert specification 'nvarchar' exceeds the maximum allowed for any data type (4000).");

    [TestMethod]
    public void Cast_InvalidScale_OnLine1_PrefixesLine1()
    {
        // Msg 1002 is one of the few SQL Server errors that puts the line number into the message text itself.
        AssertSqlMessage("select cast('x' as datetime2(8))", "Line 1: Specified scale 8 is invalid.");
    }

    [TestMethod]
    public void Cast_InvalidScale_OnLaterLine_PrefixesActualLine()
    {
        // Line N tracks the line of the offending type token. Single-statement form so ExecuteScalar surfaces the parse error immediately.
        AssertSqlMessage("\n\nselect cast('x' as datetime2(8))", "Line 3: Specified scale 8 is invalid.");
    }

    [TestMethod]
    public void Cast_InvalidScale_InLaterStatementOfBatch_FiresWhenReaderReachesIt()
    {
        // Multi-statement batches parse lazily one statement at a time; statement 3's error doesn't fire until NextResult reaches it.
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("select 1;\nselect 2;\nselect cast('x' as datetime2(8))");
        using var reader = command.ExecuteReader();

        IsTrue(reader.NextResult());
        var ex = Throws<System.Data.Common.DbException>(() =>
        {
            _ = reader.NextResult();
            _ = reader.NextResult();
        });
        AreEqual("Line 3: Specified scale 8 is invalid.", ex.Message);
    }

    [TestMethod]
    [DataRow("date")]
    [DataRow("datetime2")]
    [DataRow("time")]
    [DataRow("datetimeoffset")]
    public void Cast_IntToNonLegacyDateType_RaisesMsg529(string targetType)
    {
        // Only legacy datetime/smalldatetime accept integer casts; non-legacy types reject with Msg 529.
        AssertSqlMessage($"select cast(0 as {targetType})", $"Explicit conversion from data type int to {targetType} is not allowed.");
    }

    [TestMethod]
    [DataRow("date")]
    [DataRow("datetime2")]
    [DataRow("time")]
    [DataRow("datetimeoffset")]
    public void Cast_NonLegacyDateTypeToInt_RaisesMsg529(string sourceType)
    {
        var seed = sourceType == "time" ? "12:00:00" : "2024-01-15";
        AssertSqlMessage($"select cast(cast('{seed}' as {sourceType}) as int)", $"Explicit conversion from data type {sourceType} to int is not allowed.");
    }

    [TestMethod]
    [DataRow("cast('hello world' as varchar(5))", "hello")]
    [DataRow("cast(N'hello world' as nvarchar(5))", "hello")]
    [DataRow("cast('hello world' as varchar(1))", "h")]
    [DataRow("cast(cast('2026-05-09' as date) as varchar(9))", "2026-05-0")]
    [DataRow("cast(cast('12:00:00' as time(0)) as varchar(7))", "12:00:0")]
    [DataRow("cast(cast('2026-05-09 12:00:00' as datetime) as varchar(10))", "May  9 202")]
    public void Cast_NarrowVarchar_StringAndDateSources_SilentlyTruncate(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void Cast_NarrowVarbinary_SilentlyTruncates()
    {
        var result = ExecuteScalar("select cast(0x0102030405 as varbinary(3))") as byte[];
        IsNotNull(result);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, result);
    }

    /// <summary>
    /// String → varbinary CAST encodes through CP1252 for varchar/char and
    /// UTF-16 LE for nvarchar/nchar, then the CAST-level path truncates to
    /// the declared length. <c>varbinary(N)</c> never pads. Probe-confirmed
    /// against SQL Server 2025 (2026-05-22).
    /// </summary>
    [TestMethod]
    public void Cast_VarcharToVarbinary_EncodesCp1252Bytes()
    {
        var result = ExecuteScalar("select cast('abc' as varbinary(10))") as byte[];
        IsNotNull(result);
        CollectionAssert.AreEqual("abc"u8.ToArray(), result);
    }

    [TestMethod]
    public void Cast_NarrowVarbinary_StringSource_Truncates()
    {
        var result = ExecuteScalar("select cast('abcdefghijklmn' as varbinary(5))") as byte[];
        IsNotNull(result);
        CollectionAssert.AreEqual("abcde"u8.ToArray(), result);
    }

    [TestMethod]
    public void Cast_NvarcharToVarbinary_EncodesUtf16LeBytes()
    {
        var result = ExecuteScalar("select cast(N'abc' as varbinary(10))") as byte[];
        IsNotNull(result);
        CollectionAssert.AreEqual(new byte[] { 0x61, 0x00, 0x62, 0x00, 0x63, 0x00 }, result);
    }

    /// <summary>
    /// <c>binary(N)</c> right-pads with zero bytes when the source encoding
    /// is shorter than N (verified <c>CAST('abc' AS BINARY(10)) →
    /// 0x61626300000000000000</c>). FromBinary applies the
    /// pad-or-truncate normalization inside the CoerceTo path.
    /// </summary>
    [TestMethod]
    public void Cast_VarcharToFixedBinary_PadsWithZeros()
    {
        var result = ExecuteScalar("select cast('abc' as binary(10))") as byte[];
        IsNotNull(result);
        CollectionAssert.AreEqual(new byte[] { 0x61, 0x62, 0x63, 0, 0, 0, 0, 0, 0, 0 }, result);
    }

    [TestMethod]
    public void Cast_VarcharToFixedBinary_LongerSourceTruncates()
    {
        var result = ExecuteScalar("select cast('abcdefghijklmn' as binary(5))") as byte[];
        IsNotNull(result);
        CollectionAssert.AreEqual("abcde"u8.ToArray(), result);
    }

    [TestMethod]
    [DataRow("cast(123456 as varchar(3))", "*")]
    [DataRow("cast(123456 as varchar(5))", "*")]
    [DataRow("cast(-123 as varchar(3))", "*")]
    [DataRow("cast(cast(255 as tinyint) as varchar(2))", "*")]
    [DataRow("cast(cast(32767 as smallint) as varchar(4))", "*")]
    public void Cast_IntToNarrowVarchar_ReturnsAsteriskFallback(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void Cast_IntToNarrowNVarchar_RaisesMsg8115() =>
        AssertSqlMessage("select cast(123456 as nvarchar(3))", "Arithmetic overflow error converting expression to data type nvarchar.");

    /// <summary>bigint doesn't get the asterisk fallback that smaller integers do.</summary>
    [TestMethod]
    public void Cast_BigIntToNarrowVarchar_RaisesMsg8115() =>
        AssertSqlMessage("select cast(cast(123 as bigint) as varchar(2))", "Arithmetic overflow error converting expression to data type varchar.");

    /// <summary>
    /// Decimal/numeric source picks the "numeric"-worded variant of Msg 8115,
    /// distinct from the "expression"-worded integer variant.
    /// </summary>
    [TestMethod]
    public void Cast_DecimalToNarrowVarchar_RaisesMsg8115WithNumericWording() =>
        AssertSqlMessage("select cast(cast(123.45 as decimal(5,2)) as varchar(5))", "Arithmetic overflow error converting numeric to data type varchar.");

    [TestMethod]
    public void Cast_MoneyToNarrowVarchar_RaisesMsg234() =>
        AssertSqlMessage("select cast(cast(99.99 as money) as varchar(4))", "There is insufficient result space to convert a money value to varchar.");

    [TestMethod]
    public void Cast_FloatToNarrowVarchar_RaisesMsg232()
    {
        var ex = Throws<System.Data.Common.DbException>(() => ExecuteScalar("select cast(cast(1.5e30 as float) as varchar(5))"));
        Contains("Arithmetic overflow error for type varchar", ex.Message);
    }

    /// <summary>
    /// CAST/CONVERT context defaults to length 30 (vs column-context 1). 'hello'
    /// fits in 30 so it round-trips; would truncate to 'h' if width were 1.
    /// </summary>
    [TestMethod]
    [DataRow("varchar")]
    [DataRow("nvarchar")]
    public void Cast_NoParensVarcharFamily_DefaultsToWidth30(string typeName) =>
        AreEqual("hello", ExecuteScalar($"select cast('hello' as {typeName})"));

    [TestMethod]
    public void Cast_NoParensVarbinary_DefaultsToWidth30()
    {
        // 5 bytes fits in 30; would truncate to 1 byte if width were the column-context default.
        var result = ExecuteScalar("select cast(0x0102030405 as varbinary)") as byte[];
        IsNotNull(result);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, result);
    }

    [TestMethod]
    [DataRow("try_cast('42' as int)", 42)]
    [DataRow("try_cast(42 as int)", 42)]
    [DataRow("try_cast(42 as bigint)", 42L)]
    [DataRow("try_cast('  42  ' as int)", 42)]
    [DataRow("try_cast('' as int)", 0)]
    public void TryCast_GoodValue_ReturnsValue(string expression, object expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void TryCast_NullSource_ReturnsNull() =>
        IsInstanceOfType<DBNull>(ExecuteScalar("select try_cast(null as int)"));

    [TestMethod]
    [DataRow("try_cast('abc' as int)")]                       // Msg 245
    [DataRow("try_cast('1.5' as int)")]                       // Msg 245
    [DataRow("try_cast('0x05' as int)")]                      // Msg 245
    [DataRow("try_cast('not a date' as date)")]               // Msg 241
    [DataRow("try_cast('2026-13-01' as date)")]               // Msg 241
    [DataRow("try_cast('not a guid' as uniqueidentifier)")]   // Msg 8169
    [DataRow("try_cast('99999' as tinyint)")]                 // Msg 244
    [DataRow("try_cast('99999' as smallint)")]                // Msg 244
    [DataRow("try_cast(99999 as tinyint)")]                   // Msg 244
    [DataRow("try_cast(2147483648 as int)")]                  // Msg 8115
    [DataRow("try_cast(123.456 as decimal(3,1))")]            // Msg 8115
    [DataRow("try_cast('9999-12-31 23:59:59.999' as datetime)")] // Msg 242
    public void TryCast_ConversionFailure_ReturnsNull(string expression) =>
        IsInstanceOfType<DBNull>(ExecuteScalar($"select {expression}"));

    /// <summary>
    /// Mirrors CAST: silent truncation for string sources is not a
    /// conversion failure, so TRY_CAST returns the truncated string, not NULL.
    /// </summary>
    [TestMethod]
    public void TryCast_OversizeStringTruncates() =>
        AreEqual("hel", ExecuteScalar("select try_cast('hello' as varchar(3))"));

    [TestMethod]
    public void TryCast_ExplicitConversionNotAllowed_StillThrowsMsg529() =>
        AssertSqlMessage("select try_cast(0 as date)", "Explicit conversion from data type int to date is not allowed.");

    [TestMethod]
    public void TryCast_InnerCastFailure_StillPropagates()
    {
        // TRY_CAST swallows only the cast-level failure, not source-evaluation
        // errors. The inner CAST raises Msg 245 before the outer wrapper sees a value.
        var ex = Throws<System.Data.Common.DbException>(() => ExecuteScalar("select try_cast(cast('abc' as int) as bigint)"));
        Contains("Conversion failed", ex.Message);
    }

    // --- varbinary → date-family decoding ---
    //
    // SSMS bulk-INSERT export emits each date/time/datetime/datetimeoffset
    // literal as `CAST(0x… AS <type>)` against the SQL Server binary wire
    // format. The reference bytes below were probed against SQL Server 2025
    // on 2026-05-17 using
    // `select cast(cast('2017-07-26T19:46:29.3386912+00:00' as datetimeoffset(7)) as varbinary(20))`
    // and matching variants for each target type. Layouts:
    //   date              — 3 bytes LE: days since 0001-01-01
    //   time(N)           — 1 scale byte + LE time ticks (10^-N s units),
    //                       3 / 4 / 5 bytes for scales 0–2 / 3–4 / 5–7
    //   datetime2(N)      — 1 scale byte + LE time + LE 3-byte date
    //   datetimeoffset(N) — same as datetime2 + LE int16 offset minutes;
    //                       SQL Server stores the time + date in UTC
    //   datetime          — 8 bytes BE: int32 days since 1900-01-01 + uint32
    //                       1/300-second ticks since midnight
    //   smalldatetime     — 4 bytes BE: uint16 days + uint16 minutes

    [TestMethod]
    public void CastVarbinaryToDate_RoundTripsViaWireFormat() =>
        AreEqual(new DateTime(2017, 7, 26), ExecuteScalar("select cast(0x173D0B as date)"));

    [TestMethod]
    public void CastVarbinaryToDateTime2Scale7_RoundTripsViaWireFormat() =>
        AreEqual(new DateTime(2017, 7, 26, 19, 46, 29), ExecuteScalar("select cast(0x078058F3BFA5173D0B as datetime2(7))"));

    [TestMethod]
    public void CastVarbinaryToDateTime2Scale0_RoundTripsViaWireFormat() =>
        AreEqual(new DateTime(2017, 7, 26, 19, 46, 29), ExecuteScalar("select cast(0x00151601173D0B as datetime2(0))"));

    [TestMethod]
    public void CastVarbinaryToDateTimeOffsetUtc_RoundTripsViaWireFormat() =>
        AreEqual(
            new DateTimeOffset(2017, 7, 26, 19, 46, 29, TimeSpan.Zero).AddTicks(3386912),
            ExecuteScalar("select cast(0x07A00627C0A5173D0B0000 as datetimeoffset(7))"));

    // -05:00 wall-clock has UTC components 2017-07-27T00:46:29.3386912,
    // stored as the UTC time/date bytes plus a signed offset of -300 minutes.
    [TestMethod]
    public void CastVarbinaryToDateTimeOffsetWithOffset_RoundTripsViaWireFormat() =>
        AreEqual(
            new DateTimeOffset(2017, 7, 26, 19, 46, 29, TimeSpan.FromHours(-5)).AddTicks(3386912),
            ExecuteScalar("select cast(0x07A04E937E06183D0BD4FE as datetimeoffset(7))"));

    [TestMethod]
    public void CastVarbinaryToLegacyDateTime_RoundTripsViaWireFormat() =>
        AreEqual(new DateTime(2017, 7, 26, 19, 46, 29), ExecuteScalar("select cast(0x0000A7BC0145E09C as datetime)"));

    [TestMethod]
    public void CastVarbinaryToSmallDateTime_RoundTripsViaWireFormat() =>
        AreEqual(new DateTime(2017, 7, 26, 19, 46, 0), ExecuteScalar("select cast(0xA7BC04A2 as smalldatetime)"));

    [TestMethod]
    public void CastVarbinaryToTime_RoundTripsViaWireFormat() =>
        AreEqual(new TimeSpan(19, 46, 29), ExecuteScalar("select cast(0x078058F3BFA5 as time(7))"));
}
