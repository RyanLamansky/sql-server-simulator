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

    public static DbCommand CreateCommand(this DbConnection connection, string? commandText, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;

        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            command.Parameters.Add(parameter);
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
        => simulation.CreateCommand(commandText).ExecuteNonQuery();

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
