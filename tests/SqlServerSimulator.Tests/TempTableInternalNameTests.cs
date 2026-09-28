using System.Text.RegularExpressions;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A local <c>#temp</c> table's messages name it by its padded name inside
/// <c>tempdb</c> — the written name padded with underscores to 116 characters
/// and twelve hex digits of a server-wide counter — while a <c>##</c> table
/// keeps its own, and a constraint conflict names the <c>#temp</c> table bare
/// (probed 2026-09-28 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class TempTableInternalNameTests
{
    [TestMethod]
    [DataRow("create table #t (v varchar(2)); insert #t values ('abc')", 2628, "#t")]
    [DataRow("create table #t (v varchar(2)); insert #t values ('a'); update #t set v = 'abc'", 2628, "#t")]
    [DataRow("create table #t (v int not null); insert #t values (null)", 515, "#t")]
    [DataRow("create table #abcdefghijklmnopqrstuvwxyz0123456789 (v varchar(2)); insert #abcdefghijklmnopqrstuvwxyz0123456789 values ('abc')", 2628, "#abcdefghijklmnopqrstuvwxyz0123456789")]
    public void LocalTempTable_IsNamedByItsPaddedName(string sql, int number, string written)
    {
        var message = new Simulation().AssertSqlError(sql, number).Message;
        var name = Regex.Match(message, @"table 'tempdb\.dbo\.([^']*)'").Groups[1].Value;
        AreEqual(128, name.Length);
        StartsWith(written.PadRight(116, '_'), name);
        MatchesRegex(new Regex("^[0-9A-F]{12}$"), name[116..]);
    }

    [TestMethod]
    public void EachLocalTempTable_DrawsTheNextSuffix()
    {
        var simulation = new Simulation();
        string Suffix(string table)
        {
            var message = simulation.AssertSqlError($"create table {table} (v varchar(2)); insert {table} values ('abc')", 2628).Message;
            return Regex.Match(message, @"([0-9A-F]{12})'").Groups[1].Value;
        }
        AreEqual(Convert.ToInt64(Suffix("#a"), 16) + 1, Convert.ToInt64(Suffix("#b"), 16));
    }

    [TestMethod]
    public void GlobalTempTable_KeepsItsName()
        => new Simulation().AssertSqlError(
            "create table ##g (v varchar(2)); insert ##g values ('abc')",
            2628,
            "String or binary data would be truncated in table 'tempdb.dbo.##g', column 'v'. Truncated value: 'ab'.");

    [TestMethod]
    public void CheckConflict_NamesTheTempTableBare()
        => new Simulation().AssertSqlError(
            "create table #t (v int constraint ck1 check (v > 0)); insert #t values (0)",
            547,
            "The INSERT statement conflicted with the CHECK constraint \"ck1\". The conflict occurred in database \"tempdb\", table \"#t\", column 'v'.");
}
