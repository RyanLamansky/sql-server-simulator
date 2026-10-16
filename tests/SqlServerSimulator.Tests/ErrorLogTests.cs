using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The SQL Server error log: <c>xp_readerrorlog</c> / <c>sp_readerrorlog</c>,
/// <c>xp_enumerrorlogs</c> / <c>sp_enumerrorlogs</c>, <c>sp_cycle_errorlog</c>
/// and <c>DBCC ERRORLOG</c>, and what writes it — <c>RAISERROR … WITH LOG</c>,
/// <c>xp_logevent</c>, registered messages marked <c>@with_log</c>, refused
/// logins, trace flags and <c>sp_configure</c> — probed 2026-10-16 against SQL
/// Server 2025.
/// </summary>
[TestClass]
public sealed class ErrorLogTests
{
    private static List<string> Rows(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand(sql);
        using var reader = command.ExecuteReader();
        return [.. reader.EnumerateRecords().Select(static record => string.Join('|', Enumerable.Range(0, record.FieldCount).Select(i => record.IsDBNull(i) ? "NULL" : record.GetValue(i).ToInvariantString())))];
    }

    private static List<string> Rows(Simulation simulation, string sql)
    {
        using var connection = simulation.CreateOpenConnection();
        return Rows(connection, sql);
    }

    /// <summary>The process column and text of each entry <paramref name="search"/> finds in the current log.</summary>
    private static List<string> Logged(Simulation simulation, string search) =>
        Rows(simulation, $"exec xp_readerrorlog 0, 1, N'{search}'").ConvertAll(static row => row[(row.IndexOf('|', StringComparison.Ordinal) + 1)..].TrimEnd('\r', '\n'));

    private static SimulatedDbConnection Login(Simulation simulation, string login, string? database = null)
    {
        var connection = simulation.CreateDbConnection();
        connection.ConnectionString = $"User ID={login};Password=Pa55word!;Initial Catalog={database ?? "master"}";
        connection.Open();
        return connection;
    }

