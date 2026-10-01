using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A scalar function real inlines into the query calling it is compiled with
/// that query, so a body naming an object that no longer exists sends a
/// non-aborting Msg 208 attributed to the function — once per call, while the
/// batch compiles, ahead of everything the batch runs — and the statement then
/// runs and raises its own (probed 2026-09-30 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class ScalarUdfInliningTests
{
    /// <summary>The body's missing object sits on line 6 of the batch that creates it.</summary>
    private const string Function = """
        create function dbo.f() returns int as
        begin
          declare @x int;
          -- 4
          select @x = count(*)
            from nosuch;
          return @x;
        end
        """;

    private static DbConnection Open(params string[] more)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(["create table t (a int); insert t values (1), (2)", Function, .. more]);
        return simulation.CreateOpenConnection();
    }

    private static string Entries(SimulatedSqlException error) =>
        string.Join(", ", error.Errors.Select(static e => $"{e.Number} L{e.LineNumber}{(e.Procedure.Length == 0 ? "" : " " + e.Procedure)}"));

    /// <summary>
    /// Every error reading <paramref name="sql"/> through a reader raises, in
    /// order — <c>ExecuteReader</c>'s, then each advance's — or "none".
    /// </summary>
    private static string ReaderError(DbConnection connection, string sql)
    {
        List<string> errors = [];
        try
        {
            using var reader = connection.CreateCommand(sql).ExecuteReader();
            bool more;
            do
            {
                try
                {
                    while (reader.Read())
                    {
                    }
                }
                catch (SimulatedSqlException error)
                {
                    errors.Add(Entries(error));
                }
                try
                {
                    more = reader.NextResult();
                }
                catch (SimulatedSqlException error)
                {
                    errors.Add(Entries(error));
                    more = true;
                }
            }
            while (more);
        }
        catch (SimulatedSqlException error)
        {
            errors.Add(Entries(error));
        }
        return errors.Count == 0 ? "none" : string.Join(", ", errors);
    }

    [TestMethod]
    public void AMissingObject_FailsOnceAsTheBatchCompiles_ThenTheStatementRaisesItsOwn()
    {
        using var connection = Open();
        AreEqual("208 L6 f, 208 L1", ReaderError(connection, "select 'a'; select dbo.f()"));
    }

    [TestMethod]
    public void EachCall_FailsOnItsOwn()
    {
        using var connection = Open();
        AreEqual("208 L6 f, 208 L6 f, 208 L1", ReaderError(connection, "select dbo.f(), dbo.f()"));
    }

    /// <summary>
    /// The failure is the compile's, so a call a false conjunct never reaches
    /// and a TRY around the statement change nothing about it.
    /// </summary>
    [TestMethod]
    [DataRow("select 1 where 1 = 0 and dbo.f() = 1")]
    [DataRow("begin try select dbo.f() end try begin catch select error_number() end catch")]
    [DataRow("if 1 = 0 select dbo.f()")]
    public void TheFailure_PrecedesWhatTheBatchRuns(string sql)
    {
        using var connection = Open();
        AreEqual("208 L6 f", ReaderError(connection, sql));
    }

    [TestMethod]
    [DataRow("select a from t where a = dbo.f()")]
    [DataRow("select * from t cross apply (select dbo.f() x) q")]
    [DataRow("update t set a = dbo.f()")]
    [DataRow("insert t values (dbo.f())")]
    [DataRow("merge t using (select 1 a) s on t.a = s.a when matched then update set a = dbo.f();")]
    [DataRow("declare @v int = (select dbo.f())")]
    [DataRow("declare @v int; select @v = dbo.f() from t")]
    [DataRow("select distinct dbo.f() from t")]
    [DataRow("select dbo.f() from t group by a")]
    [DataRow("select a from t order by (select dbo.f())")]
    [DataRow("declare @tv table (a int); select dbo.f() from @tv")]
    public void AQueryCallingTheFunction_Fails(string sql)
    {
        using var connection = Open();
        StartsWith("208 L6 f", ReaderError(connection, sql));
    }

    /// <summary>
    /// What real doesn't inline sends nothing: an operand of a statement that
    /// is no query, an <c>IF</c> condition, an <c>ORDER BY</c> or
    /// <c>GROUP BY</c> call, a statement led by a common table expression or
    /// told not to, a <c>SELECT</c> assigning variables with no <c>FROM</c>,
    /// and the select list of a <c>DISTINCT</c>, ordered query or an ordered
    /// set operation.
    /// </summary>
    [TestMethod]
    [DataRow("declare @v int = dbo.f()")]
    [DataRow("print dbo.f()")]
    [DataRow("if (select dbo.f()) = 1 print 1")]
    [DataRow("select a from t order by dbo.f()")]
    [DataRow("select dbo.f() from t group by dbo.f()")]
    [DataRow("with c as (select 1 x) select dbo.f() from c")]
    [DataRow("select dbo.f() option (use hint ('DISABLE_TSQL_SCALAR_UDF_INLINING'))")]
    [DataRow("declare @v int; select @v = dbo.f()")]
    [DataRow("select distinct dbo.f() from t order by 1")]
    [DataRow("select dbo.f() x from t union all select 1 order by x")]
    public void WhatRealDoesNotInline_SendsNothing(string sql)
    {
        using var connection = Open();
        DoesNotContain(" f", ReaderError(connection, sql));
    }

    [TestMethod]
    [DataRow("alter database current set compatibility_level = 140")]
    [DataRow("alter database scoped configuration set tsql_scalar_udf_inlining = off")]
    [DataRow("alter function dbo.f() returns int as begin declare @x int; while @x < 1 set @x = 1; select @x = count(*) from nosuch; return @x end")]
    public void NoInlining_SendsNothing(string setup)
    {
        using var connection = Open(setup);
        AreEqual("208 L1", ReaderError(connection, "select dbo.f()"));
    }

    /// <summary>A compiled batch reuses its plan, so running it again sends nothing more.</summary>
    [TestMethod]
    [DataRow("select 'a'; select dbo.f()")]
    [DataRow("exec ('select dbo.f()')")]
    public void ACompiledBatch_RunAgain_SendsNothing(string sql)
    {
        using var connection = Open();
        StartsWith("208 L6 f", ReaderError(connection, sql));
        AreEqual("208 L1", ReaderError(connection, sql));
    }

    /// <summary>A statement compiling again as it runs sends again then.</summary>
    [TestMethod]
    public void Recompile_FailsWithTheBatchAndAsItRuns_EveryTime()
    {
        using var connection = Open();
        const string Sql = "select dbo.f() option (recompile)";
        AreEqual("208 L6 f, 208 L6 f, 208 L1", ReaderError(connection, Sql));
        AreEqual("208 L6 f, 208 L6 f, 208 L1", ReaderError(connection, Sql));
    }

    /// <summary>A statement the batch's compile deferred fails as it compiles, ahead of its own rows.</summary>
    [TestMethod]
    public void ADeferredStatement_FailsAsItRuns()
    {
        using var connection = Open();
        using var reader = connection.CreateCommand("create table u (a int); select 'a'; select dbo.f() from u").ExecuteReader();
        IsTrue(reader.Read());
        var error = Throws<SimulatedSqlException>(() => reader.NextResult());
        AreEqual("208 L6 f", Entries(error));
    }

    [TestMethod]
    public void AtAtError_ReadsTheFailureUntilTheFirstStatementEnds()
    {
        using var connection = Open();
        var error = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert t values (@@error); insert t values (@@error); select 1 where 1 = 0 and dbo.f() = 1").ExecuteNonQuery());
        AreEqual("208 L6 f", Entries(error));
        AreEqual("208,0", string.Join(",", connection.CreateCommand("select a from t where a not in (1, 2) order by a desc").ExecuteReader().EnumerateRecords().Select(static r => r.GetInt32(0))));
    }

    [TestMethod]
    public void ANestedCall_FailsUnderTheInnerFunction()
    {
        using var connection = Open("create function dbo.g() returns int as begin return dbo.f() end");
        AreEqual("208 L6 f, 208 L1", ReaderError(connection, "select dbo.g()"));
    }

    /// <summary>A qualified name fails on line 13 wherever it sits, real's own constant.</summary>
    [TestMethod]
    public void AQualifiedName_FailsOnLine13()
    {
        using var connection = Open("create function dbo.q() returns int as begin return (select count(*) from dbo.nope) end");
        AreEqual("208 L13 q, 208 L1", ReaderError(connection, "select dbo.q()"));
    }

    [TestMethod]
    [DataRow("create view v as select dbo.f() x", "select * from v")]
    [DataRow("create function dbo.i() returns table as return select dbo.f() x", "select * from dbo.i()")]
    public void AViewOrInlineFunctionBody_InlinesItsCalls(string module, string sql)
    {
        using var connection = Open(module);
        AreEqual("208 L6 f, 208 L1", ReaderError(connection, sql));
    }

    /// <summary>A failure sits among a failing compile's binder errors where its call bound.</summary>
    [TestMethod]
    [DataRow("select dbo.f(); select nosuchcol from t", "208 L6 f, 207 L1")]
    [DataRow("select nosuchcol from t; select dbo.f()", "207 L1, 208 L6 f")]
    [DataRow("select dbo.f(), nosuchcol from t", "208 L6 f, 207 L1")]
    [DataRow("select nosuchcol, dbo.f() from t", "207 L1")]
    [DataRow("if nosuchcol = 1 select dbo.f() from t", "207 L1, 208 L6 f")]
    [DataRow("select dbo.f(); select 1 +", "102 L1")]
    public void AFailingCompile_CarriesTheFailures(string sql, string expected)
    {
        using var connection = Open();
        AreEqual(expected, ReaderError(connection, sql));
    }

    /// <summary>The statement's own Msg 208 is the body's, raised as it runs, so a write it ends earns Msg 3621.</summary>
    [TestMethod]
    public void AWriteTheBodyEnds_EarnsMsg3621()
    {
        using var connection = Open();
        var error = Throws<SimulatedSqlException>(() => connection.CreateCommand("update t set a = dbo.f(); select 'after'").ExecuteNonQuery());
        AreEqual("208 L6 f, 208 L1, 3621 L1", Entries(error));
    }
    /// <summary>
    /// <c>WITH INLINE = OFF</c> keeps an inlineable body from inlining, so only
    /// the statement's own Msg 208 is raised, and <c>sys.sql_modules</c> reads
    /// <c>inline_type</c> 0 beside <c>is_inlineable</c> 1 (probed 2026-10-01
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void InlineOff_NeverInlines()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create function dbo.f() returns int with inline = off as begin declare @x int; select @x = count(*) from nosuch; return @x end",
            "create function dbo.g() returns int with inline = off as begin return 1 end");
        using var connection = simulation.CreateOpenConnection();
        AreEqual("208 L1", ReaderError(connection, "select dbo.f()"));
        AreEqual("0,1", connection.CreateCommand("select concat(inline_type, ',', is_inlineable) from sys.sql_modules where object_id = object_id('dbo.g')").ExecuteScalar());
    }

    /// <summary>
    /// <c>WITH INLINE = ON</c> over a body that can't inline is Msg 16203,
    /// after the body's binder errors and ahead of the name check (probed
    /// 2026-10-01 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("create function dbo.h() returns int with inline = on as begin return datepart(year, getdate()) end", 16203)]
    [DataRow("create function dbo.f() returns int with inline = on as begin return datepart(year, getdate()) end", 16203)]
    [DataRow("create function dbo.h() returns int with inline = on as begin return datepart(year, getdate()) + (select nocol from sys.objects) end", 207)]
    public void InlineOn_OverABodyThatCannotInline_IsRefused(string sql, int number)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(Function);
        _ = simulation.AssertSqlError(sql, number);
    }
}
