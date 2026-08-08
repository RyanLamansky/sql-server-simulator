using System.Data.Common;

namespace SqlServerSimulator;

static class Extensions
{
    public static DbCommand CreateCommand(this Simulation simulation, string? commandText)
        => simulation.CreateOpenConnection().CreateCommand(commandText);

    public static DbCommand CreateCommand(this DbConnection connection, string? commandText)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;

        return command;
    }

    public static DbCommand CreateCommand(this DbConnection connection, string? commandText, params ReadOnlySpan<(string Name, object Value)> parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;

        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            _ = command.Parameters.Add(parameter);
            parameter.ParameterName = name;
            parameter.Value = value;
        }

        return command;
    }

    public static DbConnection CreateOpenConnection(this Simulation simulation)
    {
        var connection = simulation.CreateDbConnection();
        connection.Open();
        return connection;
    }

    public static int ExecuteNonQuery(this Simulation simulation, string commandText)
    {
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand(commandText);
        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// Runs each <paramref name="batches"/> entry as its own ADO.NET command
    /// on a shared open connection. The split exists because CREATE/ALTER
    /// PROCEDURE / FUNCTION / VIEW / TRIGGER / SCHEMA must be the first
    /// statement in a query batch (Msg 111) — and a VIEW or FUNCTION must be
    /// its batch's <em>only</em> statement, since its body runs to the end of
    /// the batch (Msg 156 / 102 at whatever follows). Passing several such
    /// statements, or a trailing query, through a single CommandText fails
    /// fast, so tests use this helper to give each one its own batch.
    /// </summary>
    public static void ExecuteBatches(this Simulation simulation, params ReadOnlySpan<string> batches)
    {
        using var connection = simulation.CreateOpenConnection();
        foreach (var commandText in batches)
        {
            using var command = connection.CreateCommand(commandText);
            _ = command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// <see cref="ExecuteBatches"/> whose <em>last</em> entry is the measured
    /// one: every earlier entry runs for its effect, and the last one's first
    /// column of its first row comes back. Keeps the one-expression test shape
    /// available when the setup contains a statement that has to own its batch.
    /// </summary>
    public static object? ExecuteBatchesScalar(this Simulation simulation, params ReadOnlySpan<string> batches)
    {
        using var connection = simulation.CreateOpenConnection();
        for (var i = 0; i < batches.Length - 1; i++)
        {
            using var setup = connection.CreateCommand(batches[i]);
            _ = setup.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand(batches[^1]);
        return command.ExecuteScalar();
    }

    /// <summary>
    /// Reader-returning <see cref="ExecuteBatchesScalar"/>: the earlier entries
    /// run for their effect, the last one's rows come back. The connection
    /// outlives the call so the reader stays usable, matching
    /// <see cref="ExecuteReader"/>.
    /// </summary>
    public static DbDataReader ExecuteBatchesReader(this Simulation simulation, params ReadOnlySpan<string> batches)
    {
        var connection = simulation.CreateOpenConnection();
        for (var i = 0; i < batches.Length - 1; i++)
        {
            using var setup = connection.CreateCommand(batches[i]);
            _ = setup.ExecuteNonQuery();
        }

        return connection.CreateCommand(batches[^1]).ExecuteReader();
    }

    public static object? ExecuteScalar(this Simulation simulation, string commandText)
    {
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand(commandText);
        return command.ExecuteScalar();
    }

    public static T ExecuteScalar<T>(this Simulation simulation, string commandText)
        where T : struct
    {
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand(commandText);
        var result = command.ExecuteScalar();
        Assert.IsNotNull(result);
        return Assert.IsInstanceOfType<T>(result);
    }

    public static void ValidateSyntaxError(this Simulation simulation, string commandText, string nearSyntax)
    {
        var exception = Assert.Throws<DbException>(() => simulation.ExecuteScalar(commandText));

        Assert.AreEqual($"Incorrect syntax near '{nearSyntax}'.", exception.Message);

        // The following checks verify that the DbException matches what Microsoft.Data.SqlClient produces.
        Assert.AreEqual(unchecked((int)0x80131904), exception.HResult);
        Assert.AreEqual(unchecked((int)0x80131904), exception.ErrorCode);
        Assert.AreEqual("Core Microsoft SqlClient Data Provider", exception.Source);
        Assert.IsFalse(exception.IsTransient);

        var data = exception.Data;
        Assert.HasCount(6, data);
        Assert.AreEqual("Microsoft SQL Server", data["HelpLink.ProdName"]);
        Assert.AreEqual("99.00.1000", data["HelpLink.ProdVer"]); // This should probably be a simulation property.
        Assert.AreEqual("MSSQLServer", data["HelpLink.EvtSrc"]);
        Assert.AreEqual("102", data["HelpLink.EvtID"]);
        Assert.AreEqual("https://go.microsoft.com/fwlink", data["HelpLink.BaseHelpUrl"]);
        Assert.AreEqual("20476", data["HelpLink.LinkId"]);
    }

    public static DbDataReader ExecuteReader(this Simulation simulation, string commandText)
        => simulation.CreateCommand(commandText).ExecuteReader();

    public static IEnumerable<DbDataReader> EnumerateRecords(this DbDataReader reader)
    {
        while (reader.Read())
            yield return reader;
    }

    /// <summary>
    /// Verifies that <paramref name="commandText"/> against this simulation raises a
    /// <see cref="SimulatedSqlException"/> whose
    /// <see cref="SimulatedSqlException.Number"/> matches <paramref name="errorNumber"/>.
    /// Returns the exception for further assertions.
    /// </summary>
    public static SimulatedSqlException AssertSqlError(this Simulation simulation, string commandText, int errorNumber)
    {
        var ex = Assert.Throws<SimulatedSqlException>(() => simulation.ExecuteScalar(commandText));
        Assert.AreEqual(errorNumber, ex.Number);
        return ex;
    }

    /// <summary>
    /// Exact-message variant of <see cref="AssertSqlError(Simulation, string, int)"/>.
    /// </summary>
    public static void AssertSqlError(this Simulation simulation, string commandText, int errorNumber, string expectedMessage)
    {
        var ex = simulation.AssertSqlError(commandText, errorNumber);
        Assert.AreEqual(expectedMessage, ex.Message);
    }

    /// <summary>
    /// Ceiling for "the state a concurrency test set up became observable".
    /// Only ever waited out on the failure path, where the assertion that
    /// follows reports the last sample.
    /// </summary>
    private const int PollTimeoutMs = 10_000;

    /// <summary>
    /// Samples <paramref name="probe"/> until <paramref name="settled"/>
    /// accepts the value or the deadline passes, then hands the last sample
    /// back for the caller to assert on. This is what a test observing
    /// another connection's blocked state needs instead of a fixed sleep:
    /// the background thread holding that statement reaches the lock whenever
    /// the machine schedules it, so any single wall-clock guess is a bet a
    /// loaded CI runner eventually loses. Returning the sample rather than
    /// asserting keeps the caller's own assertion — and its failure message —
    /// intact.
    /// </summary>
    public static async Task<T> PollUntil<T>(Func<T> probe, Func<T, bool> settled, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + PollTimeoutMs;
        while (true)
        {
            var sample = probe();
            if (settled(sample) || Environment.TickCount64 >= deadline)
                return sample;
            await Task.Delay(10, cancellationToken);
        }
    }
}
