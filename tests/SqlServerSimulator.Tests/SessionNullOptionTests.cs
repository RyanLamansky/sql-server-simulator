using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>SET ANSI_NULLS</c>, <c>SET CONCAT_NULL_YIELDS_NULL</c> and
/// <c>SET ANSI_WARNINGS</c> as they change results, and the type a bare
/// <c>NULL</c> takes in string <c>+</c> — probed 2026-09-24 against SQL
/// Server 2025.
/// </summary>
[TestClass]
public sealed class SessionNullOptionTests
{
    private const string Rows = "create table an (a int); insert an values (1), (null);";

    [TestMethod]
    [DataRow("a = null", 1)]
    [DataRow("null = a", 1)]
    [DataRow("a <> null", 1)]
    [DataRow("a in (null)", 1)]
    [DataRow("a in (1, null)", 2)]
    [DataRow("a not in (null)", 1)]
    [DataRow("not (a = null)", 1)]
    [DataRow("a = @x", 1)]
    [DataRow("a = cast(null as int)", 0)]
    [DataRow("a = 1 + null", 0)]
    [DataRow("a = nullif(1, 1)", 0)]
    [DataRow("a > null", 0)]
    [DataRow("a = (select top 1 a from an where a is null)", 0)]
    public void AnsiNullsOff_MakesComparisonsWithANullOrVariableTwoValued(string predicate, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"{Rows} set ansi_nulls off; declare @x int; select count(*) from an where {predicate}"));

    [TestMethod]
    public void AnsiNullsOff_LeavesColumnToColumnComparisonsAlone()
        => AreEqual(1, new Simulation().ExecuteScalar($"{Rows} set ansi_nulls off; select count(*) from an x join an y on x.a = y.a"));

    [TestMethod]
    public void AnsiNullsOff_AnswersCaseConditionsTwoValued()
        => AreEqual("1|1|0|0", new Simulation().ExecuteScalar("""
            set ansi_nulls off;
            select concat(case when null = null then 1 else 0 end, '|', case when 1 <> null then 1 else 0 end, '|',
                          case when 'a' = null then 1 else 0 end, '|', case when null <> null then 1 else 0 end)
            """));

    [TestMethod]
    public void AnsiNullsOn_KeepsComparisonsWithNullUnknown()
        => AreEqual(0, new Simulation().ExecuteScalar($"{Rows} select count(*) from an where a = null"));

    [TestMethod]
    public void Procedure_UsesTheAnsiNullsItWasCreatedUnder()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand(Rows).ExecuteNonQuery();
        _ = connection.CreateCommand("set ansi_nulls off").ExecuteNonQuery();
        _ = connection.CreateCommand("create procedure p_off as select count(*) from an where a = null").ExecuteNonQuery();
        _ = connection.CreateCommand("set ansi_nulls on").ExecuteNonQuery();
        _ = connection.CreateCommand("create procedure p_on as select count(*) from an where a = null").ExecuteNonQuery();
        AreEqual(1, connection.CreateCommand("exec p_off").ExecuteScalar());
        _ = connection.CreateCommand("set ansi_nulls off").ExecuteNonQuery();
        AreEqual(0, connection.CreateCommand("exec p_on").ExecuteScalar());
    }

    [TestMethod]
    public void SameTextUnderBothSettings_ReadsEachWay()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand(Rows).ExecuteNonQuery();
        const string Query = "select count(*) from an where a = null";
        AreEqual(0, connection.CreateCommand(Query).ExecuteScalar());
        _ = connection.CreateCommand("set ansi_nulls off").ExecuteNonQuery();
        AreEqual(1, connection.CreateCommand(Query).ExecuteScalar());
    }

    [TestMethod]
    [DataRow("'a' + null", "a")]
    [DataRow("null + 'b'", "b")]
    [DataRow("'a' + cast(null as varchar(5))", "a")]
    public void ConcatNullYieldsNullOff_ReadsANullOperandAsEmpty(string expression, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"set concat_null_yields_null off; select {expression}"));

    [TestMethod]
    public void ConcatNullYieldsNullOff_KeepsTwoNullsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("set concat_null_yields_null off; declare @a varchar(5), @b varchar(5); select @a + @b"));

    [TestMethod]
    public void ConcatNullYieldsNullOff_JoinsAroundANullVariable()
        => AreEqual("xy", new Simulation().ExecuteScalar("set concat_null_yields_null off; declare @v varchar(5); select 'x' + @v + 'y'"));

    [TestMethod]
    [DataRow("'a' + null", "varchar", 2)]
    [DataRow("null + 'abc'", "varchar", 4)]
    [DataRow("N'a' + null", "nvarchar", 4)]
    public void BareNullBesideAString_IsAOneCharacterString(string expression, string type, int maxLength)
        => AreEqual($"{type}|{maxLength}", new Simulation().ExecuteScalar($"""
            select {expression} as x into t;
            select concat(type_name(system_type_id), '|', max_length) from sys.columns where object_id = object_id('t')
            """));

    [TestMethod]
    [DataRow("varchar(3)", "'abcdef'", "abc")]
    [DataRow("char(2)", "'abc'", "ab")]
    public void AnsiWarningsOff_TruncatesAnInsertSilently(string type, string value, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"set ansi_warnings off; create table t (v {type}); insert t values ({value}); select v from t"));

    [TestMethod]
    public void AnsiWarningsOff_TruncatesABinaryInsert()
        => CollectionAssert.AreEqual(new byte[] { 1, 2 }, (byte[])new Simulation().ExecuteScalar("set ansi_warnings off; create table t (b varbinary(2)); insert t values (0x010203); select b from t")!);

    [TestMethod]
    [DataRow("set nocount on", 0)]
    [DataRow("set ansi_nulls on", 0)]
    [DataRow("set language us_english", 0)]
    [DataRow("set transaction isolation level read committed", 0)]
    [DataRow("declare @z int; set @z = 5", 1)]
    [DataRow("declare @x int", 1)]
    public void SetOption_ResetsRowCount(string statement, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"create table r (a int); insert r values (1); {statement}; select @@rowcount"));
}
