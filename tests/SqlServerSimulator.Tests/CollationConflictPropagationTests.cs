using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Coverage for what happens to a collation conflict a producing operator
/// can't settle: which operators report it on the spot (Msg 457, for the
/// code-page-bearing <c>varchar</c> family only), which let it travel as SQL
/// Server's <c>No collation</c> label, and which of the operations downstream
/// then report it — an output column (Msg 451), an operation that needs a
/// definite collation (Msg 4191), <c>DISTINCT</c> / <c>CONVERT</c> /
/// <c>COLLATE</c> (Msg 446), a <c>varchar</c> implicit conversion (Msg 456),
/// or a set operator that has to compare (Msg 5335).
/// Companion to <see cref="CollationDeclaredColumnTests"/>, which covers the
/// per-column declaration and the conflicts settled at the operator itself.
/// </summary>
[TestClass]
public sealed class CollationConflictPropagationTests
{
    private const string ConflictPair =
        "\"Latin1_General_CS_AS\" and \"Latin1_General_CI_AS\"";

    /// <summary>
    /// A conflicted <c>varchar</c> result can't be materialized — the family
    /// carries a code page, and the engine doesn't know which — so each of the
    /// four string-producing operators reports Msg 457 where it stands, naming
    /// itself. Real spells string <c>+</c> as <c>add</c>, <c>||</c> as
    /// <c>concat</c>, and upper-cases the set operator.
    /// </summary>
    [TestMethod]
    [DataRow("select a.s + b.s from a, b", "add")]
    [DataRow("select a.s || b.s from a, b", "concat")]
    [DataRow("select case when 1 = 1 then a.s else b.s end from a, b", "CASE")]
    [DataRow("select s from a union all select s from b", "UNION ALL")]
    public void VarcharConflict_ReportedAtTheOperator_Msg457(string sql, string operatorName)
    {
        var sim = SeededCrossCollationTables();
        sim.AssertSqlError(
            sql,
            457,
            $"Implicit conversion of varchar value to varchar cannot be performed because the collation of the value is unresolved due to a collation conflict between {ConflictPair} in {operatorName} operator.");
    }

    /// <summary>
    /// The same four operators over <c>nvarchar</c> instead let the conflict
    /// travel — UTF-16 needs no code page — so nothing reports until the select
    /// list has to name an output collation, which is Msg 451 with the clause
    /// and the slot's 1-based ordinal.
    /// </summary>
    [TestMethod]
    [DataRow("select a.n + b.n from a, b", "add")]
    [DataRow("select a.n || b.n from a, b", "concat")]
    [DataRow("select case when 1 = 1 then a.n else b.n end from a, b", "CASE")]
    [DataRow("select n from a union all select n from b", "UNION ALL")]
    public void NvarcharConflict_ReportedAtTheOutputColumn_Msg451(string sql, string operatorName)
    {
        var sim = SeededCrossCollationTables();
        sim.AssertSqlError(sql, 451, OutputColumnMessage(operatorName, "SELECT", 1));
    }

    /// <summary>
    /// A mixed <c>varchar</c> + <c>nvarchar</c> pair promotes to
    /// <c>nvarchar</c>, and the promoted result is what decides the message —
    /// so the pair takes Msg 451 whichever side the <c>nvarchar</c> is on.
    /// </summary>
    [TestMethod]
    [DataRow("select a.s + b.n from a, b", "add")]
    [DataRow("select a.n + b.s from a, b", "add")]
    [DataRow("select a.s || b.n from a, b", "concat")]
    [DataRow("select case when 1 = 1 then a.s else b.n end from a, b", "CASE")]
    [DataRow("select s from a union all select n from b", "UNION ALL")]
    public void MixedFamilyConflict_PromotesToNvarchar_Msg451(string sql, string operatorName)
    {
        var sim = SeededCrossCollationTables();
        sim.AssertSqlError(sql, 451, OutputColumnMessage(operatorName, "SELECT", 1));
    }