    [TestMethod]
    public void CurrentLog_OpensWithTheStartupPreamble()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand("exec xp_readerrorlog");
        using var reader = command.ExecuteReader();
        CollectionAssert.AreEqual(new[] { "LogDate", "ProcessInfo", "Text" }, Enumerable.Range(0, 3).Select(reader.GetName).ToArray());
        CollectionAssert.AreEqual(new[] { "datetime", "nvarchar", "nvarchar" }, Enumerable.Range(0, 3).Select(reader.GetDataTypeName).ToArray());
        var schema = reader.GetColumnSchema();
        AreEqual(12, schema[1].ColumnSize);
        AreEqual(3999, schema[2].ColumnSize);
        var texts = new List<string>();
        while (reader.Read())
        {
            AreEqual("Server", reader.GetString(1));
            texts.Add(reader.GetString(2));
        }
        reader.Close();
        AreEqual(((string)new Simulation().ExecuteScalar("select @@version")!).Replace("\n", "\r\n", StringComparison.Ordinal), texts[0]);
        CollectionAssert.AreEqual(new[]
        {
            "UTC adjustment: 0:00",
            "(c) Microsoft Corporation.",
            "All rights reserved.",
            $"Server process ID is {simulation.ExecuteScalar("select serverproperty('ProcessID')")}.",
            "Logging SQL Server messages in file '/var/opt/mssql/log/errorlog'.",
            "Registry startup parameters: \r\n\t -d /var/opt/mssql/data/master.mdf\r\n\t -l /var/opt/mssql/data/mastlog.ldf\r\n\t -e /var/opt/mssql/log/errorlog",
            // The last entry of a log carries the file's closing CRLF.
            "SQL Server is now ready for client connections. This is an informational message; no user action is required.\r\n",
        }, texts.Skip(1).ToArray());
    }

    [TestMethod]
    public void Raiserror_WithLog_WritesTheErrorLineAboveSeverityTen()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            raiserror('logged %s', 10, 1, 'info') with log;
            raiserror('logged quiet', 0, 7) with log;
            raiserror('not logged', 10, 1);
            begin try raiserror('logged caught', 16, 3) with log end try begin catch end catch;
            begin try throw 50001, 'logged thrown', 1 end try begin catch end catch;
            """);
        var logged = Logged(simulation, "logged");
        IsTrue(logged.TrueForAll(static row => row.StartsWith("spid", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(new[] { "logged info", "logged quiet", "logged caught" }, logged.ConvertAll(static row => row.Split('|')[1]));
        CollectionAssert.AreEqual(new[] { "Error: 50000, Severity: 16, State: 3." }, Logged(simulation, "Error: 50000").ConvertAll(static row => row.Split('|')[1]));
    }

    [TestMethod]
    public void RegisteredMessage_WithLog_IsLoggedWithoutWithLog()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            exec sp_addmessage 60200, 16, N'registered %d', @with_log = 'true';
            exec sp_addmessage 60201, 10, N'registered info %d', @with_log = 'true';
            exec sp_addmessage 60202, 16, N'registered unlogged %d';
            begin try raiserror(60200, 16, 2, 7) end try begin catch end catch;
            raiserror(60201, 10, 2, 8);
            begin try raiserror(60202, 16, 2, 9) end try begin catch end catch;
            """);
        CollectionAssert.AreEqual(new[] { "registered 7", "registered info 8" }, Logged(simulation, "registered").ConvertAll(static row => row.Split('|')[1]));
        CollectionAssert.AreEqual(new[] { "Error: 60200, Severity: 16, State: 2." }, Logged(simulation, "Error: 602").ConvertAll(static row => row.Split('|')[1]));
    }

    [TestMethod]
    public void XpLogEvent_WritesBySeverity()
    {
        var simulation = new Simulation();
        AreEqual("0", simulation.ExecuteScalar("""
            declare @rc int;
            exec @rc = xp_logevent 60000, 'event plain';
            exec xp_logevent 60001, 'event warning', warning;
            exec xp_logevent 60002, N'event error', 'ERROR';
            exec xp_logevent 60003, 'event 100%% %d', @whatever = 'informational';
            exec xp_logevent 60004, '';
            exec xp_logevent 60005, 'after the empty one';
            select cast(@rc as varchar)
            """));
        CollectionAssert.AreEqual(new[] { "event plain", "event warning", "event error", "event 100%% %d" }, Logged(simulation, "event").ConvertAll(static row => row.Split('|')[1]));
        CollectionAssert.AreEqual(new[] { "Error: 60001, Severity: 15, State: 1.", "Error: 60002, Severity: 16, State: 1." }, Logged(simulation, "Error: 600").ConvertAll(static row => row.Split('|')[1]));
        // An empty message is a NULL text.
        AreEqual(1, simulation.ExecuteScalar<int>("create table #l (d datetime, p nvarchar(12), t nvarchar(3999)); insert #l exec xp_readerrorlog; select count(*) from #l where t is null"));
    }

    [TestMethod]
    [DataRow("exec @rc = xp_logevent 60000", "Error executing extended stored procedure: Invalid Parameter.")]
    [DataRow("exec @rc = xp_logevent 'abc', 'x'", "Error executing extended stored procedure: Invalid Parameter.")]
    [DataRow("declare @n bigint = 60000; exec @rc = xp_logevent @n, 'x'", "Error executing extended stored procedure: Invalid Parameter.")]
    [DataRow("exec @rc = xp_logevent 60000, 'x', 'error', 'extra'", "Error executing extended stored procedure: Invalid Parameter.")]
    [DataRow("exec @rc = xp_logevent 50000, 'x'", "Invalid error number. Error number must be greater than 50000.")]
    [DataRow("exec @rc = xp_logevent 40000, 5", "Invalid error number. Error number must be greater than 50000.")]
    [DataRow("exec @rc = xp_logevent 60000, 123", "Error executing extended stored procedure: Invalid Parameter Type")]
    [DataRow("exec @rc = xp_logevent 60000, null", "Error executing extended stored procedure: Invalid Parameter Type")]
    [DataRow("declare @m varchar(max) = 'x'; exec @rc = xp_logevent 60000, @m", "Error executing extended stored procedure: Invalid Parameter Type")]
    [DataRow("exec @rc = xp_logevent 60000, 'x', 5", "Error executing extended stored procedure: Invalid Parameter Type")]
    [DataRow("exec @rc = xp_logevent 60000, 'x', 'warn'", "Invalid severity value. Severity must be 'Informational' (default), 'Warning', or 'Error'.")]
    public void XpLogEvent_Refusals_AreClassZeroMessagesAndReturnOne(string call, string message)
    {
        using var connection = new Simulation().CreateOpenConnection();
        var messages = new List<SimulatedError>();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.AddRange(e.Errors);
        AreEqual(1, connection.CreateCommand($"declare @rc int; {call}; select @rc").ExecuteScalar());
        HasCount(1, messages);
        AreEqual((0, (byte)0, (byte)0, 0, "", message), (messages[0].Number, messages[0].Class, messages[0].State, messages[0].LineNumber, messages[0].Procedure, messages[0].Message));
    }

    [TestMethod]
    public void ReadErrorLog_FiltersSortsAndBoundsByTime()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            exec xp_logevent 60000, 'Alpha one';
            exec xp_logevent 60000, 'alpha TWO';
            exec xp_logevent 60000, 'beta two';
            """);
        static List<string> Texts(Simulation simulation, string sql) => Rows(simulation, sql).ConvertAll(static row => row.Split('|')[2].TrimEnd('\r', '\n'));
        // Each search string must appear, case aside; neither is a pattern.
        CollectionAssert.AreEqual(new[] { "Alpha one", "alpha TWO" }, Texts(simulation, "exec xp_readerrorlog 0, 1, N'ALPHA'"));
        CollectionAssert.AreEqual(new[] { "alpha TWO" }, Texts(simulation, "exec xp_readerrorlog 0, 1, N'alpha', N'two'"));
        CollectionAssert.AreEqual(new[] { "alpha TWO", "beta two" }, Texts(simulation, "exec xp_readerrorlog -1, 1, NULL, N'two'"));
        IsEmpty(Texts(simulation, "exec xp_readerrorlog 0, 1, N'alpha%'"));
        // An order beginning desc reads the log backward.
        CollectionAssert.AreEqual(new[] { "alpha TWO", "Alpha one" }, Texts(simulation, "exec xp_readerrorlog 0, 1, N'alpha', NULL, NULL, NULL, N'DESCENDING'"));
        CollectionAssert.AreEqual(new[] { "Alpha one", "alpha TWO" }, Texts(simulation, "exec xp_readerrorlog 0, 1, N'alpha', NULL, NULL, NULL, 'Asc '"));
        // Both time bounds are inclusive; a string reads in mdy whatever DATEFORMAT says.
        CollectionAssert.AreEqual(new[] { "Alpha one", "alpha TWO" }, Texts(simulation, """
            create table #l (d datetime, p nvarchar(12), t nvarchar(3999));
            insert #l exec xp_readerrorlog 0, 1, N'alpha one';
            declare @start datetime = (select d from #l);
            set dateformat dmy;
            exec xp_readerrorlog 0, 1, N'alpha', NULL, @start, '12/31/9999'
            """));
        IsEmpty(Texts(simulation, "exec xp_readerrorlog 0, 1, N'alpha', NULL, NULL, N'2000-01-01'"));
        CollectionAssert.AreEqual(new[] { "Alpha one", "alpha TWO" }, Texts(simulation, "exec xp_readerrorlog 0, 1, N'alpha', NULL, N'', NULL"));
        // sp_readerrorlog passes @p1 alone when @p2 is NULL.
        HasCount(Rows(simulation, "exec xp_readerrorlog").Count, Rows(simulation, "exec sp_readerrorlog 0, NULL, N'alpha'"));
        CollectionAssert.AreEqual(new[] { "beta two" }, Texts(simulation, "exec sp_readerrorlog @p1 = 0, @p2 = 1, @p3 = 'beta'"));
    }

    [TestMethod]
    [DataRow("exec @rc = xp_readerrorlog 'abc'", 12, "Error executing extended stored procedure: Invalid Parameter Type")]
    [DataRow("exec @rc = xp_readerrorlog default", 12, "Error executing extended stored procedure: Invalid Parameter Type")]
    [DataRow("declare @n bigint = 0; exec @rc = xp_readerrorlog @n", 12, "Error executing extended stored procedure: Invalid Parameter Type")]
    [DataRow("exec @rc = xp_readerrorlog 0, NULL", 12, "Error executing extended stored procedure: Invalid Parameter Type")]
    [DataRow("exec @rc = xp_readerrorlog 0, 1, 'varchar search'", 12, "Error executing extended stored procedure: Invalid Parameter Type")]
    [DataRow("exec @rc = xp_readerrorlog 0, 1, NULL, NULL, 5", 12, "Error executing extended stored procedure: Invalid Parameter Type")]
    [DataRow("exec @rc = xp_readerrorlog -2", 12, "Invalid parameter value.")]
    [DataRow("exec @rc = xp_readerrorlog 0, 0", 12, "Invalid parameter value.")]
    [DataRow("exec @rc = xp_readerrorlog 0, 1, NULL, NULL, NULL, NULL, N'des'", 12, "Invalid parameter value.")]
    [DataRow("exec @rc = xp_readerrorlog 99, 3", 15, "Error executing extended stored procedure: Invalid Parameter")]
    [DataRow("exec @rc = xp_readerrorlog 0, 1, 5, NULL, N'garbage'", 15, "The format for the date filter is incorrect. Please use a valid SQL Server date or datetime format. For example, valid formats are \u00EF\u00BF\u00BDYYYY-MM-DD' and \u00EF\u00BF\u00BDMM/DD/YYYY\u00EF\u00BF\u00BD.")]
    [DataRow("declare @d datetime2 = '2026-01-01'; exec @rc = xp_readerrorlog 0, 1, NULL, NULL, @d", 15, "The format for the date filter is incorrect. Please use a valid SQL Server date or datetime format. For example, valid formats are \u00EF\u00BF\u00BDYYYY-MM-DD' and \u00EF\u00BF\u00BDMM/DD/YYYY\u00EF\u00BF\u00BD.")]
    [DataRow("exec @rc = xp_readerrorlog 1", 16, "xp_readerrorlog() returned error 2, 'The system cannot find the file specified.'")]
    [DataRow("exec @rc = xp_readerrorlog 0, 2", 16, "xp_readerrorlog() returned error 2, 'The system cannot find the file specified.'")]
    [DataRow("exec @rc = xp_enumerrorlogs 0", 12, "Invalid parameter value.")]
    [DataRow("exec @rc = xp_enumerrorlogs 3", 12, "Error executing extended stored procedure: Invalid Parameter Type")]
    [DataRow("exec @rc = xp_enumerrorlogs 1, 5", 12, "Error executing extended stored procedure: Invalid Parameter Type")]
    public void ExtendedProcedureRefusals_AreMsg22004_ThatNeitherCatchNorSetAtAtError(string call, int severity, string message)
    {
        var simulation = new Simulation();
        var error = simulation.AssertSqlError($"""
            create table r (e int, rc int);
            declare @rc int;
            begin try
                {call};
                insert r values (@@error, @rc);
            end try
            begin catch
                insert r values (-1, -1);
            end catch
            """, 22004).Errors[0];
        AreEqual(((byte)severity, (byte)1, 0, "", message), (error.Class, error.State, error.LineNumber, error.Procedure, error.Message));
        AreEqual("0|1", simulation.ExecuteScalar("select concat(e, '|', rc) from r"));
    }

    [TestMethod]
    public void ReadErrorLog_ReadsTheUtf8FileAByteToACharacter()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("raiserror(N'accent \u00FCn\u00EF', 10, 1) with log; exec xp_logevent 60000, 'later'");
        CollectionAssert.AreEqual(new[] { "accent \u00C3\u00BCn\u00C3\u00AF" }, Rows(simulation, "exec xp_readerrorlog 0, 1, N'accent'").ConvertAll(static row => row.Split('|')[2]));
        // Only the log's last entry carries the closing CRLF.
        CollectionAssert.AreEqual(new[] { "later\r\n" }, Rows(simulation, "exec xp_readerrorlog 0, 1, N'later'").ConvertAll(static row => row.Split('|')[2]));
    }

    [TestMethod]
    public void TextPast2011Bytes_IsNotWritten()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            declare @fits varchar(8000) = 'fits ' + replicate('x', 2006), @long varchar(8000) = 'long ' + replicate('x', 2007);
            exec xp_logevent 60000, @fits;
            exec xp_logevent 60000, @long, 'error';
            declare @n nvarchar(4000) = N'wide ' + replicate(N'ü', 1004);
            begin try raiserror(@n, 16, 1) with log end try begin catch end catch;
            """);
        HasCount(1, Rows(simulation, "exec xp_readerrorlog 0, 1, N'fits'"));
        IsEmpty(Rows(simulation, "exec xp_readerrorlog 0, 1, N'long'"));
        IsEmpty(Rows(simulation, "exec xp_readerrorlog 0, 1, N'Error:'"));
    }

    [TestMethod]
    public void CycleErrorLog_ArchivesTheCurrentLog()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        var messages = new List<SimulatedError>();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.AddRange(e.Errors);
        _ = connection.CreateCommand("exec xp_logevent 60000, 'before the cycle'").ExecuteNonQuery();
        AreEqual(0, connection.CreateCommand("declare @rc int; exec @rc = sp_cycle_errorlog; select @rc").ExecuteScalar());
        AreEqual((2528, 9, "sp_cycle_errorlog"), (messages[0].Number, messages[0].LineNumber, messages[0].Procedure));
        var spid = connection.CreateCommand("select 'spid' + cast(@@spid as varchar)").ExecuteScalar();

        var current = Rows(connection, "exec xp_readerrorlog 0").ConvertAll(static row => row[(row.IndexOf('|', StringComparison.Ordinal) + 1)..]);
        HasCount(7, current);
        StartsWith($"{spid}|Microsoft SQL Server 2025", current[0]);
        AreEqual($"{spid}|The error log has been reinitialized. See the previous log for older entries.\r\n", current[6]);
        var archived = Rows(connection, "exec xp_readerrorlog 1").ConvertAll(static row => row[(row.IndexOf('|', StringComparison.Ordinal) + 1)..]);
        AreEqual($"{spid}|before the cycle", archived[^2]);
        AreEqual($"{spid}|Attempting to cycle error log. This is an informational message only; no user action is required.\r\n", archived[^1]);

        // Each line costs 37 bytes beyond its UTF-8 text.
        var expectedBytes = current.Sum(static row => 37 + System.Text.Encoding.UTF8.GetByteCount(row[(row.IndexOf('|', StringComparison.Ordinal) + 1)..].TrimEnd('\r', '\n')));
        var logs = Rows(connection, "exec xp_enumerrorlogs");
        HasCount(2, logs);
        AreEqual(expectedBytes.ToInvariantString(), logs[0].Split('|')[2]);
        IsTrue(System.Text.RegularExpressions.Regex.IsMatch(logs[1], @"^1\|\d\d/\d\d/\d{4}  \d\d:\d\d\|\d+$"), logs[1]);
        IsEmpty(Rows(connection, "exec xp_enumerrorlogs 2"));
    }

    [TestMethod]
    public void Cycling_KeepsSixArchives_AndDbccErrorLogCycles()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("exec sp_cycle_errorlog; exec sp_cycle_errorlog; exec sp_cycle_errorlog; exec sp_cycle_errorlog; exec sp_cycle_errorlog; exec sp_cycle_errorlog; dbcc errorlog with no_infomsgs; exec sp_enumerrorlogs");
        CollectionAssert.AreEqual(new[] { "0", "1", "2", "3", "4", "5", "6" }, Rows(simulation, "exec sp_enumerrorlogs").ConvertAll(static row => row.Split('|')[0]));
        // The startup log, seven cycles back, is gone.
        IsEmpty(Rows(simulation, "exec xp_readerrorlog 6, 1, N'Registry startup'"));
        _ = simulation.AssertSqlError("dbcc errorlog(1)", 2583);
    }

    [TestMethod]
    public void Permissions_GateTheReadersAndTheCycle()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            use master;
            create login nobody with password = 'Pa55word!';
            create login stateviewer with password = 'Pa55word!';
            grant view server state to stateviewer;
            create login logviewer with password = 'Pa55word!';
            grant view any error log to logviewer;
            create login secadmin with password = 'Pa55word!';
            alter server role securityadmin add member secadmin;
            create login perfviewer with password = 'Pa55word!';
            grant view server performance state to perfviewer;
            create login granted with password = 'Pa55word!';
            create user granted for login granted;
            grant execute on sys.xp_readerrorlog to granted;
            grant execute on sys.sp_cycle_errorlog to granted;
            create database other;
            """);
        _ = simulation.ExecuteNonQuery("use other; create user nobody for login nobody; alter role db_owner add member nobody");

        foreach (var login in new[] { "nobody", "stateviewer", "perfviewer" })
        {
            using var connection = Login(simulation, login, login == "nobody" ? "other" : null);
            foreach (var procedure in new[] { "xp_readerrorlog", "xp_enumerrorlogs", "xp_logevent 60000, 'x'", "sp_cycle_errorlog" })
            {
                var denied = Throws<SimulatedSqlException>(() => connection.CreateCommand($"exec {procedure}").ExecuteNonQuery());
                var name = procedure.Split(' ')[0];
                AreEqual((229, 1, name, $"The EXECUTE permission was denied on the object '{name}', database 'mssqlsystemresource', schema 'sys'."),
                    (denied.Number, denied.Errors[0].LineNumber, denied.Errors[0].Procedure, denied.Errors[0].Message));
            }
        }
        foreach (var login in new[] { "nobody", "perfviewer" })
        {
            using var connection = Login(simulation, login);
            var refused = Throws<SimulatedSqlException>(() => connection.CreateCommand("exec sp_readerrorlog").ExecuteNonQuery()).Errors[0];
            AreEqual((27219, 11, "sp_readerrorlog"), (refused.Number, refused.LineNumber, refused.Procedure));
            refused = Throws<SimulatedSqlException>(() => connection.CreateCommand("exec sp_enumerrorlogs").ExecuteNonQuery()).Errors[0];
            AreEqual((27219, 8, "sp_enumerrorlogs"), (refused.Number, refused.LineNumber, refused.Procedure));
        }
        foreach (var login in new[] { "stateviewer", "logviewer", "secadmin" })
        {
            using var connection = Login(simulation, login);
            IsNotEmpty(Rows(connection, "exec sp_readerrorlog 0, 1, N'Server process'"));
            HasCount(1, Rows(connection, "exec sp_enumerrorlogs"));
        }
        using (var granted = Login(simulation, "granted"))
        {
            IsNotEmpty(Rows(granted, "exec xp_readerrorlog 0, 1, N'Server process'"));
            var refused = Throws<SimulatedSqlException>(() => granted.CreateCommand("exec sp_cycle_errorlog").ExecuteNonQuery()).Errors[0];
            AreEqual((15247, 5, "sp_cycle_errorlog"), (refused.Number, refused.LineNumber, refused.Procedure));
        }
        using var nobody = Login(simulation, "nobody");
        AreEqual(2571, Throws<SimulatedSqlException>(() => nobody.CreateCommand("dbcc errorlog").ExecuteNonQuery()).Number);
    }

    [TestMethod]
    public void Raiserror_WithLog_TakesAlterTrace()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            use master;
            create login tracer with password = 'Pa55word!';
            grant alter trace to tracer;
            create login plain with password = 'Pa55word!';
            """);
        using (var tracer = Login(simulation, "tracer"))
            _ = tracer.CreateCommand("raiserror('traced', 10, 1) with log").ExecuteNonQuery();
        using (var plain = Login(simulation, "plain"))
            AreEqual(2778, Throws<SimulatedSqlException>(() => plain.CreateCommand("raiserror('plain', 10, 1) with log").ExecuteNonQuery()).Number);
        HasCount(1, Logged(simulation, "traced"));
    }

    [TestMethod]
    public void RefusedLogins_LogTheirReason()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            use master;
            create login known with password = 'Pa55word!';
            create login disabled1 with password = 'Pa55word!';
            alter login disabled1 disable;
            create login denied with password = 'Pa55word!';
            deny connect sql to denied;
            """);
        void Refused(string connectionString)
        {
            var connection = simulation.CreateDbConnection();
            connection.ConnectionString = connectionString;
            _ = Throws<SimulatedSqlException>(connection.Open);
        }
        Refused("User ID=known;Password=wrong");
        Refused("User ID=unknown;Password=wrong");
        Refused("User ID=disabled1;Password=Pa55word!");
        Refused("User ID=denied;Password=Pa55word!");
        Refused("User ID=known;Password=Pa55word!;Initial Catalog=nosuch");
        CollectionAssert.AreEqual(new[]
        {
            "Logon|Error: 18456, Severity: 14, State: 8.",
            "Logon|Login failed for user 'known'. Reason: Password did not match that for the login provided. [CLIENT: <local machine>]",
            "Logon|Error: 18456, Severity: 14, State: 5.",
            "Logon|Login failed for user 'unknown'. Reason: Could not find a login matching the name provided. [CLIENT: <local machine>]",
            "Logon|Error: 18470, Severity: 14, State: 1.",
            "Logon|Login failed for user 'disabled1'. Reason: The account is disabled. [CLIENT: <local machine>]",
            "Logon|Error: 18456, Severity: 14, State: 147.",
            "Logon|Login failed for user 'denied'. Reason: Login-based server access validation failed with an infrastructure error. Login lacks Connect SQL permission. [CLIENT: <local machine>]",
            "Logon|Error: 18456, Severity: 14, State: 38.",
            "Logon|Login failed for user 'known'. Reason: Failed to open the explicitly specified database 'nosuch'. [CLIENT: <local machine>]",
        }, Rows(simulation, "exec xp_readerrorlog 0, 1, NULL, NULL, NULL, NULL, N'asc'").Skip(8).Select(static row => row[(row.IndexOf('|', StringComparison.Ordinal) + 1)..].TrimEnd('\r', '\n')).ToArray());
    }

    [TestMethod]
    public void TraceFlagsAndConfigurationChanges_AreLogged()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        var spid = connection.CreateCommand("dbcc traceon(3604, 2588) with no_infomsgs; dbcc traceoff(2588, -1); dbcc traceon(0); exec sp_configure 'show advanced options', 0; select @@spid").ExecuteScalar();
        CollectionAssert.AreEqual(new[]
        {
            $"DBCC TRACEON 3604, server process ID (SPID) {spid}. This is an informational message only; no user action is required.",
            $"DBCC TRACEON 2588, server process ID (SPID) {spid}. This is an informational message only; no user action is required.",
            $"DBCC TRACEOFF 2588, server process ID (SPID) {spid}. This is an informational message only; no user action is required.",
        }, Logged(simulation, "DBCC TRACE").ConvertAll(static row => row.Split('|')[1]));
        CollectionAssert.AreEqual(new[] { "Configuration option 'show advanced options' changed from 0 to 0. Run the RECONFIGURE statement to install." },
            Logged(simulation, "Configuration option").ConvertAll(static row => row.Split('|')[1]));
    }

    /// <summary>The text of each entry <paramref name="search"/> finds in the current log.</summary>
    private static List<string> LoggedTexts(Simulation simulation, string search) =>
        Logged(simulation, search).ConvertAll(static row => row[(row.IndexOf('|', StringComparison.Ordinal) + 1)..]);

    [TestMethod]
    public void AlterDatabaseSet_LogsEachOptionInRealsSpelling()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create database d;
            alter database d set recovery simple;
            alter database d set recovery simple;
            alter database d set ansi_nulls off, Ansi_Padding on;
            alter database d set Target_Recovery_Time = 60 MINUTES;
            alter database d set delayed_durability = Allowed;
            alter database d set Accelerated_Database_Recovery = on;
            alter database d set page_verify torn_page_detection;
            alter database d set compatibility_level = 150;
            alter database d set allow_snapshot_isolation on;
            alter database d set auto_create_statistics on (incremental = on);
            alter database d set auto_create_statistics off;
            alter database d set auto_create_statistics on;
            alter database d set enable_broker;
            alter database d set single_user;
            alter database d set multi_user;
            alter database d set containment = none;
            alter database d set change_tracking = on (auto_cleanup = on);
            alter database d set change_tracking (change_retention = 5 days);
            alter database d set change_tracking = off;
            alter database d set automatic_tuning = auto;
            alter database d set automatic_tuning (force_last_good_plan = off);
            """);
        // Real applies FILESTREAM options as their batch compiles, so its lines lead the batch's.
        _ = simulation.ExecuteNonQuery("alter database d set filestream (non_transacted_access = read_only, directory_name = 'dir')");
        CollectionAssert.AreEqual(new[]
        {
            "RECOVERY to SIMPLE",
            "RECOVERY to SIMPLE",
            "ANSI_NULLS to OFF",
            "ANSI_PADDING to ON",
            "target_recovery_time to 60",
            "delayed_durability to allowed",
            "accelerated_database_recovery to ON",
            "PAGE_VERIFY to TORN_PAGE_DETECTION",
            "COMPATIBILITY_LEVEL to 150",
            "ALLOW_SNAPSHOT_ISOLATION to ON",
            "INCREMENTAL to ON",
            "AUTO_CREATE_STATISTICS to ON",
            "AUTO_CREATE_STATISTICS to OFF",
            "INCREMENTAL to OFF",
            "AUTO_CREATE_STATISTICS to ON",
            "ENABLE_BROKER to ON",
            "SINGLE_USER to ON",
            "MULTI_USER to ON",
            "contained to none",
            "change_tracking to ON",
            "change_tracking to ON",
            "change_tracking to OFF",
            "automatic_tuning to auto",
            "FILESTREAM NON_TRANSACTED_ACCESS to READ_ONLY",
            "FILESTREAM DIRECTORY_NAME to dir",
        }, LoggedTexts(simulation, "Setting database option").ConvertAll(static text => text["Setting database option ".Length..^" for database 'd'.".Length]));
        IsTrue(Logged(simulation, "Setting database option").TrueForAll(static row => row.StartsWith("spid", StringComparison.Ordinal)));
        // A refused option writes nothing.
        var refused = new Simulation();
        _ = refused.AssertSqlError("alter database master set read_only", 5058);
        IsEmpty(Logged(refused, "Setting database option"));
    }

    [TestMethod]
    public void DatabaseStartups_AreLoggedAsTheDatabaseRestarts()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create database ldb;
            alter database ldb set read_only;
            alter database ldb set read_only;
            alter database ldb set read_write;
            alter database ldb set read_committed_snapshot on;
            alter database ldb set read_committed_snapshot on;
            alter database ldb set offline;
            alter database ldb set offline;
            alter database ldb set online;
            alter database ldb set online;
            alter database ldb set emergency;
            alter database ldb set online;
            """);
        CollectionAssert.AreEqual(new[]
        {
            "Starting up database 'ldb'.",
            "Setting database option READ_ONLY to ON for database 'ldb'.",
            "Starting up database 'ldb'.",
            "Setting database option READ_ONLY to ON for database 'ldb'.",
            "Setting database option READ_WRITE to ON for database 'ldb'.",
            "Starting up database 'ldb'.",
            "Setting database option READ_COMMITTED_SNAPSHOT to ON for database 'ldb'.",
            "Starting up database 'ldb'.",
            "Setting database option READ_COMMITTED_SNAPSHOT to ON for database 'ldb'.",
            "Setting database option OFFLINE to ON for database 'ldb'.",
            "Setting database option OFFLINE to ON for database 'ldb'.",
            "Setting database option ONLINE to ON for database 'ldb'.",
            "Starting up database 'ldb'.",
            "Setting database option ONLINE to ON for database 'ldb'.",
            "Setting database option EMERGENCY to ON for database 'ldb'.",
            "Starting up database 'ldb'.",
            "The database 'ldb' is marked EMERGENCY_MODE and is in a state that does not allow recovery to be run.",
            "Setting database option ONLINE to ON for database 'ldb'.",
            "Starting up database 'ldb'.",
        }, LoggedTexts(simulation, "ldb"));
    }

    [TestMethod]
    public void AlterDatabaseSetQueryStore_LogsStateAlwaysAndOptionsWhenChanged()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create database d;
            alter database d set query_store = off;
            alter database d set query_store = on;
            alter database d set query_store = on (max_storage_size_mb = 500);
            alter database d set query_store (max_storage_size_mb = 500, operation_mode = read_only);
            alter database d set query_store = on (operation_mode = read_write, data_flush_interval_seconds = 60, interval_length_minutes = 60);
            alter database d set query_store (wait_stats_capture_mode = off, query_capture_mode = custom, size_based_cleanup_mode = off,
                query_capture_policy = (stale_capture_policy_threshold = 1 days, execution_count = 30));
            alter database d set query_store (query_capture_policy = (stale_capture_policy_threshold = 36 hours));
            alter database d set query_store clear all;
            """);
        CollectionAssert.AreEqual(new[]
        {
            "query_store to off",
            "query_store to on",
            "query_store to on",
            "query_store max_storage_size_mb to 500",
            // An OPERATION_MODE beside them logs the unchanged too.
            "query_store to read_only",
            "query_store max_storage_size_mb to 500",
            "query_store to read_write",
            "query_store flush_interval_seconds to 60",
            "query_store interval_length_minutes to 60",
            "query_store query_capture_mode to custom",
            "query_store size_based_cleanup_mode to off",
            "query_store execution_count to 30",
            "query_store stale_capture_policy_threshold to 1 day",
            "query_store wait_stats_capture_mode to off",
            "query_store stale_capture_policy_threshold to 36 hours",
            "query_store to clear",
        }, LoggedTexts(simulation, "Setting database option").ConvertAll(static text => text["Setting database option ".Length..^" for database 'd'.".Length]));
    }

    [TestMethod]
    public void DbccChecks_LogTheirCompletion()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create database cdb;
            create login checker with password = 'Pa55word!';
            """);
        _ = simulation.ExecuteNonQuery("""
            use cdb;
            create table t (id int constraint pk_t primary key);
            create user checker for login checker;
            alter role db_owner add member checker;
            dbcc checkdb;
            dbcc checkdb (N'CDB', noindex) with physical_only, no_infomsgs, tablock;
            dbcc checkdb with maxdop = 2, tableresults, all_errormsgs, extended_logical_checks;
            dbcc checkdb with estimateonly;
            dbcc checktable ('t', 1) with no_infomsgs;
            dbcc checktable ('dbo.t', noindex);
            dbcc checkalloc;
            dbcc checkcatalog;
            dbcc checkfilegroup;
            execute as login = 'checker';
            dbcc checkdb with no_infomsgs;
            revert;
            dbcc checkdb (master) with no_infomsgs;
            dbcc checkdb (tempdb) with no_infomsgs;
            """);
        var logged = LoggedTexts(simulation, "executed by");
        const string Done = " found 0 errors and repaired 0 errors. Elapsed time: 0 hours 0 minutes 0 seconds.  ";
        CollectionAssert.AreEqual(new[]
        {
            "DBCC CHECKDB (cdb) executed by sa" + Done,
            "DBCC CHECKDB (cdb, noindex) WITH no_infomsgs, tablock, physical_only executed by sa" + Done,
            "DBCC CHECKDB (cdb) WITH all_errormsgs, tableresults, maxdop = 2 executed by sa" + Done,
            "DBCC CHECKDB (cdb) WITH estimateonly executed by sa" + Done,
            "DBCC CHECKTABLE (cdb.dbo.t, pk_t) WITH no_infomsgs executed by sa" + Done,
            "DBCC CHECKTABLE (cdb.dbo.t, noindex) executed by sa" + Done,
            "DBCC CHECKALLOC (cdb) executed by sa" + Done,
            "DBCC CHECKCATALOG (cdb) executed by sa" + Done,
            "DBCC CHECKFILEGROUP (PRIMARY) executed by sa" + Done,
            "DBCC CHECKDB (cdb) WITH no_infomsgs executed by checker" + Done,
            "DBCC CHECKDB (master) WITH no_infomsgs executed by sa" + Done,
            "DBCC CHECKDB (mssqlsystemresource) WITH no_infomsgs executed by sa" + Done,
            "DBCC CHECKDB (tempdb) WITH no_infomsgs executed by sa" + Done,
        }, logged.ConvertAll(static text => text[..(text.IndexOf(Done, StringComparison.Ordinal) + Done.Length)]));
        // Each takes an internal snapshot but under TABLOCK and in tempdb and the resource database.
        var snapshots = logged.ConvertAll(static text => text[(text.IndexOf("seconds.  ", StringComparison.Ordinal) + "seconds.  ".Length)..]);
        CollectionAssert.AreEqual(new[] { 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0 }, snapshots.ConvertAll(static tail => tail.Length == 0 ? 0 : 1));
        foreach (var tail in snapshots.FindAll(static tail => tail.Length != 0))
        {
            var match = System.Text.RegularExpressions.Regex.Match(tail, "^Internal database snapshot has split point LSN = 00000022:([0-9a-f]{8}):0001 and first LSN = 00000022:([0-9a-f]{8}):0001\\.$");
            IsTrue(match.Success, tail);
            AreEqual(Convert.ToInt64(match.Groups[2].Value, 16) + 16, Convert.ToInt64(match.Groups[1].Value, 16));
        }
        // The summary goes to the log only when it went out as a message.
        CollectionAssert.AreEqual(new[]
        {
            "CHECKDB found 0 allocation errors and 0 consistency errors in database 'cdb'.",
            "CHECKALLOC found 0 allocation errors and 0 consistency errors in database 'cdb'.",
            "CHECKFILEGROUP found 0 allocation errors and 0 consistency errors in database 'cdb'.",
        }, LoggedTexts(simulation, "allocation errors"));
    }

    [TestMethod]
    public void CheckDb_IsRecalledAsTheDatabaseStartsUp()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create database rdb; alter database rdb set read_only; alter database rdb set read_write");
        AreEqual("1900-01-01 00:00:00.000", simulation.ExecuteScalar("select convert(varchar(23), databasepropertyex('rdb', 'LastGoodCheckDbTime'), 121)"));
        // Neither an estimate nor the narrower checks count.
        _ = simulation.ExecuteNonQuery("use rdb; dbcc checkdb with estimateonly; dbcc checkalloc with no_infomsgs; dbcc checkcatalog");
        AreEqual("1900-01-01 00:00:00.000", simulation.ExecuteScalar("select convert(varchar(23), databasepropertyex('rdb', 'LastGoodCheckDbTime'), 121)"));
        _ = simulation.ExecuteNonQuery("use rdb; dbcc checkdb with physical_only, no_infomsgs");
        var lastGood = (string)simulation.ExecuteScalar("select convert(varchar(23), databasepropertyex('rdb', 'LastGoodCheckDbTime'), 121)")!;
        AreNotEqual("1900-01-01 00:00:00.000", lastGood);
        _ = simulation.ExecuteNonQuery("alter database rdb set read_only");
        CollectionAssert.AreEqual(new[]
        {
            "Starting up database 'rdb'.",
            "Starting up database 'rdb'.",
            "Starting up database 'rdb'.",
            "Starting up database 'rdb'.",
            $"CHECKDB for database 'rdb' finished without errors on {lastGood} (local time). This is an informational message only; no user action is required.",
        }, LoggedTexts(simulation, "rdb''").FindAll(static text => !text.StartsWith("Setting", StringComparison.Ordinal)));
        // A backup carries it, as real's boot page does, to the database its restore rebuilds.
        _ = simulation.ExecuteNonQuery("alter database rdb set read_write; backup database rdb to disk = '/b/rdb.bak'; drop database rdb; restore database rdb from disk = '/b/rdb.bak'");
        AreEqual(lastGood, simulation.ExecuteScalar("select convert(varchar(23), databasepropertyex('rdb', 'LastGoodCheckDbTime'), 121)"));
        AreEqual($"CHECKDB for database 'rdb' finished without errors on {lastGood} (local time). This is an informational message only; no user action is required.", LoggedTexts(simulation, "rdb''")[^2]);
    }

    /// <summary>The process column and text of every entry past the startup preamble.</summary>
    private static List<string> EngineLines(Simulation simulation) =>
        Rows(simulation, "exec xp_readerrorlog").ConvertAll(static row => row[(row.IndexOf('|', StringComparison.Ordinal) + 1)..].TrimEnd('\r', '\n')).FindAll(static row => !row.StartsWith("Server|", StringComparison.Ordinal));

    [TestMethod]
    public void BackupAndRestore_LogTheirLines()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("create database bdb; exec sp_addumpdevice 'disk', 'dev', '/backups/dev.bak'").ExecuteNonQuery();
        var completions = new List<string>();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => completions.AddRange(e.Errors.Where(static error => error.Number == 3014).Select(static error => error.Message));
        _ = connection.CreateCommand("backup database bdb to disk = '/backups/bdb.bak'").ExecuteNonQuery();
        _ = connection.CreateCommand("backup database bdb to dev with init, copy_only").ExecuteNonQuery();
        _ = connection.CreateCommand("restore database bdb from disk = '/backups/bdb.bak' with replace").ExecuteNonQuery();
        var spid = connection.CreateCommand("select @@spid").ExecuteScalar();
        var lines = EngineLines(simulation);
        // The LSNs and page counts stand in for real's; mask them to compare the shapes.
        var masked = lines.ConvertAll(static line => System.Text.RegularExpressions.Regex.Replace(line, "(pages dumped: |LSN: 34:)[0-9]+", "$1n"));
        CollectionAssert.AreEqual(new[]
        {
            $"spid{spid}|Starting up database 'bdb'.",
            "Backup|Database backed up. Database: bdb, creation date(time): 2025/01/01(00:00:00), pages dumped: n, first LSN: 34:n:1, last LSN: 34:n:1, number of dump devices: 1, device information: (FILE=1, TYPE=DISK: {'/backups/bdb.bak'}). This is an informational message only. No user action is required.",
            "Backup|" + completions[0],
            "Backup|Database backed up. Database: bdb, creation date(time): 2025/01/01(00:00:00), pages dumped: n, first LSN: 34:n:1, last LSN: 34:n:1, number of dump devices: 1, device information: (FILE=1, TYPE=DISK: {'dev'}). This is an informational message only. No user action is required.",
            "Backup|" + completions[1],
            $"spid{spid}|Starting up database 'bdb'.",
            $"spid{spid}|The database 'bdb' is marked RESTORING and is in a state that does not allow recovery to be run.",
            $"spid{spid}|Starting up database 'bdb'.",
            "Backup|Restore is complete on database 'bdb'.  The database is now available.",
            "Backup|Database was restored: Database: bdb, creation date(time): 2025/01/01(00:00:00), first LSN: 34:n:1, last LSN: 34:n:1, number of dump devices: 1, device information: (FILE=1, TYPE=DISK: {'/backups/bdb.bak'}). Informational message. No user action required.",
            "Backup|" + completions[2],
        }, masked);
        // Real's set is nine pages past the files' own, and a full backup spans 24 log blocks.
        var pages = int.Parse(completions[0].Split(' ')[4], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains($"pages dumped: {pages + 9},", lines[1]);
        var lsns = System.Text.RegularExpressions.Regex.Matches(lines[1], "LSN: 34:([0-9]+):1");
        AreEqual(long.Parse(lsns[0].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) + 24, long.Parse(lsns[1].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        // The restore reports the set's own LSNs.
        AreEqual(lines[1][lines[1].IndexOf("first LSN", StringComparison.Ordinal)..lines[1].IndexOf(", number", StringComparison.Ordinal)],
            lines[9][lines[9].IndexOf("first LSN", StringComparison.Ordinal)..lines[9].IndexOf(", number", StringComparison.Ordinal)]);
    }

    [TestMethod]
    public void BackupAndRestore_Failures_LogAsRealDoes()
    {
        var simulation = new Simulation { OpenBackupFile = static (path, access) => access == FileAccess.Write && path.StartsWith("/nowhere/", StringComparison.Ordinal) ? null : new MemoryStream() };
        _ = simulation.ExecuteNonQuery("create database fdb; alter database fdb set recovery simple");
        using var connection = simulation.CreateOpenConnection();
        var spid = connection.CreateCommand("select @@spid").ExecuteScalar();
        void Fails(string sql) => _ = Throws<SimulatedSqlException>(() => connection.CreateCommand(sql).ExecuteNonQuery());
        Fails("backup database nosuch to disk = '/b/x.bak'");
        Fails("backup log fdb to disk = '/b/x.bak'");
        Fails("backup database fdb to disk = '/nowhere/x.bak'");
        // A transaction and tempdb are refused before the backup begins.
        Fails("begin tran; backup database fdb to disk = '/b/x.bak'");
        _ = connection.CreateCommand("if @@trancount > 0 rollback").ExecuteNonQuery();
        Fails("backup database tempdb to disk = '/b/x.bak'");
        CollectionAssert.AreEqual(new[]
        {
            "Backup|Error: 3041, Severity: 16, State: 1.",
            "Backup|BACKUP failed to complete the command BACKUP DATABASE nosuch. Check the backup application log for detailed messages.",
            "Backup|Error: 3041, Severity: 16, State: 1.",
            "Backup|BACKUP failed to complete the command BACKUP LOG fdb. Check the backup application log for detailed messages.",
            $"spid{spid}|Error: 18204, Severity: 16, State: 1.",
            $"spid{spid}|BackupDiskFile::CreateMedia: Backup device '/nowhere/x.bak' failed to create. Operating system error 3(The system cannot find the path specified.).",
            "Backup|Error: 3041, Severity: 16, State: 1.",
            "Backup|BACKUP failed to complete the command BACKUP DATABASE fdb. Check the backup application log for detailed messages.",
        }, EngineLines(simulation).FindAll(static line => !line.Contains("database option", StringComparison.Ordinal) && !line.Contains("Starting up", StringComparison.Ordinal)));
        // A restore's failure to open its device is the device's line alone.
        var restoring = new Simulation();
        using var restorer = restoring.CreateOpenConnection();
        var restorerSpid = restorer.CreateCommand("select @@spid").ExecuteScalar();
        _ = Throws<SimulatedSqlException>(() => restorer.CreateCommand("restore database fdb from disk = '/b/missing.bak'").ExecuteNonQuery());
        CollectionAssert.AreEqual(new[]
        {
            $"spid{restorerSpid}|Error: 18204, Severity: 16, State: 1.",
            $"spid{restorerSpid}|BackupDiskFile::OpenMedia: Backup device '/b/missing.bak' failed to open. Operating system error 2(The system cannot find the file specified.).",
        }, EngineLines(restoring));
    }

    [TestMethod]
    public void Kill_LogsTheKillersClient()
    {
        var simulation = new Simulation();
        using var victim = simulation.CreateOpenConnection();
        var victimSpid = victim.CreateCommand("select @@spid").ExecuteScalar();
        using var killer = simulation.CreateDbConnection();
        killer.ConnectionString = "Workstation ID=probehost";
        killer.Open();
        _ = killer.CreateCommand($"kill {victimSpid}").ExecuteNonQuery();
        CollectionAssert.AreEqual(new[] { $"spid{killer.CreateCommand("select @@spid").ExecuteScalar()}|Process ID {victimSpid} was killed by hostname probehost, host process ID {Environment.ProcessId}." },
            Logged(simulation, "was killed"));
    }

    [TestMethod]
    public void LogonTrigger_Messages_GoToTheErrorLog()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create login lt with password = 'Pa55word!'",
            """
            create trigger tr on all server for logon as
            begin
                if original_login() = N'lt'
                begin
                    print 'printed ' + original_login();
                    raiserror('raised ten %d', 10, 1, 42);
                    raiserror('raised sixteen', 16, 3);
                    raiserror('raised zero', 0, 1);
                    print '';
                end
            end
            """);
        using var connection = Login(simulation, "lt");
        var spid = connection.CreateCommand("select @@spid").ExecuteScalar();
        CollectionAssert.AreEqual(new[]
        {
            $"spid{spid}|printed lt",
            $"spid{spid}|raised ten 42",
            $"spid{spid}|Error: 50000, Severity: 16, State: 3.",
            $"spid{spid}|raised sixteen",
            $"spid{spid}|raised zero",
            // An empty PRINT is a single space, as on the wire.
            $"spid{spid}| ",
        }, EngineLines(simulation).FindAll(line => line.StartsWith($"spid{spid}|", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("print 'before'; declare @x int = 1 / 0;", new[] { "before", "Error: 8134, Severity: 16, State: 1.", "Divide by zero error encountered." })]
    [DataRow("select 'a result';", new[] { "Error: 575, Severity: 16, State: 1.", "A LOGON trigger returned a resultset. Modify the LOGON trigger to not return resultsets." })]
    public void LogonTrigger_Failure_LogsItsErrorAndTheRefusal(string body, string[] sessionLines)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create login lt with password = 'Pa55word!'",
            $"create trigger tr on all server for logon as begin if original_login() = N'lt' begin {body} end end");
        AreEqual(17892, Throws<SimulatedSqlException>(() => Login(simulation, "lt")).Number);
        var lines = EngineLines(simulation);
        var sessionTexts = lines.FindAll(static line => line.StartsWith("spid", StringComparison.Ordinal));
        CollectionAssert.AreEqual(sessionLines, sessionTexts.ConvertAll(static line => line[(line.IndexOf('|', StringComparison.Ordinal) + 1)..]));
        CollectionAssert.AreEqual(new[]
        {
            "Logon|Error: 17892, Severity: 20, State: 1.",
            "Logon|Logon failed for login 'lt' due to trigger execution. [CLIENT: <local machine>]",
        }, lines[^2..]);
    }

    [TestMethod]
    public void DiagnosticsLogConfigurations_ReportsTheStockRow()
    {
        var simulation = new Simulation();
        CollectionAssert.AreEqual(new[] { "1|/var/opt/mssql/log/|100|10" }, Rows(simulation, "select * from sys.dm_os_server_diagnostics_log_configurations"));
        _ = simulation.ExecuteNonQuery("create login nobody with password = 'Pa55word!'");
        using var nobody = Login(simulation, "nobody");
        AreEqual(300, Throws<SimulatedSqlException>(() => nobody.CreateCommand("select * from sys.dm_os_server_diagnostics_log_configurations").ExecuteNonQuery()).Number);
    }

    [TestMethod]
    public void Procedures_ListAsSqlServer2025ListsThem()
        => CollectionAssert.AreEqual(new[]
        {
            "sp_cycle_errorlog|-1060662208|P",
            "sp_enumerrorlogs|-573550902|P",
            "sp_readerrorlog|-529892888|P",
            "xp_enumerrorlogs|-542478583|X",
            "xp_logevent|-865724525|X",
            "xp_readerrorlog|-314669890|X",
        }, Rows(new Simulation(), "select name, object_id, rtrim(type) from sys.all_objects where name like '%errorlog%' or name = 'xp_logevent' order by name"));
}
