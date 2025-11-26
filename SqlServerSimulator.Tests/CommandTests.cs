using System.Data;
using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public class CommandTests
{
    private static DbCommand CreateCommand() => new Simulation().CreateDbConnection().CreateCommand();

    private static DbCommand CreateCommand(string? commandText) => new Simulation().CreateCommand(commandText);

    private static DbConnection CreateOpenConnection() => new Simulation().CreateOpenConnection();

    [TestMethod]
    public void DbConnectionPassThrough()
    {
        using var connection = new Simulation().CreateDbConnection();
        using var command = connection.CreateCommand();
        AreSame(connection, command.Connection);
    }

    [TestMethod]
    public void CreateCommandIsNotNull() => IsNotNull(CreateCommand());

    [TestMethod]
    public void CommandTypeDefaultsText() => AreEqual(System.Data.CommandType.Text, CreateCommand().CommandType);

    [TestMethod]
    public void CreateCommandWithCommandText()
    {
        const string commandText = "select 1";
        using var command = CreateCommand(commandText);
        AreEqual(commandText, command.CommandText);
    }

    [TestMethod]
    public void NullCommandTextConvertedToEmptyString() => AreSame(string.Empty, CreateCommand("").CommandText);

    [TestMethod]
    public void CommandTimeoutRangeCheck()
    {
        using var command = CreateCommand();
        AreEqual(30, command.CommandTimeout);
        var exception = ThrowsExactly<ArgumentException>(() => command.CommandTimeout = -1);
        AreEqual(nameof(command.CommandTimeout), exception.ParamName);

        command.CommandTimeout = 5;
        AreEqual(5, command.CommandTimeout);
    }

    [TestMethod]
    public void DesignTimeVisibleBehavior()
    {
        using var command = CreateCommand();
        IsTrue(command.DesignTimeVisible);
        command.DesignTimeVisible = false;
        IsFalse(command.DesignTimeVisible);
        command.DesignTimeVisible = true;
        IsTrue(command.DesignTimeVisible);
    }

    [TestMethod]
    public void TransactionPassThrough()
    {
        using var connection = CreateOpenConnection();
        using var transaction = connection.BeginTransaction();
        AreSame(transaction.Connection, connection);
        using var command = connection.CreateCommand();
        IsNull(command.Transaction);

        command.Transaction = transaction;
        AreSame(transaction, command.Transaction);
        command.Transaction = null;
        IsNull(command.Transaction);
    }

    sealed class TestTransaction : DbTransaction
    {
        public override IsolationLevel IsolationLevel => throw new NotImplementedException();
        protected override DbConnection? DbConnection => throw new NotImplementedException();
        public override void Commit() => throw new NotImplementedException();
        public override void Rollback() => throw new NotImplementedException();
    }

    [TestMethod]
    public void TransactionRestrictions()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        using (var sim1con2 = simulation.CreateOpenConnection())
        {
            using var otherConnectionTransaction = sim1con2.BeginTransaction();

            using var doomedCommand = connection.CreateCommand();

            _ = Throws<Exception>(() => doomedCommand.Transaction = otherConnectionTransaction);
        }

        using var connection2 = CreateOpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command2 = connection2.CreateCommand();
        _ = Throws<Exception>(() => command2.Transaction = transaction);
        _ = Throws<Exception>(() => command2.Transaction = new TestTransaction());
    }
}
