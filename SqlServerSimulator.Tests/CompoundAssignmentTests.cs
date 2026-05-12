using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for compound assignment (<c>+=</c> / <c>-=</c> / <c>*=</c>
/// / <c>/=</c> / <c>%=</c> / <c>&amp;=</c> / <c>|=</c> / <c>^=</c>) at both
/// surfaces it appears in T-SQL: <c>SET @v op= expr</c> for variables and
/// <c>UPDATE t SET col op= expr</c> for table columns. Compound forms are
/// implemented as a parse-time desugar — <c>@v += rhs</c> is rewritten as
/// <c>FromCompoundOp('+', VariableReference(@v), rhs)</c> and the existing
/// arithmetic / string-concat dispatch handles the runtime semantics
/// (NULL propagation, string concat, decimal widening, divide-by-zero).
/// All assertions probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class CompoundAssignmentTests
{
    [TestMethod]
    public void Variable_PlusEquals_Int()
        => AreEqual(15, ExecuteScalar<int>("declare @v int = 10; set @v += 5; select @v"));

    [TestMethod]
    public void Variable_MinusEquals_Int()
        => AreEqual(7, ExecuteScalar<int>("declare @v int = 10; set @v -= 3; select @v"));

    [TestMethod]
    public void Variable_StarEquals_Int()
        => AreEqual(20, ExecuteScalar<int>("declare @v int = 10; set @v *= 2; select @v"));

    [TestMethod]
    public void Variable_SlashEquals_IntTruncates()
        => AreEqual(3, ExecuteScalar<int>("declare @v int = 10; set @v /= 3; select @v"));

    [TestMethod]
    public void Variable_PercentEquals_Int()
        => AreEqual(1, ExecuteScalar<int>("declare @v int = 10; set @v %= 3; select @v"));

    [TestMethod]
    public void Variable_AndEquals_Int()
        => AreEqual(2, ExecuteScalar<int>("declare @v int = 10; set @v &= 6; select @v"));

    [TestMethod]
    public void Variable_OrEquals_Int()
        => AreEqual(15, ExecuteScalar<int>("declare @v int = 10; set @v |= 5; select @v"));

    [TestMethod]
    public void Variable_XorEquals_Int()
        => AreEqual(12, ExecuteScalar<int>("declare @v int = 10; set @v ^= 6; select @v"));

    /// <summary>
    /// NULL propagates through compound arithmetic — an uninitialized
    /// variable is NULL, and NULL <c>+</c> int → NULL.
    /// </summary>
    [TestMethod]
    public void Variable_NullPlusEquals_StaysNull()
        => AreEqual(DBNull.Value, ExecuteScalar("declare @v int; set @v += 5; select @v"));

    [TestMethod]
    public void Variable_StringPlusEquals_Concatenates()
        => AreEqual("hi there", ExecuteScalar("declare @s varchar(50) = 'hi'; set @s += ' there'; select @s"));

    [TestMethod]
    public void Variable_StringPlusEquals_NullRhs_Propagates()
        => AreEqual(DBNull.Value, ExecuteScalar("declare @s varchar(50) = 'hi'; set @s += cast(null as varchar(5)); select @s"));

    /// <summary>
    /// Decimal participation widens the same way it does for plain <c>*</c>:
    /// <c>decimal(10,2) * int</c> stays decimal with scale preserved.
    /// </summary>
    [TestMethod]
    public void Variable_DecimalStarEquals_PreservesScale()
        => AreEqual(7.00m, ExecuteScalar<decimal>("declare @v decimal(10,2) = 3.50; set @v *= 2; select @v"));

    /// <summary>
    /// Decimal compound divide-by-zero raises Msg 8134, matching the plain
    /// <c>cast(10 as decimal(10,2)) / 0</c> path. Integer compound
    /// divide-by-zero surfaces a raw <see cref="DivideByZeroException"/>
    /// instead — pre-existing simulator divergence (see CLAUDE.md and
    /// IfBlockTests's <c>Integer_DivideByZero_*</c>).
    /// </summary>
    [TestMethod]
    public void Variable_Decimal_DivideByZero_RaisesMsg8134()
        => AssertSqlError("declare @v decimal(10,2) = 10; set @v /= 0; select @v", 8134,
            "Divide by zero error encountered.");

    /// <summary>
    /// Probe-confirmed: SQL Server tokenizes <c>+ =</c> with a space as two
    /// separate operators and raises Msg 102 near <c>'+'</c>. The simulator's
    /// adjacency check enforces no whitespace between the arith char and
    /// trailing <c>=</c>; the "near" token text may differ slightly from real
    /// SQL Server (pre-existing simulator pattern, see CLAUDE.md).
    /// </summary>
    [TestMethod]
    public void Variable_PlusSpaceEquals_RaisesMsg102()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("declare @v int = 10; set @v + = 5; select @v"));
        AreEqual("102", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Variable_PlusEquals_ChainedAcrossStatements()
    {
        var simulation = new Simulation();
        AreEqual(6, simulation.ExecuteScalar<int>("declare @v int = 0; set @v += 1; set @v += 2; set @v += 3; select @v"));
    }

    [TestMethod]
    public void Update_PlusEquals_AllRows()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int, v int);
            insert t values (1, 10), (2, 20)
            """);
        AreEqual(2, simulation.ExecuteNonQuery("update t set v += 5"));
        using var reader = simulation.CreateCommand("select v from t order by id").ExecuteReader();
        IsTrue(reader.Read()); AreEqual(15, reader.GetInt32(0));
        IsTrue(reader.Read()); AreEqual(25, reader.GetInt32(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Update_PlusEquals_WhereClause_OnlySelectedRows()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int, v int);
            insert t values (1, 10), (2, 20), (3, 30)
            """);
        AreEqual(1, simulation.ExecuteNonQuery("update t set v += 100 where id = 2"));
        using var reader = simulation.CreateCommand("select v from t order by id").ExecuteReader();
        IsTrue(reader.Read()); AreEqual(10, reader.GetInt32(0));
        IsTrue(reader.Read()); AreEqual(120, reader.GetInt32(0));
        IsTrue(reader.Read()); AreEqual(30, reader.GetInt32(0));
    }

    /// <summary>
    /// Multi-column SET with mixed plain / compound. The pre-update snapshot
    /// is shared across all assignments in a single SET (matches existing
    /// multi-column UPDATE semantics), so <c>a *= 2, b = a</c> sees the
    /// original <c>a</c>, not the doubled one.
    /// </summary>
    [TestMethod]
    public void Update_MultiColumnSet_MixedPlainAndCompound()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int, a int, b int);
            insert t values (1, 3, 0)
            """);
        AreEqual(1, simulation.ExecuteNonQuery("update t set a *= 2, b = a where id = 1"));
        using var reader = simulation.CreateCommand("select a, b from t").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(6, reader.GetInt32(0));
        AreEqual(3, reader.GetInt32(1));
    }

    [TestMethod]
    public void Update_QualifiedColumn_PlusEquals()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int, v int);
            insert t values (1, 10)
            """);
        AreEqual(1, simulation.ExecuteNonQuery("update t set t.v += 5 where id = 1"));
        AreEqual(15, simulation.ExecuteScalar<int>("select v from t"));
    }

    [TestMethod]
    public void Update_StringPlusEquals_Concatenates()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int, s varchar(20));
            insert t values (1, 'hi'), (2, 'there')
            """);
        AreEqual(1, simulation.ExecuteNonQuery("update t set s += '!' where id = 1"));
        using var reader = simulation.CreateCommand("select s from t order by id").ExecuteReader();
        IsTrue(reader.Read()); AreEqual("hi!", reader.GetString(0));
        IsTrue(reader.Read()); AreEqual("there", reader.GetString(0));
    }

    /// <summary>
    /// NULL column value + int → NULL: the compound operator participates in
    /// three-valued logic the same way plain <c>+</c> does.
    /// </summary>
    [TestMethod]
    public void Update_NullColumnPlusEquals_StaysNull()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int, v int null);
            insert t values (1, null), (2, 5)
            """);
        AreEqual(2, simulation.ExecuteNonQuery("update t set v += 10"));
        using var reader = simulation.CreateCommand("select v from t order by id").ExecuteReader();
        IsTrue(reader.Read()); IsTrue(reader.IsDBNull(0));
        IsTrue(reader.Read()); AreEqual(15, reader.GetInt32(0));
    }

    [TestMethod]
    public void Update_FromJoinSyntax_CompoundWithAlias()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int, v int);
            create table u (id int, bonus int);
            insert t values (1, 10), (2, 20);
            insert u values (1, 5), (2, 50)
            """);
        AreEqual(2, simulation.ExecuteNonQuery("update t set v += u.bonus from t join u on t.id = u.id"));
        using var reader = simulation.CreateCommand("select v from t order by id").ExecuteReader();
        IsTrue(reader.Read()); AreEqual(15, reader.GetInt32(0));
        IsTrue(reader.Read()); AreEqual(70, reader.GetInt32(0));
    }
}
