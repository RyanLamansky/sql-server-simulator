using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Parser-tree-balancing tests for binary operator precedence. Each row
/// places a specific operator on the right of another two-sided
/// expression — that's the path that reads the right-side child's
/// <c>Precedence</c> property in <c>TwoSidedExpression.AdjustForPrecedence</c>.
/// Without these chains, the per-subclass <c>Precedence</c> overrides on
/// Bitwise* / Divide / Modulus / Subtract sit at 0% because the simpler
/// <c>a OP b</c> shape doesn't require precedence comparison.
/// </summary>
[TestClass]
public sealed class OperatorPrecedenceTests
{
    [TestMethod]
    [DataRow("1 + 2 - 3", 0)]   // Subtract on the right of Add (same precedence, left-assoc swap).
    [DataRow("8 - 4 + 2", 6)]   // Add on the right of Subtract.
    [DataRow("1 + 2 & 3", 3)]   // BitwiseAnd on the right of Add.
    [DataRow("1 + 2 | 4", 7)]   // BitwiseOr on the right of Add.
    [DataRow("1 + 2 ^ 5", 6)]   // BitwiseExclusiveOr on the right of Add: (1+2)^5 = 3^5 = 6.
    [DataRow("10 + 8 / 4", 12)] // Divide on the right of Add (Divide is higher precedence; doesn't swap).
    [DataRow("1 + 5 % 3", 3)]   // Modulus on the right of Add (same precedence): (1+5)%3 = 6%3 = 0... wait, swap means Modulus parent: 1+(5%3) = 1+2 = 3.
    [DataRow("2 * 3 + 4", 10)]  // Add on the right of Multiply (Add lower precedence; swap).
    public void OperatorChainProducesExpected(string expr, int expected) =>
        AreEqual(expected, ExecuteScalar($"select {expr}"));
}
