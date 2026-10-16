using System.Data.Common;
using System.Xml.Linq;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The text a statement carrying a password or secret reports through the
/// surfaces that show statement and batch text, probed 2026-10-16 against SQL
/// Server 2025.
/// </summary>
[TestClass]
public sealed class SecretStatementTextTests
{
    private static Simulation WithSession()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create event session s on server
            add event sqlserver.sql_batch_starting (where sqlserver.client_app_name = N'app'),
            add event sqlserver.sql_batch_completed (where sqlserver.client_app_name = N'app'),
            add event sqlserver.sql_statement_completed (where sqlserver.client_app_name = N'app')
            add target package0.ring_buffer
            """);
        _ = sim.ExecuteNonQuery("alter event session s on server state = start");
        return sim;
    }

    private static DbConnection Client(Simulation sim)
    {
        var connection = sim.CreateDbConnection();
        connection.ConnectionString = "Application Name=app";
        connection.Open();
        return connection;
    }

    /// <summary>Each event the ring buffer holds, oldest first, as its name and its statement or batch text.</summary>
    private static List<string> Events(Simulation sim)
    {
        var data = (string)sim.ExecuteScalar("""
            select t.target_data from sys.dm_xe_session_targets t join sys.dm_xe_sessions s on s.address = t.event_session_address
            where s.name = 's' and t.target_name = 'ring_buffer'
            """)!;
        return [.. XElement.Parse(data).Elements("event").Select(e =>
            e.Attribute("name")!.Value + "|" + e.Elements("data").First(d => d.Attribute("name")!.Value is "statement" or "batch_text").Element("value")!.Value)];
    }

    [TestMethod]
    public void StatementEvents_ReadTheLabel_AndBatchEventsAMaskOfTheStatementsLength()
    {
        var sim = WithSession();
        using (var client = Client(sim))
            _ = client.CreateCommand("select 1 as a;\ncreate login l with password = 'Xy!12345abcd';\nselect pwdencrypt('abc') p;\nselect 2 as b;").ExecuteNonQuery();
        // Each mask is its statement's length, the CREATE LOGIN's taking its
        // semicolon and the SELECT's leaving it.
        var masked = "select 1 as a;\n" + "--*CREATE LOGIN".PadRight("create login l with password = 'Xy!12345abcd';".Length, '-')
            + "\n" + "--*SELECT WITHOUT QUERY".PadRight("select pwdencrypt('abc') p".Length, '-') + ";\nselect 2 as b;";
        var events = Events(sim);
        CollectionAssert.AreEqual(new[]
        {
            "sql_batch_starting|" + masked,
            "sql_statement_completed|select 1 as a",
            "sql_statement_completed|-- CREATE LOGIN",
            "sql_statement_completed|-- SELECT WITHOUT QUERY",
            "sql_statement_completed|select 2 as b",
            "sql_batch_completed|" + masked,
        }, events);
    }

    [TestMethod]
    [DataRow("alter login l disable", "alter login l disable")]
    [DataRow("select 'password' as p", "select 'password' as p")]
    [DataRow("/* password */ select 3 as c", "select 3 as c")]
    [DataRow("alter login l with password = 'Xy!12345abce'", "-- ALTER LOGIN")]
    [DataRow("create application role r with password = 'Xy!12345abcd'", "-- CREATE APPLICATION ROLE")]
    [DataRow("create master key encryption by password = 'Xy!12345abcd'", "-- CREATE MASTER KEY")]
    [DataRow("create credential c with identity = 'x', secret = 'Xy!12345abcd'", "-- CREATE CREDENTIAL")]
    [DataRow("exec sp_password @new = 'Xy!12345abce', @loginame = 'l'", "-- sp_password")]
    public void Statements_AreLabelledByKind_AndOnlyWithTheirSecretAsAToken(string statement, string reported)
    {
        var sim = WithSession();
        _ = sim.ExecuteNonQuery("create login l with password = 'Xy!12345abcd'");
        using (var client = Client(sim))
        {
            try
            {
                _ = client.CreateCommand(statement).ExecuteNonQuery();
            }
            catch (SimulatedSqlException)
            {
            }
        }
        Contains("sql_statement_completed|" + reported, Events(sim));
    }

    [TestMethod]
    public void TheBatch_ReachesNoSessionSurface()
    {
        var sim = new Simulation();
        using var secret = sim.CreateOpenConnection();
        _ = secret.CreateCommand("select 1 as a; create login l with password = 'Xy!12345abcd';").ExecuteNonQuery();
        var spid = (short)secret.CreateCommand("select @@spid").ExecuteScalar()!;
        _ = secret.CreateCommand("select 1 as a; create login l2 with password = 'Xy!12345abcd';").ExecuteNonQuery();
        using var observer = sim.CreateOpenConnection();
        var spidText = spid.ToInvariantString();
        AreEqual(DBNull.Value, observer.CreateCommand($"select event_info from sys.dm_exec_input_buffer({spidText}, null)").ExecuteScalar());
        using (var reader = observer.CreateCommand($"dbcc inputbuffer({spidText})").ExecuteReader())
        {
            IsTrue(reader.Read());
            IsTrue(reader.IsDBNull(2));
        }
        AreEqual(0, observer.CreateCommand($"select count(*) from sys.dm_exec_connections c cross apply sys.dm_exec_sql_text(c.most_recent_sql_handle) t where c.session_id = {spidText}").ExecuteScalar());
    }

    [TestMethod]
    public void APlanCachedForTheBatch_NamesTheMaskedText()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (i int); insert t values (1)");
        _ = sim.ExecuteNonQuery("select count(*) from t as qq; select pwdencrypt('x') p; select 2 as bqq;");
        _ = sim.ExecuteNonQuery("select count(*) from t as qq; create login l with password = 'Xy!12345abcd'; select 2 as bqq;");
        AreEqual("select count(*) from t as qq; --*SELECT WITHOUT QUERY-; select 2 as bqq;", sim.ExecuteScalar("""
            select string_agg(t.text, ' | ') from sys.dm_exec_cached_plans p cross apply sys.dm_exec_sql_text(p.plan_handle) t
            where t.text like '%as bqq%' and t.text not like '%dm_exec_cached_plans%'
            """));
    }
}