    /// <summary>
    /// Two explicit <c>COLLATE</c> postfixes are the operator's own Msg 468
    /// whatever the result family — the same rank split every comparison site
    /// makes, and the one case that never produces a travelling conflict.
    /// </summary>
    [TestMethod]
    [DataRow("select (a.s collate Latin1_General_CI_AS) + (b.s collate Latin1_General_CS_AS) from a, b", "add")]
    [DataRow("select (a.n collate Latin1_General_CI_AS) + (b.n collate Latin1_General_CS_AS) from a, b", "add")]
    [DataRow("select (a.n collate Latin1_General_CI_AS) || (b.n collate Latin1_General_CS_AS) from a, b", "concat")]
    [DataRow("select case when 1 = 1 then a.n collate Latin1_General_CI_AS else b.n collate Latin1_General_CS_AS end from a, b", "CASE")]
    [DataRow("select n collate Latin1_General_CI_AS from a union all select n collate Latin1_General_CS_AS from b", "UNION ALL")]
    public void TwoExplicitPostfixes_RaiseMsg468(string sql, string operatorName)
    {
        var sim = SeededCrossCollationTables();
        sim.AssertSqlError(
            sql,
            468,
            $"Cannot resolve the collation conflict between {ConflictPair} in the {operatorName} operation.");
    }

    /// <summary>
    /// The three clauses that name an output slot, and the ordinal each
    /// reports: <c>SELECT</c> and <c>ORDER BY</c> count from their own first
    /// term, <c>GROUP BY</c> from 2 — the grouped projection real builds
    /// carries one column ahead of the keys. A set operator's columns number
    /// by output position like the select list's.
    /// </summary>
    [TestMethod]
    [DataRow("select a.i, a.n + b.n from a, b", "add", "SELECT", 2)]
    [DataRow("select a.i from a, b order by a.n + b.n", "add", "ORDER BY", 1)]
    [DataRow("select count(*) from a, b group by a.n + b.n", "add", "GROUP BY", 2)]
    [DataRow("select i, n from a union all select i, n from b", "UNION ALL", "SELECT", 2)]
    [DataRow("select i, i, n from a union all select i, i, n from b", "UNION ALL", "SELECT", 3)]
    public void OutputSlot_NamesClauseAndOrdinal(string sql, string operatorName, string clause, int ordinal)
    {
        var sim = SeededCrossCollationTables();
        sim.AssertSqlError(sql, 451, OutputColumnMessage(operatorName, clause, ordinal));
    }

    /// <summary>
    /// An <c>ORDER BY</c> that names the conflicted projection column — by
    /// ordinal or by alias — reports as the <c>ORDER BY</c> slot, because real
    /// settles the select list last of all. With no <c>ORDER BY</c> reaching
    /// it, the same projection reports its own slot.
    /// </summary>
    [TestMethod]
    [DataRow("select a.i, a.n + b.n as c from a, b order by 2", "ORDER BY", 1)]
    [DataRow("select a.n + b.n as c, a.i from a, b order by c", "ORDER BY", 1)]
    [DataRow("select a.n + b.n as c, a.i from a, b order by a.i", "SELECT", 1)]
    public void OrderByReferencingTheProjection_ReportsTheOrderBySlot(string sql, string clause, int ordinal)
    {
        var sim = SeededCrossCollationTables();
        sim.AssertSqlError(sql, 451, OutputColumnMessage("add", clause, ordinal));
    }

    /// <summary>
    /// A <c>WHERE</c> predicate's own Msg 4191 also beats the select list's
    /// slot, and a <c>GROUP BY</c> term's Msg 451 does too.
    /// </summary>
    [TestMethod]
    public void OtherClausesReportAheadOfTheSelectList()
    {
        var sim = SeededCrossCollationTables();
        sim.AssertSqlError(
            "select a.n + b.n from a, b where len(concat(a.n, b.n)) > 0",
            4191,
            "Cannot resolve collation conflict for len operation.");
        sim.AssertSqlError(
            "select concat(a.n, b.n) from a, b group by concat(a.n, b.n)",
            451,
            OutputColumnMessage("concat", "GROUP BY", 2));
    }

