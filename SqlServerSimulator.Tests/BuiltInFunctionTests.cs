using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public sealed class BuiltInFunctionTests
{
    private static T ExecuteScalar<T>(string commandText) where T : struct => new Simulation().ExecuteScalar<T>(commandText);

    [TestMethod]
    public void UnrecognizedBuiltInFunction()
    {
        var exception = Throws<DbException>(() => ExecuteScalar<int>("select frog()"));
        AreEqual("'frog' is not a recognized built-in function name.", exception.Message);
    }

    [TestMethod]
    public void DataLength() => AreEqual(4, ExecuteScalar<int>("select datalength(1)"));

    [TestMethod]
    public void DataLengthAllCaps() => AreEqual(4, ExecuteScalar<int>("select DATALENGTH(1)"));
}
