using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The server options that decide what a new login or session starts with:
/// <c>user options</c> and <c>default language</c> (probed 2026-10-17
/// against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class SessionStartOptionTests
{
    private static Simulation Configured(string option, int value)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"exec sp_configure '{option}', {value}; reconfigure"));
        return simulation;
    }

    private static DbConnection Login(Simulation simulation, string login)
    {
        var connection = simulation.CreateDbConnection();
        connection.ConnectionString = $"User ID={login};Password=Pa55word!";
        connection.Open();
        return connection;
    }

    [TestMethod]
    public void UserOptions_ReachANewSession_ButNotTheOneAlreadyOpen()
    {
        var simulation = new Simulation();
        using var open = simulation.CreateOpenConnection();
        _ = open.CreateCommand("exec sp_configure 'user options', 517; reconfigure").ExecuteNonQuery();
        AreEqual(5432, open.CreateCommand("select @@options").ExecuteScalar());
        // NOCOUNT joins; CURSOR_CLOSE_ON_COMMIT is the login's to settle, and
        // bit 1 has no option.
        using var fresh = simulation.CreateOpenConnection();
        AreEqual(5944, fresh.CreateCommand("select @@options").ExecuteScalar());
        AreEqual(-1, fresh.CreateCommand("create table t (a int); insert t values (1)").ExecuteNonQuery());
    }

    [TestMethod]
    [DataRow(192, 5624)]
    [DataRow(10240, 13624)]
    public void UserOptions_OnlyTheBitsTheLoginLeavesAlone(int userOptions, int sessionOptions)
    {
        var simulation = Configured("user options", userOptions);
        using var fresh = simulation.CreateOpenConnection();
        AreEqual(sessionOptions, fresh.CreateCommand("select @@options").ExecuteScalar());
    }

    [TestMethod]
    public void DefaultLanguage_IsWhatANewLoginKeeps()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create login before_change with password = 'Pa55word!'");
        _ = simulation.ExecuteNonQuery("""
            exec sp_configure 'default language', 2; reconfigure;
            create login after_change with password = 'Pa55word!';
            create login explicit with password = 'Pa55word!', default_language = us_english;
            exec sp_addlogin 'legacy', 'Pa55word!';
            create login reset with password = 'Pa55word!', default_language = us_english;
            exec sp_defaultlanguage 'reset'
            """);
        AreEqual("after_change:Français,before_change:us_english,explicit:us_english,legacy:Français,reset:Français", simulation.ExecuteScalar("""
            select string_agg(name + ':' + default_language_name, ',') within group (order by name)
            from sys.server_principals where type = 'S' and name <> 'sa'
            """));
    }

    [TestMethod]
    public void LoginDefaultLanguage_StartsTheSession()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create login fr with password = 'Pa55word!', default_language = [Français]; create user fr for login fr");
        using var connection = Login(simulation, "fr");
        AreEqual("Français|1|dmy|2003-01-02", connection.CreateCommand("""
            select concat(@@language, '|', @@datefirst, '|', (select date_format from sys.dm_exec_sessions where session_id = @@spid), '|', convert(char(10), cast('2/1/2003' as date), 120))
            """).ExecuteScalar());
    }
}
