using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The <c>real</c> / <c>float</c> arithmetic and aggregate result types, and
/// the static-vs-runtime parity the type system depends on. <c>real</c> wins
/// over every arithmetic partner except <c>float</c> — so <c>real + int</c> is
/// <c>real</c>, not <c>float</c> — while <c>SUM</c> / <c>AVG</c> widen it to
/// <c>float</c> and <c>MIN</c> / <c>MAX</c> keep it. Modulo has no float form
/// at all. Every expectation here was probed against SQL Server 2025 through
/// <c>sys.dm_exec_describe_first_result_set</c>, and the values through
/// <c>CAST(… AS binary(4))</c> bit comparisons.
/// </summary>
/// <remarks>
/// <see cref="Arithmetic_EveryNumericPair_RuntimeValueMatchesDeclaredType"/> is
/// the regression net rather than any one expectation: the row encoder rejects
/// a value whose type isn't the one the projection schema declared, so reading
/// each pair through a table is a direct assertion that
/// <c>Expression.Run</c> and <c>Expression.GetSqlType</c> agree. Before the
/// fix, <c>real + int</c> declared <c>real</c> and produced a <c>double</c>,
/// which surfaced to the consumer as a raw <c>ArgumentException</c>.
/// </remarks>
[TestClass]
public sealed class RealTypePromotionTests
{
    /// <summary>
    /// One column per numeric type, one row, so an expression over any pair
    /// runs through the row encoder rather than the FROM-less synthesized-row
    /// path (which bridges a static / runtime type mismatch with a coercion
    /// and would hide exactly the drift under test).
    /// </summary>
    private static DbConnection OneRowOfEveryNumericType()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand(
            "create table t (r real, f float, i int, bi bigint, si smallint, ti tinyint, "
            + "d decimal(10, 2), mo money, sm smallmoney, bt bit)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1, 1, 1, 1, 1, 1, 1, 1, 1, 1)").ExecuteNonQuery();
        return connection;
    }

    private static readonly string[] NumericColumns =
        ["r", "f", "i", "bi", "si", "ti", "d", "mo", "sm", "bt"];

    private static string DeclaredType(DbConnection connection, string expression)
    {
        using var reader = connection.CreateCommand($"select {expression} as v from t").ExecuteReader();
        _ = reader.Read();
        return reader.GetDataTypeName(0);
    }

    [TestMethod]
    public void Arithmetic_EveryNumericPair_RuntimeValueMatchesDeclaredType()
    {
        using var connection = OneRowOfEveryNumericType();
        foreach (var left in NumericColumns)
        {
            foreach (var right in NumericColumns)
            {
                foreach (var op in "+-*/%")
                {
                    // A pair real refuses raises the modeled SQL error; anything
                    // else escaping (notably the encoder's ArgumentException on a
                    // static / runtime type mismatch) is the drift under test.
                    try
                    {
                        using var reader = connection.CreateCommand($"select {left} {op} {right} as v from t").ExecuteReader();
                        _ = reader.Read();
                        AreEqual(reader.GetFieldType(0), reader.GetValue(0).GetType(), $"{left} {op} {right}");
                    }
                    catch (DbException)
                    {
                    }
                }
            }
        }
    }

    [TestMethod]
    [DataRow("r", "r", "real")]
    [DataRow("r", "f", "float")]
    [DataRow("f", "r", "float")]
    [DataRow("f", "f", "float")]
    [DataRow("r", "i", "real")]
    [DataRow("i", "r", "real")]
    [DataRow("r", "bi", "real")]
    [DataRow("bi", "r", "real")]
    [DataRow("r", "si", "real")]
    [DataRow("r", "ti", "real")]
    [DataRow("r", "bt", "real")]
    [DataRow("r", "d", "real")]
    [DataRow("d", "r", "real")]
    [DataRow("r", "mo", "real")]
    [DataRow("mo", "r", "real")]
    [DataRow("r", "sm", "real")]
    [DataRow("f", "i", "float")]
    [DataRow("i", "f", "float")]
    [DataRow("f", "d", "float")]
    [DataRow("f", "mo", "float")]
    public void Arithmetic_ApproximatePair_TakesRealUnlessFloatIsPresent(string left, string right, string expected)
    {
        using var connection = OneRowOfEveryNumericType();
        foreach (var op in "+-*/")
            AreEqual(expected, DeclaredType(connection, $"{left} {op} {right}"));
    }

    [TestMethod]
    public void Arithmetic_RealAgainstNonFloat_RoundsToSingleWidth()
    {
        // 16777216 is float's last exactly-representable integer, so a single-
        // width result swallows the +1 and a double-width one doesn't. Real
        // returns 0x4B800000 for `real + bigint` as it does for `real + real`.
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual(16777216f, connection.CreateCommand(
            "select cast(16777216 as real) + cast(1 as bigint)").ExecuteScalar());
        AreEqual(16777217.0, connection.CreateCommand(
            "select cast(16777216 as float) + cast(1 as bigint)").ExecuteScalar());
    }

    [TestMethod]
    public void Arithmetic_RealDividedByInt_MatchesTheRealPairResult()
    {
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual(
            connection.CreateCommand("select cast(1 as real) / cast(7 as real)").ExecuteScalar(),
            connection.CreateCommand("select cast(1 as real) / 7").ExecuteScalar());
    }

    [TestMethod]
    [DataRow("r", "r", "real")]
    [DataRow("r", "f", "real")]
    [DataRow("f", "r", "float")]
    [DataRow("f", "f", "float")]
    public void Modulo_TwoApproximateOperands_RaisesMsg8117NamingTheLeft(string left, string right, string named)
    {
        using var connection = OneRowOfEveryNumericType();
        var ex = Throws<DbException>(() => connection.CreateCommand($"select {left} % {right} from t").ExecuteScalar());
        AreEqual($"Operand data type {named} is invalid for modulo operator.", ex.Message);
        AreEqual("8117", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    [DataRow("r", "i", "real", "int")]
    [DataRow("i", "r", "int", "real")]
    [DataRow("r", "bi", "real", "bigint")]
    [DataRow("r", "d", "real", "decimal")]
    [DataRow("d", "r", "decimal", "real")]
    [DataRow("r", "mo", "real", "money")]
    [DataRow("r", "sm", "real", "smallmoney")]
    [DataRow("r", "bt", "real", "bit")]
    [DataRow("f", "i", "float", "int")]
    [DataRow("f", "mo", "float", "money")]
    public void Modulo_ApproximateWithExactNumeric_RaisesMsg402(string left, string right, string leftName, string rightName)
    {
        using var connection = OneRowOfEveryNumericType();
        var ex = Throws<DbException>(() => connection.CreateCommand($"select {left} % {right} from t").ExecuteScalar());
        AreEqual($"The data types {leftName} and {rightName} are incompatible in the modulo operator.", ex.Message);
        AreEqual("402", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    [DataRow("'5' % f", "varchar", "float")]
    [DataRow("f % '5'", "float", "varchar")]
    [DataRow("r % 0x02", "real", "varbinary")]
    [DataRow("0x02 % r", "varbinary", "real")]
    public void Modulo_ApproximateWithStringOrBinary_RaisesMsg402(string expression, string leftName, string rightName)
    {
        using var connection = OneRowOfEveryNumericType();
        var ex = Throws<DbException>(() => connection.CreateCommand($"select {expression} from t").ExecuteScalar());
        AreEqual($"The data types {leftName} and {rightName} are incompatible in the modulo operator.", ex.Message);
        AreEqual("402", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    [DataRow("sum", "float")]
    [DataRow("avg", "float")]
    [DataRow("min", "real")]
    [DataRow("max", "real")]
    [DataRow("stdev", "float")]
    [DataRow("var", "float")]
    public void Aggregate_OverReal_WidensOnlyForSumAndAvg(string aggregate, string expected)
    {
        using var connection = OneRowOfEveryNumericType();
        AreEqual(expected, DeclaredType(connection, $"{aggregate}(r)"));
        AreEqual(expected, DeclaredType(connection, $"{aggregate}(r) over ()"));
    }

    [TestMethod]
    public void Sum_OverReal_AccumulatesAtDoubleWidth()
    {
        // Each single widens exactly on the way into a double accumulator, so
        // the +1s survive where a single-width running total would lose them —
        // real returns 16777220 for this input.
        var connection = new Simulation().CreateOpenConnection();
        using (connection)
        {
            _ = connection.CreateCommand("create table t (a real)").ExecuteNonQuery();
            _ = connection.CreateCommand("insert t values (16777216), (1), (1), (1), (1)").ExecuteNonQuery();
            AreEqual(16777220.0, connection.CreateCommand("select sum(a) from t").ExecuteScalar());
            AreEqual(3355444.0, connection.CreateCommand("select avg(a) from t").ExecuteScalar());
        }
    }

    [TestMethod]
    [DataRow("sum", "sm", "money")]
    [DataRow("avg", "sm", "money")]
    [DataRow("min", "sm", "smallmoney")]
    [DataRow("max", "sm", "smallmoney")]
    public void Aggregate_OverSmallMoney_ReportsMoneyForSumAndAvg(string aggregate, string column, string expected)
    {
        using var connection = OneRowOfEveryNumericType();
        AreEqual(expected, DeclaredType(connection, $"{aggregate}({column})"));
    }
}