    /// <summary>
    /// Where an assignment target supplies the collation, a travelling
    /// conflict settles against it and the statement runs. The
    /// <c>varchar</c> operators still report — they never let the conflict
    /// travel in the first place — while <c>CONCAT</c>, which does, reports
    /// the implicit conversion instead (Msg 456, naming the producing
    /// operator rather than the consuming one).
    /// </summary>
    [TestMethod]
    public void AssignmentTargetSettlesTheConflict()
    {
        var sim = SeededCrossCollationTables();
        _ = sim.ExecuteNonQuery("create table dest (v varchar(50), nv nvarchar(50))");

        AreEqual(1, sim.ExecuteNonQuery("insert dest (nv) select a.n + b.n from a, b"));
        AreEqual(1, sim.ExecuteNonQuery("insert dest (nv) select concat(a.n, b.n) from a, b"));
        AreEqual(1, sim.ExecuteNonQuery("insert dest (nv) select case when 1 = 1 then a.n else b.n end from a, b"));
        AreEqual(2, sim.ExecuteNonQuery("insert dest (nv) select n from a union all select n from b"));
        AreEqual("ok", sim.ExecuteScalar("declare @v nvarchar(50); select @v = a.n + b.n from a, b; select 'ok'"));

        sim.AssertSqlError(
            "insert dest (v) select a.s + b.s from a, b",
            457,
            $"Implicit conversion of varchar value to varchar cannot be performed because the collation of the value is unresolved due to a collation conflict between {ConflictPair} in add operator.");
        sim.AssertSqlError(
            "insert dest (v) select concat(a.s, b.s) from a, b",
            456,
            $"Implicit conversion of varchar value to varchar cannot be performed because the resulting collation is unresolved due to collation conflict between {ConflictPair} in concat operator.");
    }

    /// <summary>
    /// The same split at an <c>UPDATE</c>'s <c>SET</c> value, which binds on a
    /// path of its own: the Unicode conflict settles against the target column,
    /// the <c>varchar</c> one is Msg 456.
    /// </summary>
    [TestMethod]
    public void UpdateSetValueSettlesOrRefuses()
    {
        var sim = SeededCrossCollationTables();
        AreEqual(1, sim.ExecuteNonQuery("update a set n = concat(a.n, b.n) from a, b"));
        sim.AssertSqlError(
            "update a set s = concat(a.s, b.s) from a, b",
            456,
            $"Implicit conversion of varchar value to varchar cannot be performed because the resulting collation is unresolved due to collation conflict between {ConflictPair} in concat operator.");
    }

    /// <summary>
    /// <c>SELECT … INTO</c> is not an assignment in that sense — it
    /// materializes a column of its own, so it names the output collation and
    /// reports Msg 451.
    /// </summary>
    [TestMethod]
    public void SelectInto_NamesItsOwnOutputCollation()
    {
        var sim = SeededCrossCollationTables();
        sim.AssertSqlError(
            "select a.n + b.n as c into made from a, b",
            451,
            OutputColumnMessage("add", "SELECT", 1));
    }

    /// <summary>
    /// An <c>EXISTS</c> body's projection is never materialized, so a conflict
    /// in it settles into nothing and the predicate answers normally — where
    /// the same select list at statement level raises.
    /// </summary>
    [TestMethod]
    [DataRow("select 1 where exists (select concat(a.n, b.n) from a, b)")]
    [DataRow("select 1 where exists (select a.n + b.n from a, b)")]
    [DataRow("select 1 where exists (select concat(a.s, b.s) from a, b)")]
    public void ExistsDiscardsItsProjection(string sql)
    {
        var sim = SeededCrossCollationTables();
        AreEqual(1, sim.ExecuteScalar(sql));
    }

