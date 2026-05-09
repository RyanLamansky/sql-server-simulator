using System.Data.Common;

namespace SqlServerSimulator;

internal static class TestHelpers
{
    public static object? ExecuteScalar(string commandText) => new Simulation().ExecuteScalar(commandText);

    public static T ExecuteScalar<T>(string commandText) where T : struct => new Simulation().ExecuteScalar<T>(commandText);

    /// <summary>
    /// Verifies that <paramref name="commandText"/> raises a <see cref="DbException"/> whose
    /// SQL Server error number (carried in <c>Data["HelpLink.EvtID"]</c>) matches
    /// <paramref name="errorNumber"/>. Returns the exception so callers can do additional
    /// message assertions (e.g. <c>Assert.StartsWith</c>).
    /// </summary>
    public static DbException AssertSqlError(string commandText, int errorNumber)
    {
        var ex = Assert.Throws<DbException>(() => ExecuteScalar(commandText));
        Assert.AreEqual(errorNumber.ToString(), ex.Data["HelpLink.EvtID"]);
        return ex;
    }

    /// <summary>
    /// Verifies that <paramref name="commandText"/> raises a <see cref="DbException"/> with
    /// exact-match <paramref name="expectedMessage"/> and the given error number.
    /// </summary>
    public static void AssertSqlError(string commandText, int errorNumber, string expectedMessage)
    {
        var ex = AssertSqlError(commandText, errorNumber);
        Assert.AreEqual(expectedMessage, ex.Message);
    }

    /// <summary>
    /// Verifies that <paramref name="commandText"/> raises a <see cref="DbException"/> with
    /// the given <paramref name="expectedMessage"/>. For tests that don't pin an error number.
    /// </summary>
    public static void AssertSqlMessage(string commandText, string expectedMessage)
    {
        var ex = Assert.Throws<DbException>(() => ExecuteScalar(commandText));
        Assert.AreEqual(expectedMessage, ex.Message);
    }
}
