using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// How CAST reads a string as each date-time type — a sample of a matrix of
/// some two hundred and sixty strings probed 2026-09-24 against SQL Server
/// 2025, where every value and every error number matched. Each expected
/// value is real's <c>CONVERT(varchar, TRY_CAST(s AS type), 121)</c>, and
/// <c>#</c> is a string it refuses.
/// </summary>
[TestClass]
public sealed class DateTimeStringTests
{
    private static string Read(string text, string type) =>
        (string)new Simulation().ExecuteScalar(
            $"select isnull(convert(varchar(40), try_cast(N'{text.Replace("'", "''", StringComparison.Ordinal)}' as {type}), 121), '#')")!;

    [TestMethod]
    // A space-separated date and time for date and time alike.
    [DataRow("2024-12-31 23:59:59", "date", "2024-12-31")]
    [DataRow("2024-12-31 23:59:59", "time", "23:59:59.0000000")]
    // The ISO T wants seconds.
    [DataRow("2024-12-31T23:59", "datetime2", "#")]
    // Past seven fractional digits the newer types round; the legacy pair takes three.
    [DataRow("2024-12-31 23:59:59.12345678", "datetime2", "2024-12-31 23:59:59.1234568")]
    [DataRow("2024-12-31 23:59:59.1234567", "datetime", "#")]
    [DataRow("2024-12-31 23:59:59.99999999", "datetime2", "2025-01-01 00:00:00.0000000")]
    [DataRow("2024-12-31 23:59:59.99999999", "time", "23:59:59.9999999")]
    [DataRow("10:00:00.1234567891", "time", "#")]
    // After a colon, milliseconds.
    [DataRow("10:00:00:5", "time", "10:00:00.0050000")]
    [DataRow("10:00:00:1234", "time", "#")]
    // An empty string is 1900-01-01 for every type.
    [DataRow("", "date", "1900-01-01")]
    [DataRow("", "datetime2", "1900-01-01 00:00:00.0000000")]
    [DataRow("12:00", "date", "1900-01-01")]
    // Month-day-year, and a one- or two-digit year pivoting at 50.
    [DataRow("12/31/2024", "date", "2024-12-31")]
    [DataRow("31/12/2024", "date", "#")]
    [DataRow("1/1/49", "date", "2049-01-01")]
    [DataRow("1/1/50", "date", "1950-01-01")]
    [DataRow("1/2/3", "date", "2003-01-02")]
    [DataRow("24/12/31", "date", "#")]
    [DataRow("241231", "date", "2024-12-31")]
    [DataRow("2024", "date", "2024-01-01")]
    [DataRow("99", "date", "#")]
    // English month names, three-letter or in full.
    [DataRow("Jan 5 2024", "date", "2024-01-05")]
    [DataRow("5 January 2024", "date", "2024-01-05")]
    [DataRow("January 2024", "date", "2024-01-01")]
    [DataRow("Jun 2024 15", "date", "2024-06-15")]
    [DataRow("2024 Jun", "date", "2024-06-01")]
    [DataRow("05-JAN-24", "date", "2024-01-05")]
    [DataRow("5Jan2024", "date", "2024-01-05")]
    [DataRow("Sept 5 2024", "date", "#")]
    [DataRow("Jan-05-2024", "date", "#")]
    [DataRow("January 5, 2024 10:00 AM", "datetime", "2024-01-05 10:00:00.000")]
    // AM and PM.
    [DataRow("13:00 PM", "time", "13:00:00.0000000")]
    [DataRow("12:00 AM", "time", "00:00:00.0000000")]
    [DataRow("1PM", "time", "13:00:00.0000000")]
    [DataRow("00 PM", "time", "#")]
    [DataRow("2024-12-31 23", "datetime2", "#")]
    // The legacy pair alone: a time before the date, mixed separators, a fraction after the minutes.
    [DataRow("10:00 2024-01-01", "datetime", "2024-01-01 10:00:00.000")]
    [DataRow("10:00 2024-01-01", "datetime2", "#")]
    [DataRow("2024-12/31", "smalldatetime", "2024-12-31 00:00:00.000")]
    [DataRow("2024-12/31", "date", "#")]
    [DataRow("2024-12-31 10:00.5", "datetime", "2024-12-31 10:00:00.500")]
    // Offsets: the newer types take ±h:mm up to fourteen hours; the legacy pair a Z after an ISO T only.
    [DataRow("2024-12-31 23:59:59 +2:00", "datetimeoffset", "2024-12-31 23:59:59.0000000 +02:00")]
    [DataRow("2024-12-31 23:59:59 +14:01", "datetimeoffset", "#")]
    [DataRow("2024-12-31 23:59:59 +02:00", "datetime", "#")]
    [DataRow("2024-12-31T23:59:59Z", "datetime", "2024-12-31 23:59:59.000")]
    [DataRow("2024-12-31 10:00:00 Z", "datetime", "#")]
    [DataRow("2024-06-15T10:30:00 Z", "datetime2", "#")]
    // Tabs are spaces to the newer types alone.
    [DataRow("2024-06-15\t10:30", "datetime2", "2024-06-15 10:30:00.0000000")]
    [DataRow("2024-06-15\t10:30", "datetime", "#")]
    // The legacy pair's own rounding.
    [DataRow("2024-12-31 23:59:59.998", "datetime", "2024-12-31 23:59:59.997")]
    [DataRow("2024-12-31 23:59:29.999", "smalldatetime", "2025-01-01 00:00:00.000")]
    public void CastReadsTheStringAsRealDoes(string text, string type, string expected)
        => AreEqual(expected, Read(text, type));

    [TestMethod]
    [DataRow("2024-13-01", "datetime", 242)]
    [DataRow("31/12/2024", "datetime", 242)]
    [DataRow("24:00", "datetime", 242)]
    [DataRow("2024-12-31 23", "datetime", 242)]
    [DataRow("2024-12-31 .5", "datetime", 242)]
    [DataRow("Jan-05-2024", "smalldatetime", 242)]
    [DataRow("Sept 5 2024", "datetime", 241)]
    [DataRow("2024-12-31T23:59", "datetime", 241)]
    [DataRow("Sept 5 2024", "smalldatetime", 295)]
    [DataRow("2024-13-01", "datetime2", 241)]
    public void LegacyTypesSplitUnreadableFromOutOfRange(string text, string type, int number)
        => new Simulation().AssertSqlError($"select cast('{text}' as {type})", number);

    [TestMethod]
    public void OffsetCarryingUtcPastYear9999_RaisesMsg8114()
        => new Simulation().AssertSqlError(
            "select cast(N'9999-12-31 23:59:59 -05:00' as datetimeoffset)",
            8114,
            "Error converting data type nvarchar to datetimeoffset.");
}
