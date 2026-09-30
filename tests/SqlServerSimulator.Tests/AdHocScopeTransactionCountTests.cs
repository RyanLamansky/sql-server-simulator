using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// An <c>sp_executesql</c> scope — a command carrying parameters — that ends
/// with <c>@@TRANCOUNT</c> other than it began with answers Msg 266 after its
/// results, as a procedure does (probed 2026-09-30 against SQL Server 2025).
/// A plain batch is judged by nothing.
/// </summary>
[TestClass]
public sealed class AdHocScopeTransactionCountTests
{
    private static SimulatedSqlException Run(DbConnectionHolder holder, string text)
        => Throws<SimulatedSqlException>(() => holder.Execute(text));

    private sealed class DbConnectionHolder(Simulation simulation) : IDisposable
    {
        private readonly System.Data.Common.DbConnection connection = simulation.CreateOpenConnection();

        public object? Execute(string text)
        {
            using var command = this.connection.CreateCommand(text);
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@zz";
            parameter.Value = 1;
            _ = command.Parameters.Add(parameter);
            return command.ExecuteScalar();
        }

        public object? Plain(string text) => this.connection.CreateCommand(text).ExecuteScalar();

        public void Dispose() => this.connection.Dispose();
    }

    [TestMethod]
    [DataRow("begin tran; select 1", 0, 1)]
    [DataRow("begin tran; begin tran; select 1", 0, 2)]
    [DataRow("begin tran; select 1/0; select 2", 0, 1)]
    public void AnOpenedTransaction_Is266_AfterTheResults(string body, int previous, int current)
    {
        using var holder = new DbConnectionHolder(new Simulation());
        var error = Run(holder, body);
        AreEqual(266, error.Errors[^1].Number);
        AreEqual($"Transaction count after EXECUTE indicates a mismatching number of BEGIN and COMMIT statements. Previous count = {previous}, current count = {current}.", error.Errors[^1].Message);
    }

    [TestMethod]
    public void ACommitOrRollbackOfTheCallersTransaction_IsJudgedToo()
    {
        using var holder = new DbConnectionHolder(new Simulation());
        _ = holder.Plain("begin tran");
        AreEqual(1, holder.Execute("select @@trancount"));
        AreEqual(266, Run(holder, "rollback").Number);
        AreEqual(0, holder.Plain("select @@trancount"));
    }

    [TestMethod]
    public void BalancedRolledBackAndImplicit_RaiseNothing()
    {
        using var holder = new DbConnectionHolder(new Simulation());
        AreEqual(0, holder.Execute("begin tran; commit; select @@trancount"));
        AreEqual(0, holder.Execute("begin tran; begin tran; rollback; select @@trancount"));
        AreEqual(1, holder.Execute("set implicit_transactions on; create table #t (a int); insert #t values (1); select @@trancount"));
    }

    [TestMethod]
    public void APlainBatch_IsNotJudged()
    {
        using var holder = new DbConnectionHolder(new Simulation());
        AreEqual(1, holder.Plain("begin tran; select @@trancount"));
        AreEqual(1, holder.Plain("select @@trancount"));
    }

    [TestMethod]
    public void ATriggersRollback_EndsTheBatchWith3609_AndNo266()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int)");
        simulation.ExecuteBatches("create trigger tr on t after insert as begin raiserror('nope', 16, 1); rollback; end");
        using var holder = new DbConnectionHolder(simulation);
        _ = holder.Plain("begin tran");
        var error = Run(holder, "insert t values (1)");
        AreEqual(50000, error.Errors[0].Number);
        AreEqual(3609, error.Errors[1].Number);
        AreEqual(2, error.Errors.Count);
    }
}
