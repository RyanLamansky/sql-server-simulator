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

    [TestMethod]
    [DataRow("exec sp_addmessage 50010, 26, N'x'", 15041, 38, "User-defined error messages must have a severity level between 1 and 25.")]
    [DataRow("exec sp_addmessage 50010, 16, null", 15071, 24, "Usage: sp_addmessage <msgnum>,<severity>,<msgtext> [,<language> [,FALSE | TRUE [,REPLACE]]]")]
    [DataRow("exec sp_addmessage 50010, 16, N'x', @with_log = 'maybe'", 15271, 73, "Invalid @with_log parameter value. Valid values are 'true' or 'false'.")]
    [DataRow("exec sp_addmessage 50010, 16, N'x', @lang = 'French'", 15279, 97, "You must add the us_english version of this message before you can add the 'French' version.")]
    [DataRow("exec sp_dropmessage null", 15177, 18, "Usage: sp_dropmessage <msg number> [,<language> | 'ALL']")]
    [DataRow("exec sp_dropmessage 50000", 15178, 25, "Cannot drop a message with an ID less than 50,000.")]
    [DataRow("exec sp_altermessage 50010, 'FOO', 'true'", 15176, 21, "The only valid @parameter value is 'WITH_LOG'.")]
    [DataRow("exec sp_altermessage 50010, 'WITH_LOG', 'maybe'", 15277, 32, "The only valid @parameter_value values are 'true' or 'false'.")]
    public void MessageProcedure_ArgumentRefusals_RaiseFromTheirSourceLine(string sql, int number, int line, string message)
    {
        var ex = new Simulation().AssertSqlError(sql, number);
        AreEqual(message, ex.Errors[0].Message);
        AreEqual(line, ex.LineNumber);
        AreEqual((byte)1, ex.State);
    }

    [TestMethod]
    public void AlterMessage_NullId_Raises290AtState2()
    {
        var ex = new Simulation().AssertSqlError("exec sp_altermessage null, 'WITH_LOG', 'true'", 290);
        AreEqual("Invalid EXECUTE statement using object \"ErrorMessage\", method \"Lock\".", ex.Errors[0].Message);
        AreEqual((byte)2, ex.State);
        AreEqual(38, ex.LineNumber);
    }

    [TestMethod]
    public void AddMessage_LocalizedSeverityMustMatch_Raises15304()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("exec sp_addmessage 50011, 16, N'x'");
        sim.AssertSqlError("exec sp_addmessage 50011, 10, N'y', @lang = 'French'", 15304,
            "The severity level of the 'French' version of this message must be the same as the severity level (16) of the us_english version.");
    }

    [TestMethod]
    public void DropMessage_UsEnglishWithLocalizedVersions_Raises15280()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("exec sp_addmessage 50012, 16, N'x'; exec sp_addmessage 50012, 16, N'y', @lang = 'French'");
        sim.AssertSqlError("exec sp_dropmessage 50012, 'us_english'", 15280,
            "All localized versions of this message must be dropped before the us_english version can be dropped.");
    }

    [TestMethod]
    public void OmittedLanguage_IsTheSessionLanguage()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("exec sp_addmessage 50013, 16, N'x %s'; set language French; exec sp_addmessage 50013, 16, N'fr %1!'").ExecuteNonQuery();
        AreEqual("1033,1036", connection.CreateCommand("select string_agg(language_id, ',') within group (order by language_id) from sys.messages where message_id = 50013").ExecuteScalar());
        _ = connection.CreateCommand("exec sp_dropmessage 50013, null").ExecuteNonQuery();
        AreEqual("1033", connection.CreateCommand("select string_agg(language_id, ',') from sys.messages where message_id = 50013").ExecuteScalar());
    }

    [TestMethod]
    public void LocalizedMessage_PositionalPlaceholders_ReadTheUsEnglishSpecifiers()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("exec sp_addmessage 50014, 16, N'x %s %d'; exec sp_addmessage 50014, 16, N'fr %2! et %1!', @lang = 'French'");
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("set language French").ExecuteNonQuery();
        AreEqual("fr 5 et abc", connection.CreateCommand("select formatmessage(50014, N'abc', 5)").ExecuteScalar());
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("raiserror(50014, 16, 1, N'abc', 5)").ExecuteNonQuery());
        AreEqual("fr 5 et abc", ex.Errors[0].Message);
    }

    [TestMethod]
    public void LocalizedMessage_PlaceholderPastTheSpecifiers_Raises2787_FormatMessageGoesTerse()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("exec sp_addmessage 50015, 16, N'x %s'; exec sp_addmessage 50015, 16, N'fr %3!', @lang = 'French'");
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("set language French").ExecuteNonQuery();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("raiserror(50015, 16, 1, N'abc')").ExecuteNonQuery());
        AreEqual(2787, ex.Number);
        AreEqual((byte)2, ex.State);
        AreEqual("Invalid format specification: '3!'.", ex.Errors[0].Message);
        StartsWith("Error: 50015, Severity: 16, State: 1. (Params:). The error is printed in terse mode", (string)connection.CreateCommand("select formatmessage(50015, N'abc')").ExecuteScalar()!);
    }
}
