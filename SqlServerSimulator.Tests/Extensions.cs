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

    public static DbCommand CreateCommand(this DbConnection connection, string? commandText, Action<Func<DbParameter>> addParameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        addParameters(() =>
        {
            var parameter = command.CreateParameter();
            command.Parameters.Add(parameter);
            return parameter;
        });

        return command;
    }

    public static DbConnection CreateOpenConnection(this Simulation simulation)
    {
        var connection = simulation.CreateDbConnection();
        connection.Open();
        return connection;
    }

    public static object? ExecuteScalar(this Simulation simulation, string commandText)
        => simulation.CreateCommand(commandText).ExecuteScalar();

    public static DbDataReader ExecuteReader(this Simulation simulation, string commandText)
        => simulation.CreateCommand(commandText).ExecuteReader();

    public static IEnumerable<DbDataReader> EnumerateRecords(this DbDataReader reader)
    {
        while (reader.Read())
            yield return reader;
    }
}
