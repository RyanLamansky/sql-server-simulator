using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Savepoints through the transaction object — <see cref="SimulatedDbTransaction.Save"/>
/// and <see cref="SimulatedDbTransaction.Rollback(string)"/> — and through SQL
/// text, and the transaction object's life past an end it didn't make, as
/// SqlClient 7 behaves against SQL Server 2025 (probed 2026-09-28).
/// </summary>
[TestClass]
public sealed class TransactionSavepointTests
{
    private static SimulatedDbConnection Open()
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        _ = connection.CreateCommand("create table t (a int)").ExecuteNonQuery();
        return connection;
    }

    private static object? Run(SimulatedDbConnection connection, SimulatedDbTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [TestMethod]
    public void Save_ThenRollbackToIt_UndoesOnlyTheLaterWork()
    {
        using var connection = Open();
        var transaction = connection.BeginTransaction();
        _ = Run(connection, transaction, "insert t values (1)");
        transaction.Save("sp1");
        _ = Run(connection, transaction, "insert t values (2)");
        transaction.Rollback("SP1");
        AreEqual(1, Run(connection, transaction, "select @@trancount"));
        IsNotNull(transaction.Connection);
        transaction.Commit();
        AreEqual(1, Run(connection, null, "select count(*) from t"));
    }

    [TestMethod]
    public void Name_PastThirtyTwoCharacters_IsMsg103AtTheRequestsState()
    {
        using var connection = Open();
        var transaction = connection.BeginTransaction();
        transaction.Save(new string('a', 32));
        var ex = Throws<SimulatedSqlException>(() => transaction.Save(new string('b', 33)));
        AreEqual(103, ex.Number);
        AreEqual(30, ex.State);
        AreEqual(103, Throws<SimulatedSqlException>(() => transaction.Rollback(new string('b', 33))).Number);
        transaction.Rollback(new string('a', 32));
        AreEqual(1, Run(connection, transaction, "select @@trancount"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public void Name_NullOrEmpty_IsRefusedByTheClient(string? name)
    {
        using var connection = Open();
        var transaction = connection.BeginTransaction();
        _ = Throws<ArgumentException>(() => transaction.Save(name!));
        _ = Throws<ArgumentException>(() => transaction.Rollback(name!));
        transaction.Save(" ");
        transaction.Rollback(" ");
    }

    [TestMethod]
    public void RollbackToAnUnknownName_IsMsg6401_AndKeepsTheTransaction()
    {
        using var connection = Open();
        var transaction = connection.BeginTransaction();
        _ = Run(connection, transaction, "insert t values (1)");
        var ex = Throws<SimulatedSqlException>(() => transaction.Rollback("nosuch"));
        AreEqual(6401, ex.Number);
        AreEqual("Cannot roll back nosuch. No transaction or savepoint of that name was found.", ex.Message);
        transaction.Commit();
        AreEqual(1, Run(connection, null, "select count(*) from t"));
    }

    [TestMethod]
    public void RollbackToASavepoint_ConsumesItAndEveryLaterOne()
    {
        using var connection = Open();
        var transaction = connection.BeginTransaction();
        transaction.Save("q");
        _ = Run(connection, transaction, "insert t values (1)");
        transaction.Save("Q");
        _ = Run(connection, transaction, "insert t values (2)");
        transaction.Rollback("q");
        AreEqual(1, Run(connection, transaction, "select count(*) from t"));
        transaction.Rollback("q");
        AreEqual(0, Run(connection, transaction, "select count(*) from t"));
        transaction.Save("a");
        transaction.Save("b");
        transaction.Rollback("a");
        AreEqual(6401, Throws<SimulatedSqlException>(() => transaction.Rollback("b")).Number);
        transaction.Rollback();
    }

    [TestMethod]
    public void SqlTextSavepoints_StackAndAreConsumed()
    {
        using var connection = Open();
        var transaction = connection.BeginTransaction();
        AreEqual(1, Run(connection, transaction, """
            save tran s; insert t values (1);
            save tran s; insert t values (2);
            rollback tran s;
            select count(*) from t
            """));
        AreEqual(0, Run(connection, transaction, "rollback tran s; select count(*) from t"));
        AreEqual(6401, Throws<SimulatedSqlException>(() => Run(connection, transaction, "rollback tran s")).Number);
        transaction.Rollback();
    }

    [TestMethod]
    public void SqlTextSavepoint_NamedAsTheTransaction_IsRolledBackFirst()
    {
        using var connection = Open();
        AreEqual(1, Run(connection, null, "begin tran t1; save tran t1; insert t values (9); rollback tran t1; select @@trancount"));
        AreEqual(0, Run(connection, null, "rollback tran t1; select @@trancount"));
    }

    [TestMethod]
    public void RollbackNamingTheSqlTextTransaction_RollsItAllBack()
    {
        using var connection = Open();
        _ = Run(connection, null, "begin tran outer1; insert t values (1)");
        var transaction = connection.BeginTransaction();
        transaction.Save("s");
        _ = Run(connection, transaction, "insert t values (2)");
        transaction.Rollback("s");
        AreEqual(2, Run(connection, transaction, "select @@trancount"));
        transaction.Rollback("outer1");
        IsNull(transaction.Connection);
        _ = Throws<InvalidOperationException>(transaction.Commit);
        AreEqual(0, Run(connection, null, "select @@trancount + count(*) from t"));
    }

    [TestMethod]
    public void CompletedTransaction_RefusesSavepoints()
    {
        using var connection = Open();
        var transaction = connection.BeginTransaction();
        transaction.Commit();
        _ = Throws<InvalidOperationException>(() => transaction.Save("s"));
        _ = Throws<InvalidOperationException>(() => transaction.Rollback("s"));
    }

    [TestMethod]
    [DataRow("rollback")]
    [DataRow("commit")]
    [DataRow("rollback; select 1/0")]
    [DataRow("set xact_abort on; insert t values (1); select 1/0")]
    [DataRow("begin try select convert(int, 'x') end try begin catch end catch")]
    public void TransactionEndedElsewhere_TheFirstApiRollbackSucceeds(string ending)
    {
        using var connection = Open();
        var transaction = connection.BeginTransaction();
        try
        {
            _ = Run(connection, transaction, ending);
        }
        catch (SimulatedSqlException)
        {
        }
        _ = Run(connection, null, "set xact_abort off");
        IsNull(transaction.Connection);
        transaction.Rollback();
        _ = Throws<InvalidOperationException>(transaction.Rollback);
        _ = Throws<InvalidOperationException>(transaction.Commit);
    }

    [TestMethod]
    [DataRow("commit")]
    [DataRow("save")]
    public void TransactionEndedElsewhere_AnyOtherCallCompletesItForRollbackToo(string call)
    {
        using var connection = Open();
        var transaction = connection.BeginTransaction();
        _ = Run(connection, transaction, "rollback");
        _ = call == "commit"
            ? Throws<InvalidOperationException>(transaction.Commit)
            : Throws<InvalidOperationException>(() => transaction.Save("s"));
        _ = Throws<InvalidOperationException>(transaction.Rollback);
    }

    [TestMethod]
    public void DoomedTransaction_RefusesARollbackToASavepoint()
        => AreEqual(39309, new Simulation().ExecuteScalar("""
            set xact_abort on;
            declare @seen int;
            begin tran;
            save tran s;
            begin try declare @x int = 1/0 end try
            begin catch
                begin try rollback tran s end try
                begin catch set @seen = error_number() * 10 + xact_state() end catch
            end catch;
            rollback;
            select @seen
            """));

    [TestMethod]
    public void DoomedTransaction_UncaughtRollbackToASavepoint_EndsTheBatch()
    {
        using var connection = Open();
        var ex = Throws<SimulatedSqlException>(() => Run(connection, null, """
            set xact_abort on;
            begin tran;
            save tran s;
            begin try select 1/0 end try
            begin catch rollback tran s; select 'after' end catch;
            select 'after2'
            """));
        AreEqual(3931, ex.Number);
        AreEqual("The current transaction cannot be committed and cannot be rolled back to a savepoint. Roll back the entire transaction.", ex.Errors[0].Message);
        AreEqual(0, Run(connection, null, "select @@trancount"));
    }

    [TestMethod]
    public void RollbackToASavepoint_DiscardsTheVersionsWrittenAfterIt()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            alter database current set allow_snapshot_isolation on;
            create table t (id int not null primary key, v int);
            insert t values (1, 100)
            """);
        using (var writer = sim.CreateDbConnection())
        {
            writer.Open();
            var transaction = writer.BeginTransaction();
            _ = Run(writer, transaction, "update t set v = 200 where id = 1");
            transaction.Save("s");
            _ = Run(writer, transaction, "update t set v = 300 where id = 1; insert t values (2, 1)");
            transaction.Rollback("s");
            transaction.Commit();
        }
        AreEqual(200, sim.ExecuteScalar("set transaction isolation level snapshot; begin tran; select sum(v) from t; commit"));
    }
}
