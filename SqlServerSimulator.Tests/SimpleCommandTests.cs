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
        var x = Throws<InvalidOperationException>(new Simulation().CreateCommand("").ExecuteReader);
        AreEqual("ExecuteReader: CommandText property has not been initialized", x.Message);
    }

    [TestMethod]
    public void SpaceCommand()
    {
        IsFalse(new Simulation().ExecuteReader(" ").Read());
    }

    [TestMethod]
    public void IncompleteSingleLineCommentCommand()
    {
        _ = Throws<DbException>(() => new Simulation().ExecuteReader("-"));
    }

    [TestMethod]
    [DataRow("--")]
    [DataRow("--Test")]
    [DataRow("--Test \n")]
    [DataRow("--Test \r")]
    [DataRow("--Test \r\n")]
    [DataRow("--Test\n")]
    [DataRow("--Test\r")]
    [DataRow("--Test\r\n")]
    public void LineComment(string comment)
    {
        IsNull(new Simulation().ExecuteScalar($"{comment}"));
        AreEqual(1, new Simulation().ExecuteScalar<int>($"select 1 {comment}"));
        AreEqual(2, new Simulation().ExecuteScalar<int>($"select 2{comment}"));
        AreEqual(3, new Simulation().ExecuteScalar<int>($"select 3\n{comment}"));
        AreEqual(4, new Simulation().ExecuteScalar<int>($"select 4 \n{comment}"));
        AreEqual(5, new Simulation().ExecuteScalar<int>($"select 5\r{comment}"));
        AreEqual(6, new Simulation().ExecuteScalar<int>($"select 6 \r{comment}"));
        AreEqual(7, new Simulation().ExecuteScalar<int>($"select 7\r\n{comment}"));
        AreEqual(8, new Simulation().ExecuteScalar<int>($"select 8 \r\n{comment}"));
        AreEqual(9, new Simulation().ExecuteScalar<int>($"{comment}\nselect 9"));
        AreEqual(10, new Simulation().ExecuteScalar<int>($"{comment}\rselect 10"));
        AreEqual(11, new Simulation().ExecuteScalar<int>($"{comment}\r\nselect 11"));
    }

    [TestMethod]
    [DataRow("/* */")]
    [DataRow("/**/")]
    [DataRow("/***/")]
    [DataRow("/** **/")]
    [DataRow("/*\r*/")]
    [DataRow("/*\n*/")]
    [DataRow("/*\r\n*/")]
    [DataRow("/* /* */ */")]
    public void BlockComment(string comment)
    {
        IsNull(new Simulation().ExecuteScalar($"{comment}"));
        AreEqual(1, new Simulation().ExecuteScalar<int>($"select 1 {comment}"));
        AreEqual(2, new Simulation().ExecuteScalar<int>($"select 2{comment}"));
        AreEqual(3, new Simulation().ExecuteScalar<int>($"{comment}select 3"));
        AreEqual(4, new Simulation().ExecuteScalar<int>($"{comment} select 4"));
        AreEqual(5, new Simulation().ExecuteScalar<int>($"select{comment}5"));
        AreEqual(6, new Simulation().ExecuteScalar<int>($"select {comment}6"));
        AreEqual(7, new Simulation().ExecuteScalar<int>($"select{comment} 7"));
        AreEqual(8, new Simulation().ExecuteScalar<int>($"select {comment} 8"));
    }

    [TestMethod]
    public void MissingEndCommentMark()
    {
        var x = Throws<DbException>(() => new Simulation().ExecuteScalar("/*"));
        AreEqual("Missing end comment mark '*/'.", x.Message);
    }
}
