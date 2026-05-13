using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>value [NOT] BETWEEN lower AND upper</c>. Semantically
/// equivalent to <c>value &gt;= lower AND value &lt;= upper</c> (inclusive
/// on both ends); reversed bounds produce a definite false; any NULL operand
/// propagates per three-valued logic. Probed against SQL Server 2025
/// (2026-05-13).
/// </summary>
[TestClass]
public sealed class BetweenTests
{
    [TestMethod]
    [DataRow("5 between 1 and 10", 1)]
    [DataRow("1 between 1 and 10", 1)]
    [DataRow("10 between 1 and 10", 1)]
    [DataRow("0 between 1 and 10", 0)]
    [DataRow("11 between 1 and 10", 0)]
    [DataRow("5 not between 1 and 10", 0)]
    [DataRow("11 not between 1 and 10", 1)]
    [DataRow("5 between 10 and 1", 0)]
    [DataRow("5 not between 10 and 1", 1)]
    public void Basic(string predicate, int expectedCount) =>
        AreEqual(expectedCount,
            new Simulation().ExecuteReader($"select 1 where {predicate}").EnumerateRecords().Count());

    [TestMethod]
    [DataRow("cast(null as int) between 1 and 10")]
    [DataRow("cast(null as int) not between 1 and 10")]
    [DataRow("5 between cast(null as int) and 10")]
    [DataRow("5 between 1 and cast(null as int)")]
    [DataRow("5 between cast(null as int) and cast(null as int)")]
    [DataRow("5 not between cast(null as int) and 10")]
    [DataRow("5 not between 1 and cast(null as int)")]
    public void NullOperand_ExcludesFromWhere(string predicate) =>
        AreEqual(0,
            new Simulation().ExecuteReader($"select 1 where {predicate}").EnumerateRecords().Count());

    [TestMethod]
    public void DefinitelyOutOfRange_WithNullBound_StillFalse()
    {
        // Probe-confirmed: even though the NULL-side comparison is UNKNOWN,
        // three-valued AND with the definite-false side collapses to FALSE,
        // so the row is excluded.
        AreEqual(0,
            new Simulation().ExecuteReader("select 1 where 100 between cast(null as int) and 10").EnumerateRecords().Count());
        AreEqual(0,
            new Simulation().ExecuteReader("select 1 where -5 between 1 and cast(null as int)").EnumerateRecords().Count());
    }

    [TestMethod]
    public void StringBetween()
    {
        // 'mango' falls between 'apple' and 'pear' in ANSI string order.
        AreEqual(1,
            new Simulation().ExecuteReader("select 1 where 'mango' between 'apple' and 'pear'").EnumerateRecords().Count());
        AreEqual(0,
            new Simulation().ExecuteReader("select 1 where 'mango' not between 'apple' and 'pear'").EnumerateRecords().Count());
    }

    [TestMethod]
    public void DecimalBetween_IntBounds_TypePromotion()
    {
        AreEqual(1,
            new Simulation().ExecuteReader("select 1 where cast(2.5 as decimal(5,2)) between 1 and 5").EnumerateRecords().Count());
    }

    [TestMethod]
    public void DateBetween_StringBounds_CoercionWorks()
    {
        AreEqual(1,
            new Simulation().ExecuteReader(
                "select 1 where cast('2026-05-15' as date) between '2026-01-01' and '2026-12-31'").EnumerateRecords().Count());
    }

    [TestMethod]
    public void Precedence_BetweenBindsTighterThanAnd()
    {
        // `5 between 1 and 10 and 1 = 1` parses as
        // `(5 between 1 and 10) AND (1 = 1)` — both true → row passes.
        AreEqual(1,
            new Simulation().ExecuteReader("select 1 where 5 between 1 and 10 and 1 = 1").EnumerateRecords().Count());
    }

    [TestMethod]
    public void Precedence_BetweenInsideOr()
    {
        AreEqual(1,
            new Simulation().ExecuteReader("select 1 where 1 = 0 or 5 between 1 and 10").EnumerateRecords().Count());
    }

    [TestMethod]
    public void ArithmeticInBounds()
    {
        // Bounds accept arithmetic expressions — Expression.Parse stops at
        // the AND keyword (boolean combinator) without consuming it.
        AreEqual(1,
            new Simulation().ExecuteReader("select 1 where 5 between 1 - 1 and 10 + 1").EnumerateRecords().Count());
    }

    [TestMethod]
    public void MultiRowWhereBetween_FiltersNulls()
    {
        using var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("""
            create table bv (id int, v int);
            insert bv values (1, 5), (2, 10), (3, 15), (4, null)
            """).ExecuteNonQuery();
        using var reader = conn.CreateCommand("select id from bv where v between 5 and 12 order by id").ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new int[] { 1, 2 }, ids);
    }

    [TestMethod]
    public void MultiRowWhereNotBetween_ExcludesNulls()
    {
        // Probe-confirmed: id=4 (v=NULL) excluded by three-valued NOT (UNKNOWN
        // → UNKNOWN → excluded), id=3 (v=15) is the only out-of-range row.
        using var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("""
            create table bv (id int, v int);
            insert bv values (1, 5), (2, 10), (3, 15), (4, null)
            """).ExecuteNonQuery();
        using var reader = conn.CreateCommand("select id from bv where v not between 5 and 12 order by id").ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new int[] { 3 }, ids);
    }

    [TestMethod]
    public void CheckConstraint_BetweenRejectsOutOfRange()
    {
        // CHECK constraint passes UNKNOWN — a definitively-false BETWEEN
        // raises Msg 547. Inline single-column CHECK referencing only the
        // owning column.
        using var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("create table bt (v int check (v between 1 and 10))").ExecuteNonQuery();
        _ = conn.CreateCommand("insert bt values (5)").ExecuteNonQuery();
        using var cmd = conn.CreateCommand("insert bt values (11)");
        var ex = Throws<DbException>(() => cmd.ExecuteNonQuery());
        AreEqual("547", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void MissingAndKeyword_SyntaxError()
    {
        // `value BETWEEN lower upper` (no AND) → syntax error near upper.
        var ex = Throws<DbException>(() => new Simulation().ExecuteScalar("select 1 where 5 between 1 10"));
        AreEqual("102", ex.Data["HelpLink.EvtID"]);
    }
}
