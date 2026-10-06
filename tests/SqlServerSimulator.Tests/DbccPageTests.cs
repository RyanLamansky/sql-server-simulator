using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>DBCC PAGE</c>'s argument checks and its quiet completion without trace
/// flag 3604, <c>CHECKDB WITH TABLOCK</c>'s Msg 5232, <c>SHOWCONTIG</c>'s
/// permission check and Msg 2528 in the session's language. Every
/// expectation probed 2026-10-06 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class DbccPageTests
{
    private static List<string> Messages(Simulation simulation, string sql)
    {
        using var connection = simulation.CreateOpenConnection();
        var messages = new List<string>();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.Add(e.Message);
        _ = connection.CreateCommand(sql).ExecuteNonQuery();
        return messages;
    }

    [TestMethod]
    public void PageInRange_CompletesQuietly()
    {
        var messages = Messages(new Simulation(), "dbcc page (0, 1, 1); dbcc page (0, 2, 0, 3)");
        HasCount(2, messages);
        AreEqual("DBCC execution completed. If DBCC printed error messages, contact your system administrator.", messages[0]);
    }

    [TestMethod]
    [DataRow("dbcc page (0, 1, 99999999)", "(1:99999999)")]
    [DataRow("dbcc page (0, 1, -1)", "(1:-1)")]
    [DataRow("dbcc page (0, 7, 5, 9)", "(7:5)")]
    [DataRow("dbcc page (0, 0, 1)", "(0:1)")]
    public void PageOutOfRange_IsMsg8968(string sql, string address)
    {
        var error = new Simulation().AssertSqlError(sql, 8968);
        AreEqual($"Table error: DBCC PAGE page {address} (object ID 0, index ID 0, partition ID 0, alloc unit ID 0 (type Unknown)) is out of the range of this database.", error.Errors[0].Message);
    }

    [TestMethod]
    [DataRow("dbcc page (0, 2, 5, 9)", 2514, 9)]
    [DataRow("dbcc page (0, 1, 1, 4)", 2560, 102)]
    [DataRow("dbcc page (0, 1, 1, '1')", 2560, 9)]
    [DataRow("dbcc page (0, 1, 1.5)", 2560, 9)]
    [DataRow("dbcc page (-1, 1, 1)", 2560, 9)]
    [DataRow("dbcc page (0, 1)", 2583, 3)]
    [DataRow("dbcc page (999, 1, 1)", 2521, 10)]
    [DataRow("dbcc page (nosuch, 1, 1, 9)", 2520, 5)]
    public void Page_ArgumentRefusals(string sql, int number, int state)
        => AreEqual(state, new Simulation().AssertSqlError(sql, number).Errors[0].State);

    [TestMethod]
    public void PageDump_IsNotSupported()
    {
        _ = Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery("dbcc page (0, 1, 1) with tableresults"));
        _ = Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery("dbcc traceon (3604); dbcc page (0, 1, 1)"));
    }

    [TestMethod]
    public void CheckDbWithTabLock_SkipsTheCatalogAndBrokerChecks()
    {
        var messages = Messages(new Simulation(), "dbcc checkdb with tablock");
        StartsWith("DBCC CHECKDB will not check SQL Server catalog", messages[1]);
        IsFalse(messages.Exists(message => message.StartsWith("Service Broker", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ShowContig_AsksForAlterOnTheTable()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table dbo.t (a int); create user u without login; grant select on dbo.t to u");
        var error = sim.AssertSqlError("execute as user = 'u'; dbcc showcontig ('dbo.t')", 229);
        AreEqual("The DBCC permission was denied on the object 't', database 'simulated', schema 'dbo'.", error.Errors[0].Message);
    }

    [TestMethod]
    public void CompletionMessage_FollowsTheSessionLanguage()
    {
        var messages = Messages(new Simulation(), "set language Deutsch; dbcc freeproccache");
        AreEqual("Die DBCC-Ausführung wurde abgeschlossen. Falls DBCC Fehlermeldungen ausgegeben hat, wenden Sie sich an den Systemadministrator.", messages[^1]);
    }
}
