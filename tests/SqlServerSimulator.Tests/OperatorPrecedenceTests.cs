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
    // Three-plus additive terms whose FIRST term is a multiplication: one rotation
    // lifts the top + but leaves the rotated-down Multiply mis-grouped, so these
    // need AdjustForPrecedence to re-adjust recursively. Before that fix
    // `100 * 2 + 10 * 3 + 4` parsed as `(100 * (2 + 10 * 3)) + 4`.
    [DataRow("100 * 2 + 10 * 3 + 4", 234)]
    [DataRow("100 * 2 + 3 + 4", 207)]
    [DataRow("2 * 1 + 3 * 2 + 4 * 1", 12)]
    [DataRow("1 + 2 * 3 + 4", 11)]                    // leading literal already worked; guards against regressing it.
    [DataRow("2 * 3 + 4 * 5 + 6", 32)]
    [DataRow("100 * 2 + 10 * 3 + 4 * 1 + 5", 239)]    // four terms.
    public void OperatorChainProducesExpected(string expr, int expected) =>
        AreEqual(expected, ExecuteScalar($"select {expr}"));

    /// <summary>
    /// The unary signs sit at SQL Server's <b>additive</b> precedence level —
    /// below <c>* / %</c> — so a sign reaches past its immediate operand and
    /// takes the whole following multiplicative chain: <c>a / -b / c</c> is
    /// <c>a / (-(b / c))</c>, not <c>(a / -b) / c</c>. Probe-confirmed against
    /// SQL Server 2025 (2026-08-03). A single leading sign agrees under either
    /// grouping — negation commutes with <c>*</c> and integer division
    /// truncates symmetrically — so the divergence needs a second
    /// multiplicative operator after the sign.
    /// </summary>
    [TestMethod]
    [DataRow("100 / -10 / 2", -20)]        // 100 / (-(10 / 2)); tight binding gives -5.
    [DataRow("8 / - 2 * 4", -1)]           // 8 / (-(2 * 4)); tight binding gives -16.
    [DataRow("60 / -3 / 2", -60)]          // 60 / (-(3 / 2)) = 60 / -1; tight binding gives -10.
    [DataRow("100 / - 20 % 7", -16)]       // `%` joins the chain: 100 / (-(20 % 7)).
    [DataRow("8 / + 2 * 4", 1)]            // unary plus reaches identically.
    [DataRow("12 / - 2 * 3", -2)]
    [DataRow("2 * - 3 * 4", -24)]
    [DataRow("10 - - 6 * 2", 22)]          // sign as an additive operator's right operand.
    [DataRow("10 + - 6 * 2", -2)]
    [DataRow("- - 6 * 2", 12)]             // stacked signs: -(-(6 * 2)).
    [DataRow("- + - 6 * 2", 12)]
    [DataRow("- 6 / 3", -2)]               // single leading sign: agrees either way.
    [DataRow("2 * -3 + 1", -5)]
    public void UnarySign_TakesTheWholeMultiplicativeChain(string expr, int expected) =>
        AreEqual(expected, ExecuteScalar($"select {expr}"));

    /// <summary>
    /// A sign's reach ends at the additive level it occupies, so the additive,
    /// bitwise and shift operators all terminate its operand:
    /// <c>- 6 &amp; 3</c> is <c>(-6) &amp; 3</c> = 2, never <c>-(6 &amp; 3)</c>
    /// = -2.
    /// </summary>
    [TestMethod]
    [DataRow("- 6 + 2", -4)]
    [DataRow("- 6 & 3", 2)]
    [DataRow("- 6 | 3", -5)]
    [DataRow("- 6 ^ 3", -7)]
    [DataRow("8 & - 6 & 3", 0)]
    [DataRow("- 2 << 3", -16)]
    [DataRow("16 >> - 2 * 1", 64)]         // the shift's right operand is -(2 * 1).
    public void UnarySign_StopsAtTheAdditiveLevel(string expr, int expected) =>
        AreEqual(expected, ExecuteScalar($"select {expr}"));

    /// <summary>
    /// <c>~</c> is the one unary operator that binds <b>tighter</b> than
    /// <c>*</c>, so it takes a lone operand — but that operand may itself be a
    /// sign, which then reaches for the chain: <c>~ - 2 * 3</c> is
    /// <c>~(-(2 * 3))</c> = 5.
    /// </summary>
    [TestMethod]
    [DataRow("~ 2 * 3", -9)]               // (~2) * 3, not ~(2 * 3) = -7.
    [DataRow("12 / ~ 2 * 3", -12)]
    [DataRow("~ 2 + 3", 0)]
    [DataRow("~ - 2 * 3", 5)]              // ~(-(2 * 3)); a lone-operand `-` would give 3.
    [DataRow("- ~ 2 * 3", 9)]              // -((~2) * 3).
    public void BitwiseNot_BindsTighterThanMultiplicative(string expr, int expected) =>
        AreEqual(expected, ExecuteScalar($"select {expr}"));

    /// <summary>
    /// The reach starts from whatever operand the sign leads, so parenthesizing
    /// the <i>operand</i> doesn't stop it (<c>-(10) / 2</c> is still
    /// <c>-((10) / 2)</c>) while parenthesizing the <i>sign expression</i>
    /// does.
    /// </summary>
    [TestMethod]
    [DataRow("100 / -(10) / 2", -20)]
    [DataRow("100 / (-10) / 2", -5)]
    [DataRow("12 / -abs(2) * 3", -2)]
    [DataRow("12 / - case when 1 = 1 then 2 end * 3", -2)]
    [DataRow("12 / - (select 2) * 3", -2)]
    public void UnarySign_ReachesFromEveryOperandKind(string expr, int expected) =>
        AreEqual(expected, ExecuteScalar($"select {expr}"));

    /// <summary>
    /// The regrouping is observable through error behavior, which is what makes
    /// it more than a curiosity: the looser binding pulls a following NULL into
    /// the same multiplicative group as the zero divisor, so the division is by
    /// NULL and never by zero. Swapping the NULL for a value restores Msg 8134,
    /// which is the proof that the grouping — not the error handling — is what
    /// decides it.
    /// </summary>
    [TestMethod]
    public void UnarySignRegrouping_DividesByNullRatherThanZero()
    {
        _ = IsInstanceOfType<DBNull>(ExecuteScalar("select 5 / -0 * cast(null as int)"));
        _ = IsInstanceOfType<DBNull>(ExecuteScalar("select -91 / - 0 * - cast(null as integer)"));
        AssertSqlError("select -91 / - 0 * - 45", 8134, "Divide by zero error encountered.");
    }

    /// <summary>
    /// The regrouping moves which operands pair up, so it moves the declared
    /// decimal result type too: real reports <c>decimal(38, 17)</c> for the
    /// reaching form and <c>decimal(38, 21)</c> once parentheses stop the reach
    /// (probe-confirmed 2026-08-03 via
    /// <c>sys.dm_exec_describe_first_result_set</c>).
    /// </summary>
    [TestMethod]
    [DataRow("cast(1 as decimal(9,2)) / -cast(3 as decimal(9,4)) / cast(7 as decimal(9,6))", 17)]
    [DataRow("cast(1 as decimal(9,2)) / (-cast(3 as decimal(9,4))) / cast(7 as decimal(9,6))", 21)]
    public void UnarySignRegrouping_MovesTheDeclaredDecimalScale(string expr, int scale)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"select {expr} as v into t");
        AreEqual(38, sim.ExecuteScalar<int>("select convert(int, numeric_precision) from information_schema.columns where table_name = 't'"));
        AreEqual(scale, sim.ExecuteScalar<int>("select convert(int, numeric_scale) from information_schema.columns where table_name = 't'"));
    }

    /// <summary>
    /// A written negative literal still reads as a constant wherever the
    /// constant-detection rules key off one: an ORDER BY position number
    /// reports Msg 108 against the select-list count, and a constant
    /// <i>expression</i> there is Msg 408 whichever way the sign groups.
    /// </summary>
    [TestMethod]
    [DataRow("-1", 108)]
    [DataRow("-1 * 2", 408)]
    public void NegativeConstant_InOrderBy_KeepsItsConstantDiagnostic(string orderBy, int number) =>
        _ = AssertSqlError($"select a from (values (1), (2)) v(a) order by {orderBy}", number);
}
