using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The rest of SQL Server's <c>NEXT VALUE FOR</c> refusal family — everything
/// past the Msg 11719 nested-query list <see cref="SequenceContextRejectionTests"/>
/// covers: Msg 11720 for the eight clauses it names, 11721 for a statement that
/// dedupes or combines rowsets, 11723 for one carrying an <c>ORDER BY</c>,
/// 11725 for an aggregate's argument, 11738 for <c>PRINT</c>, 11739 for a
/// <c>TOP</c> / <c>OFFSET</c>, 11741 for the conditional family, and 11742 for
/// a <c>MERGE</c> action.
/// <para>
/// Real settles all of them while parsing, so each case also pins that the
/// sequence was left where it stood. Where two refusals apply at once real
/// reports the earlier one in
/// <c>NextValueForScope</c>'s declaration order, and the pairs below are the
/// ones probed directly against SQL Server 2025 on 2026-08-05.
/// </para>
/// </summary>
[TestClass]
public sealed class SequenceRestrictionMatrixTests
{
    private static Simulation WithSequence()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table n (id int not null primary key, v int null);
            insert n (id, v) values (1, 10), (2, 20), (3, 30);
            create table m (id int null, v int null);
            insert m (id, v) values (1, 100);
            """);
        sim.ExecuteBatches("create sequence dbo.s as int start with 1 increment by 1");
        return sim;
    }

    /// <summary>
    /// Asserts the refusal and that the sequence never moved — real reports
    /// these at parse, so a rejected batch leaves <c>last_used_value</c> NULL.
    /// </summary>
    private static void Refuses(int number, string fragment, string sql)
    {
        var sim = WithSequence();
        Contains(fragment, sim.AssertSqlError(sql, number).Message);
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.sequences where name = 's' and last_used_value is null"));
    }

    // ---- Msg 11721: DISTINCT and the set operators ------------------------

    [TestMethod]
    [DataRow("select distinct next value for dbo.s from n")]
    [DataRow("select distinct id, next value for dbo.s from n")]
    [DataRow("select next value for dbo.s from n union all select 1")]
    [DataRow("select 1 union all select next value for dbo.s")]
    [DataRow("select next value for dbo.s from n union select 1")]
    [DataRow("select next value for dbo.s from n except select 1")]
    [DataRow("select next value for dbo.s from n intersect select 1")]
    [DataRow("insert into m (id, v) select distinct next value for dbo.s, 1 from n")]
    public void DedupingStatement_IsMsg11721(string sql)
        => Refuses(11721, "cannot be used directly in a statement that uses a DISTINCT", sql);

    /// <summary>
    /// A set operator sits one token past the branch that drew, so a FROM-less
    /// first branch has to look ahead for it rather than bake its projection —
    /// otherwise the draw happens before the refusal.
    /// </summary>
    [TestMethod]
    public void FromlessFirstBranchOfASetOperation_DrawsNothing()
        => Refuses(11721, "cannot be used directly in a statement that uses a DISTINCT", "select next value for dbo.s union all select 1");

    /// <summary>A <c>DISTINCT</c> nested one level down is the enclosing statement's business, not this one's.</summary>
    [TestMethod]
    [DataRow("select next value for dbo.s from (select distinct id from n) d")]
    [DataRow("select next value for dbo.s from n where id in (select distinct id from n)")]
    public void DedupingNestedQuery_Answers(string sql)
        => AreEqual(1, WithSequence().ExecuteScalar(sql));

    // ---- Msg 11723: the statement carries an ORDER BY ---------------------

    [TestMethod]
    [DataRow("select next value for dbo.s from n order by id")]
    [DataRow("select next value for dbo.s from n order by id offset 0 rows")]
    [DataRow("select next value for dbo.s from n order by id offset 0 rows fetch next 1 rows only")]
    public void OrderedStatement_IsMsg11723(string sql)
        => Refuses(11723, "contains an ORDER BY clause unless the OVER clause is specified", sql);

    /// <summary>
    /// The reference's own <c>OVER</c> is the one exemption real grants, and it
    /// grants it here alone.
    /// </summary>
    [TestMethod]
    public void OrderedStatementWithOwnOver_Answers()
        => AreEqual(1, WithSequence().ExecuteScalar("select next value for dbo.s over (order by id) from n order by id"));

    [TestMethod]
    [DataRow("select distinct next value for dbo.s over (order by id) from n", 11721)]
    [DataRow("select next value for dbo.s over (order by id) from n union all select 1", 11721)]
    [DataRow("select sum(next value for dbo.s over (order by id)) from n", 11725)]
    [DataRow("select case when 1 = 1 then next value for dbo.s over (order by id) end from n", 11741)]
    [DataRow("select id from n where v > next value for dbo.s over (order by id)", 11720)]
    [DataRow("select next value for dbo.s over (order by id) from n order by id offset 0 rows fetch next 1 rows only", 11739)]
    public void OverExemptsTheOrderByRefusalAlone(string sql, int number)
        => _ = WithSequence().AssertSqlError(sql, number);

    // ---- Msg 11725: an aggregate's argument -------------------------------

    [TestMethod]
    [DataRow("select sum(next value for dbo.s) from n")]
    [DataRow("select max(next value for dbo.s) from n")]
    [DataRow("select count(next value for dbo.s) from n")]
    [DataRow("select sum(next value for dbo.s + 1) from n")]
    [DataRow("select sum(v + next value for dbo.s) from n")]
    [DataRow("select sum(distinct next value for dbo.s) from n")]
    [DataRow("select string_agg(cast(next value for dbo.s as varchar(10)), ',') from n")]
    [DataRow("select sum(case when 1 = 1 then next value for dbo.s end) from n")]
    public void AggregateArgument_IsMsg11725(string sql)
        => Refuses(11725, "cannot be passed as an argument to an aggregate", sql);

    // ---- Msg 11738: a statement real declines to define it in -------------

    [TestMethod]
    public void Print_IsMsg11738()
        => Refuses(11738, "not allowed in this context", "print next value for dbo.s");

    // ---- Msg 11739: TOP / OFFSET ------------------------------------------

    [TestMethod]
    [DataRow("select top (1) next value for dbo.s from n")]
    [DataRow("select top (1) case when 1 = 1 then next value for dbo.s end from n")]
    [DataRow("select top (1) next value for dbo.s over (order by id) from n")]
    public void RowLimitedStatement_IsMsg11739(string sql)
        => Refuses(11739, "cannot be used if ROWCOUNT option has been set", sql);

    // ---- Msg 11741: the conditional family --------------------------------

    [TestMethod]
    [DataRow("select case when 1 = 1 then next value for dbo.s else 0 end")]
    [DataRow("select case 1 when 1 then next value for dbo.s else 0 end")]
    [DataRow("select case when next value for dbo.s > 0 then 1 else 0 end")]
    [DataRow("select case 1 when next value for dbo.s then 1 else 0 end")]
    [DataRow("select case when 1 = 1 then 0 else next value for dbo.s end")]
    [DataRow("select case next value for dbo.s when 1 then 1 else 0 end")]
    [DataRow("select iif(1 = 1, next value for dbo.s, 0)")]
    [DataRow("select iif(next value for dbo.s > 0, 1, 0)")]
    [DataRow("select coalesce(null, next value for dbo.s)")]
    [DataRow("select coalesce(next value for dbo.s, 0)")]
    [DataRow("select nullif(next value for dbo.s, 0)")]
    [DataRow("select nullif(0, next value for dbo.s)")]
    [DataRow("select isnull(null, next value for dbo.s)")]
    [DataRow("select isnull(next value for dbo.s, 0)")]
    [DataRow("insert into m (id, v) values (1, case when 1 = 1 then next value for dbo.s end)")]
    [DataRow("update n set v = case when 1 = 1 then next value for dbo.s end")]
    [DataRow("print case when 1 = 1 then next value for dbo.s end")]
    [DataRow("declare @z int; set @z = case when 1 = 1 then next value for dbo.s end")]
    public void ConditionalArm_IsMsg11741(string sql)
        => Refuses(11741, "cannot be used within CASE, CHOOSE, COALESCE, IIF, ISNULL and NULLIF", sql);

    /// <summary>
    /// <c>CHOOSE</c> is named in real's own Msg 11741 text and yet accepts a
    /// reference — probed both slots, the index as well as a value.
    /// </summary>
    [TestMethod]
    [DataRow("select choose(1, next value for dbo.s, 0)")]
    [DataRow("select choose(2, next value for dbo.s, next value for dbo.s)")]
    public void ChooseArgument_Answers(string sql)
        => AreEqual(1, WithSequence().ExecuteScalar(sql));

    // ---- Msg 11742: a MERGE action ----------------------------------------

    [TestMethod]
    [DataRow("merge m as tgt using n as src on tgt.id = src.id when matched then update set tgt.v = next value for dbo.s;")]
    [DataRow("merge m as tgt using n as src on tgt.id = src.id when not matched then insert (id, v) values (src.id, next value for dbo.s);")]
    public void MergeAction_IsMsg11742(string sql)
        => Refuses(11742, "can only be used with MERGE if it is defined within a default constraint", sql);

    /// <summary>A <c>MERGE</c>'s <c>ON</c> is one of the eight clauses instead.</summary>
    [TestMethod]
    public void MergeOnClause_IsMsg11720()
        => Refuses(11720, "not allowed in the TOP, OVER, OUTPUT, ON, WHERE", "merge m as tgt using n as src on tgt.id = next value for dbo.s when matched then update set tgt.v = 5;");

    /// <summary>
    /// The one sequence a MERGE may draw from is one a default constraint on
    /// the target names, which the insert action reaches without writing
    /// <c>NEXT VALUE FOR</c> at all.
    /// </summary>
    [TestMethod]
    public void MergeIntoATargetWithASequenceDefault_Answers()
    {
        var sim = WithSequence();
        _ = sim.ExecuteNonQuery("create table d (id int default (next value for dbo.s), v int)");
        _ = sim.ExecuteNonQuery("merge d as tgt using n as src on tgt.id = src.id when not matched then insert (v) values (src.v);");
        AreEqual(3, sim.ExecuteScalar("select count(*) from d where id between 1 and 3"));
    }

    // ---- Msg 11720: the clauses, on a DML statement too --------------------

    [TestMethod]
    [DataRow("select top (next value for dbo.s) id from n")]
    [DataRow("select v, row_number() over (order by next value for dbo.s) from n")]
    [DataRow("select v, row_number() over (partition by next value for dbo.s order by id) from n")]
    [DataRow("update n set v = 1 output next value for dbo.s")]
    [DataRow("select id from n where v > next value for dbo.s")]
    [DataRow("select id from n group by id, next value for dbo.s")]
    [DataRow("select max(v) from n having max(v) > next value for dbo.s")]
    [DataRow("select id from n order by next value for dbo.s")]
    [DataRow("delete from n where v = next value for dbo.s")]
    [DataRow("update n set v = 1 where v = next value for dbo.s")]
    public void RestrictedClause_IsMsg11720(string sql)
        => Refuses(11720, "not allowed in the TOP, OVER, OUTPUT, ON, WHERE", sql);

    // ---- Msg 11719: the two stored expressions that were still accepted ----

    [TestMethod]
    [DataRow("create table c1 (id int check (id < next value for dbo.s))")]
    [DataRow("create table c2 (id int, c as (id + next value for dbo.s))")]
    public void ConstraintAndComputedColumn_IsMsg11719(string sql)
        => Refuses(11719, "not allowed in check constraints", sql);

    [TestMethod]
    public void AlterTableAddCheck_IsMsg11719()
    {
        var sim = WithSequence();
        Contains("not allowed in check constraints", sim.AssertSqlError("alter table n add check (v < next value for dbo.s)", 11719).Message);
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.sequences where name = 's' and last_used_value is null"));
    }

    // ---- precedence, where two refusals apply at once ---------------------

    [TestMethod]
    [DataRow("select distinct (select next value for dbo.s) from n", 11719)]
    [DataRow("select top (1) (select next value for dbo.s) from n", 11719)]
    [DataRow("select (select next value for dbo.s) from n order by id", 11719)]
    [DataRow("select distinct sum(next value for dbo.s) from n", 11725)]
    [DataRow("select top (1) sum(next value for dbo.s) from n", 11725)]
    [DataRow("select case when 1 = 1 then sum(next value for dbo.s) end from n", 11725)]
    [DataRow("select distinct top (1) next value for dbo.s from n", 11721)]
    [DataRow("select distinct id from n where v > next value for dbo.s", 11721)]
    [DataRow("select distinct case when 1 = 1 then next value for dbo.s end from n", 11721)]
    [DataRow("select distinct next value for dbo.s from n order by id", 11721)]
    [DataRow("select distinct next value for dbo.s from n union select 1", 11721)]
    [DataRow("select top (1) id from n where v > next value for dbo.s", 11720)]
    [DataRow("select sum(v) from n group by id order by next value for dbo.s", 11720)]
    public void TheEarlierRefusalWins(string sql, int number)
    {
        var sim = WithSequence();
        _ = sim.AssertSqlError(sql, number);
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.sequences where name = 's' and last_used_value is null"));
    }

    // ---- keep-working controls --------------------------------------------

    /// <summary>
    /// Every position real leaves legal, each drawing exactly what real draws.
    /// </summary>
    [TestMethod]
    public void TheLegalPositionsStillDraw()
    {
        var sim = WithSequence();
        AreEqual(1, sim.ExecuteScalar("select next value for dbo.s"));
        // One value per row, however many references the row carries.
        _ = sim.ExecuteNonQuery("create table t2 (a int, b int)");
        _ = sim.ExecuteNonQuery("insert t2 (a, b) values (next value for dbo.s, next value for dbo.s), (next value for dbo.s, next value for dbo.s)");
        AreEqual(2, sim.ExecuteScalar("select count(*) from t2 where a = b"));
        AreEqual(2, sim.ExecuteScalar("select count(distinct a) from t2"));
        // A variable, an initializer, a projection over a source, an UPDATE.
        AreEqual(4, sim.ExecuteScalar("declare @v int = next value for dbo.s; select @v"));
        _ = sim.ExecuteNonQuery("insert t2 (a) select next value for dbo.s from n");
        _ = sim.ExecuteNonQuery("update n set v = next value for dbo.s");
        AreEqual(3, sim.ExecuteScalar("select count(distinct v) from n"));
        // IF / WHILE conditions are legal too.
        AreEqual(1, sim.ExecuteScalar("if next value for dbo.s > 0 select 1"));
    }
}
