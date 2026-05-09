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
}
