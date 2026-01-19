using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public class WhereTests
{
    [TestMethod]
    [DataRow("1 = 0", 0)]
    [DataRow("1 = 1", 1)]
    [DataRow("1 > 0", 1)]
    [DataRow("1 > 1", 0)]
    [DataRow("1 >= 0", 1)]
    [DataRow("1 >= 1", 1)]
    [DataRow("1 >= 2", 0)]
    [DataRow("1 > = 0", 1)]
    [DataRow("1 > = 1", 1)]
    [DataRow("1 > = 2", 0)]
    [DataRow("0 < 1", 1)]
    [DataRow("1 < 1", 0)]
    [DataRow("1 <= 0", 0)]
    [DataRow("1 <= 1", 1)]
    [DataRow("1 <= 2", 1)]
    [DataRow("1 < = 0", 0)]
    [DataRow("1 < = 1", 1)]
    [DataRow("1 < = 2", 1)]
    [DataRow("1 <> 0", 1)]
    [DataRow("1 <> 1", 0)]
    [DataRow("1 < > 0", 1)]
    [DataRow("1 < > 1", 0)]
    [DataRow("1 != 0", 1)]
    [DataRow("1 != 1", 0)]
    [DataRow("1 ! = 0", 1)]
    [DataRow("1 ! = 1", 0)]
    [DataRow("1 !> 0", 0)]
    [DataRow("1 !> 1", 1)]
    [DataRow("1 !> 2", 1)]
    [DataRow("1 ! > 0", 0)]
    [DataRow("1 ! > 1", 1)]
    [DataRow("1 ! > 2", 1)]
    [DataRow("1 !< 0", 1)]
    [DataRow("1 !< 1", 1)]
    [DataRow("1 !< 2", 0)]
    [DataRow("1 ! < 0", 1)]
    [DataRow("1 ! < 1", 1)]
    [DataRow("1 ! < 2", 0)]
    public void PureExpressionFilter(string whereExpression, int expectedCount)
    {
        AreEqual(expectedCount, new Simulation().ExecuteReader($"select 1 where {whereExpression}").EnumerateRecords().Count());
    }
}
