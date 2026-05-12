using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>RAND([seed])</c>. The defining behavior is the
/// runtime-constant rule: a single parsed <c>RAND(...)</c> call site
/// produces ONE value reused across every row of the query — distinct call
/// sites in the same query each get their own (potentially different)
/// constant. Probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class RandTests
{
    [TestMethod]
    public void NoSeed_ReturnsFloatInUnitRange()
    {
        var v = ExecuteScalar<double>("select RAND()");
        IsTrue(v is >= 0.0 and < 1.0);
    }

    [TestMethod]
    public void SeededRand_SameSeedReturnsSameValueAcrossInvocations()
    {
        // Each ExecuteScalar runs a fresh parse → fresh Rand instance, but
        // both share the same seed-derived deterministic value.
        var first = ExecuteScalar<double>("select RAND(42)");
        var second = ExecuteScalar<double>("select RAND(42)");
        AreEqual(first, second);
    }

    [TestMethod]
    public void SeededRand_DifferentSeedsReturnDifferentValues()
    {
        var a = ExecuteScalar<double>("select RAND(1)");
        var b = ExecuteScalar<double>("select RAND(999999)");
        AreNotEqual(a, b);
    }

    [TestMethod]
    public void SeededRand_NullSeed_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select RAND(NULL)"));

    /// <summary>
    /// The defining run-once-per-query behavior: with three rows in the FROM
    /// source, a single <c>RAND()</c> call site returns the same value for
    /// every row.
    /// </summary>
    [TestMethod]
    public void RandIsRuntimeConstantAcrossRows()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table src (n int);
            insert src values (1), (2), (3)
            """).ExecuteNonQuery();
        using var reader = connection.CreateCommand("select RAND() as r from src").ExecuteReader();
        var values = new List<double>();
        while (reader.Read())
            values.Add(reader.GetDouble(0));
        HasCount(3, values);
        AreEqual(values[0], values[1]);
        AreEqual(values[1], values[2]);
    }

    /// <summary>
    /// Two distinct <c>RAND()</c> call sites in the same projection each
    /// get their own independent runtime constant.
    /// </summary>
    [TestMethod]
    public void TwoRandCalls_EachCallHasItsOwnConstant()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table src (n int);
            insert src values (1), (2)
            """).ExecuteNonQuery();
        using var reader = connection.CreateCommand("select RAND() as r1, RAND() as r2 from src").ExecuteReader();
        var rows = new List<(double R1, double R2)>();
        while (reader.Read())
            rows.Add((reader.GetDouble(0), reader.GetDouble(1)));
        HasCount(2, rows);
        // Each call site is row-stable.
        AreEqual(rows[0].R1, rows[1].R1);
        AreEqual(rows[0].R2, rows[1].R2);
    }
}
