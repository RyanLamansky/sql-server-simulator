using System.Data.Common;
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
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select cast(300 as tinyint)"));
        Contains("Arithmetic overflow", ex.Message);
    }

    [TestMethod]
    [DataRow("cast('42' as int)", 42)]
    [DataRow("cast('-42' as int)", -42)]
    [DataRow("cast('+42' as int)", 42)]
    [DataRow("cast('  42  ' as int)", 42)]      // whitespace tolerated
    [DataRow("cast('007' as int)", 7)]          // leading zeros fine
    [DataRow("cast('' as int)", 0)]             // SQL Server quirk: empty → 0
    [DataRow("cast('   ' as int)", 0)]          // whitespace-only → 0
    public void Cast_StringToInt32(string expression, int expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    [DataRow("'abc'", "Conversion failed when converting the varchar value 'abc' to data type int.")]
    [DataRow("'42.5'", "Conversion failed when converting the varchar value '42.5' to data type int.")]
    [DataRow("'0x42'", "Conversion failed when converting the varchar value '0x42' to data type int.")]
    public void Cast_BadStringToInt32_RaisesMsg245(string source, string expected)
    {
        var ex = Throws<DbException>(() => ExecuteScalar($"select cast({source} as int)"));
        AreEqual(expected, ex.Message);
    }

    [TestMethod]
    public void Cast_NVarcharToInt32_UsesNVarcharInErrorMessage()
    {
        // Source-type wording in Msg 245 reflects the actual source type.
        var ex = Throws<DbException>(() => ExecuteScalar("select cast(N'abc' as int)"));
        AreEqual("Conversion failed when converting the nvarchar value 'abc' to data type int.", ex.Message);
    }

    [TestMethod]
    public void Cast_StringOverflow_TinyInt_RaisesMsg244WithINT1()
    {
        // Tinyint/smallint use Msg 244 with internal SQL names INT1/INT2.
        var ex = Throws<DbException>(() => ExecuteScalar("select cast('300' as tinyint)"));
        AreEqual("The conversion of the varchar value '300' overflowed an INT1 column. Use a larger integer column.", ex.Message);
    }

    [TestMethod]
    public void Cast_StringOverflow_SmallInt_RaisesMsg244WithINT2()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select cast('99999' as smallint)"));
        AreEqual("The conversion of the varchar value '99999' overflowed an INT2 column. Use a larger integer column.", ex.Message);
    }

    [TestMethod]
    public void Cast_StringOverflow_Int_RaisesMsg248()
    {
        // Int has its own overflow message (Msg 248) — distinct text and no
        // "Use a larger integer column" suffix.
        var ex = Throws<DbException>(() => ExecuteScalar("select cast('99999999999' as int)"));
        AreEqual("The conversion of the varchar value '99999999999' overflowed an int column.", ex.Message);
    }

    [TestMethod]
    public void Cast_StringOverflow_BigInt_RaisesMsg8115()
    {
        // Bigint overflow falls through to the generic arithmetic-overflow
        // message; no source-value detail.
        var ex = Throws<DbException>(() => ExecuteScalar("select cast('99999999999999999999' as bigint)"));
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
        var ex = Throws<DbException>(() => ExecuteScalar($"select cast({source} as bit)"));
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
        // Numeric literal tokens are int-bounded; the source bigint here is
        // constructed by parsing a string instead.
        AreEqual("9223372036854775807", ExecuteScalar("select cast(cast('9223372036854775807' as bigint) as varchar(30))"));
    }

    [TestMethod]
    [DataRow("cast('2026-05-04' as date)", "2026-05-04")]
    [DataRow("cast('20260504' as date)", "2026-05-04")]
    [DataRow("cast('2026-05-04T13:45:30' as date)", "2026-05-04")]
    [DataRow("cast(N'2026-05-04' as date)", "2026-05-04")]
    public void Cast_StringToDate(string expression, string expectedIso)
    {
        var value = ExecuteScalar($"select {expression}");
        var dt = IsInstanceOfType<DateTime>(value);
        AreEqual(DateTime.Parse(expectedIso, System.Globalization.CultureInfo.InvariantCulture), dt);
    }

    [TestMethod]
    [DataRow("not a date")]
    [DataRow("2026-13-01")]
    public void Cast_BadStringToDate_RaisesConversionFailed(string text)
    {
        var ex = Throws<DbException>(() => ExecuteScalar($"select cast('{text}' as date)"));
        AreEqual("Conversion failed when converting date and/or time from character string.", ex.Message);
    }

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
        var value = ExecuteScalar($"select {expression}");
        var dt = IsInstanceOfType<DateTime>(value);
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
        // 0.5 second exactly at precision 0 => round to next second.
        var result = ExecuteScalar("select cast(cast('2026-05-04 13:45:30.5' as datetime2(7)) as datetime2(0))");
        var dt = IsInstanceOfType<DateTime>(result);
        AreEqual(new DateTime(2026, 5, 4, 13, 45, 31), dt);
    }

    [TestMethod]
    public void Cast_DateToDateTime2_AppliesMidnight()
    {
        var result = ExecuteScalar("select cast(cast('2026-05-04' as date) as datetime2(7))");
        AreEqual(new DateTime(2026, 5, 4), IsInstanceOfType<DateTime>(result));
    }

    [TestMethod]
    public void Cast_DateTime2ToDate_DropsTimePortion()
    {
        var result = ExecuteScalar("select cast(cast('2026-05-04 13:45:30' as datetime2(7)) as date)");
        AreEqual(new DateTime(2026, 5, 4), IsInstanceOfType<DateTime>(result));
    }

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
        var value = ExecuteScalar($"select {expression}");
        var ts = IsInstanceOfType<TimeSpan>(value);
        AreEqual(TimeSpan.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), ts);
    }

    [TestMethod]
    [DataRow("not a time")]
    [DataRow("25:00:00")]                               // out of [0,24)
    [DataRow("13:45:99")]
    public void Cast_BadStringToTime_RaisesConversionFailed(string text)
    {
        var ex = Throws<DbException>(() => ExecuteScalar($"select cast('{text}' as time(7))"));
        AreEqual("Conversion failed when converting date and/or time from character string.", ex.Message);
    }

    [TestMethod]
    [DataRow("cast(cast('13:45:30' as time(0)) as varchar(20))", "13:45:30")]
    [DataRow("cast(cast('13:45:30.123' as time(3)) as varchar(20))", "13:45:30.123")]
    [DataRow("cast(cast('13:45:30.1234567' as time(7)) as varchar(20))", "13:45:30.1234567")]
    public void Cast_TimeToString(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void Cast_Time_NarrowingPrecisionRoundsHalfUp()
    {
        // 0.5s exactly at precision 0 => round to next second.
        var result = ExecuteScalar("select cast(cast('13:45:30.5' as time(7)) as time(0))");
        AreEqual(new TimeSpan(13, 45, 31), IsInstanceOfType<TimeSpan>(result));
    }

    [TestMethod]
    public void Cast_DateTime2ToTime_DropsDatePortion()
    {
        var result = ExecuteScalar("select cast(cast('2026-05-04 13:45:30' as datetime2(7)) as time(7))");
        AreEqual(new TimeSpan(13, 45, 30), IsInstanceOfType<TimeSpan>(result));
    }

    [TestMethod]
    public void Cast_TimeToDateTime2_FillsLegacyDate()
    {
        // SQL Server fills the date portion with 1900-01-01 for time → datetime2.
        var result = ExecuteScalar("select cast(cast('13:45:30' as time(7)) as datetime2(7))");
        AreEqual(new DateTime(1900, 1, 1, 13, 45, 30), IsInstanceOfType<DateTime>(result));
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
        var value = ExecuteScalar($"select {expression}");
        var dto = IsInstanceOfType<DateTimeOffset>(value);
        AreEqual(DateTimeOffset.Parse(expectedIso, System.Globalization.CultureInfo.InvariantCulture), dto);
    }

    [TestMethod]
    public void Cast_StringWithoutOffsetToDateTimeOffset_AssumesUtc()
    {
        // SQL Server treats an offsetless string as +00:00 when casting to datetimeoffset.
        var value = ExecuteScalar("select cast('2026-05-04 13:45:30' as datetimeoffset(0))");
        var dto = IsInstanceOfType<DateTimeOffset>(value);
        AreEqual(new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.Zero), dto);
        AreEqual(TimeSpan.Zero, dto.Offset);
    }

    [TestMethod]
    [DataRow("not a datetimeoffset")]
    [DataRow("2026-13-01 13:45:30 +00:00")]
    public void Cast_BadStringToDateTimeOffset_RaisesConversionFailed(string text)
    {
        var ex = Throws<DbException>(() => ExecuteScalar($"select cast('{text}' as datetimeoffset(7))"));
        AreEqual("Conversion failed when converting date and/or time from character string.", ex.Message);
    }

    [TestMethod]
    [DataRow("cast(cast('2026-05-04 13:45:30 -07:00' as datetimeoffset(0)) as varchar(40))", "2026-05-04 13:45:30 -07:00")]
    [DataRow("cast(cast('2026-05-04 13:45:30.123 +03:00' as datetimeoffset(3)) as varchar(40))", "2026-05-04 13:45:30.123 +03:00")]
    public void Cast_DateTimeOffsetToString(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void Cast_DateTimeOffset_NarrowingPrecisionRoundsHalfUp()
    {
        var result = ExecuteScalar("select cast(cast('2026-05-04 13:45:30.5 -07:00' as datetimeoffset(7)) as datetimeoffset(0))");
        var dto = IsInstanceOfType<DateTimeOffset>(result);
        AreEqual(new DateTimeOffset(2026, 5, 4, 13, 45, 31, TimeSpan.FromHours(-7)), dto);
    }

    [TestMethod]
    public void Cast_DateTime2ToDateTimeOffset_AssumesZeroOffset()
    {
        var result = ExecuteScalar("select cast(cast('2026-05-04 13:45:30' as datetime2(7)) as datetimeoffset(7))");
        var dto = IsInstanceOfType<DateTimeOffset>(result);
        AreEqual(new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.Zero), dto);
    }

    [TestMethod]
    public void Cast_DateTimeOffsetToDateTime2_DropsOffset()
    {
        // SQL Server: casting datetimeoffset → datetime2 returns the LOCAL
        // (offset-bearing) wall-clock and discards the offset.
        var result = ExecuteScalar("select cast(cast('2026-05-04 13:45:30 -07:00' as datetimeoffset(7)) as datetime2(7))");
        AreEqual(new DateTime(2026, 5, 4, 13, 45, 30), IsInstanceOfType<DateTime>(result));
    }

    [TestMethod]
    public void Cast_DateToDateTimeOffset_AppliesMidnightAndZeroOffset()
    {
        var result = ExecuteScalar("select cast(cast('2026-05-04' as date) as datetimeoffset(7))");
        AreEqual(new DateTimeOffset(2026, 5, 4, 0, 0, 0, TimeSpan.Zero), IsInstanceOfType<DateTimeOffset>(result));
    }

    [TestMethod]
    public void Cast_DateTimeOffsetToDate_DropsTimeAndOffset()
    {
        var result = ExecuteScalar("select cast(cast('2026-05-04 23:45:30 -07:00' as datetimeoffset(7)) as date)");
        // Local date, not the UTC-shifted one.
        AreEqual(new DateTime(2026, 5, 4), IsInstanceOfType<DateTime>(result));
    }

    [TestMethod]
    public void Cast_TimeToDateTimeOffset_FillsLegacyDateAndZeroOffset()
    {
        var result = ExecuteScalar("select cast(cast('13:45:30' as time(7)) as datetimeoffset(7))");
        AreEqual(new DateTimeOffset(1900, 1, 1, 13, 45, 30, TimeSpan.Zero), IsInstanceOfType<DateTimeOffset>(result));
    }

    [TestMethod]
    public void Cast_DateTimeOffsetToTime_DropsDateAndOffset()
    {
        var result = ExecuteScalar("select cast(cast('2026-05-04 13:45:30 -07:00' as datetimeoffset(7)) as time(7))");
        AreEqual(new TimeSpan(13, 45, 30), IsInstanceOfType<TimeSpan>(result));
    }

    [TestMethod]
    public void Cast_DateTimeOffsetNullPassesThrough() =>
        IsInstanceOfType<DBNull>(ExecuteScalar("select cast(cast(null as datetimeoffset(7)) as varchar(40))"));

    [TestMethod]
    public void Cast_UnknownTypeName_RaisesMsg243()
    {
        // CAST takes a different error path than CREATE TABLE: Msg 243
        // ("Type X is not a defined system type.") instead of Msg 2715.
        var ex = Throws<DbException>(() => ExecuteScalar("select cast('x' as intz)"));
        AreEqual("Type intz is not a defined system type.", ex.Message);
    }

    [TestMethod]
    public void Cast_LengthSpecifierOnFixedType_RaisesMsg291()
    {
        // CAST equivalent of CREATE TABLE's Msg 2716. Note the absence of a
        // trailing period — SQL Server's Msg 291 message lacks one.
        var ex = Throws<DbException>(() => ExecuteScalar("select cast('x' as int(4))"));
        AreEqual("CAST or CONVERT: invalid attributes specified for type 'int'", ex.Message);
    }

    [TestMethod]
    public void Cast_VarcharSizeExceedsMaximum_UsesTypeWording()
    {
        // CAST form of Msg 131 says "given to the type 'T'" rather than the
        // column-context "given to the column 'V'".
        var ex = Throws<DbException>(() => ExecuteScalar("select cast('x' as varchar(8001))"));
        AreEqual("The size (8001) given to the type 'varchar' exceeds the maximum allowed for any data type (8000).", ex.Message);
    }

    [TestMethod]
    public void Cast_VarbinarySizeExceedsMaximum_UsesTypeWording()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select cast(0xAB as varbinary(8001))"));
        AreEqual("The size (8001) given to the type 'varbinary' exceeds the maximum allowed for any data type (8000).", ex.Message);
    }

    [TestMethod]
    public void Cast_NVarcharSizeExceedsMaximum_UsesConvertSpecificationWording()
    {
        // nvarchar in CAST has its own phrasing distinct from varchar.
        var ex = Throws<DbException>(() => ExecuteScalar("select cast(N'x' as nvarchar(4001))"));
        AreEqual("The size (4001) given to the convert specification 'nvarchar' exceeds the maximum allowed for any data type (4000).", ex.Message);
    }

    [TestMethod]
    public void Cast_InvalidScale_OnLine1_PrefixesLine1()
    {
        // Msg 1002 is one of the few SQL Server errors that puts the line
        // number into the message text itself.
        var ex = Throws<DbException>(() => ExecuteScalar("select cast('x' as datetime2(8))"));
        AreEqual("Line 1: Specified scale 8 is invalid.", ex.Message);
    }

    [TestMethod]
    public void Cast_InvalidScale_OnLaterLine_PrefixesActualLine()
    {
        // The "Line N" tracks the line of the offending type token, not a
        // fixed line 1 — verified against real SQL Server. Single statement
        // with leading blank lines so ExecuteScalar surfaces the parse error
        // immediately (multi-statement batches parse lazily; see the
        // semicolon-batch variant below).
        var ex = Throws<DbException>(() => ExecuteScalar(
            "\n" +
            "\n" +
            "select cast('x' as datetime2(8))"));
        AreEqual("Line 3: Specified scale 8 is invalid.", ex.Message);
    }

    [TestMethod]
    public void Cast_InvalidScale_InLaterStatementOfBatch_FiresWhenReaderReachesIt()
    {
        // Multi-statement batches are parsed lazily one statement at a time
        // (CreateResultSetsForCommand is an iterator). The third statement's
        // parse error doesn't fire until the consumer asks for its result
        // set via NextResult.
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand(
            "select 1;\n" +
            "select 2;\n" +
            "select cast('x' as datetime2(8))");
        using var reader = command.ExecuteReader();

        IsTrue(reader.NextResult());                            // advance past statement 1
        var ex = Throws<DbException>(() =>
        {
            _ = reader.NextResult();                            // forces parse of statement 3
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
        // Only legacy datetime/smalldatetime accept integer casts; the
        // non-legacy date types reject with Msg 529 ("Explicit conversion
        // ... is not allowed."). The implicit-promotion counterpart raises
        // Msg 206 — see TypePromotionTests.
        var ex = Throws<DbException>(() => ExecuteScalar($"select cast(0 as {targetType})"));
        AreEqual($"Explicit conversion from data type int to {targetType} is not allowed.", ex.Message);
    }

    [TestMethod]
    [DataRow("date")]
    [DataRow("datetime2")]
    [DataRow("time")]
    [DataRow("datetimeoffset")]
    public void Cast_NonLegacyDateTypeToInt_RaisesMsg529(string sourceType)
    {
        var seed = sourceType == "time" ? "12:00:00" : "2024-01-15";
        var ex = Throws<DbException>(() => ExecuteScalar($"select cast(cast('{seed}' as {sourceType}) as int)"));
        AreEqual($"Explicit conversion from data type {sourceType} to int is not allowed.", ex.Message);
    }
}
