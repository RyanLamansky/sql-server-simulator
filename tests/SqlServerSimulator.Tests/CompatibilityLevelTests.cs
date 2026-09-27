using System.Data;
using System.Data.Common;

namespace SqlServerSimulator;

/// <summary>
/// Exercises compatibility-level state, trace flags, and the
/// <c>VERBOSE_TRUNCATION_WARNINGS</c> scoped option through the truncation
/// error format. Verbose output is Msg 2628 (table/column/value); legacy is
/// Msg 8152 ("String or binary data would be truncated.").
/// </summary>
[TestClass]
public class CompatibilityLevelTests
{
    [TestMethod]
    public void DefaultCompat_ProducesVerboseTruncation()
    {
        // Fresh simulations default to compatibility level 170 (SQL Server 2025).
        var ex = AssertTruncates(connection => { /* no compat override */ });
        Assert.Contains("would be truncated in table", ex.Message);
        Assert.Contains("Truncated value", ex.Message);
    }

    // VERBOSE_TRUNCATION_WARNINGS (on by default) selects the verbose message
    // at compatibility level 150 and up; trace flag 460 forces it whatever the
    // level or the option says (probed 2026-09-27 against SQL Server 2025).
    [TestMethod]
    [DataRow(140, null, false, false)]
    [DataRow(150, null, false, true)]
    [DataRow(160, null, false, true)]
    [DataRow(140, "on", false, false)]
    [DataRow(170, "off", false, false)]
    [DataRow(140, null, true, true)]
    [DataRow(170, "off", true, true)]
    [DataRow(140, "off", true, true)]
    public void Truncation_VerboseByCompatOptionAndTraceFlag(int compatibilityLevel, string? option, bool traceFlag, bool verbose)
    {
        var ex = AssertTruncates(connection =>
        {
            _ = connection.CreateCommand($"alter database current set compatibility_level = {compatibilityLevel}").ExecuteNonQuery();
            if (option is not null)
                _ = connection.CreateCommand($"alter database scoped configuration set verbose_truncation_warnings = {option}").ExecuteNonQuery();
            if (traceFlag)
                _ = connection.CreateCommand("dbcc traceon ( 460 )").ExecuteNonQuery();
        });
        Assert.AreEqual(verbose ? 2628 : 8152, ex.Number);
        Assert.AreEqual(verbose ? (byte)1 : (byte)30, ex.State);
    }

    [TestMethod]
    public void TraceFlag460_OffRevertsToCompatDefault()
    {
        var ex = AssertTruncates(connection =>
        {
            _ = connection.CreateCommand("alter database current set compatibility_level = 140").ExecuteNonQuery();
            _ = connection.CreateCommand("dbcc traceon ( 460 )").ExecuteNonQuery();
            _ = connection.CreateCommand("dbcc traceoff ( 460 )").ExecuteNonQuery();
        });
        Assert.AreEqual("String or binary data would be truncated.", ex.Errors[0].Message);
    }

    [TestMethod]
    public void InvalidCompatibilityLevel_RaisesMsg15048()
    {
        // SQL Server's Msg 15048 lists valid values but doesn't echo the
        // rejected value back — verified against real SQL Server 2025.
        using var connection = new Simulation().CreateOpenConnection();
        using var alter = connection.CreateCommand("alter database current set compatibility_level = 145");
        var ex = Assert.Throws<SimulatedSqlException>(() => alter.ExecuteNonQuery());
        Assert.AreEqual("Valid values of the database compatibility level are 100, 110, 120, 130, 140, 150, 160 or 170.", ex.Message);
    }

    [TestMethod]
    [DataRow("alter database [simulated] set compatibility_level = 160")]
    [DataRow("alter database current set compatibility_level = 160")]
    public void AlterDatabase_AcceptsBracketedAndCurrentNames(string command)
    {
        // The simulator has a single database, so any name (including the
        // SQL-keyword form CURRENT and the bracket-escaped [master]) is fine.
        using var connection = new Simulation().CreateOpenConnection();
        using var alter = connection.CreateCommand(command);
        Assert.AreEqual(-1, alter.ExecuteNonQuery());
    }

    /// <summary>
    /// Runs <paramref name="configure"/> against a freshly created simulation,
    /// then attempts an INSERT that is guaranteed to truncate and returns the
    /// resulting <see cref="SimulatedSqlException"/>. Centralizes the boilerplate so each
    /// test focuses on the behavior it verifies.
    /// </summary>
    private static SimulatedSqlException AssertTruncates(Action<DbConnection> configure)
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v varchar(5) )").ExecuteNonQuery();

        configure(connection);

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert t values ( @p )";
        var p = insert.CreateParameter();
        p.ParameterName = "p";
        p.DbType = DbType.AnsiString;
        p.Value = "hello world"; // 11 bytes, varchar(5) = 5 → must truncate
        _ = insert.Parameters.Add(p);

        return Assert.Throws<SimulatedSqlException>(() => insert.ExecuteNonQuery());
    }
}
