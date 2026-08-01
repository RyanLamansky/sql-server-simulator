using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>OBJECTPROPERTY(id, 'IsDeterministic')</c> and
/// <c>'IsSchemaBound'</c> across the module kinds. Probe-confirmed against
/// SQL Server 2025: a module is deterministic only when it is schema-bound,
/// reaches no nondeterministic built-in, and every module it references is
/// itself deterministic. Real answers <c>IsDeterministic</c> for views,
/// scalar functions and both TVF kinds; a procedure, trigger, table, sequence
/// or synonym gets NULL.
/// </summary>
[TestClass]
public sealed class ModuleDeterminismTests
{
    /// <summary>
    /// Schema-binding is a precondition, not a contributing signal: a body
    /// with nothing nondeterministic in it still reports 0 without the
    /// option (probe-confirmed — this is the rule most callers get wrong).
    /// </summary>
    [TestMethod]
    public void ScalarFunction_WithoutSchemaBinding_IsNondeterministic()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create function f(@a int) returns int as begin return @a * 2 end");
        AreEqual(0, sim.ExecuteScalar("select objectproperty(object_id('dbo.f'), 'IsDeterministic')"));
    }

    [TestMethod]
    public void ScalarFunction_SchemaBoundPureBody_IsDeterministic()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create function f(@a int) returns int with schemabinding as begin return @a * 2 end");
        AreEqual(1, sim.ExecuteScalar("select objectproperty(object_id('dbo.f'), 'IsDeterministic')"));
    }

    /// <summary>
    /// One representative per nondeterministic family: the current-time
    /// readers, the side-effecting generators, session and connection state,
    /// server / database metadata, the security scalars, the <c>ERROR_*</c>
    /// family, and the language- or DATEFIRST-dependent formatters. Each was
    /// probed by wrapping the same expression in a schema-bound function on
    /// the reference instance.
    /// </summary>
    [TestMethod]
    [DataRow("cast(getdate() as int)")]
    [DataRow("cast(sysdatetime() as int)")]
    [DataRow("cast(sysutcdatetime() as int)")]
    [DataRow("cast(current_timestamp as int)")]
    [DataRow("cast(newid() as int)")]
    [DataRow("cast(rand() as int)")]
    [DataRow("cast(rand(5) as int)")]
    [DataRow("@@spid")]
    [DataRow("@@rowcount")]
    [DataRow("@@nestlevel")]
    [DataRow("cast(format(@a, 'd') as int)")]
    [DataRow("cast(datename(month, cast('2020-01-01' as date)) as int)")]
    [DataRow("datepart(week, cast('2020-01-01' as date))")]
    [DataRow("datepart(weekday, cast('2020-01-01' as date))")]
    [DataRow("isdate('2020-01-01')")]
    [DataRow("cast(parse('5' as int) as int)")]
    [DataRow("cast(try_parse('5' as int) as int)")]
    [DataRow("object_id('dbo.f')")]
    [DataRow("db_id()")]
    [DataRow("cast(db_name() as int)")]
    [DataRow("cast(schema_name(1) as int)")]
    [DataRow("cast(suser_sname() as int)")]
    [DataRow("cast(host_name() as int)")]
    [DataRow("cast(app_name() as int)")]
    [DataRow("cast(original_login() as int)")]
    [DataRow("cast(current_user as int)")]
    [DataRow("cast(system_user as int)")]
    [DataRow("user_id()")]
    [DataRow("cast(session_context(N'k') as int)")]
    [DataRow("cast(context_info() as int)")]
    [DataRow("cast(serverproperty('Edition') as int)")]
    [DataRow("cast(collationproperty('Latin1_General_CI_AS', 'CodePage') as int)")]
    [DataRow("error_number()")]
    [DataRow("xact_state()")]
    [DataRow("cast(compress('abc') as int)")]
    [DataRow("type_id('int')")]
    [DataRow("objectproperty(1, 'IsTable')")]
    public void SchemaBoundBody_ReachingNondeterministicBuiltIn_Reports0(string expression)
    {
        var sim = new Simulation();
        sim.ExecuteBatches($"create function f(@a int) returns int with schemabinding as begin return ({expression}) end");
        AreEqual(0, sim.ExecuteScalar("select objectproperty(object_id('dbo.f'), 'IsDeterministic')"));
    }

    /// <summary>
    /// The near misses. Each of these looks stateful but probes deterministic
    /// on the reference instance, so the analysis must not over-report:
    /// hashing is pure, <c>DATEPART</c> is nondeterministic only for the
    /// DATEFIRST-dependent units (<c>iso_week</c> is fixed), and reading a
    /// table, aggregating it or ranking over it is all deterministic.
    /// </summary>
    [TestMethod]
    [DataRow("@a * 2")]
    [DataRow("abs(@a)")]
    [DataRow("len(upper('abc'))")]
    [DataRow("checksum(@a)")]
    [DataRow("binary_checksum(@a)")]
    [DataRow("cast(hashbytes('SHA2_256', 'abc') as int)")]
    [DataRow("cast(quotename('a') as int)")]
    [DataRow("datepart(year, cast('2020-01-01' as date))")]
    [DataRow("datepart(iso_week, cast('2020-01-01' as date))")]
    [DataRow("datediff(day, cast('2020-01-01' as date), cast('2020-02-01' as date))")]
    [DataRow("cast(dateadd(day, 1, cast('2020-01-01' as date)) as int)")]
    [DataRow("cast(eomonth(cast('2020-01-01' as date)) as int)")]
    [DataRow("cast(decompress(0x) as int)")]
    [DataRow("isnumeric('5')")]
    [DataRow("cast(min_active_rowversion() as int)")]
    [DataRow("(select count(*) from dbo.t)")]
    [DataRow("(select max(x) from dbo.t)")]
    [DataRow("(select top 1 x from dbo.t order by x)")]
    [DataRow("(select top 1 r from (select row_number() over (order by x) r from dbo.t) w)")]
    public void SchemaBoundBody_PureExpression_Reports1(string expression)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (x int)",
            $"create function f(@a int) returns int with schemabinding as begin return ({expression}) end");
        AreEqual(1, sim.ExecuteScalar("select objectproperty(object_id('dbo.f'), 'IsDeterministic')"));
    }

    /// <summary>
    /// <c>AT TIME ZONE</c> reads the server's time-zone table, so real
    /// classifies it nondeterministic even though <c>SWITCHOFFSET</c> and
    /// <c>TODATETIMEOFFSET</c> — which take the offset as an argument — are
    /// deterministic.
    /// </summary>
    [TestMethod]
    public void SchemaBoundBody_AtTimeZone_Reports0()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("""
            create function f() returns datetimeoffset with schemabinding as
            begin return cast('2020-01-01' as datetime2) at time zone 'UTC' end
            """);
        AreEqual(0, sim.ExecuteScalar("select objectproperty(object_id('dbo.f'), 'IsDeterministic')"));
    }

    /// <summary>
    /// Real applies the same rule to views and both TVF kinds — a
    /// schema-bound one with a pure body reports 1, and dropping either the
    /// option or the purity takes it to 0.
    /// </summary>
    [TestMethod]
    [DataRow("create view m with schemabinding as select cast(1 as int) x", 1)]
    [DataRow("create view m as select cast(1 as int) x", 0)]
    [DataRow("create view m with schemabinding as select host_name() x", 0)]
    [DataRow("create function m() returns table with schemabinding as return (select cast(1 as int) x)", 1)]
    [DataRow("create function m() returns table as return (select cast(1 as int) x)", 0)]
    [DataRow("create function m() returns table with schemabinding as return (select host_name() x)", 0)]
    [DataRow("create function m() returns @r table (x int) with schemabinding as begin insert @r values (1); return end", 1)]
    [DataRow("create function m() returns @r table (x int) as begin insert @r values (1); return end", 0)]
    [DataRow("create function m() returns @r table (x datetime) with schemabinding as begin insert @r values (getdate()); return end", 0)]
    public void ViewsAndTableValuedFunctions_FollowTheSameRule(string create, int expected)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(create);
        AreEqual(expected, sim.ExecuteScalar("select objectproperty(object_id('dbo.m'), 'IsDeterministic')"));
    }

    /// <summary>
    /// Determinism is transitive across every module reference shape — a
    /// called scalar function, a view in the FROM clause, and a TVF in the
    /// FROM clause all propagate their own verdict outward.
    /// </summary>
    [TestMethod]
    public void ReferencedModule_PropagatesItsVerdict()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create function leaf_pure(@a int) returns int with schemabinding as begin return @a + 1 end",
            "create function leaf_clock() returns int with schemabinding as begin return (datepart(week, getdate())) end",
            "create view v_pure with schemabinding as select cast(1 as int) x",
            "create view v_clock with schemabinding as select host_name() x",
            "create function tvf_clock() returns table with schemabinding as return (select host_name() x)",
            "create function calls_pure(@a int) returns int with schemabinding as begin return dbo.leaf_pure(@a) end",
            "create function calls_clock() returns int with schemabinding as begin return dbo.leaf_clock() end",
            "create function reads_pure_view() returns int with schemabinding as begin return (select count(*) from dbo.v_pure) end",
            "create function reads_clock_view() returns int with schemabinding as begin return (select count(*) from dbo.v_clock) end",
            "create function reads_clock_tvf() returns int with schemabinding as begin return (select count(*) from dbo.tvf_clock()) end",
            "create view v_over_clock with schemabinding as select x from dbo.v_clock");
        AreEqual(1, sim.ExecuteScalar("select objectproperty(object_id('dbo.calls_pure'), 'IsDeterministic')"));
        AreEqual(0, sim.ExecuteScalar("select objectproperty(object_id('dbo.calls_clock'), 'IsDeterministic')"));
        AreEqual(1, sim.ExecuteScalar("select objectproperty(object_id('dbo.reads_pure_view'), 'IsDeterministic')"));
        AreEqual(0, sim.ExecuteScalar("select objectproperty(object_id('dbo.reads_clock_view'), 'IsDeterministic')"));
        AreEqual(0, sim.ExecuteScalar("select objectproperty(object_id('dbo.reads_clock_tvf'), 'IsDeterministic')"));
        AreEqual(0, sim.ExecuteScalar("select objectproperty(object_id('dbo.v_over_clock'), 'IsDeterministic')"));
    }

    /// <summary>
    /// The verdict is computed per read rather than frozen at CREATE, so a
    /// callee that changes moves the caller. Reaching that state takes the
    /// route real leaves open — the schema-binding gate blocks the ALTER while
    /// the caller stands (Msg 3729, covered in
    /// <c>SchemaBindingDependencyTests</c>), so the caller is dropped and
    /// recreated around it.
    /// </summary>
    [TestMethod]
    public void RedefiningCallee_MovesTheCallersVerdict()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create function leaf(@a int) returns int with schemabinding as begin return @a + 1 end",
            "create function caller(@a int) returns int with schemabinding as begin return dbo.leaf(@a) end");
        AreEqual(1, sim.ExecuteScalar("select objectproperty(object_id('dbo.caller'), 'IsDeterministic')"));

        sim.ExecuteBatches(
            "drop function caller",
            "alter function leaf(@a int) returns int with schemabinding as begin return (@a + @@spid) end",
            "create function caller(@a int) returns int with schemabinding as begin return dbo.leaf(@a) end");
        AreEqual(0, sim.ExecuteScalar("select objectproperty(object_id('dbo.caller'), 'IsDeterministic')"));
    }

    /// <summary>
    /// A reference cycle terminates. Real refuses to schema-bind a function
    /// to itself, so there is no reference answer to match — the contract
    /// here is only that the walk halts and the module's own body still
    /// decides.
    /// </summary>
    [TestMethod]
    public void SelfReference_Terminates()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create function f(@a int) returns int with schemabinding as begin if @a > 0 return dbo.f(@a - 1) return 0 end");
        AreEqual(1, sim.ExecuteScalar("select objectproperty(object_id('dbo.f'), 'IsDeterministic')"));
    }

    /// <summary>
    /// Real reports <c>IsDeterministic</c> only for the module kinds that
    /// can carry the property; everything else is NULL rather than 0.
    /// </summary>
    [TestMethod]
    public void NonModuleObjects_ReportNull()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (x int)",
            "create sequence s as int start with 1",
            "create synonym syn for dbo.t",
            "create procedure p as select 1",
            "create trigger trg on t after insert as select 1");
        AreEqual(DBNull.Value, sim.ExecuteScalar("select objectproperty(object_id('dbo.t'), 'IsDeterministic')"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select objectproperty(object_id('dbo.s'), 'IsDeterministic')"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select objectproperty(object_id('dbo.syn'), 'IsDeterministic')"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select objectproperty(object_id('dbo.p'), 'IsDeterministic')"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select objectproperty(object_id('dbo.trg'), 'IsDeterministic')"));
    }

    /// <summary>
    /// <c>IsSchemaBound</c> tracks the option on every schema-bindable module
    /// kind, is 0 for a procedure (which can never carry it), and NULL for a
    /// non-module object — all probe-confirmed.
    /// </summary>
    [TestMethod]
    public void IsSchemaBound_CoversEveryModuleKind()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (x int)",
            "create view v_bound with schemabinding as select cast(1 as int) x",
            "create view v_plain as select cast(1 as int) x",
            "create function fn_bound() returns int with schemabinding as begin return 1 end",
            "create function fn_plain() returns int as begin return 1 end",
            "create function itvf_bound() returns table with schemabinding as return (select cast(1 as int) x)",
            "create function mstvf_bound() returns @r table (x int) with schemabinding as begin return end",
            "create procedure p as select 1",
            "create trigger trg on t after insert as select 1");
        AreEqual(1, sim.ExecuteScalar("select objectproperty(object_id('dbo.v_bound'), 'IsSchemaBound')"));
        AreEqual(0, sim.ExecuteScalar("select objectproperty(object_id('dbo.v_plain'), 'IsSchemaBound')"));
        AreEqual(1, sim.ExecuteScalar("select objectproperty(object_id('dbo.fn_bound'), 'IsSchemaBound')"));
        AreEqual(0, sim.ExecuteScalar("select objectproperty(object_id('dbo.fn_plain'), 'IsSchemaBound')"));
        AreEqual(1, sim.ExecuteScalar("select objectproperty(object_id('dbo.itvf_bound'), 'IsSchemaBound')"));
        AreEqual(1, sim.ExecuteScalar("select objectproperty(object_id('dbo.mstvf_bound'), 'IsSchemaBound')"));
        AreEqual(0, sim.ExecuteScalar("select objectproperty(object_id('dbo.p'), 'IsSchemaBound')"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select objectproperty(object_id('dbo.t'), 'IsSchemaBound')"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select objectproperty(object_id('dbo.trg'), 'IsSchemaBound')"));
    }

    /// <summary>
    /// <c>sys.sql_modules.is_schema_bound</c> carries the function's flag
    /// alongside the view's, and <c>OBJECTPROPERTYEX</c> answers both
    /// properties identically to <c>OBJECTPROPERTY</c>.
    /// </summary>
    [TestMethod]
    public void SqlModulesAndObjectPropertyEx_AgreeWithObjectProperty()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create function fn_bound() returns int with schemabinding as begin return 1 end",
            "create function fn_plain() returns int as begin return 1 end");
        IsTrue(sim.ExecuteScalar<bool>("select is_schema_bound from sys.sql_modules where object_id = object_id('dbo.fn_bound')"));
        IsFalse(sim.ExecuteScalar<bool>("select is_schema_bound from sys.sql_modules where object_id = object_id('dbo.fn_plain')"));
        AreEqual(1, sim.ExecuteScalar("select cast(objectpropertyex(object_id('dbo.fn_bound'), 'IsSchemaBound') as int)"));
        AreEqual(1, sim.ExecuteScalar("select cast(objectpropertyex(object_id('dbo.fn_bound'), 'IsDeterministic') as int)"));
        AreEqual(0, sim.ExecuteScalar("select cast(objectpropertyex(object_id('dbo.fn_plain'), 'IsDeterministic') as int)"));
    }
}
