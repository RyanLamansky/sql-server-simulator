using System.Data.Common;

namespace SqlServerSimulator;

internal static class TestHelpers
{
    public static object? ExecuteScalar(string commandText) => new Simulation().ExecuteScalar(commandText);

    public static T ExecuteScalar<T>(string commandText) where T : struct => new Simulation().ExecuteScalar<T>(commandText);

    /// <summary>
    /// A simulation that has already run <paramref name="createTypes"/>. Real
    /// compiles a batch before running any of it, so a type is usable only from
    /// the batch after the one that creates it.
    /// </summary>
    public static Simulation WithType(string createTypes)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(createTypes);
        return simulation;
    }

    /// <summary>
    /// Verifies that <paramref name="commandText"/> raises a <see cref="SimulatedSqlException"/> whose
    /// SQL Server error number matches <paramref name="errorNumber"/>. Returns the exception so callers can do additional
    /// message assertions (e.g. <c>Assert.StartsWith</c>).
    /// </summary>
    public static SimulatedSqlException AssertSqlError(string commandText, int errorNumber)
    {
        var ex = Assert.Throws<SimulatedSqlException>(() => ExecuteScalar(commandText));
        Assert.AreEqual(errorNumber, ex.Number);
        return ex;
    }

    /// <summary>
    /// Verifies that <paramref name="commandText"/> raises a <see cref="SimulatedSqlException"/> with
    /// the given error number, whose own text is exactly <paramref name="expectedMessage"/>
    /// (the exception's <c>Message</c> joins the entries that follow it too).
    /// </summary>
    public static void AssertSqlError(string commandText, int errorNumber, string expectedMessage)
    {
        var ex = AssertSqlError(commandText, errorNumber);
        Assert.AreEqual(expectedMessage, ex.Errors[0].Message);
    }

    /// <summary>
    /// <see cref="AssertSqlError(string, int, string)"/> that also pins the error's
    /// <paramref name="state"/>, for the errors real raises in several states.
    /// </summary>
    public static void AssertSqlError(string commandText, int errorNumber, byte state, string expectedMessage)
    {
        var error = AssertSqlError(commandText, errorNumber).Errors[0];
        Assert.AreEqual(expectedMessage, error.Message);
        Assert.AreEqual(state, error.State);
    }

    /// <summary>
    /// Verifies that <paramref name="commandText"/> raises a <see cref="SimulatedSqlException"/> with
    /// the given <paramref name="expectedMessage"/>. For tests that don't pin an error number.
    /// </summary>
    public static void AssertSqlMessage(string commandText, string expectedMessage)
    {
        var ex = Assert.Throws<SimulatedSqlException>(() => ExecuteScalar(commandText));
        Assert.AreEqual(expectedMessage, ex.Message);
    }

    /// <summary>How many rows <paramref name="table"/> holds, read on <paramref name="connection"/>, inside whatever transaction it has open.</summary>
    public static int CountRows(DbConnection connection, string table) =>
        (int)connection.CreateCommand($"select count(*) from {table}").ExecuteScalar()!;

    /// <summary>The number of every entry <paramref name="error"/> carries, in the order the batch raised them.</summary>
    public static int[] Numbers(SimulatedSqlException error) => [.. error.Errors.Select(static entry => entry.Number)];
}
