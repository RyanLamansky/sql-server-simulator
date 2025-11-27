namespace SqlServerSimulator;

internal static class TestHelpers
{
    public static object? ExecuteScalar(string commandText) => new Simulation().ExecuteScalar(commandText);

    public static T ExecuteScalar<T>(string commandText) where T : struct => new Simulation().ExecuteScalar<T>(commandText);
}