    /// <summary>
    /// Every operation that needs a definite collation to do its work reports
    /// <b>Msg 4191</b> naming itself and nothing else — not the conflicting
    /// pair, and not the operator that produced the conflict. Real's own odd
    /// one out is in here: <c>TRIM</c> reports <c>Trim</c> capitalized where
    /// every sibling is lower-case.
    /// </summary>
    [TestMethod]
    [DataRow("select len(concat(a.n, b.n)) from a, b", "len")]
    [DataRow("select upper(concat(a.n, b.n)) from a, b", "upper")]
    [DataRow("select lower(concat(a.n, b.n)) from a, b", "lower")]
    [DataRow("select ltrim(concat(a.n, b.n)) from a, b", "ltrim")]
    [DataRow("select rtrim(concat(a.n, b.n)) from a, b", "rtrim")]
    [DataRow("select trim(concat(a.n, b.n)) from a, b", "Trim")]
    [DataRow("select substring(concat(a.n, b.n), 1, 1) from a, b", "substring")]
    [DataRow("select charindex(concat(a.n, b.n), N'xyz') from a, b", "charindex")]
    [DataRow("select charindex(N'x', concat(a.n, b.n)) from a, b", "charindex")]
    [DataRow("select patindex(N'%x%', concat(a.n, b.n)) from a, b", "patindex")]
    [DataRow("select replace(concat(a.n, b.n), N'x', N'z') from a, b", "replace")]
    [DataRow("select reverse(concat(a.n, b.n)) from a, b", "reverse")]
    [DataRow("select stuff(concat(a.n, b.n), 1, 1, N'z') from a, b", "stuff")]
    [DataRow("select left(concat(a.n, b.n), 1) from a, b", "left")]
    [DataRow("select right(concat(a.n, b.n), 1) from a, b", "right")]
    [DataRow("select soundex(concat(a.n, b.n)) from a, b", "soundex")]
    [DataRow("select difference(concat(a.n, b.n), N'x') from a, b", "difference")]
    [DataRow("select translate(concat(a.n, b.n), N'x', N'y') from a, b", "translate")]
    [DataRow("select unicode(concat(a.n, b.n)) from a, b", "unicode")]
    [DataRow("select max(concat(a.n, b.n)) from a, b", "max")]
    [DataRow("select min(concat(a.n, b.n)) from a, b", "min")]
    [DataRow("select string_agg(concat(a.n, b.n), N',') from a, b", "string_agg")]
    public void ConsumingOperationReportsMsg4191(string sql, string operationName)
    {
        var sim = SeededCrossCollationTables();
        sim.AssertSqlError(sql, 4191, $"Cannot resolve collation conflict for {operationName} operation.");
    }

    /// <summary>
    /// A comparison names itself the same way, using the spelled-out operator
    /// vocabulary Msg 468 uses. <c>IN</c> and <c>BETWEEN</c> report through the
    /// comparison they desugar to.
    /// </summary>
    [TestMethod]
    [DataRow("select 1 from a, b where concat(a.n, b.n) = N'x'", "equal to")]
    [DataRow("select 1 from a, b where concat(a.n, b.n) <> N'x'", "not equal to")]
    [DataRow("select 1 from a, b where concat(a.n, b.n) < N'x'", "less than")]
    [DataRow("select 1 from a, b where concat(a.n, b.n) >= N'x'", "greater than or equal to")]
    [DataRow("select 1 from a, b where concat(a.n, b.n) in (N'x', N'y')", "equal to")]
    [DataRow("select 1 from a, b where concat(a.n, b.n) between N'a' and N'z'", "greater than or equal to")]
    [DataRow("select 1 from a, b where concat(a.n, b.n) like N'x%'", "like")]
    public void ComparisonReportsMsg4191(string sql, string operationName)
    {
        var sim = SeededCrossCollationTables();
        sim.AssertSqlError(sql, 4191, $"Cannot resolve collation conflict for {operationName} operation.");
    }

    /// <summary>
    /// A conflict a predicate consumes reports from the predicate wherever the
    /// predicate lives — a <c>JOIN</c>'s <c>ON</c>, a <c>HAVING</c>, an
    /// <c>IN (SELECT …)</c> — not from the projection that produced it.
    /// </summary>
    [TestMethod]
    [DataRow("select 1 from a join b on concat(a.n, b.n) = b.n", "equal to")]
    [DataRow("select count(*) from a, b group by a.i having max(concat(a.n, b.n)) > N'a'", "max")]
    public void PredicateSitesReportMsg4191(string sql, string operationName)
    {
        var sim = SeededCrossCollationTables();
        sim.AssertSqlError(sql, 4191, $"Cannot resolve collation conflict for {operationName} operation.");
    }

