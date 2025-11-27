using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public sealed class BuiltInFunctionTests
{
    private static object? ExecuteScalar(string commandText) => new Simulation().ExecuteScalar(commandText);

    private static T ExecuteScalar<T>(string commandText) where T : struct => new Simulation().ExecuteScalar<T>(commandText);

    [TestMethod]
    public void UnrecognizedBuiltInFunction()
    {
        var exception = Throws<DbException>(() => ExecuteScalar<int>("select frog()"));
        AreEqual("'frog' is not a recognized built-in function name.", exception.Message);
    }

    [TestMethod]
    public void DataLengthOfNull() => IsInstanceOfType<DBNull>(ExecuteScalar("select datalength(null)"));

    [TestMethod]
    [DataRow("1", 4)]
    public void DataLength(string input, object? output) => AreEqual(output, ExecuteScalar($"select datalength({input})"));

    [TestMethod]
    public void DataLengthAllCaps() => AreEqual(4, ExecuteScalar<int>("select DATALENGTH(1)"));
}
