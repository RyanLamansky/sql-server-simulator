using System.Diagnostics;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A TDS attention can land after its request was read but before the request's
/// execution opens its cancellation scope; SqlClient sends only one attention
/// per cancel, so the scope must start cancelled rather than let the batch run
/// to completion.
/// </summary>
[TestClass]
public sealed class RequestAttentionTests
{
    private static SimulatedDbConnection OpenWithTable()
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        _ = Run(connection, "create table t (id int)");
        return connection;
    }

    private static object? Run(SimulatedDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [TestMethod]
    public void AttentionBeforeTheScopeOpens_CancelsTheRequest()
    {
        using var connection = OpenWithTable();
        connection.CancelRequest(connection.BeginRequest());

        var elapsed = Stopwatch.StartNew();
        _ = ThrowsExactly<SimulatedSqlException>(() => Run(connection, "waitfor delay '00:00:30'; insert t values (2)"));
        IsLessThan(10, elapsed.Elapsed.TotalSeconds);

        _ = connection.BeginRequest();
        AreEqual(0, Run(connection, "select count(*) from t"));
    }

    [TestMethod]
    public void AttentionForAFinishedRequest_LeavesTheNextAlone()
    {
        using var connection = OpenWithTable();
        var finished = connection.BeginRequest();
        _ = connection.BeginRequest();
        connection.CancelRequest(finished);

        _ = Run(connection, "insert t values (1)");
        AreEqual(1, Run(connection, "select count(*) from t"));
    }
}
