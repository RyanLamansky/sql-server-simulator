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
    public void Cte_RecursiveSelfReference_RaisesNotSupported()
        => Throws<NotSupportedException>(() => WithSourceTable().ExecuteNonQuery("""
            with a as (
                select 1 as n
                union all
                select n + 1 from a where n < 5
            ) select * from a
            """));

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
}
