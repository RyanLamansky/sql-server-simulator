using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the <c>WITH name [(col, …)] AS (SELECT …)</c>
/// non-recursive CTE prefix that scopes to one following SELECT / INSERT
/// / UPDATE / DELETE / MERGE statement. Recursive CTEs are not modeled.
/// </summary>
[TestClass]
public sealed class CommonTableExpressionTests
{
    private static Simulation WithSourceTable()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table src (id int, score int);
            insert src (id, score) values (1, 10), (2, 20), (3, 30)
            """);
        return simulation;
    }

    [TestMethod]
    public void Cte_Vanilla_ProjectsBodyRows()
    {
        var simulation = WithSourceTable();
        using var reader = simulation.ExecuteReader("with c as (select id, score from src) select id, score from c order by id");
        var rows = new List<(int id, int score)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        CollectionAssert.AreEqual(new[] { (1, 10), (2, 20), (3, 30) }, rows);
    }

    [TestMethod]
    public void Cte_RenameList_RenamesProjection()
    {
        var simulation = WithSourceTable();
        using var reader = simulation.ExecuteReader("with c (a, b) as (select id, score from src) select a, b from c order by a");
        IsTrue(reader.Read());
        AreEqual("a", reader.GetName(0));
        AreEqual("b", reader.GetName(1));
        AreEqual(1, reader.GetInt32(0));
        AreEqual(10, reader.GetInt32(1));
    }

    [TestMethod]
    public void Cte_MultipleBindings_LaterReferencesEarlier()
        => AreEqual(2, WithSourceTable().ExecuteScalar("""
            with a as (select id, score from src),
                 b as (select id from a where score > 10)
            select count(*) from b
            """));

    [TestMethod]
    public void Cte_MultipleReferences_EachReExecutesPlan()
    {
        var simulation = WithSourceTable();
        using var reader = simulation.ExecuteReader("""
            with c as (select id from src)
            select x.id, y.id
            from c x cross join c y
            where x.id < y.id
            order by x.id, y.id
            """);
        var rows = new List<(int x, int y)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        CollectionAssert.AreEqual(new[] { (1, 2), (1, 3), (2, 3) }, rows);
    }

    [TestMethod]
    public void Cte_AliasOnCteReference_QualifiesColumns()
        => AreEqual(20, WithSourceTable().ExecuteScalar("with c as (select id, score from src) select alias.score from c as alias where alias.id = 2"));

    [TestMethod]
    public void Cte_WhereReferencesCteColumns()
        => AreEqual(2, WithSourceTable().ExecuteScalar("with c as (select id, score from src) select count(*) from c where score >= 20"));

    [TestMethod]
    public void Cte_StarProjectionInBody_ExpandsBeforeRename()
    {
        var simulation = WithSourceTable();
        using var reader = simulation.ExecuteReader("with c as (select * from src) select id, score from c order by id");
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual(10, reader.GetInt32(1));
    }

    [TestMethod]
    public void Cte_UnionInBody_Works()
        => AreEqual(6, new Simulation().ExecuteScalar("""
            with c as (select 1 as v union all select 2 union all select 3)
            select sum(v) from c
            """));

    // CTE shadows a real table of the same name for the prefixed statement.
    [TestMethod]
    public void Cte_ShadowsRealTable()
    {
        var simulation = WithSourceTable();
        AreEqual(42, simulation.ExecuteScalar("with src as (select 42 as score) select score from src"));
    }

    [TestMethod]
    public void Cte_PrefixesInsertSelect_RowsLandInDestination()
    {
        var simulation = WithSourceTable();
        _ = simulation.ExecuteNonQuery("create table dst (a int)");
        AreEqual(2, simulation.ExecuteNonQuery("with c as (select score from src where score >= 20) insert dst (a) select score from c"));
        AreEqual(2, simulation.ExecuteScalar("select count(*) from dst"));
        AreEqual(50, simulation.ExecuteScalar("select sum(a) from dst"));
    }

    [TestMethod]
    public void Cte_PrefixesUpdate_FromJoin()
    {
        var simulation = WithSourceTable();
        _ = simulation.ExecuteNonQuery("create table u (id int, score int); insert u select id, score from src");
        AreEqual(2, simulation.ExecuteNonQuery("with c as (select id from src where score >= 20) update u set score = score * 10 from u inner join c on c.id = u.id"));
        AreEqual(10, simulation.ExecuteScalar("select score from u where id = 1"));
        AreEqual(200, simulation.ExecuteScalar("select score from u where id = 2"));
        AreEqual(300, simulation.ExecuteScalar("select score from u where id = 3"));
    }

    [TestMethod]
    public void Cte_PrefixesDelete_FromJoin()
    {
        var simulation = WithSourceTable();
        _ = simulation.ExecuteNonQuery("create table d (id int, score int); insert d select id, score from src");
        AreEqual(2, simulation.ExecuteNonQuery("with c as (select id from src where score >= 20) delete d from d inner join c on c.id = d.id"));
        AreEqual(1, simulation.ExecuteScalar("select count(*) from d"));
    }

    [TestMethod]
    public void Cte_DuplicateName_RaisesMsg239()
        => new Simulation().AssertSqlError("with a as (select 1 as v), a as (select 2 as v) select * from a", 239,
            "Duplicate common table expression name 'a' was specified.");

    [TestMethod]
    public void Cte_RenameTooFew_RaisesMsg8158()
        => WithSourceTable().AssertSqlError("with c (x) as (select id, score from src) select * from c", 8158,
            "'c' has more columns than were specified in the column list.");

    [TestMethod]
    public void Cte_RenameTooMany_RaisesMsg8159()
        => WithSourceTable().AssertSqlError("with c (x, y, z) as (select id, score from src) select * from c", 8159,
            "'c' has fewer columns than were specified in the column list.");

    [TestMethod]
    public void Cte_OrderByWithoutTopOrOffset_RaisesMsg1033()
        => WithSourceTable().AssertSqlError("with c as (select id from src order by id) select * from c", 1033,
            "The ORDER BY clause is invalid in views, inline functions, derived tables, subqueries, and common table expressions, unless TOP, OFFSET or FOR XML is also specified.");

    [TestMethod]
    public void Cte_OrderByWithTop_Allowed()
        => AreEqual(3, WithSourceTable().ExecuteScalar("with c as (select top 1 id from src order by id desc) select id from c"));

    [TestMethod]
    public void Cte_OrderByWithOffsetFetch_Allowed()
        => AreEqual(2, WithSourceTable().ExecuteScalar("with c as (select id from src order by id desc offset 1 rows fetch next 1 rows only) select id from c"));

    [TestMethod]
    public void Cte_Recursive_Counter()
    {
        using var reader = new Simulation().ExecuteReader("""
            with c as (
                select 1 as n
                union all
                select n + 1 from c where n < 5
            ) select n from c order by n
            """);
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, values);
    }

    [TestMethod]
    public void Cte_Recursive_HierarchyTraversal()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table emp (id int, mgr_id int, name nvarchar(20));
            insert emp values (1, null, 'CEO'), (2, 1, 'VP1'), (3, 1, 'VP2'), (4, 2, 'Dir1'), (5, 4, 'Mgr1')
            """);
        using var reader = simulation.ExecuteReader("""
            with org as (
                select id, mgr_id, name, 0 as depth from emp where mgr_id is null
                union all
                select e.id, e.mgr_id, e.name, o.depth + 1
                from emp e inner join org o on e.mgr_id = o.id
            )
            select id, name, depth from org order by depth, id
            """);
        var rows = new List<(int id, string name, int depth)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));
        CollectionAssert.AreEqual(new[] { (1, "CEO", 0), (2, "VP1", 1), (3, "VP2", 1), (4, "Dir1", 2), (5, "Mgr1", 3) }, rows);
    }

    [TestMethod]
    public void Cte_Recursive_DefaultMaxRecursion100_RaisesMsg530()
        => new Simulation().AssertSqlError("with c as (select 1 as n union all select n+1 from c) select count(*) from c", 530,
            "The statement terminated. The maximum recursion 100 has been exhausted before statement completion.");

    [TestMethod]
    public void Cte_Recursive_OptionMaxRecursionLow_LimitInMessage()
        => new Simulation().AssertSqlError("with c as (select 1 as n union all select n+1 from c) select count(*) from c option (maxrecursion 5)", 530,
            "The statement terminated. The maximum recursion 5 has been exhausted before statement completion.");

    [TestMethod]
    public void Cte_Recursive_OptionMaxRecursionZero_Unlimited()
        => AreEqual(200, new Simulation().ExecuteScalar("with c as (select 1 as n union all select n+1 from c where n < 200) select count(*) from c option (maxrecursion 0)"));

    [TestMethod]
    public void Cte_Recursive_MultipleAnchors()
    {
        using var reader = new Simulation().ExecuteReader("""
            with c as (
                select 1 as n
                union all select 100
                union all select n+1 from c where n < 3
            ) select n from c order by n
            """);
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 100 }, values);
    }

    [TestMethod]
    public void Cte_Recursive_AnchorAfterRecursive_RaisesMsg247()
        => new Simulation().AssertSqlError("with c as (select 1 as n union all select 2 union all select n+1 from c where n < 4 union all select 99) select n from c", 247,
            "An anchor member was found in the recursive part of recursive query \"c\".");

    [TestMethod]
    public void Cte_Recursive_MultipleSelfRefInOneBranch_RaisesMsg253()
        => new Simulation().AssertSqlError("with c as (select 1 as n union all select c1.n + c2.n from c c1 cross join c c2 where c1.n < 5) select n from c", 253,
            "Recursive member of a common table expression 'c' has multiple recursive references.");

    [TestMethod]
    public void Cte_Recursive_TypeMismatch_RaisesMsg240()
        => new Simulation().AssertSqlError("with c as (select cast(1 as smallint) as n union all select cast(n+1 as int) from c where n < 5) select n from c", 240,
            "Types don't match between the anchor and the recursive part in column \"n\" of recursive query \"c\".");

    [TestMethod]
    public void Cte_Recursive_UnionWithoutAll_RaisesMsg252()
        => new Simulation().AssertSqlError("with c as (select 1 as n union select n+1 from c where n < 3) select n from c", 252,
            "Recursive common table expression 'c' does not contain a top-level UNION ALL operator.");

    [TestMethod]
    public void Cte_Recursive_NoUnionAtAll_RaisesMsg252()
        => new Simulation().AssertSqlError("with c as (select n+1 from c where n < 5) select n from c", 252);

    [TestMethod]
    public void Cte_Recursive_ZeroIterations_OnlyAnchor()
    {
        // Anchor produces a row whose WHERE in the recursive part rejects
        // immediately; result is just the anchor's rows.
        using var reader = new Simulation().ExecuteReader("with c as (select 100 as n union all select n+1 from c where n < 50) select n from c");
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 100 }, values);
    }

    [TestMethod]
    public void Cte_Recursive_ConsumesParentRowsPerIteration()
    {
        // The recursive branch reads the previous-iteration rowset, NOT
        // the cumulative result-so-far. With anchor n=1 and recursive
        // `select n+1 from c where n < 3`, iteration 1 produces n=2,
        // iteration 2 produces n=3, iteration 3 produces no rows (n=3
        // doesn't satisfy n<3), so the result is {1, 2, 3}.
        using var reader = new Simulation().ExecuteReader("with c as (select 1 as n union all select n+1 from c where n < 3) select n from c order by n");
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, values);
    }

    [TestMethod]
    public void Cte_Recursive_AggregateOverFinalResult()
        => AreEqual(15, new Simulation().ExecuteScalar("with c as (select 1 as n union all select n+1 from c where n < 5) select sum(n) from c"));

    [TestMethod]
    public void Cte_Recursive_StringConcatenation()
    {
        using var reader = new Simulation().ExecuteReader("""
            with path as (
                select cast('root' as varchar(100)) as p, 0 as depth
                union all
                select cast(p + '/' + cast(depth + 1 as varchar(10)) as varchar(100)), depth + 1
                from path where depth < 3
            )
            select p from path order by depth
            """);
        var values = new List<string>();
        while (reader.Read())
            values.Add(reader.GetString(0));
        CollectionAssert.AreEqual(new[] { "root", "root/1", "root/1/2", "root/1/2/3" }, values);
    }

    [TestMethod]
    public void Cte_BindingClearsBetweenStatements()
    {
        var simulation = WithSourceTable();
        // First statement uses a CTE binding named 'c'; the second statement
        // should NOT see 'c' anymore — it must fail with a missing-table error.
        _ = simulation.ExecuteNonQuery("with c as (select id from src) select count(*) from c");
        _ = Throws<DbException>(() => simulation.ExecuteNonQuery("select count(*) from c"));
    }

    [TestMethod]
    public void Cte_WithParameter()
    {
        var simulation = WithSourceTable();
        using var connection = simulation.CreateOpenConnection();
        var command = connection.CreateCommand("with c as (select id, score from src where id >= @minId) select count(*) from c", ("minId", 2));
        AreEqual(2, command.ExecuteScalar());
    }

    // CTE is reachable from a scalar subquery inside the prefixed statement
    // (the binding lives in ParserContext.CteBindings for the whole statement).
    [TestMethod]
    public void Cte_ReachableFromScalarSubqueryInProjection()
    {
        var simulation = WithSourceTable();
        using var reader = simulation.ExecuteReader("""
            with c as (select id, score from src)
            select id, (select max(score) from c) as max_score
            from c
            order by id
            """);
        var rows = new List<(int id, int max)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        CollectionAssert.AreEqual(new[] { (1, 30), (2, 30), (3, 30) }, rows);
    }

    /// <summary>
    /// SQL Server forbids a set of constructs in a recursive CTE's recursive
    /// member. All probe-confirmed verbatim against SQL Server 2025
    /// (2026-07-31); previously the simulator accepted every one, which is the
    /// dangerous direction — the query runs here and fails in production.
    /// </summary>
    [TestMethod]
    [DataRow("select distinct n+1 from c where n < 3", 460, "DISTINCT operator is not allowed in the recursive part of a recursive common table expression 'c'.")]
    [DataRow("select top 1 n+1 from c where n < 3", 461, "The TOP or OFFSET operator is not allowed in the recursive part of a recursive common table expression 'c'.")]
    [DataRow("select n+1 from c where n < 3 group by n", 467, "GROUP BY, HAVING, or aggregate functions are not allowed in the recursive part of a recursive common table expression 'c'.")]
    [DataRow("select max(n)+1 from c where n < 3", 467, "GROUP BY, HAVING, or aggregate functions are not allowed in the recursive part of a recursive common table expression 'c'.")]
    public void RecursiveMember_RejectsForbiddenConstructs(string recursiveMember, int error, string message)
        => new Simulation().AssertSqlError(
            $"with c as (select 1 n union all {recursiveMember}) select count(*) from c", error, message);

    /// <summary>
    /// An outer join in the recursive member is Msg 462. Driven off a table so
    /// the join has a second source that isn't the CTE — two CTE references
    /// would be Msg 253 instead.
    /// </summary>
    [TestMethod]
    public void RecursiveMember_RejectsOuterJoin()
        => new Simulation().AssertSqlError(
            """
            create table rt (id int, parent int);
            insert rt values (1, null), (2, 1);
            with c as (
                select id, parent from rt where parent is null
                union all
                select r.id, r.parent from rt r left join c on c.id = r.parent)
            select count(*) from c
            """,
            462,
            "Outer join is not allowed in the recursive part of a recursive common table expression 'c'.");

    /// <summary>
    /// The restriction covers the recursive member's whole text, so a
    /// construct inside a nested subquery or derived table counts too —
    /// probe-confirmed, and the reason these are recorded at their parse sites
    /// rather than read off the member's own plan.
    /// </summary>
    [TestMethod]
    [DataRow("select n+1 from c where n in (select distinct 1 x)", 460)]
    [DataRow("select n+1 from c where n < (select top 1 3 x)", 461)]
    [DataRow("select n+1 from c where n < (select max(v) from (select 3 v) t)", 467)]
    [DataRow("select n+1 from c cross join (select distinct 1 y) z where n < 3", 460)]
    public void RecursiveMember_RestrictionReachesNestedScopes(string recursiveMember, int error)
        => _ = new Simulation().AssertSqlError(
            $"with c as (select 1 n union all {recursiveMember}) select count(*) from c", error);

    /// <summary>The anchor member has no such restrictions — only the recursive one does.</summary>
    [TestMethod]
    public void AnchorMember_AllowsTheSameConstructs()
    {
        var sim = new Simulation();
        AreEqual(3, sim.ExecuteScalar("with c as (select distinct 1 n union all select n+1 from c where n < 3) select count(*) from c"));
        AreEqual(3, sim.ExecuteScalar("with c as (select top 1 1 n union all select n+1 from c where n < 3) select count(*) from c"));
        AreEqual(3, sim.ExecuteScalar("with c as (select max(n) n from (select 1 n) t union all select n+1 from c where n < 3) select count(*) from c"));
    }

    /// <summary>A non-recursive CTE keeps every construct — the restriction is recursion-specific.</summary>
    [TestMethod]
    public void NonRecursiveCte_AllowsTheSameConstructs()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table nr (id int); insert nr values (1), (1), (2)");
        AreEqual(2, sim.ExecuteScalar("with c as (select distinct id from nr) select count(*) from c"));
        AreEqual(1, sim.ExecuteScalar("with c as (select top 1 id from nr) select count(*) from c"));
        AreEqual(2, sim.ExecuteScalar("with c as (select id from nr group by id) select count(*) from c"));
    }
}
