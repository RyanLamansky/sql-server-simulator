using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The <c>two digit year cutoff</c> server option: a one- or two-digit year in
/// a date string reads as the year ending in those digits among the hundred
/// ending at the cutoff (probed 2026-10-17 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class TwoDigitYearCutoffTests
{
    private static Simulation WithCutoff(int cutoff)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"exec sp_configure 'show advanced options', 1; reconfigure; exec sp_configure 'two digit year cutoff', {cutoff}; reconfigure"));
        return simulation;
    }

    [TestMethod]
    public void Default_PivotsAtFifty()
        => AreEqual("2049|1950", new Simulation().ExecuteScalar("select concat(year(cast('1/1/49' as date)), '|', year(cast('1/1/50' as date)))"));

    [TestMethod]
    [DataRow("cast(@s as date)", 1931)]
    [DataRow("cast(@s as datetime)", 1931)]
    [DataRow("cast(@s as datetime2)", 1931)]
    [DataRow("cast(@s as smalldatetime)", 1931)]
    [DataRow("cast(@s as datetimeoffset)", 1931)]
    [DataRow("cast('1/1/30' as date)", 2030)]
    [DataRow("cast('310101' as date)", 1931)]
    [DataRow("cast('Jan 1 31' as datetime)", 1931)]
    [DataRow("convert(datetime, '01/01/31', 1)", 1931)]
    [DataRow("convert(date, '31.01.01', 2)", 1931)]
    [DataRow("try_convert(date, @s)", 1931)]
    [DataRow("@s", 1931)]
    [DataRow("datefromparts(31, 1, 1)", 31)]
    // PARSE reads through the .NET culture, whose own pivot stands.
    [DataRow("parse(@s as date using 'en-US')", 2031)]
    public void Cutoff2030_ReadsThirtyOneAsNineteenThirtyOne(string expression, int year)
        => AreEqual(year, WithCutoff(2030).ExecuteScalar($"declare @s varchar(10) = '1/1/31'; select year({expression})"));

    [TestMethod]
    [DataRow(9999, "99", 9999)]
    [DataRow(9999, "00", 9900)]
    [DataRow(1753, "53", 1753)]
    [DataRow(1753, "54", 1654)]
    public void Cutoff_EndsTheHundredYears(int cutoff, string written, int year)
        => AreEqual(year, WithCutoff(cutoff).ExecuteScalar($"select year(cast('1/1/{written}' as date))"));

    [TestMethod]
    public void BeforeTheLegacyRange_TryCastIsNull()
        => AreEqual(DBNull.Value, WithCutoff(1753).ExecuteScalar("select try_cast('1/1/54' as datetime)"));

    [TestMethod]
    public void Staged_ChangesNothing()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("exec sp_configure 'show advanced options', 1; reconfigure; exec sp_configure 'two digit year cutoff', 2030");
        AreEqual(2031, simulation.ExecuteScalar("select year(cast('1/1/31' as date))"));
    }

    /// <summary>
    /// The cutoff is read as the request begins: one it installs reaches the
    /// next request, not its own dynamic SQL.
    /// </summary>
    [TestMethod]
    public void Reconfigured_TakesEffectFromTheNextRequest()
    {
        var simulation = WithCutoff(2030);
        AreEqual("1931|1931", simulation.ExecuteScalar("""
            declare @s varchar(10) = '1/1/31', @r int;
            exec sp_configure 'two digit year cutoff', 2040;
            reconfigure;
            create table #r (y int);
            exec ('insert #r select year(cast(''1/1/31'' as date))');
            select concat(year(cast(@s as date)), '|', (select y from #r))
            """));
        AreEqual(2031, simulation.ExecuteScalar("select year(cast('1/1/31' as date))"));
    }

    /// <summary>A cached plan's date literal reads through the cutoff again once it changes.</summary>
    [TestMethod]
    public void CachedPlan_ReadsTheNewCutoff()
    {
        var simulation = new Simulation();
        const string query = "select year(cast('1/1/31' as date))";
        AreEqual(2031, simulation.ExecuteScalar(query));
        AreEqual(2031, simulation.ExecuteScalar(query));
        _ = simulation.ExecuteNonQuery("exec sp_configure 'show advanced options', 1; reconfigure; exec sp_configure 'two digit year cutoff', 2030; reconfigure");
        AreEqual(1931, simulation.ExecuteScalar(query));
    }
}
