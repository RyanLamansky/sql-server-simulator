using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// How SQL Server splits the two "that name doesn't resolve" errors. It splits
/// by <em>what</em> failed, not by where: a <b>qualified</b> name whose
/// qualifier names no source in scope is <b>Msg 4104</b> on the whole written
/// identifier, while a known qualifier's missing column — and any unqualified
/// miss — is <b>Msg 207</b> on the leaf alone.
/// </summary>
/// <remarks>
/// Probed against SQL Server 2025 on 2026-08-05 across the select list, WHERE,
/// GROUP BY, ORDER BY, an ON predicate, a scalar / <c>IN</c> subquery, a
/// FROM-less SELECT, an aggregate operand, an <c>INSERT … SELECT</c>, and both
/// the SET list and the WHERE of <c>UPDATE</c> / <c>DELETE</c>. The sibling-scope
/// members of the same family live in <see cref="GeneratorSourceScopeTests"/>.
/// </remarks>
[TestClass]
public sealed class UnbindableNameTests
{
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null, s varchar(10) not null);
            create table u (uid int not null);
            insert t values (1, 'a');
            insert u values (1);
            """);
        return sim;
    }

    private static void Msg4104(Simulation sim, string sql, string identifier)
        => AreEqual($"The multi-part identifier \"{identifier}\" could not be bound.", sim.AssertSqlError(sql, 4104).Message);

    private static void Msg207(Simulation sim, string sql, string leaf)
        => AreEqual($"Invalid column name '{leaf}'.", sim.AssertSqlError(sql, 207).Message);

    /// <summary>The two errors side by side on the same table, which is the whole rule.</summary>
    [TestMethod]
    public void UnknownQualifierIs4104_UnknownColumnUnderAKnownOneIs207()
    {
        var sim = Seeded();
        Msg4104(sim, "select zz.id from t", "zz.id");
        Msg207(sim, "select t.nosuch from t", "nosuch");
        Msg207(sim, "select nosuch from t", "nosuch");
    }

    /// <summary>An alias shadows the table name, so the table name itself stops qualifying.</summary>
    [TestMethod]
    public void AnAliasedSourceIsNamedByItsAlias()
    {
        var sim = Seeded();
        Msg4104(sim, "select * from t x where t.id = 1", "t.id");
        Msg207(sim, "select * from t x where x.nosuch = 1", "nosuch");
    }

    /// <summary>Every clause of a query answers the same way.</summary>
    [TestMethod]
    public void EveryClauseSplitsTheSameWay()
    {
        var sim = Seeded();
        Msg4104(sim, "select * from t where zz.id = 1", "zz.id");
        Msg4104(sim, "select * from t group by zz.id", "zz.id");
        Msg4104(sim, "select * from t order by zz.id", "zz.id");
        Msg4104(sim, "select id from t order by zz.id", "zz.id");
        Msg4104(sim, "select * from t join u on zz.id = u.uid", "zz.id");
        Msg4104(sim, "select * from t where t.s in (select zz.id from u)", "zz.id");
        Msg4104(sim, "select (select zz.id) from t", "zz.id");
        Msg4104(sim, "insert into t (id, s) select zz.id, 'x' from u", "zz.id");
    }

    /// <summary>A FROM-less SELECT holds no sources, so any qualified name it can't hand outward is 4104.</summary>
    [TestMethod]
    public void AFromLessSelectHasNoQualifiersAtAll()
    {
        var sim = Seeded();
        Msg4104(sim, "select zz.id", "zz.id");
        Msg4104(sim, "select max(zz.id)", "zz.id");
        Msg4104(sim, "select (select max(zz.id))", "zz.id");
        Msg207(sim, "select nosuch", "nosuch");
    }

    /// <summary>An aggregate operand inside a grouped query takes the same split.</summary>
    [TestMethod]
    public void AnAggregateOperandSplitsTheSameWay()
    {
        var sim = Seeded();
        Msg4104(sim, "select count(*) from t group by t.id having max(zz.id) > 1", "zz.id");
        Msg207(sim, "select count(*) from t group by t.id having max(t.nosuch) > 1", "nosuch");
    }

    /// <summary>
    /// A single-table UPDATE / DELETE admits exactly one qualifier — the target
    /// as written — so a bogus one is 4104 even when its leaf names a real
    /// column. <c>UPDATE v SET id = t.id</c> is refused too, though <c>t</c> is
    /// the view's own base table.
    /// </summary>
    [TestMethod]
    public void SingleTableDmlAdmitsOnlyTheWrittenTargetAsAQualifier()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create view v as select id, s from t");
        Msg4104(sim, "update t set id = zz.id", "zz.id");
        Msg4104(sim, "update t set s = t.s + zz.s", "zz.s");
        Msg4104(sim, "update t set id = 1 where zz.id = 1", "zz.id");
        Msg4104(sim, "update t set id = (select max(zz.id))", "zz.id");
        Msg4104(sim, "delete from t where zz.id = 1", "zz.id");
        Msg4104(sim, "update v set id = t.id", "t.id");
        Msg207(sim, "update t set id = t.nosuch", "nosuch");
        Msg207(sim, "update t set id = 1 where t.nosuch = 1", "nosuch");
    }

    /// <summary>The qualifiers that same statement does admit: its own leaf, however the target was written.</summary>
    [TestMethod]
    public void SingleTableDmlAcceptsTheTargetsOwnLeaf()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create view v as select id, s from t");
        _ = sim.ExecuteNonQuery("update dbo.t set id = t.id");
        _ = sim.ExecuteNonQuery("update t set id = dbo.t.id");
        _ = sim.ExecuteNonQuery("update t set t.id = 5");
        _ = sim.ExecuteNonQuery("update t set id = 1 where dbo.t.id = 5");
        _ = sim.ExecuteNonQuery("update v set id = v.id");
        AreEqual(1, sim.ExecuteScalar("select count(*) from t where id = 1"));
    }
}
