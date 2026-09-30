using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// THROW reads its message for <c>%</c> escapes as an operating-system
/// message template, not as RAISERROR's format (probed 2026-09-30 against
/// SQL Server 2025).
/// </summary>
[TestClass]
public sealed class ThrowMessageFormatTests
{
    [TestMethod]
    [DataRow("no percent", "no percent")]
    [DataRow("100%%", "100%")]
    [DataRow("a%%b", "a%b")]
    [DataRow("%%d", "%d")]
    [DataRow("%.2f", ".2f")]
    [DataRow("50% off", "50 off")]
    [DataRow("% d", " d")]
    [DataRow("a%.b%.c", "a.b.c")]
    [DataRow("x%!x", "x!x")]
    [DataRow("%0abc", "abc")]
    [DataRow("%05d", "5d")]
    [DataRow("%d", "")]
    [DataRow("100%", "")]
    [DataRow("a%b", "")]
    [DataRow("%1", "")]
    [DataRow("%r", "")]
    [DataRow("%t", "")]
    [DataRow("%%%", "")]
    [DataRow("%x %s", "")]
    public void PercentEscapes(string message, string sent)
        => AreEqual(sent, new Simulation().ExecuteScalar($"begin try throw 50000, '{message}', 1 end try begin catch select error_message() end catch"));

    [TestMethod]
    public void PercentN_SendsALineBreakThenItsOwnLetter()
        => AreEqual("a\r\nnb", new Simulation().ExecuteScalar("begin try throw 50000, 'a%nb', 1 end try begin catch select error_message() end catch"));

    [TestMethod]
    public void AVariableMessage_IsReadTheSameWay()
        => AreEqual("50 off", new Simulation().ExecuteScalar("declare @m nvarchar(20) = N'50% off'; begin try throw 50000, @m, 1 end try begin catch select error_message() end catch"));

    [TestMethod]
    public void TheReRaise_KeepsTheMessageItAlreadyHas()
    {
        var error = Throws<SimulatedSqlException>(() => new Simulation().ExecuteScalar("begin try throw 50000, '100%%', 1 end try begin catch throw end catch"));
        AreEqual("100%", error.Errors[0].Message);
    }
}
