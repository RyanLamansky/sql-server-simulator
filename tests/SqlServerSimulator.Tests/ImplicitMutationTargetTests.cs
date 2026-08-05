using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// An <c>UPDATE</c> / <c>DELETE</c> whose <c>FROM</c> clause introduces no
/// source for the target it names. Real binds the target as an additional,
/// implicitly cross-joined source — <c>UPDATE u SET id = d.n FROM (SELECT 1 AS
/// n) d</c> is <c>u CROSS JOIN d</c> — rather than refusing the statement, and
/// <c>u</c>'s own columns stay in scope for the SET list, the WHERE, the OUTPUT
/// clause and any correlated subquery.
/// </summary>
/// <remarks>
/// Every shape here was run on both engines against SQL Server 2025 on
/// 2026-08-05, values and rows-affected compared. The multiplication a cross
/// join implies is in rows <em>examined</em>, not rows affected: each target row
/// is written once however many join rows it meets.
/// </remarks>
[TestClass]
public sealed class ImplicitMutationTargetTests
{
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table u (id int, s varchar(10));
            create table u2 (id int, s varchar(10));
            insert u values (0, 'x');
            insert u2 values (7, 'y');
            """);
        return sim;
    }

    /// <summary>A parenthesized derived table as the only FROM source, in the plain and WHERE-bearing forms.</summary>
    [TestMethod]
    public void ADerivedTableAloneCrossJoinsTheTarget()
    {
        var sim = Seeded();
        AreEqual(1, sim.ExecuteNonQuery("update u set id = d.n from (select 1 as n) d"));
        AreEqual(1, sim.ExecuteScalar("select id from u"));
        AreEqual(1, sim.ExecuteNonQuery("update u set id = d.n from (select 3 as n) d where u.id > 0"));
        AreEqual(3, sim.ExecuteScalar("select id from u"));
        AreEqual(1, sim.ExecuteNonQuery("update u set s = 'z' from (select 1 as n) d, (select 2 as m) e"));
        AreEqual("z", sim.ExecuteScalar("select s from u"));
    }

    /// <summary>The FROM may be any shape at all — a join, an outer join, an APPLY, or a plain other table.</summary>
    [TestMethod]
    public void TheFromClauseMayBeAnyShape()
    {
        var sim = Seeded();
        AreEqual(1, sim.ExecuteNonQuery("update u set id = d.n from (select 8 as n) d join u2 on 1 = 1"));
        AreEqual(8, sim.ExecuteScalar("select id from u"));
        AreEqual(1, sim.ExecuteNonQuery("update u set id = c.n from (select 9 as n) d cross apply (select d.n as n) c"));
        AreEqual(9, sim.ExecuteScalar("select id from u"));
        AreEqual(1, sim.ExecuteNonQuery("update u set id = 80 from (select 1 as n) d left join u2 on 1 = 0"));
        AreEqual(80, sim.ExecuteScalar("select id from u"));
        AreEqual(1, sim.ExecuteNonQuery("update u set id = u2.id from u2"));
        AreEqual(7, sim.ExecuteScalar("select id from u"));
        AreEqual(0, sim.ExecuteNonQuery("update u set id = 50 from u2 where u.s = u2.s"));
    }

    /// <summary>A schema-qualified target and a <c>#temp</c> one bind the same way.</summary>
    [TestMethod]
    public void TheTargetMayBeQualifiedOrTemporary()
    {
        var sim = Seeded();
        AreEqual(1, sim.ExecuteNonQuery("update dbo.u set id = d.n from (select 6 as n) d"));
        AreEqual(6, sim.ExecuteScalar("select id from u"));

        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("create table #u (id int); insert #u values (0)").ExecuteNonQuery();
        AreEqual(1, connection.CreateCommand("update #u set id = d.n from (select 1 as n) d").ExecuteNonQuery());
        AreEqual(1, connection.CreateCommand("select id from #u").ExecuteScalar());
        AreEqual(1, connection.CreateCommand("delete #u from (select 1 as n) d where #u.id = d.n").ExecuteNonQuery());
        AreEqual(0, connection.CreateCommand("select count(*) from #u").ExecuteScalar());
    }

    /// <summary>Both DELETE spellings reach the same binding, and the WHERE joins the two sides.</summary>
    [TestMethod]
    public void BothDeleteSpellingsBindTheTarget()
    {
        var sim = Seeded();
        AreEqual(0, sim.ExecuteNonQuery("delete u from (select 6 as n) d where u.id = d.n"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from u"));
        AreEqual(1, sim.ExecuteNonQuery("delete from u from (select 0 as n) d where u.id = d.n"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from u"));
    }

    /// <summary>The target's columns are in scope for OUTPUT and for a correlated subquery in the WHERE.</summary>
    [TestMethod]
    public void TheTargetsColumnsStayInScope()
    {
        var sim = Seeded();
        using var connection = sim.CreateOpenConnection();
        using (var reader = connection
            .CreateCommand("update u set id = 60 output deleted.id, inserted.id from (select 1 as n) d").ExecuteReader())
        {
            IsTrue(reader.Read());
            AreEqual(0, reader.GetInt32(0));
            AreEqual(60, reader.GetInt32(1));
            IsFalse(reader.Read());
        }

        AreEqual(0, sim.ExecuteNonQuery(
            "update u set id = 70 from (select 1 as n) d where exists (select 1 from u2 where u2.s = u.s)"));
        AreEqual(60, sim.ExecuteScalar("select id from u"));
    }

    /// <summary>
    /// The cross join multiplies rows examined, not rows affected: two target
    /// rows against a one-row FROM report two, and each is written once.
    /// </summary>
    [TestMethod]
    public void EachTargetRowIsWrittenOnce()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("insert u values (0, 'x')");
        AreEqual(2, sim.ExecuteNonQuery("update u set id = d.n from (select 8 as n) d join u2 on 1 = 1"));
        AreEqual(2, sim.ExecuteScalar("select count(*) from u where id = 8"));
    }

    /// <summary>
    /// A leading identifier that names no table stays real's Msg 208, and one
    /// the FROM answers with a <em>derived</em> source of the same name still
    /// binds there (real then reports its own missing column).
    /// </summary>
    [TestMethod]
    public void ALeadingNameThatIsNeitherATableNorAnAliasStaysMsg208()
    {
        var sim = Seeded();
        sim.AssertSqlError("update u set id = 90 from u3", 208, "Invalid object name 'u3'.");
        _ = sim.AssertSqlError("update u set id = d.n from (select 4 as n)", 102);
    }
}