    /// <summary>
    /// The complement: operations that only move characters around, or never
    /// look at collation at all, pass the conflict through. What they hand on
    /// still reports at the output column, and a wrapping consumer still
    /// reports its own Msg 4191 — so the marker travels arbitrarily deep.
    /// </summary>
    [TestMethod]
    public void PropagatingOperationsCarryTheConflictOnward()
    {
        var sim = SeededCrossCollationTables();
        sim.AssertSqlError("select replicate(concat(a.n, b.n), 2) from a, b", 451, OutputColumnMessage("concat", "SELECT", 1));
        sim.AssertSqlError("select string_escape(concat(a.n, b.n), 'json') from a, b", 451, OutputColumnMessage("concat", "SELECT", 1));
        sim.AssertSqlError("select cast(concat(a.n, b.n) as nvarchar(10)) from a, b", 451, OutputColumnMessage("concat", "SELECT", 1));
        sim.AssertSqlError("select isnull(concat(a.n, b.n), N'z') from a, b", 451, OutputColumnMessage("concat", "SELECT", 1));
        sim.AssertSqlError("select concat(a.n, b.n) + N'x' from a, b", 451, OutputColumnMessage("concat", "SELECT", 1));
        sim.AssertSqlError(
            "select len(cast(concat(a.n, b.n) as nvarchar(10))) from a, b",
            4191,
            "Cannot resolve collation conflict for len operation.");
        sim.AssertSqlError(
            "select len(concat(a.n, b.n) + N'x') from a, b",
            4191,
            "Cannot resolve collation conflict for len operation.");
        sim.AssertSqlError(
            "select len(replicate(concat(a.n, b.n), 2)) from a, b",
            4191,
            "Cannot resolve collation conflict for len operation.");
    }

    /// <summary>
    /// Operations with no collation to demand answer normally over a
    /// conflicted value.
    /// </summary>
    [TestMethod]
    public void CollationBlindOperationsSucceed()
    {
        var sim = SeededCrossCollationTables();
        AreEqual(4, sim.ExecuteScalar("select datalength(concat(a.n, b.n)) from a, b"));
        AreEqual(1, sim.ExecuteScalar("select count(concat(a.n, b.n)) from a, b"));
        IsNotNull(sim.ExecuteScalar("select hashbytes('MD5', concat(a.n, b.n)) from a, b"));
    }

    /// <summary>
    /// <c>DISTINCT</c> compares the values it dedups, so it reports the
    /// conflict — as Msg 446 State 11, which names the producing operator and
    /// <c>DISTINCT</c> together rather than taking either the output-column or
    /// the consuming-operation wording. Both string families, and the
    /// aggregate's own <c>DISTINCT</c> too.
    /// </summary>
    [TestMethod]
    [DataRow("select distinct concat(a.n, b.n) from a, b")]
    [DataRow("select distinct concat(a.s, b.s) from a, b")]
    [DataRow("select count(distinct concat(a.n, b.n)) from a, b")]
    public void Distinct_RaisesMsg446State11(string sql)
    {
        var sim = SeededCrossCollationTables();
        var ex = sim.AssertSqlError(sql, 446);
        AreEqual($"Cannot resolve collation conflict between {ConflictPair} in concat operator for DISTINCT operation.", ex.Message);
        AreEqual((byte)11, ex.State);
    }

    /// <summary>
    /// A conversion never resolves a collation conflict — it inherits one. A
    /// Unicode target carries it onward; a <c>varchar</c> target has to name a
    /// code page and can't, which is Msg 446 State 20 (spelled <c>CONVERT</c>
    /// for a <c>CAST</c> too).
    /// </summary>
    [TestMethod]
    [DataRow("select cast(concat(a.n, b.n) as varchar(10)) from a, b")]
    [DataRow("select convert(varchar(10), concat(a.n, b.n)) from a, b")]
    [DataRow("select cast(concat(a.s, b.s) as varchar(10)) from a, b")]
    public void ConvertToVarchar_RaisesMsg446State20(string sql)
    {
        var sim = SeededCrossCollationTables();
        var ex = sim.AssertSqlError(sql, 446);
        AreEqual($"Cannot resolve collation conflict between {ConflictPair} in concat operator for CONVERT operation.", ex.Message);
        AreEqual((byte)20, ex.State);
    }

