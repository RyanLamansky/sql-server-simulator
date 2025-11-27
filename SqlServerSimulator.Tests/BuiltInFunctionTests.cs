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
    [DataRow("abs")]
    [DataRow("datalength")]
    public void NullPassThrough(string function)
    {
        AreEqual(function.ToLowerInvariant(), function);
        _ = IsInstanceOfType<DBNull>(ExecuteScalar($"select {function}(null)"));
        _ = IsInstanceOfType<DBNull>(ExecuteScalar($"select {function.ToUpperInvariant()}(null)"));
    }

    [TestMethod]
    [DataRow("datalength", "1", 4)]
    [DataRow("abs", "1", 1)]
    [DataRow("abs", "0", 0)]
    [DataRow("abs", "-1", 1)]
    public void BuiltInFunction(string function, string input, object output)
    {
        AreEqual(function.ToLowerInvariant(), function);
        AreEqual(output, ExecuteScalar($"select {function}({input})"));
        AreEqual(output, ExecuteScalar($"select {function.ToUpperInvariant()}({input})"));
    }
}
