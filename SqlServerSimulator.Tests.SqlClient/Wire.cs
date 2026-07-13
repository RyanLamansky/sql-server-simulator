using System.Data.Common;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Shared plumbing for the loopback wire tests: connection-string builders,
/// an in-process ADO oracle over the same <see cref="Simulation"/>, and value
/// comparison that understands the boxed shapes SqlClient hands back.
/// </summary>
internal static class Wire
{
    public static string ConnectionString(SimulatedNetworkListener listener, string extra = "") =>
        $"Server=127.0.0.1,{listener.Port};User ID=sa;Password=anything;TrustServerCertificate=True;Pooling=False;Connect Timeout=15{extra}";

    /// <summary>Pooling enabled with a single physical connection, so a close/reopen reuses it and exercises the reset-connection bit.</summary>
    public static string PooledConnectionString(SimulatedNetworkListener listener) =>
        $"Server=127.0.0.1,{listener.Port};User ID=sa;Password=anything;TrustServerCertificate=True;Max Pool Size=1;Connect Timeout=15";

    public static async Task<SqlConnection> OpenAsync(SimulatedNetworkListener listener, CancellationToken cancellationToken, string extra = "")
    {
        var connection = new SqlConnection(ConnectionString(listener, extra));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>Runs a batch through the in-process ADO surface of the same simulation (no wire involved).</summary>
    public static void ExecInProc(Simulation simulation, string sql)
    {
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Inserts through the in-process surface with one bound parameter. The wire
    /// listener does not yet accept RPC / parameterized commands, so large or
    /// binary payloads seed via ADO here, then read back over the wire.
    /// </summary>
    public static void ExecInProcParam(Simulation simulation, string sql, string parameterName, object value)
    {
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = parameterName;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>Materializes a query's rows through the in-process surface; the dual-read oracle for values whose exact bytes are nontrivial.</summary>
    public static List<object?[]> ReadAllInProc(Simulation simulation, string sql)
    {
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        return Drain(reader);
    }

    public static List<object?[]> Drain(DbDataReader reader)
    {
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Equality that unwraps byte arrays (reference-equal by default) so the oracle compares payloads.</summary>
    public static void AssertValueEqual(object expected, object actual)
    {
        if (expected is byte[] expectedBytes && actual is byte[] actualBytes)
            CollectionAssert.AreEqual(expectedBytes, actualBytes);
        else
            AreEqual(expected, actual);
    }
}
