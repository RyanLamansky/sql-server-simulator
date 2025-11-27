using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

[TestClass]
public sealed class BuiltInFunctionTests
{

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
