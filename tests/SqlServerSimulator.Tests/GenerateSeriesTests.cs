using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the built-in <c>GENERATE_SERIES(start, stop [, step])</c>
/// rowset function. Validates the output schema, type-inference rules
/// (integer-subtype distinctness, decimal-family collapse), default-step
/// direction (probe-confirmed against SQL Server 2025), NULL handling
/// (empty rowset), and error paths (Msg 313 / 4199 / 5373 / 8116 / 8144).
/// </summary>
[TestClass]
public sealed class GenerateSeriesTests
{
    private static List<long> ReadAllLong(DbDataReader reader)
    {
        var rows = new List<long>();
        while (reader.Read())
            rows.Add(reader.GetInt64(0));
        return rows;
    }

    [TestMethod]
    public void Basic_TwoArg_AscendingSequence()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand("select value from GENERATE_SERIES(1, 5)").ExecuteReader();
        var rows = new List<int>();
        while (reader.Read()) rows.Add(reader.GetInt32(0));
        HasCount(5, rows);
        AreEqual(1, rows[0]);
        AreEqual(5, rows[4]);
    }

    [TestMethod]
    public void OutputColumn_NamedValue()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand("select * from GENERATE_SERIES(1, 1)").ExecuteReader();
        AreEqual(1, reader.FieldCount);
        AreEqual("value", reader.GetName(0));
    }

    [TestMethod]
    public void ThreeArg_PositiveStep()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand("select value from GENERATE_SERIES(1, 10, 2)").ExecuteReader();
        var rows = new List<int>();
        while (reader.Read()) rows.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1, 3, 5, 7, 9 }, rows);
    }

    [TestMethod]
    public void DefaultStep_Descending_WhenStartGreaterThanStop()
    {
        // Default step direction follows start vs stop: -1 when start > stop,
        // 1 otherwise (matches Microsoft's docs and live SQL Server 2025).
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand("select value from GENERATE_SERIES(5, 1)").ExecuteReader();
        var rows = new List<int>();
        while (reader.Read()) rows.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 5, 4, 3, 2, 1 }, rows);
    }

    [TestMethod]
    public void ExplicitNegativeStep_Descending()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand("select value from GENERATE_SERIES(5, 1, -1)").ExecuteReader();
        var rows = new List<int>();
        while (reader.Read()) rows.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 5, 4, 3, 2, 1 }, rows);
    }

    [TestMethod]
    public void WrongDirectionStep_PositiveStartGreaterThanStop_Empty()
    {
        // Probe: explicit positive step with start>stop → empty rowset, no error.
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand("select value from GENERATE_SERIES(5, 1, 1)").ExecuteReader();
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void WrongDirectionStep_NegativeStartLessThanStop_Empty()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand("select value from GENERATE_SERIES(1, 5, -1)").ExecuteReader();
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void StartEqualsStop_SingleRow()
    {
        using var conn = new Simulation().CreateOpenConnection();
        AreEqual(1, conn.CreateCommand("select count(*) from GENERATE_SERIES(5, 5)").ExecuteScalar());
        AreEqual(5, conn.CreateCommand("select value from GENERATE_SERIES(5, 5)").ExecuteScalar());
    }

    [TestMethod]
    public void StepLandsExactlyOnStop_IncludesStop()
    {
        using var conn = new Simulation().CreateOpenConnection();
        var rows = new List<int>();
        using var reader = conn.CreateCommand("select value from GENERATE_SERIES(0, 10, 5)").ExecuteReader();
        while (reader.Read()) rows.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 0, 5, 10 }, rows);
    }

    [TestMethod]
    public void StepUndershootsStop_StopsBefore()
    {
        using var conn = new Simulation().CreateOpenConnection();
        var rows = new List<int>();
        using var reader = conn.CreateCommand("select value from GENERATE_SERIES(0, 9, 5)").ExecuteReader();
        while (reader.Read()) rows.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 0, 5 }, rows);
    }

    [TestMethod]
    public void NegativeRange()
    {
        using var conn = new Simulation().CreateOpenConnection();
        var rows = new List<int>();
        using var reader = conn.CreateCommand("select value from GENERATE_SERIES(-3, 3)").ExecuteReader();
        while (reader.Read()) rows.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { -3, -2, -1, 0, 1, 2, 3 }, rows);
    }

    // === Type preservation ===

    [TestMethod]
    public void TinyInt_PreservesType()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand("select value from GENERATE_SERIES(cast(1 as tinyint), cast(3 as tinyint))").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(typeof(byte), reader.GetFieldType(0));
        AreEqual((byte)1, reader.GetByte(0));
    }

    [TestMethod]
    public void SmallInt_PreservesType()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand("select value from GENERATE_SERIES(cast(1 as smallint), cast(3 as smallint))").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(typeof(short), reader.GetFieldType(0));
    }

    [TestMethod]
    public void BigInt_PreservesType()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand("select value from GENERATE_SERIES(cast(1 as bigint), cast(3 as bigint))").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(typeof(long), reader.GetFieldType(0));
    }

    [TestMethod]
    public void Decimal_PreservesType()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand("select value from GENERATE_SERIES(cast(1.0 as decimal(10,1)), cast(2.0 as decimal(10,1)), cast(0.5 as decimal(10,1)))").ExecuteReader();
        var rows = new List<decimal>();
        while (reader.Read()) rows.Add(reader.GetDecimal(0));
        CollectionAssert.AreEqual(new[] { 1.0m, 1.5m, 2.0m }, rows);
    }

    [TestMethod]
    public void Decimal_MixedPrecisionAndScale_Promotes()
    {
        // Probe: DECIMAL(10,1) + DECIMAL(10,2) is accepted; the projected
        // scale unifies to the wider one.
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand("select value from GENERATE_SERIES(cast(1.0 as decimal(10,1)), cast(3.00 as decimal(10,2)))").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(typeof(decimal), reader.GetFieldType(0));
    }

    // === NULL handling ===

    [TestMethod]
    public void NullStart_Empty()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand("select value from GENERATE_SERIES(cast(NULL as int), 5)").ExecuteReader();
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void NullStop_Empty()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand("select value from GENERATE_SERIES(1, cast(NULL as int))").ExecuteReader();
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void NullStep_Empty()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand("select value from GENERATE_SERIES(1, 5, cast(NULL as int))").ExecuteReader();
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void UntypedNullStart_Empty()
    {
        // Probe: bare NULL is accepted and produces an empty rowset; SQL
        // Server infers the column type from the non-NULL arg.
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand("select value from GENERATE_SERIES(NULL, 5)").ExecuteReader();
        IsFalse(reader.Read());
    }

    // === Errors ===

    [TestMethod]
    public void StepZero_Msg4199()
    {
        var ex = new Simulation().AssertSqlError("select value from GENERATE_SERIES(1, 5, 0)", 4199);
        Assert.Contains("Argument value 0 is invalid for argument 3 of generate_series function", ex.Message);
    }

    [TestMethod]
    public void MismatchedIntegerSubtypes_Msg5373()
    {
        var ex = new Simulation().AssertSqlError("select value from GENERATE_SERIES(1, cast(5 as bigint))", 5373);
        Assert.Contains("same type", ex.Message);
    }

    [TestMethod]
    public void IntAndDecimalMismatch_Msg5373()
    {
        _ = new Simulation().AssertSqlError("select value from GENERATE_SERIES(cast(1.5 as decimal(10,1)), 5)", 5373);
    }

    [TestMethod]
    public void StepTypeMismatch_Msg5373()
    {
        _ = new Simulation().AssertSqlError("select value from GENERATE_SERIES(1, 5, cast(1 as bigint))", 5373);
    }

    [TestMethod]
    public void FloatArg_Msg8116()
    {
        var ex = new Simulation().AssertSqlError("select value from GENERATE_SERIES(1.0e0, 5.0e0)", 8116);
        Assert.Contains("Argument data type float is invalid for argument 1 of generate_series function", ex.Message);
    }

    [TestMethod]
    public void MoneyArg_Msg8116()
    {
        _ = new Simulation().AssertSqlError("select value from GENERATE_SERIES(cast(1 as money), cast(5 as money))", 8116);
    }

    [TestMethod]
    public void VarcharArg_Msg8116()
    {
        _ = new Simulation().AssertSqlError("select value from GENERATE_SERIES('1', '5')", 8116);
    }

    [TestMethod]
    public void DateArg_Msg8116()
    {
        _ = new Simulation().AssertSqlError("select value from GENERATE_SERIES(cast('2025-01-01' as date), cast('2025-01-10' as date))", 8116);
    }

    [TestMethod]
    public void ZeroArgs_Msg313()
    {
        var ex = new Simulation().AssertSqlError("select value from GENERATE_SERIES()", 313);
        Assert.Contains("insufficient number of arguments", ex.Message);
        Assert.Contains("GENERATE_SERIES", ex.Message);
    }

    [TestMethod]
    public void OneArg_Msg313()
    {
        _ = new Simulation().AssertSqlError("select value from GENERATE_SERIES(5)", 313);
    }

    [TestMethod]
    public void FourArgs_Msg8144()
    {
        var ex = new Simulation().AssertSqlError("select value from GENERATE_SERIES(1, 5, 1, 1)", 8144);
        Assert.Contains("too many arguments", ex.Message);
    }

    // === Composition ===

    [TestMethod]
    public void CrossJoin_SelfMultiplies()
    {
        using var conn = new Simulation().CreateOpenConnection();
        AreEqual(6, conn.CreateCommand("select count(*) from GENERATE_SERIES(1,3) a cross join GENERATE_SERIES(1,2) b").ExecuteScalar());
    }

    [TestMethod]
    public void CrossApply_LateralPerOuterRow()
    {
        // Per outer row, GENERATE_SERIES(1, outer.n) yields outer.n rows.
        // For n IN {3, 5}: 3 + 5 = 8 rows total.
        using var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("""
            create table src (n int);
            insert src values (3), (5)
            """).ExecuteNonQuery();
        AreEqual(8, conn.CreateCommand("""
            select count(*)
            from src s
            cross apply GENERATE_SERIES(1, s.n) as v
            """).ExecuteScalar());
    }

    [TestMethod]
    public void AliasedColumn_QualifiedAccess()
    {
        using var conn = new Simulation().CreateOpenConnection();
        AreEqual(6, conn.CreateCommand("select sum(v.value) from GENERATE_SERIES(1,3) v").ExecuteScalar());
    }

    [TestMethod]
    public void Variable_StartAndStop()
    {
        AreEqual(10, new Simulation().ExecuteScalar("declare @s int = 1, @e int = 4; select sum(value) from GENERATE_SERIES(@s, @e)"));
    }

    [TestMethod]
    public void Expression_StartAndStop()
    {
        AreEqual(21, new Simulation().ExecuteScalar("select sum(value) from GENERATE_SERIES(1+0, 2*3)"));
    }

    [TestMethod]
    public void BigIntNearMax_NoOverflow()
    {
        // Probe: start near MAX_BIGINT, stop = MAX_BIGINT, step = 3 yields
        // three rows (the last is 9223372036854775806; the next iteration's
        // overflow is the termination signal, not an exception).
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand(
            "select value from GENERATE_SERIES(cast(9223372036854775800 as bigint), cast(9223372036854775807 as bigint), cast(3 as bigint))").ExecuteReader();
        var rows = ReadAllLong(reader);
        CollectionAssert.AreEqual(new[] { 9223372036854775800L, 9223372036854775803L, 9223372036854775806L }, rows);
    }

    [TestMethod]
    public void DecimalStep_FractionalIncrements()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand(
            "select value from GENERATE_SERIES(cast(0 as decimal(5,1)), cast(1 as decimal(5,1)), cast(0.3 as decimal(5,1)))").ExecuteReader();
        var rows = new List<decimal>();
        while (reader.Read()) rows.Add(reader.GetDecimal(0));
        CollectionAssert.AreEqual(new[] { 0.0m, 0.3m, 0.6m, 0.9m }, rows);
    }
}
