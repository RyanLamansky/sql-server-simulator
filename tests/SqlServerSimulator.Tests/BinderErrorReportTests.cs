using System.Text.RegularExpressions;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A statement reports every binder error it carries, as real does: each
/// unbindable reference once per occurrence, in the binder's clause order, at
/// its own line — with real's stopping rules for the clauses a failure ends
/// and the checks an earlier error suppresses. Each expectation is real's
/// report for the same text (probed 2026-09-27 against SQL Server 2025),
/// rendered as <c>number:line:name</c> per error.
/// </summary>
[TestClass]
public sealed partial class BinderErrorReportTests
{
    private const string Setup = "create table t (a int, b int, c varchar(10)); create table u (a int, d int);";

    /// <summary>Runs <paramref name="statement"/> as a batch of its own after the setup and renders every error it reports.</summary>
    private static string Report(string statement)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Setup);
        var ex = Throws<SimulatedSqlException>(() => simulation.ExecuteNonQuery(statement));
        return Render(ex);
    }

    private static string Render(SimulatedSqlException ex) =>
        string.Join(" ", ex.Errors.Where(error => error.Class > 10).Select(error => $"{error.Number}:{error.LineNumber}:{NameIn(error.Message)}"));

    /// <summary>The first quoted name in <paramref name="message"/>, or empty.</summary>
    private static string NameIn(string message) =>
        QuotedName().Match(message) is { Success: true } name ? name.Groups[1].Value + name.Groups[2].Value : "";

    [GeneratedRegex("'([^']*)'|\"([^\"]*)\"")]
    private static partial Regex QuotedName();

    [TestMethod]
    [DataRow("select x1, x2 from t", "207:1:x1 207:1:x2")]
    [DataRow("select x1, x1 from t where x1 = 1", "207:1:x1 207:1:x1 207:1:x1")]
    [DataRow("select zz.a, x1, t.x2, zz.b from t", "4104:1:zz.a 207:1:x1 207:1:x2 4104:1:zz.b")]
    [DataRow("select zz.a, qq.c from t group by zz.a", "4104:1:zz.a 4104:1:zz.a 4104:1:qq.c")]
    [DataRow("select a, d, x1\nfrom t join u on t.a = u.a\nwhere a = 1", "209:3:a 209:1:a 207:1:x1")]
    [DataRow("select a, a, x1 from t, u", "209:1:a 209:1:a 207:1:x1")]
    [DataRow("select a from t t1 join u on t.a = u.a", "4104:1:t.a 209:1:a")]
    [DataRow("select *, x1 from t where x2 = 1", "207:1:x2 207:1:x1")]
    [DataRow("select x1 from t where x2 in (select x3 from u)", "207:1:x2 207:1:x3 207:1:x1")]
    [DataRow("select (select x1 from u where u.a = t.x2) from t", "207:1:x2 207:1:x1")]
    [DataRow("select x1 from t\nunion all\nselect x2 from u\nunion all\nselect x3 from t", "207:1:x1 207:3:x2 207:5:x3")]
    [DataRow("select " + "y0, y1, y2, y3, y4, y5, y6, y7, y8, y9, y10, y11, y12, y13, y14, y15, y16, y17, y18, y19, y20, y21, y22, y23, y24, y25, y26, y27, y28, y29, y30, y31, y32, y33, y34, y35, y36, y37, y38, y39, y40 from t",
        "207:1:y0 207:1:y1 207:1:y2 207:1:y3 207:1:y4 207:1:y5 207:1:y6 207:1:y7 207:1:y8 207:1:y9 207:1:y10 207:1:y11 207:1:y12 207:1:y13 207:1:y14 207:1:y15 207:1:y16 207:1:y17 207:1:y18 207:1:y19 207:1:y20 207:1:y21 207:1:y22 207:1:y23 207:1:y24 207:1:y25 207:1:y26 207:1:y27 207:1:y28 207:1:y29 207:1:y30 207:1:y31 207:1:y32 207:1:y33 207:1:y34 207:1:y35 207:1:y36 207:1:y37 207:1:y38 207:1:y39 207:1:y40")]
    public void EveryUnbindableReference_ReportsOncePerOccurrence(string statement, string expected)
        => AreEqual(expected, Report(statement));

    /// <summary>FROM (its ON predicates and derived tables), WHERE, GROUP BY, HAVING, the select list, ORDER BY, TOP — each error at its own line.</summary>
    [TestMethod]
    [DataRow("select x1\nfrom t\nwhere x2 = 1\ngroup by x3\nhaving x4 > 1\norder by x5", "207:3:x2 207:4:x3 207:5:x4 207:1:x1 207:6:x5")]
    [DataRow("select x1\nfrom t join u on x2 = u.a\njoin u u2 on x3 = 1\nwhere x4 = 1", "207:2:x2 207:3:x3 207:4:x4 207:1:x1")]
    [DataRow("select x1,\n (select x2 from u)\nfrom t\nwhere exists (select x3 from u where x4 = 1)", "207:4:x4 207:4:x3 207:1:x1 207:2:x2")]
    [DataRow("select x2\nfrom (select x1 from t) d\nwhere x3 = 1", "207:2:x1 207:3:x3 207:1:x2")]
    [DataRow("select x1 from t cross apply (select x2 from u where u.a = t.x3) ca", "207:1:x3 207:1:x2 207:1:x1")]
    [DataRow("select top (select x1) x2 from t where x3 = 1", "207:1:x3 207:1:x2 207:1:x1")]
    [DataRow("select a from t order by x1, x2", "207:1:x1 207:1:x2")]
    [DataRow("select x1 from t where x2=1 union select x3 from u where x4 = 1 order by x5", "207:1:x2 207:1:x1 207:1:x4 207:1:x3 207:1:x5 104:1:")]
    [DataRow("select a,\nx1\nfrom t", "207:2:x1")]
    [DataRow("select a\nfrom t\norder by\nx1,\nx2", "207:4:x1 207:5:x2")]
    [DataRow("select x1 from t join u on x2 = 1 join u on x3 = 1 where x4 = 1", "207:1:x2 1013:1:u")]
    [DataRow("select x1 from t, t where x2 = 1", "1013:1:t")]
    public void Report_FollowsTheBindersClauseOrder(string statement, string expected)
        => AreEqual(expected, Report(statement));

    /// <summary>
    /// A statement real compiles through simple parameterization — one table,
    /// a literal in the WHERE, none of the constructs that disqualify it —
    /// reports every error at its first line.
    /// </summary>
    [TestMethod]
    [DataRow("select a\nfrom t\nwhere\nx1 = 1\nand x2 = 2", "207:1:x1 207:1:x2")]
    [DataRow("select a,\nx9\nfrom t\nwhere\nx1 = 1", "207:1:x1 207:1:x9")]
    [DataRow("select a\nfrom t\nwhere\nx1 = b", "207:4:x1")]
    [DataRow("select a\nfrom t\nwhere\nx1 in (1, 3)", "207:4:x1 207:4:x1")]
    [DataRow("select a\nfrom t\nwhere\nx1 = 1\ngroup by a", "207:4:x1")]
    [DataRow("select a\nfrom u, t\nwhere\nx1 = 1", "207:4:x1 209:1:a")]
    [DataRow("declare @v int = 1;\nselect @v\nfrom t\nwhere\nx1 = 1", "207:5:x1")]
    [DataRow("update t\nset x1 = 1", "207:1:x1")]
    [DataRow("update t\nset\na = x1", "207:3:x1")]
    public void SimplyParameterizedStatement_ReportsItsFirstLine(string statement, string expected)
        => AreEqual(expected, Report(statement));

    /// <summary>
    /// A GROUP BY violation reports after its expression's own name errors,
    /// and only when nothing ahead of the expression failed; an aggregate
    /// nesting (Msg 130) or WHERE (Msg 147) refusal likewise yields to an
    /// earlier name error.
    /// </summary>
    [TestMethod]
    [DataRow("select a, x1 from t group by b", "8120:1:t.a 207:1:x1")]
    [DataRow("select x1, a from t group by c", "207:1:x1")]
    [DataRow("select a, b from t group by c", "8120:1:t.a")]
    [DataRow("select a + x1 from t group by b", "207:1:x1 8120:1:t.a")]
    [DataRow("select x1 + a from t group by b", "207:1:x1")]
    [DataRow("select a + c from t group by b", "8120:1:t.a 8120:1:t.c")]
    [DataRow("select count(*) from t group by a having b > 1 and x1 = 1 and c = 'x'", "207:1:x1 8121:1:t.b 8121:1:t.c")]
    [DataRow("select a, count(*) from t group by b having x1 > 1 and c = 'x'", "207:1:x1")]
    [DataRow("select a from t group by b having c = 1", "8121:1:t.c")]
    [DataRow("select b from t group by a order by c, x1", "8120:1:t.b 207:1:x1")]
    [DataRow("select\na\nfrom t\ngroup by b", "8120:2:t.a")]
    [DataRow("select x1 from t where count(*) > 1 and x2 = 1", "147:1: 207:1:x2 207:1:x1")]
    [DataRow("select a from t where count(x1) > 1", "207:1:x1")]
    [DataRow("select sum(count(*)), x1 from t", "130:1: 207:1:x1")]
    [DataRow("select sum(sum(x1)), x2 from t", "207:1:x1 207:1:x2")]
    [DataRow("select x1 from (select 1) d", "8155:1:d 207:1:x1")]
    public void GroupingAndAggregateChecks_YieldToEarlierErrors(string statement, string expected)
        => AreEqual(expected, Report(statement));

    /// <summary>
    /// An illegal conversion real meets before any other error is reported
    /// alone, at the statement's line; one it meets after an error is skipped,
    /// as is any type check over an unbindable operand.
    /// </summary>
    [TestMethod]
    [DataRow("select x1, cast(cast(1 as date) as int), x2 from t", "207:1:x1 207:1:x2")]
    [DataRow("select x1 from t where cast(1 as date) = 1 and x2 = 1", "529:1:")]
    [DataRow("select a + cast(1 as date), x1 from t where x2 = 1", "207:1:x2 207:1:x1")]
    [DataRow("select x1 + cast('2020-01-01' as date), x2 from t", "207:1:x1 207:1:x2")]
    [DataRow("select b,\n cast(cast(1 as date) as int)\nfrom t", "529:1:")]
    public void TypeChecks_ReportAloneOrNotAtAll(string statement, string expected)
        => AreEqual(expected, Report(statement));

    /// <summary>
    /// The shapes real binds by expansion report an operand once per copy, and
    /// the ones it reorders report in its order: a CASE's conditions before its
    /// results, a window's OVER clause before its arguments, an IN list last
    /// element first.
    /// </summary>
    [TestMethod]
    [DataRow("select coalesce(x1, x2, x3) from t", "207:1:x1 207:1:x2 207:1:x1 207:1:x2 207:1:x3")]
    [DataRow("select nullif(x1, 1) + x2 from t", "207:1:x1 207:1:x1 207:1:x2")]
    [DataRow("select a from t where x1 between x2 and x3", "207:1:x1 207:1:x2 207:1:x1 207:1:x3")]
    [DataRow("select a from t where x1 in (x2, x3)", "207:1:x1 207:1:x3 207:1:x1 207:1:x2")]
    [DataRow("select a from t where a in (x1, x2, x3, x4)", "207:1:x4 207:1:x3 207:1:x2 207:1:x1")]
    [DataRow("select case x1 when x2 then x3 when x4 then x5 else x6 end from t", "207:1:x1 207:1:x2 207:1:x1 207:1:x4 207:1:x3 207:1:x5 207:1:x6")]
    [DataRow("select case when x1 = 1 then x2 when x3 = 1 then x4 else x5 end from t", "207:1:x1 207:1:x3 207:1:x2 207:1:x4 207:1:x5")]
    [DataRow("select sum(x1 + x2) over (order by x3) from t", "207:1:x3 207:1:x1 207:1:x2")]
    [DataRow("select lag(x1, 1, x2) over (order by x3) from t", "207:1:x3 207:1:x1 207:1:x2")]
    [DataRow("select string_agg(x1, ',') within group (order by x2), x3 from t", "207:1:x1 207:1:x2 207:1:x3")]
    public void ExpandedShapes_ReportInTheBindersOrder(string statement, string expected)
        => AreEqual(expected, Report(statement));

    /// <summary>
    /// A CTE that fails ends the statement's report; so do an UPDATE's FROM /
    /// WHERE (before its SET targets) and its SET targets (before its values),
    /// and a MERGE's ON. An INSERT binds its source before its column list,
    /// and its SELECT-list arity refusal preempts everything.
    /// </summary>
    [TestMethod]
    [DataRow("with c1 as (select x1 from t) select x2 from c1", "207:1:x1")]
    [DataRow("with c1 as (select x1 from t), c2 as (select x2 from t) select a from c1", "207:1:x1 207:1:x2")]
    [DataRow("with c1 as (select a from t) select x1, x2 from c1", "207:1:x1 207:1:x2")]
    [DataRow("update t set x1 = x2 where x3 = 1", "207:1:x3")]
    [DataRow("update t set x1 = x2, b = x3", "207:1:x1")]
    [DataRow("update t set x1 = 1, x2 = 2", "207:1:x1 207:1:x2")]
    [DataRow("update t set a = x1 where x2 = 1 and x3 = 1", "207:1:x2 207:1:x3")]
    [DataRow("update t set a = x1, x2 = 1 from t join u on t.a = u.a where u.d = 1", "207:1:x2")]
    [DataRow("update t set a = x2, x5 = 1\nfrom t join u on x3 = 1\nwhere x4 = 1", "207:2:x3 207:3:x4")]
    [DataRow("update t set a = x2 output inserted.x1, deleted.x3", "207:1:x2 207:1:x1 207:1:x3")]
    [DataRow("with c as (select a from t) update c set x1 = 1 where x2 = 1", "207:1:x2")]
    [DataRow("delete t from t join u on x1 = u.a where x2 = 1", "207:1:x1 207:1:x2")]
    [DataRow("delete t output deleted.x1, x2 where x3 = 1", "207:1:x3 207:1:x1 207:1:x2")]
    [DataRow("insert t (a, x1) select x2, x3 from u", "207:1:x2 207:1:x3 207:1:x1")]
    [DataRow("insert t (a, x1, x2) values (1, 2, x3)", "207:1:x3 207:1:x1 207:1:x2")]
    [DataRow("insert t (a) output inserted.x1, x3 select x2 from u", "207:1:x2 207:1:x1 207:1:x3")]
    [DataRow("insert t (a, x1) select a from u", "120:1:")]
    [DataRow("insert t (a, x1) values (1, 2, 3)", "207:1:x1 110:1:")]
    [DataRow("insert t (a, b) values (x1, 2, 3)", "207:1:x1 110:1:")]
    [DataRow("insert t values (x1, 2, 3, 4)", "207:1:x1 213:1:")]
    [DataRow("insert t (a) values (x1), (x2, 3)", "207:1:x1 207:1:x2 10709:1:")]
    [DataRow("merge t using u on x1 = u.a\nwhen matched then update set x2 = x3\nwhen not matched then insert (a, x4) values (x5, 1);", "207:1:x1")]
    [DataRow("merge t using u on t.a = u.a\nwhen matched then update set x1 = x2\nwhen not matched then insert (a, x4) values (x5, 1);", "207:3:x4 207:2:x1 207:2:x2 207:3:x5")]
    [DataRow("merge t using (select x1 from u where x2 = 1) s on t.a = s.a when matched then delete;", "207:1:x2 207:1:x1 207:1:a")]
    public void StatementShapes_StopWhereTheBinderStops(string statement, string expected)
        => AreEqual(expected, Report(statement));

    /// <summary>An explicit value for a rowversion column reports with a ragged VALUES list's refusal.</summary>
    [TestMethod]
    public void ExplicitTimestamp_ReportsWithRaggedValues()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table r (a int, ts rowversion)");
        AreEqual("273:1: 10709:1:", Render(Throws<SimulatedSqlException>(() => simulation.ExecuteNonQuery("insert r (a, ts) values (1, 2), (3)"))));
    }

    /// <summary>A module body reports its statements' whole reports, attributed to the module.</summary>
    [TestMethod]
    [DataRow("create proc p as\nselect x1,\nx2 from t\nwhere x3 = 1", "207:4:x3 207:2:x1 207:3:x2", "p")]
    [DataRow("create view v as select x1, x2 from t where x3 = 1", "207:1:x3 207:1:x1 207:1:x2", "v")]
    [DataRow("create function f() returns int as begin return (select x1 from t where x2 = 1) end", "207:1:x2 207:1:x1", "f")]
    [DataRow("create function f2() returns table as return select x1, x2 from t where x3 = 1", "207:1:x3 207:1:x1 207:1:x2", "f2")]
    public void ModuleBody_ReportsEveryBinderError(string statement, string expected, string module)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Setup);
        var ex = Throws<SimulatedSqlException>(() => simulation.ExecuteNonQuery(statement));
        AreEqual(expected, Render(ex));
        IsTrue(ex.Errors.All(error => error.Procedure == module));
    }

    /// <summary>An IF condition reports its own errors, then its branches theirs.</summary>
    [TestMethod]
    public void IfCondition_ReportsBeforeItsBranches()
        => AreEqual("207:1:x1 207:2:x2 207:4:x3", Report("if exists (select x1 from t)\nselect x2 from t\nelse\nselect x3 from t"));

    /// <summary>Every statement of a batch reports, the statements' reports in order.</summary>
    [TestMethod]
    public void EveryStatement_OfTheBatchReports()
        => AreEqual("207:1:x1 207:2:x2 207:2:x3", Report("select x1 from t where b = 1\nselect x2, x3 from u"));

    /// <summary>
    /// A statement that binds only when it runs — its table is created by the
    /// same batch — reports its whole report then, and ends the batch.
    /// </summary>
    [TestMethod]
    public void DeferredStatement_ReportsWhenItRuns()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        var printed = new List<string>();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => printed.Add(e.Message);
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("create table t2 (a int);\nselect x1, x2\nfrom t2\nwhere x3 = 1;\nprint 'after'").ExecuteNonQuery());
        AreEqual("207:2:x3 207:2:x1 207:2:x2", Render(ex));
        IsEmpty(printed);
    }

    /// <summary>
    /// A CATCH around a dynamic batch that fails to compile reads the report's
    /// first error, where an error raised as several at run time shows its last.
    /// </summary>
    [TestMethod]
    [DataRow("select x1 from t where x2 = 1", "207 Invalid column name 'x2'.")]
    [DataRow("select zz.a, x1 from t", "4104 The multi-part identifier \"zz.a\" could not be bound.")]
    [DataRow("select x1 from t; select x2 from t", "207 Invalid column name 'x1'.")]
    [DataRow("select a from t group by b order by x1", "8120 Column 't.a' is invalid in the select list because it is not contained in either an aggregate function or the GROUP BY clause.")]
    public void TryCatch_ReadsTheReportsFirstError(string dynamicBatch, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"""
            {Setup}
            begin try exec('{dynamicBatch}') end try
            begin catch select cast(error_number() as varchar) + ' ' + error_message() end catch
            """));

    [TestMethod]
    public void DynamicBatch_ReportsEveryError()
        => AreEqual("207:1:x2 207:1:x1", Report("exec('select x1 from t where x2 = 1')"));
}
