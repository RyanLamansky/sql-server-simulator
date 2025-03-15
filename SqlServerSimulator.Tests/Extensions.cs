using Microsoft.VisualStudio.TestTools.UnitTesting;
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
        => simulation.CreateCommand(commandText).ExecuteNonQuery();

    public static object? ExecuteScalar(this Simulation simulation, string commandText)
        => simulation.CreateCommand(commandText).ExecuteScalar();

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
        Assert.AreEqual(6, data.Count);
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
}
