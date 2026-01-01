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
        using var connection = new Simulation().CreateDbConnection();
        using var command = connection.CreateCommand();

        connection.Open();

        var x = Throws<InvalidOperationException>(command.ExecuteReader);
        AreEqual("ExecuteReader: CommandText property has not been initialized", x.Message);
    }

    [TestMethod]
    public void EmptyCommand()
    {
        using var connection = new Simulation().CreateDbConnection();
        connection.Open();

        using var command = connection.CreateCommand("");

        var x = Throws<InvalidOperationException>(command.ExecuteReader);
        AreEqual("ExecuteReader: CommandText property has not been initialized", x.Message);
    }

    [TestMethod]
    public void SpaceCommand()
    {
        using var connection = new Simulation().CreateDbConnection();
        connection.Open();

        using var command = connection.CreateCommand(" ");
        using var reader = command.ExecuteReader();

        IsFalse(reader.Read());
    }

    [TestMethod]
    public void IncompleteSingleLineCommentCommand()
    {
        using var connection = new Simulation().CreateDbConnection();
        connection.Open();

        using var command = connection.CreateCommand("-");
        _ = Throws<DbException>(command.ExecuteReader);
    }

    [TestMethod]
    public void SingleLineEmptyCommentCommand()
    {
        using var connection = new Simulation().CreateDbConnection();
        connection.Open();

        using var command = connection.CreateCommand("--");
        using var reader = command.ExecuteReader();

        IsFalse(reader.Read());
    }

    [TestMethod]
    public void SingleLineCommentCommand()
    {
        using var connection = new Simulation().CreateDbConnection();
        connection.Open();

        using var command = connection.CreateCommand("-- Test");
        using var reader = command.ExecuteReader();

        IsFalse(reader.Read());
    }

    [TestMethod]
    public void SingleLineCommentWithNewlineCommand()
    {
        using var connection = new Simulation().CreateDbConnection();
        connection.Open();

        using var command = connection.CreateCommand("-- Test\n");
        using var reader = command.ExecuteReader();

        IsFalse(reader.Read());
    }

    [TestMethod]
    public void SingleLineCommentWithCarriageReturnCommand()
    {
        using var connection = new Simulation().CreateDbConnection();
        connection.Open();

        using var command = connection.CreateCommand("-- Test\r");
        using var reader = command.ExecuteReader();

        IsFalse(reader.Read());
    }

    [TestMethod]
    public void BlockComment()
    {
        AreEqual(-1, new Simulation().ExecuteNonQuery("/* */"));
    }
}
