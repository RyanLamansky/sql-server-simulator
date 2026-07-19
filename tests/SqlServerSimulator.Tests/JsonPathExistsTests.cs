using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>JSON_PATH_EXISTS(json, path)</c>: returns <c>1</c> when
/// the path resolves to an existing value, <c>0</c> otherwise. NULL on
/// either input returns NULL. Routes through the same
/// <see cref="Parser.JsonPath"/> infrastructure as JSON_VALUE / JSON_QUERY.
/// </summary>
[TestClass]
public sealed class JsonPathExistsTests
{
    [TestMethod]
    public void PathExists_Returns1()
        => IsTrue((bool)new Simulation().ExecuteScalar("select json_path_exists('{\"a\":1}', '$.a')")!);

    [TestMethod]
    public void PathMissing_Returns0()
        => IsFalse((bool)new Simulation().ExecuteScalar("select json_path_exists('{\"a\":1}', '$.b')")!);

    [TestMethod]
    public void NestedPathExists_Returns1()
        => IsTrue((bool)new Simulation().ExecuteScalar("select json_path_exists('{\"a\":{\"b\":2}}', '$.a.b')")!);

    [TestMethod]
    public void ArrayIndexExists_Returns1()
        => IsTrue((bool)new Simulation().ExecuteScalar("select json_path_exists('[10, 20, 30]', '$[1]')")!);

    [TestMethod]
    public void ArrayIndexOutOfRange_Returns0()
        => IsFalse((bool)new Simulation().ExecuteScalar("select json_path_exists('[10, 20, 30]', '$[5]')")!);

    [TestMethod]
    public void NullJson_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select json_path_exists(cast(null as nvarchar(max)), '$.a')")!);

    [TestMethod]
    public void NullPath_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select json_path_exists('{}', cast(null as nvarchar(100)))")!);

    [TestMethod]
    public void InvalidJsonLax_Returns0()
        => IsFalse((bool)new Simulation().ExecuteScalar("select json_path_exists('not json', '$.a')")!);

    [TestMethod]
    public void RootPath_Returns1()
        => IsTrue((bool)new Simulation().ExecuteScalar("select json_path_exists('{}', '$')")!);
}
