namespace SqlServerSimulator;

internal static class TestHelpers
{
    public static object? ExecuteScalar(string commandText) => new Simulation().ExecuteScalar(commandText);

    public static T ExecuteScalar<T>(string commandText) where T : struct => new Simulation().ExecuteScalar<T>(commandText);

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
    /// Verifies that <paramref name="commandText"/> raises a <see cref="SimulatedSqlException"/> with
    /// the given <paramref name="expectedMessage"/>. For tests that don't pin an error number.
    /// </summary>
    public static void AssertSqlMessage(string commandText, string expectedMessage)
    {
        var ex = Assert.Throws<SimulatedSqlException>(() => ExecuteScalar(commandText));
        Assert.AreEqual(expectedMessage, ex.Message);
    }
}