    /// <summary>
    /// A <c>COLLATE</c> postfix settles a travelling conflict outright for the
    /// Unicode family — but a <c>varchar</c> whose collation never resolved has
    /// bytes in no known code page and can't be re-collated at all, which is
    /// Msg 446 State 6.
    /// </summary>
    [TestMethod]
    public void CollatePostfix_SettlesNvarcharAndRefusesVarchar()
    {
        var sim = SeededCrossCollationTables();
        AreEqual("xy", sim.ExecuteScalar("select concat(a.n, b.n) collate Latin1_General_CI_AS from a, b"));
        var ex = sim.AssertSqlError("select concat(a.s, b.s) collate Latin1_General_CI_AS from a, b", 446);
        AreEqual($"Cannot resolve collation conflict between {ConflictPair} in concat operator for COLLATE operation.", ex.Message);
        AreEqual((byte)6, ex.State);
    }

    /// <summary>
    /// <c>UNION</c> / <c>INTERSECT</c> / <c>EXCEPT</c> dedup, and a value with
    /// no collation has no comparison to dedup by — so a branch that arrived
    /// already unresolved is Msg 5335 rather than the Msg 468 a
    /// freshly-conflicting pair of branches takes.
    /// </summary>
    [TestMethod]
    public void SetOpOverAnUnresolvedBranch_RaisesMsg5335()
    {
        var sim = SeededCrossCollationTables();
        _ = sim.ExecuteNonQuery("create table dest (nv nvarchar(50))");
        sim.AssertSqlError(
            "insert dest (nv) select concat(a.n, b.n) from a, b union select n from a",
            5335,
            "The data type nvarchar cannot be used as an operand to the UNION, INTERSECT or EXCEPT operators because it is not comparable.");
        _ = sim.AssertSqlError("select n from a union select n from b", 468);
    }

    /// <summary>
    /// Every one of these binds while compiling, so an empty rowset reports
    /// exactly what a populated one does, and a module whose body carries the
    /// conflict fails at <c>CREATE</c> with the error attributed to it.
    /// </summary>
    [TestMethod]
    public void ConflictsBindAtCompileTime()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table a (s varchar(20) collate Latin1_General_CI_AS, n nvarchar(20) collate Latin1_General_CI_AS, i int);
            create table b (s varchar(20) collate Latin1_General_CS_AS, n nvarchar(20) collate Latin1_General_CS_AS, i int)
            """);
        sim.AssertSqlError(
            "select 1 from a, b where len(concat(a.n, b.n)) > 0",
            4191,
            "Cannot resolve collation conflict for len operation.");
        sim.AssertSqlError("select a.n + b.n from a, b", 451, OutputColumnMessage("add", "SELECT", 1));

        var consumerError = sim.AssertSqlError("create procedure p1 as select 1 from a, b where len(concat(a.n, b.n)) > 0", 4191);
        AreEqual("p1", consumerError.Procedure);
        var outputError = sim.AssertSqlError("create procedure p2 as select a.n + b.n from a, b", 451);
        AreEqual("p2", outputError.Procedure);
    }

    /// <summary>
    /// The conflict-free paths stay clean: matching collations, a literal
    /// operand (coercible-default, which yields), an explicit <c>COLLATE</c> on
    /// one side, and <c>ISNULL</c> — which takes its first argument's collation
    /// outright rather than unifying — all bind and run.
    /// </summary>
    [TestMethod]
    [DataRow("select a.n + a.n from a")]
    [DataRow("select a.n + N'x' from a")]
    [DataRow("select a.n + (b.n collate Latin1_General_CI_AS) from a, b")]
    [DataRow("select isnull(a.n, b.n) from a, b")]
    [DataRow("select len(a.n + N'x') from a")]
    public void ResolvableOperandsSucceed(string sql)
    {
        var sim = SeededCrossCollationTables();
        using var reader = sim.ExecuteReader(sql);
        while (reader.Read())
        {
            // Draining is the assertion — the statement must bind and run.
        }
    }

    private static string OutputColumnMessage(string operatorName, string clause, int ordinal) =>
        $"Cannot resolve collation conflict between {ConflictPair} in {operatorName} operator occurring in {clause} statement column {ordinal}.";

    private static Simulation SeededCrossCollationTables()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table a (s varchar(20) collate Latin1_General_CI_AS, n nvarchar(20) collate Latin1_General_CI_AS, i int);
            create table b (s varchar(20) collate Latin1_General_CS_AS, n nvarchar(20) collate Latin1_General_CS_AS, i int);
            insert a values ('x', N'x', 1); insert b values ('y', N'y', 1)
            """);
        return sim;
    }
}
