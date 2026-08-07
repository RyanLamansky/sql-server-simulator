using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the <c>WITH name [(col, …)] AS (SELECT …)</c> CTE
/// prefix — the recursive and non-recursive forms, the statements it scopes to
/// (SELECT / INSERT / UPDATE / DELETE / MERGE), the stored bodies that may open
/// with one (view, inline TVF, cursor declaration), and the parenthesized
/// query positions that may not.
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

    /// <summary>
    /// Msg 1033 covers all five constructs its own text names — a view, an
    /// inline function, a derived table, a subquery and a CTE — not just the
    /// CTE and view bodies. Probe-confirmed against SQL Server 2025 on
    /// 2026-08-06; the derived-table, subquery and inline-function forms were
    /// accepted here before.
    /// </summary>
    [TestMethod]
    [DataRow("select * from (select id from src order by id) d")]
    [DataRow("select 1 where 1 in (select id from src order by id)")]
    [DataRow("select 1 where exists (select id from src order by id)")]
    [DataRow("select (select id from src order by id) as r")]
    public void OrderByWithoutTopOrOffset_RaisesMsg1033_InEveryNestedConstruct(string sql)
        => WithSourceTable().AssertSqlError(sql, 1033,
            "The ORDER BY clause is invalid in views, inline functions, derived tables, subqueries, and common table expressions, unless TOP, OFFSET or FOR XML is also specified.");

    [TestMethod]
    public void InlineFunctionBody_OrderByWithoutTop_RaisesMsg1033()
    {
        var sim = WithSourceTable();
        _ = sim.AssertSqlError(
            "create function dbo.f() returns table return (select id from src order by id)", 1033);
    }

    /// <summary>
    /// A companion TOP, OFFSET or FETCH clears it in each of them.
    /// </summary>
    [TestMethod]
    [DataRow("select * from (select top 1 id from src order by id) d", 1)]
    // OFFSET 0 skips nothing, so the derived table still yields every row —
    // what it clears is the rejection, not the rows.
    [DataRow("select * from (select id from src order by id offset 0 rows) d", 3)]
    // src's lowest id is 1, so the TOP 1 subquery yields it and the WHERE passes.
    [DataRow("select 1 where 1 in (select top 1 id from src order by id)", 1)]
    [DataRow("select (select top 1 id from src order by id) as r", 1)]
    public void OrderByWithTopOrOffset_IsAllowedInEveryNestedConstruct(string sql, int expectedRows)
        => AreEqual(expectedRows, WithSourceTable().ExecuteScalar($"select count(*) from ({sql}) q(c1)"));

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

    // ---- CTE prefix on a stored body ----
    //
    // A module body is its own parse unit — the statement dispatch loop's WITH
    // handling never sees it — so every body-parse site recognizes the prefix
    // itself. Probe-confirmed against SQL Server 2025 (2026-08-01): a view, an
    // inline TVF and a cursor declaration each accept one; the parenthesized
    // query positions below refuse it.

    private static Simulation WithBodySource()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table dbo.b (id int not null, grp int not null);
            insert dbo.b values (1, 10), (2, 10), (3, 20)
            """);
        return sim;
    }

    [TestMethod]
    public void CteInViewBody_ProjectsRows()
    {
        var sim = WithBodySource();
        sim.ExecuteBatches("create view dbo.v as with c as (select id, grp from dbo.b) select id, grp from c");
        AreEqual(3, sim.ExecuteScalar("select count(*) from dbo.v"));
        AreEqual(2, sim.ExecuteScalar("select count(*) from dbo.v where grp = 10"));
    }

    /// <summary>The view's own column-rename list applies over the CTE-fed projection.</summary>
    [TestMethod]
    public void CteInViewBody_WithColumnList_RenamesOutput()
    {
        var sim = WithBodySource();
        sim.ExecuteBatches("create view dbo.v (a, g) as with c as (select id, grp from dbo.b) select id, grp from c");
        AreEqual(6, sim.ExecuteScalar("select sum(a) from dbo.v"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from dbo.v where g = 20"));
    }

    [TestMethod]
    public void CteInViewBody_MultipleBindings_CascadeInsideTheBody()
    {
        var sim = WithBodySource();
        sim.ExecuteBatches("""
            create view dbo.v as
            with c1 as (select id, grp from dbo.b),
                 c2 as (select id from c1 where grp = 10)
            select id from c2
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from dbo.v"));
    }

    /// <summary>
    /// The body re-parses per invocation, so each execution owns fresh
    /// bindings — repeated and self-joined reads of a recursive-CTE view can't
    /// cross-feed one another's iteration rowset.
    /// </summary>
    [TestMethod]
    public void RecursiveCteInViewBody_Iterates()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create view dbo.v as with c as (select 1 as n union all select n + 1 from c where n < 5) select n from c");
        AreEqual(5, sim.ExecuteScalar("select count(*) from dbo.v"));
        AreEqual(15, sim.ExecuteScalar("select sum(n) from dbo.v"));
        AreEqual(25, sim.ExecuteScalar("select count(*) from dbo.v a cross join dbo.v b"));
    }

    /// <summary>Both replacement legs re-parse the body, so both accept a prefix.</summary>
    [TestMethod]
    public void CteInViewBody_SurvivesAlterAndCreateOrAlter()
    {
        var sim = WithBodySource();
        sim.ExecuteBatches(
            "create view dbo.v as with c as (select id from dbo.b) select id from c",
            "alter view dbo.v as with c as (select id from dbo.b where grp = 20) select id from c");
        AreEqual(1, sim.ExecuteScalar("select count(*) from dbo.v"));
        sim.ExecuteBatches("create or alter view dbo.v as with c as (select id from dbo.b where id > 1) select id from c");
        AreEqual(2, sim.ExecuteScalar("select count(*) from dbo.v"));
    }

    /// <summary>
    /// The trailing <c>WITH CHECK OPTION</c> still parses after a CTE-prefixed
    /// body — the body parse stops on the same post-body WITH either way.
    /// </summary>
    [TestMethod]
    public void CteInViewBody_TrailingWithCheckOption_Parses()
    {
        var sim = WithBodySource();
        sim.ExecuteBatches("create view dbo.v as with c as (select id, grp from dbo.b) select id, grp from c with check option");
        AreEqual("CASCADE", sim.ExecuteScalar("select check_option from information_schema.views where table_name = 'v'"));
    }

    /// <summary>
    /// Msg 1033 governs the view body's own ORDER BY exactly as it does an
    /// unprefixed body: rejected bare, accepted with TOP.
    /// </summary>
    [TestMethod]
    public void CteInViewBody_OrderByNeedsTop()
    {
        var sim = WithBodySource();
        _ = sim.AssertSqlError("create view dbo.v as with c as (select id from dbo.b) select id from c order by id", 1033);
        sim.ExecuteBatches("create view dbo.v as with c as (select id from dbo.b) select top 2 id from c order by id");
        AreEqual(2, sim.ExecuteScalar("select count(*) from dbo.v"));
    }

    [TestMethod]
    [DataRow("return (with c as (select id from dbo.b) select id from c)")]
    [DataRow("return with c as (select id from dbo.b) select id from c")]
    public void CteInInlineTvfBody_BothReturnForms(string body)
    {
        var sim = WithBodySource();
        sim.ExecuteBatches($"create function dbo.f() returns table as {body}");
        AreEqual(3, sim.ExecuteScalar("select count(*) from dbo.f()"));
    }

    /// <summary>
    /// The paren-less <c>RETURN</c> form's body span ends at a statement
    /// keyword only at the body's own nesting level — a SELECT inside a derived
    /// table, a subquery or a CTE definition belongs to the body.
    /// </summary>
    [TestMethod]
    [DataRow("select id from (select id from dbo.b) d", 3)]
    [DataRow("select id from dbo.b where grp in (select grp from dbo.b where grp = 10)", 2)]
    public void ParenlessInlineTvfBody_KeepsNestedSelects(string body, int expectedRows)
    {
        var sim = WithBodySource();
        sim.ExecuteBatches($"create function dbo.f() returns table as return {body}");
        AreEqual(expectedRows, sim.ExecuteScalar("select count(*) from dbo.f()"));
    }

    /// <summary>A multi-statement TVF's body statements reach the dispatch loop, prefix included.</summary>
    [TestMethod]
    public void CteInMultiStatementTvfBody_Works()
    {
        var sim = WithBodySource();
        sim.ExecuteBatches("""
            create function dbo.f() returns @r table (id int) as
            begin
                with c as (select id from dbo.b where grp = 10) insert @r select id from c;
                return
            end
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from dbo.f()"));
    }

    [TestMethod]
    public void CteInProcedureBody_Works()
    {
        var sim = WithBodySource();
        sim.ExecuteBatches("create procedure dbo.p as with c as (select id from dbo.b) select count(*) from c");
        AreEqual(3, sim.ExecuteScalar("exec dbo.p"));
    }

    [TestMethod]
    public void CteInCursorDeclaration_Fetches()
    {
        var sim = WithBodySource();
        AreEqual(20, sim.ExecuteScalar("""
            declare @g int;
            declare cur cursor for with c as (select grp from dbo.b where grp = 20) select grp from c;
            open cur;
            fetch next from cur into @g;
            close cur;
            deallocate cur;
            select @g
            """));
    }

    /// <summary>
    /// Every parenthesized query position refuses a prefix — real answers
    /// Msg 156 (followed by Msg 319 and Msg 102, of which the simulator raises
    /// the first). The scalar UDF's <c>RETURN (…)</c> is an expression in the
    /// body, so the CREATE's body bind is where it fails.
    /// </summary>
    [TestMethod]
    public void CtePrefix_InParenthesizedQueryPosition_Raises156()
    {
        var sim = WithBodySource();
        var derived = sim.AssertSqlError("select * from (with c as (select id from dbo.b) select id from c) d", 156);
        AreEqual("Incorrect syntax near the keyword 'with'.", derived.Message);
        _ = sim.AssertSqlError("select (with c as (select max(id) m from dbo.b) select m from c)", 156);
        _ = sim.AssertSqlError("select id from dbo.b where id in (with c as (select id from dbo.b) select id from c)", 156);

        _ = sim.AssertSqlError(
            "create function dbo.f() returns int as begin return (with c as (select id from dbo.b) select max(id) from c) end", 156);
    }

    /// <summary>
    /// A recursive member sees the CTE's columns under the names the
    /// <c>WITH cte (…)</c> list declares, not the anchor's own projection
    /// names — which is what lets AdventureWorks' <c>uspGetBillOfMaterials</c>
    /// family write <c>[RecursionLevel] + 1</c> against an anchor whose
    /// matching column is the unaliased literal <c>0</c>.
    /// </summary>
    [TestMethod]
    [DataRow("select c.n + 1, c.lvl + 1 from c where c.n < 4")]
    [DataRow("select n + 1, lvl + 1 from c where n < 4")]
    [DataRow("select x.n + 1, [lvl] + 1 from c x where x.n < 4")]
    public void RecursiveMember_ReadsTheDeclaredColumnNames(string recursiveMember)
        => AreEqual(
            "1:0 2:1 3:2 4:3",
            new Simulation().ExecuteScalar(
                $"""
                with c(n, lvl) as (select 1, 0 union all {recursiveMember})
                select string_agg(concat(n, ':', lvl), ' ') within group (order by n) from c
                """));

    /// <summary>
    /// Real binds the recursive member against the declared list whatever its
    /// length, so the arity mismatch is what surfaces — Msg 8158 / 8159, not a
    /// Msg 207 on the name the member read (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void RecursiveMember_ColumnListArityMismatch_ReportsTheArityError()
    {
        var sim = new Simulation();
        sim.AssertSqlError(
            "with c(a) as (select 1, 0 union all select a + 1, 0 from c where a < 3) select * from c",
            8158,
            "'c' has more columns than were specified in the column list.");
        sim.AssertSqlError(
            "with c(a, b, d) as (select 1, 0 union all select a + 1, 0 from c where a < 3) select * from c",
            8159,
            "'c' has fewer columns than were specified in the column list.");
    }
}
