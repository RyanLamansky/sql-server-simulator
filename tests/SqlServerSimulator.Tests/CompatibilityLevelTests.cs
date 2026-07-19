using System.Data;
using System.Data.Common;

namespace SqlServerSimulator;

/// <summary>
/// Exercises compatibility-level state, trace flags, and the
/// <c>VERBOSE_TRUNCATION_WARNINGS</c> scoped option through the truncation
/// error format — the one user-visible behavior that currently varies by
/// compat level. Verbose output is Msg 2628 (table/column/value); legacy is
/// Msg 8152 ("String or binary data would be truncated.").
/// </summary>
[TestClass]
public class CompatibilityLevelTests
{
    [TestMethod]
    public void DefaultCompat_ProducesVerboseTruncation()
    {
        // Fresh simulations default to compatibility level 170 (SQL Server 2025);
        // verbose truncation is the default at any level >= 160.
        var ex = AssertTruncates(connection => { /* no compat override */ });
        Assert.Contains("would be truncated in table", ex.Message);
        Assert.Contains("Truncated value", ex.Message);
    }

    [TestMethod]
    public void Compat150_ProducesLegacyTruncation()
    {
        var ex = AssertTruncates(connection =>
        {
            using var alter = connection.CreateCommand("alter database master set compatibility_level = 150");
            _ = alter.ExecuteNonQuery();
        });
        Assert.AreEqual("String or binary data would be truncated.", ex.Message);
    }

    [TestMethod]
    public void Compat160_ProducesVerboseTruncation()
    {
        // 160 is the level at which verbose became default (SQL Server 2022).
        var ex = AssertTruncates(connection =>
        {
            using var alter = connection.CreateCommand("alter database master set compatibility_level = 160");
            _ = alter.ExecuteNonQuery();
        });
        Assert.Contains("would be truncated in table", ex.Message);
    }

    [TestMethod]
    public void TraceFlag460_ForcesVerboseUnderLegacyCompat()
    {
        var ex = AssertTruncates(connection =>
        {
            _ = connection.CreateCommand("alter database master set compatibility_level = 150").ExecuteNonQuery();
            _ = connection.CreateCommand("dbcc traceon ( 460 )").ExecuteNonQuery();
        });
        Assert.Contains("would be truncated in table", ex.Message);
    }

    [TestMethod]
    public void TraceFlag460_OffRevertsToCompatDefault()
    {
        var ex = AssertTruncates(connection =>
        {
            _ = connection.CreateCommand("alter database master set compatibility_level = 150").ExecuteNonQuery();
            _ = connection.CreateCommand("dbcc traceon ( 460 )").ExecuteNonQuery();
            _ = connection.CreateCommand("dbcc traceoff ( 460 )").ExecuteNonQuery();
        });
        Assert.AreEqual("String or binary data would be truncated.", ex.Message);
    }

    [TestMethod]
    public void VerboseTruncationWarningsOff_OverridesModernCompat()
    {
        // The scoped configuration wins over the compat-level default — the
        // user explicitly opted out of verbose on a 170 database.
        var ex = AssertTruncates(connection =>
        {
            _ = connection.CreateCommand("alter database scoped configuration set verbose_truncation_warnings = off").ExecuteNonQuery();
        });
        Assert.AreEqual("String or binary data would be truncated.", ex.Message);
    }

    [TestMethod]
    public void VerboseTruncationWarningsOn_OverridesLegacyCompat()
    {
        var ex = AssertTruncates(connection =>
        {
            _ = connection.CreateCommand("alter database master set compatibility_level = 150").ExecuteNonQuery();
            _ = connection.CreateCommand("alter database scoped configuration set verbose_truncation_warnings = on").ExecuteNonQuery();
        });
        Assert.Contains("would be truncated in table", ex.Message);
    }

    [TestMethod]
    public void ExplicitVerboseSetting_WinsOverTraceFlag()
    {
        // Precedence: an explicit VERBOSE_TRUNCATION_WARNINGS setting trumps
        // trace flag 460. OFF + 460 ON should still produce legacy output.
        var ex = AssertTruncates(connection =>
        {
            _ = connection.CreateCommand("dbcc traceon ( 460 )").ExecuteNonQuery();
            _ = connection.CreateCommand("alter database scoped configuration set verbose_truncation_warnings = off").ExecuteNonQuery();
        });
        Assert.AreEqual("String or binary data would be truncated.", ex.Message);
    }

    [TestMethod]
    public void InvalidCompatibilityLevel_RaisesMsg15048()
    {
        // SQL Server's Msg 15048 lists valid values but doesn't echo the
        // rejected value back — verified against real SQL Server 2025.
        using var connection = new Simulation().CreateOpenConnection();
        using var alter = connection.CreateCommand("alter database master set compatibility_level = 145");
        var ex = Assert.Throws<DbException>(() => alter.ExecuteNonQuery());
        Assert.AreEqual("Valid values of the database compatibility level are 100, 110, 120, 130, 140, 150, 160 or 170.", ex.Message);
    }

    [TestMethod]
    [DataRow("alter database [master] set compatibility_level = 160")]
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
    /// resulting <see cref="DbException"/>. Centralizes the boilerplate so each
    /// test focuses on the behavior it verifies.
    /// </summary>
    private static DbException AssertTruncates(Action<DbConnection> configure)
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

        return Assert.Throws<DbException>(() => insert.ExecuteNonQuery());
    }
}
