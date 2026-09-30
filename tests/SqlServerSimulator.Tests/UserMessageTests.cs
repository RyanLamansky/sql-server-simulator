using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sp_addmessage</c> / <c>sp_altermessage</c> / <c>sp_dropmessage</c>, <c>sys.messages</c>
/// and the registered messages <c>RAISERROR</c> and <c>FORMATMESSAGE</c> read.
/// </summary>
[TestClass]
public sealed class UserMessageTests
{
    [TestMethod]
    public void AddedMessage_AppearsInSysMessages()
        => AreEqual("Hello %s", new Simulation().ExecuteScalar("""
            exec sp_addmessage 50001, 16, N'Hello %s';
            select text from sys.messages where message_id = 50001 and language_id = 1033
            """));

    [TestMethod]
    public void AddedMessage_FormatsThroughFormatMessage()
        => AreEqual("Hello world", new Simulation().ExecuteScalar("""
            exec sp_addmessage 50002, 16, N'Hello %s';
            select formatmessage(50002, N'world')
            """));

    [TestMethod]
    public void AddedMessage_RaisedByRaiserror_UsesItsSeverity()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("exec sp_addmessage 50003, 11, N'Low %d'");
        var ex = sim.AssertSqlError("raiserror(50003, -1, 1, 7)", 50003);
        AreEqual((byte)11, ex.Class);
        AreEqual("Low 7", ex.Errors[0].Message);
    }

    [TestMethod]
    public void AddMessage_Duplicate_Raises15043()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("exec sp_addmessage 50004, 16, N'x'");
        _ = sim.AssertSqlError("exec sp_addmessage 50004, 16, N'y'", 15043);
    }

    [TestMethod]
    public void AddMessage_Replace_OverwritesText()
        => AreEqual("second", new Simulation().ExecuteScalar("""
            exec sp_addmessage 50005, 16, N'first';
            exec sp_addmessage 50005, 16, N'second', @replace = 'replace';
            select text from sys.messages where message_id = 50005
            """));

    [TestMethod]
    public void AddMessage_IdBelow50001_Raises15040()
        => _ = new Simulation().AssertSqlError("exec sp_addmessage 49999, 16, N'x'", 15040);

    [TestMethod]
    public void DropMessage_RemovesRow()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            exec sp_addmessage 50006, 16, N'x';
            exec sp_dropmessage 50006;
            select count(*) from sys.messages where message_id = 50006
            """));

    [TestMethod]
    public void DropMessage_Missing_Raises15179()
        => _ = new Simulation().AssertSqlError("exec sp_dropmessage 50007", 15179);

    [TestMethod]
    public void AlterMessage_WithLog_SetsIsEventLogged()
        => IsTrue((bool)new Simulation().ExecuteScalar("""
            exec sp_addmessage 50008, 16, N'x';
            exec sp_altermessage 50008, 'WITH_LOG', 'true';
            select is_event_logged from sys.messages where message_id = 50008
            """)!);

    [TestMethod]
    public void MessageRegistry_RollsBackWithTransaction()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            begin tran;
            exec sp_addmessage 50009, 16, N'x';
            rollback;
            select count(*) from sys.messages where message_id = 50009
            """));
}
