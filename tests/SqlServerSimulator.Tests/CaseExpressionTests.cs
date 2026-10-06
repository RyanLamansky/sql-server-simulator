using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for SQL Server's <c>CASE</c> expression in both forms:
/// searched (<c>CASE WHEN cond THEN ... [ELSE ...] END</c>) and simple
/// (<c>CASE input WHEN val THEN ... [ELSE ...] END</c>). NULL handling,
/// type promotion across branches, and composability with other expression
/// contexts (WHERE, arithmetic, scalar subqueries) are sourced from probes
/// against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class CaseExpressionTests
{
    // === Searched form ===

    [TestMethod]
    public void Searched_FirstMatchingWhenWins()
    {
        AreEqual("a", new Simulation().ExecuteScalar("select case when 1=1 then 'a' when 1=1 then 'b' end"));
    }

    [TestMethod]
    public void Searched_NoMatchNoElse_ReturnsNull()
    {
        // ExecuteScalar returns the value of the first column of the first row.
        // For a single row with a NULL column, that's DBNull.Value.
        AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select case when 1=0 then 'a' end"));
    }

    [TestMethod]
    public void Searched_NoMatch_FallsBackToElse()
    {
        AreEqual("z", new Simulation().ExecuteScalar("select case when 1=0 then 'a' else 'z' end"));
    }

    [TestMethod]
    public void Searched_UnknownPredicate_TreatedAsExclude()
    {
        // `null = 1` evaluates to UNKNOWN; UNKNOWN is not a match (same as WHERE).
        AreEqual("unmatched", new Simulation().ExecuteScalar("select case when null = 1 then 'matched' else 'unmatched' end"));
    }

    [TestMethod]
    public void Searched_MultipleWhens_FirstTrueWins()
    {
        AreEqual("second", new Simulation().ExecuteScalar(
            "select case when 1=0 then 'first' when 2=2 then 'second' when 3=3 then 'third' end"));
    }

    // === Simple form ===

    [TestMethod]
    public void Simple_InputMatchesWhen_ReturnsThen()
    {
        AreEqual("two", new Simulation().ExecuteScalar("select case 2 when 1 then 'one' when 2 then 'two' else 'other' end"));
    }

    [TestMethod]
    public void Simple_NullInputVsNullWhen_NotAMatch()
    {
        // `case null when null` follows `=` semantics: NULL = NULL is UNKNOWN, not a match.
        AreEqual("unmatched", new Simulation().ExecuteScalar(
            "select case cast(null as int) when null then 'matched' else 'unmatched' end"));
    }

    [TestMethod]
    public void Simple_NoMatch_FallsBackToElse()
    {
        AreEqual("other", new Simulation().ExecuteScalar(
            "select case 99 when 1 then 'one' when 2 then 'two' else 'other' end"));
    }

    [TestMethod]
    public void Simple_NoMatchNoElse_ReturnsNull()
    {
        AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select case 99 when 1 then 'one' when 2 then 'two' end"));
    }

    // === Composition ===

    [TestMethod]
    public void Case_InArithmetic_FlowsThroughOperator()
    {
        AreEqual(15, new Simulation().ExecuteScalar("select case when 1=1 then 10 else 20 end + 5"));
    }

    [TestMethod]
    public void Case_InWhereClause_FiltersByDecision()
    {
        // Note: outer parens around the CASE are NOT used here — the
        // simulator's parser doesn't accept `(arith) cmp rhs` (a known
        // limitation). Bare `case ... end = 1` parses fine.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int, name nvarchar(20));
            insert t values (1, 'one'), (2, 'two'), (3, 'three')
            """);

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand(
            "select id from t where case when name='two' then 1 else 0 end = 1").ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 2 }, ids);
    }

    [TestMethod]
    public void Case_WithColumnReference_ResolvesPerRow()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int, name nvarchar(20));
            insert t values (1, 'one'), (2, 'two'), (3, 'three')
            """);

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand(
            "select case when id = 2 then name else 'other' end from t").ExecuteReader();
        var labels = new List<string>();
        while (reader.Read())
            labels.Add(reader.GetString(0));
        CollectionAssert.AreEqual(new[] { "other", "two", "other" }, labels);
    }

    [TestMethod]
    public void Case_Nested_OuterAndInnerEvaluateCorrectly()
    {
        AreEqual("inner-yes", new Simulation().ExecuteScalar(
            "select case when 1=1 then case when 2=2 then 'inner-yes' else 'inner-no' end else 'outer-no' end"));
    }

    [TestMethod]
    public void Case_WithNullElse_ReturnsThenWhenMatched()
    {
        AreEqual(5, new Simulation().ExecuteScalar("select case when 1=1 then 5 else null end"));
    }

    [TestMethod]
    public void Case_WithNullThen_ReturnsNullWhenMatched()
    {
        AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select case when 1=1 then null else 5 end"));
    }

    [TestMethod]
    public void Case_InScalarSubquery_ProjectsValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int);
            insert t values (1)
            """);

        AreEqual("hit", simulation.ExecuteScalar(
            "select (select case when t.id = 1 then 'hit' else 'miss' end from t)"));
    }

    // === Syntax errors ===

    [TestMethod]
    public void Case_EmptyNoWhen_RaisesSyntaxError()
    {
        // `case end` has no WHEN clauses; SQL Server raises Msg 156 near the
        // reserved keyword `end` (probe-confirmed against SQL Server 2025).
        var ex = Throws<SimulatedSqlException>(() => _ = new Simulation().ExecuteScalar("select case end"));
        AreEqual(156, ex.Number);
    }

    [TestMethod]
    public void Case_MissingEnd_RaisesSyntaxError()
    {
        var ex = Throws<SimulatedSqlException>(() => _ = new Simulation().ExecuteScalar("select case when 1=1 then 'a'"));
        AreEqual(102, ex.Number);
    }

    // === Msg 8133: every result expression is a bare NULL ===
    // Probed against SQL Server 2025 (2026-05-11). Class 16 State 1 verbatim:
    // "At least one of the result expressions in a CASE specification must
    //  be an expression other than the NULL constant."

    [TestMethod]
    public void Case_AllBareNull_NoElse_Searched_Msg8133()
        => new Simulation().AssertSqlError(
            "select case when 1=1 then null end",
            8133,
            "At least one of the result expressions in a CASE specification must be an expression other than the NULL constant.");

    [TestMethod]
    public void Case_AllBareNull_WithElseNull_Searched_Msg8133()
        => new Simulation().AssertSqlError(
            "select case when 1=1 then null else null end",
            8133);

    [TestMethod]
    public void Case_AllBareNull_MultipleWhens_NoElse_Msg8133()
        => new Simulation().AssertSqlError(
            "select case when 1=1 then null when 1=0 then null end",
            8133);

    [TestMethod]
    public void Case_AllBareNull_Simple_Msg8133()
        => new Simulation().AssertSqlError(
            "select case 1 when 1 then null else null end",
            8133);

    [TestMethod]
    public void Case_AllBareNull_SimpleNoElse_Msg8133()
        => new Simulation().AssertSqlError(
            "select case 1 when 1 then null end",
            8133);

    [TestMethod]
    public void Case_AllBareNull_ParenWrapped_Msg8133()
        => new Simulation().AssertSqlError(
            "select case when 1=1 then (null) else (null) end",
            8133);

    [TestMethod]
    public void Case_AllBareNull_DoubleParenWrapped_Msg8133()
        => new Simulation().AssertSqlError(
            "select case when 1=1 then ((null)) else null end",
            8133);

    [TestMethod]
    public void Case_TypedNullElse_Accepted()
    {
        // A typed NULL (`cast(null as int)`) on any branch satisfies the
        // rule because the result type can be inferred.
        AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select case when 1=1 then null else cast(null as int) end"));
    }

    [TestMethod]
    public void Case_TypedNullThen_Accepted()
    {
        AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select case when 1=1 then cast(null as varchar(10)) else null end"));
    }

    [TestMethod]
    public void Case_OneBranchTyped_OneBareNull_Accepted()
    {
        // One typed branch satisfies Msg 8133. Use int branches because the
        // simulator currently types bare NULL as int and a varchar typed
        // branch would collide on Promote (pre-existing fidelity gap that's
        // orthogonal to Msg 8133).
        AreEqual(7, new Simulation().ExecuteScalar(
            "select case when 1=1 then 7 when 1=0 then null else null end"));
    }

    [TestMethod]
    public void Case_AllBareNull_InWhere_Msg8133()
        => new Simulation().AssertSqlError(
            "select 1 where case when 1=1 then null else null end is null",
            8133);

    [TestMethod]
    public void Case_AllBareNull_Nested_OuterTrips_Msg8133()
        => new Simulation().AssertSqlError(
            "select case when 1=1 then case when 1=1 then null else null end else null end",
            8133);

    /// <summary>
    /// A condition real settles while compiling types a string or binary
    /// result as the arm it takes, in the unified collation, where an arm of
    /// the other string kind, a <c>max</c> sibling or a number keeps the
    /// unification; <c>COALESCE</c> settles over constants alone, or on a
    /// leading NOT NULL literal (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("case when 1 = 1 then 'ab' else 'abcde' end", "varchar", 2)]
    [DataRow("case when 1 = 0 then 'ab' else 'abcde' end", "varchar", 5)]
    [DataRow("case 1 when 1 then 'ab' else 'abcde' end", "varchar", 2)]
    [DataRow("case when 1 = 0 then 'x' when 2 = 2 then 'abc' else 'abcdef' end", "varchar", 3)]
    [DataRow("case when 1 = 1 then 'ab' else N'abcde' end", "nvarchar", 10)]
    [DataRow("case when 1 = 1 then 'ab' else cast('x' as varchar(max)) end", "varchar", -1)]
    [DataRow("case when 1 = 1 then 0x01 else 0x010203 end", "varbinary", 1)]
    [DataRow("case when 1 = 1 then 1 else 2.5 end", "numeric", 5)]
    [DataRow("iif(1 = 1, cast('a' as char(5)), cast('b' as varchar(2)))", "char", 5)]
    [DataRow("iif(null = null, 'ab', 'abcde')", "varchar", 5)]
    [DataRow("coalesce(cast('a' as char(5)), cast('b' as varchar(10)))", "char", 5)]
    [DataRow("coalesce(N'ab', 'abcde')", "nvarchar", 4)]
    [DataRow("coalesce(cast(null as varchar(1)), 'abc', 'abcdef')", "varchar", 3)]
    [DataRow("coalesce(upper('ab'), 'abcde')", "varchar", 5)]
    public void ConstantCondition_TypesTheResultAsTheSettledArm(string expression, string typeName, int maxLength)
    {
        var sim = new Simulation();
        AreEqual(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{typeName}|{maxLength}"), sim.ExecuteBatchesScalar(
            $"create view v as select {expression} as x",
            "select type_name(system_type_id) + '|' + cast(max_length as varchar(10)) from sys.columns where object_id = object_id('v')"));
    }

    [TestMethod]
    public void Coalesce_LeadingLiteral_SettlesBesideAColumn()
    {
        var sim = new Simulation();
        AreEqual("varchar|2|varchar|10|varchar|10", sim.ExecuteBatchesScalar(
            "create table t (s varchar(10) null)",
            "create view v as select coalesce('ab', s) a, coalesce(cast('ab' as varchar(2)), s) b, coalesce(upper('ab'), s) c from t",
            "select string_agg(type_name(system_type_id) + '|' + cast(max_length as varchar(10)), '|') within group (order by column_id) from sys.columns where object_id = object_id('v')"));
        CollectionAssert.AreEqual(new[] { false, true, true }, sim.ColumnNullability("select coalesce('ab', s) a, coalesce(cast('ab' as varchar(2)), s) b, coalesce(5 + 0, 7) c from t"));
    }

    /// <summary>
    /// The settled arm's type also decides whether JSON text embeds raw
    /// (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void ConstantCondition_JsonTextArm_EmbedsAsTheSettledArm()
        => AreEqual("""{"k":"[0]"}|{"k":"x"}|{"k":[1]}""", new Simulation().ExecuteScalar(
            "select json_object('k': coalesce(N'[0]', json_query('[1]'))) + '|' + json_object('k': iif(1 = 1, N'x', json_query('[1]'))) + '|' + json_object('k': case when 1 = 1 then json_query('[1]') else N'x' end)"));
}
