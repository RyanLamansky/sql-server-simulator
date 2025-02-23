using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public class SimpleCommandTests
{
    [TestMethod]
    public void CommandTextConvertsNullToEmpty()
    {
        AreSame(new Simulation().CreateCommand(null).CommandText, string.Empty);
    }

    [TestMethod]
    public void CommandNotInitialized()
    {
        using var connnection = new Simulation().CreateDbConnection();
        using var command = connnection.CreateCommand();

        connnection.Open();

        var x = ThrowsException<InvalidOperationException>(command.ExecuteReader);
        AreEqual("ExecuteReader: CommandText property has not been initialized", x.Message);
    }

    [TestMethod]
    public void EmptyCommand()
    {
        using var connnection = new Simulation().CreateDbConnection();
        connnection.Open();

        using var command = connnection.CreateCommand("");

        var x = ThrowsException<InvalidOperationException>(command.ExecuteReader);
        AreEqual("ExecuteReader: CommandText property has not been initialized", x.Message);
    }

    [TestMethod]
    public void SpaceCommand()
    {
        using var connnection = new Simulation().CreateDbConnection();
        connnection.Open();

        using var command = connnection.CreateCommand(" ");
        using var reader = command.ExecuteReader();

        IsFalse(reader.Read());
    }

    [TestMethod]
    [ExpectedException(typeof(DbException), AllowDerivedTypes = true)]
    public void IncompleteSingleLineCommentCommand()
    {
        using var connnection = new Simulation().CreateDbConnection();
        connnection.Open();

        using var command = connnection.CreateCommand("-");
        _ = command.ExecuteReader();
    }

    [TestMethod]
    public void SingleLineEmptyCommentCommand()
    {
        using var connnection = new Simulation().CreateDbConnection();
        connnection.Open();

        using var command = connnection.CreateCommand("--");
        using var reader = command.ExecuteReader();

        IsFalse(reader.Read());
    }

    [TestMethod]
    public void SingleLineCommentCommand()
    {
        using var connnection = new Simulation().CreateDbConnection();
        connnection.Open();

        using var command = connnection.CreateCommand("-- Test");
        using var reader = command.ExecuteReader();

        IsFalse(reader.Read());
    }

    [TestMethod]
    public void SingleLineCommentWithNewlineCommand()
    {
        using var connnection = new Simulation().CreateDbConnection();
        connnection.Open();

        using var command = connnection.CreateCommand("-- Test\n");
        using var reader = command.ExecuteReader();

        IsFalse(reader.Read());
    }

    [TestMethod]
    public void SingleLineCommentWithCarriageReturnCommand()
    {
        using var connnection = new Simulation().CreateDbConnection();
        connnection.Open();

        using var command = connnection.CreateCommand("-- Test\r");
        using var reader = command.ExecuteReader();

        IsFalse(reader.Read());
    }
}
