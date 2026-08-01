using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// CREATE-time binding of module bodies: the body of a procedure, scalar UDF,
/// multi-statement TVF or trigger is bound when the module is created, and a
/// binder error aborts the CREATE. Only missing-object resolution defers.
/// Every case is probe-confirmed against SQL Server 2025 (2026-08-01).
/// </summary>
[TestClass]
public sealed class ModuleBodyBindingTests
{
    /// <summary>
    /// Shared fixture: a table with a legacy-LOB column, a trigger parent, a
    /// table type, a one-parameter scalar UDF and a one-parameter procedure.
    /// </summary>
    private static Simulation WithFixture()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            """
            create table dbo.bt (id int not null, nm nvarchar(50) null, nt ntext null);
            create table dbo.tg (id int not null, nm nvarchar(20) null)
            """,
            "create type dbo.tt as table (id int not null)",
            "create function dbo.one(@a int) returns int as begin return @a; end",
            "create procedure dbo.callee @a int as select @a");
        return sim;
    }

    private static int ObjectCount(Simulation sim, string name)
        => sim.ExecuteScalar<int>($"select count(*) from sys.objects where name = '{name}'");

    // === What binds: the CREATE fails and the module is not created ===

    [TestMethod]
    [DataRow("select nosuchcol from dbo.bt", 207)]
    [DataRow("select id from dbo.bt a join dbo.bt b on a.id = b.id", 209)]
    [DataRow("select id, nm from dbo.bt group by id", 8120)]
    [DataRow("select id from dbo.bt order by nt", 306)]
    [DataRow("select id from dbo.bt order by 5", 108)]
    [DataRow("select from dbo.bt", 156)]
    [DataRow("select @novar", 137)]
    [DataRow("declare @v int; declare @v int; select @v", 134)]
    [DataRow("break", 135)]
    [DataRow("select cast(1 as bit) + cast(0 as bit)", 402)]
    [DataRow("select dbo.one(1, 2)", 8144)]
    [DataRow("declare @t table (a int); select nosuchcol from @t", 207)]
    public void ProcedureBody_BinderError_FailsCreateAndLeavesNoProcedure(string body, int expectedNumber)
    {
        var sim = WithFixture();
        _ = sim.AssertSqlError($"create procedure dbo.pbind as {body}", expectedNumber);
        AreEqual(0, ObjectCount(sim, "pbind"));
    }

    /// <summary>
    /// The bind reaches a never-taken branch: real binds the whole body, not
    /// just the reachable statements.
    /// </summary>
    [TestMethod]
    [DataRow("if 1 = 0 select nosuchcol from dbo.bt")]
    [DataRow("while 1 = 0 begin select nosuchcol from dbo.bt; end")]
    public void ProcedureBody_DeadBranch_StillBinds(string body)
    {
        var sim = WithFixture();
        _ = sim.AssertSqlError($"create procedure dbo.pbind as {body}", 207);
        AreEqual(0, ObjectCount(sim, "pbind"));
    }

    /// <summary>
    /// A READONLY table-valued parameter the body writes to is real's Msg 10700
    /// at CREATE — the case the over-permissive register named.
    /// </summary>
    [TestMethod]
    public void ProcedureBody_WritesReadOnlyTvp_RaisesMsg10700AtCreate()
    {
        var sim = WithFixture();
        sim.AssertSqlError(
            "create procedure dbo.pbind @rows dbo.tt readonly as insert @rows values (1)",
            10700,
            "The table-valued parameter \"@rows\" is READONLY and cannot be modified.");
        AreEqual(0, ObjectCount(sim, "pbind"));
    }

    [TestMethod]
    public void ScalarFunctionBody_BinderError_FailsCreate()
    {
        var sim = WithFixture();
        _ = sim.AssertSqlError(
            "create function dbo.fbind() returns int as begin return (select nosuchcol from dbo.bt); end", 207);
        AreEqual(0, ObjectCount(sim, "fbind"));
    }

    [TestMethod]
    public void MultiStatementTvfBody_BinderError_FailsCreate()
    {
        var sim = WithFixture();
        _ = sim.AssertSqlError(
            "create function dbo.fbind() returns @r table (a int) as begin insert @r select nosuchcol from dbo.bt; return; end", 207);
        AreEqual(0, ObjectCount(sim, "fbind"));
    }

    [TestMethod]
    [DataRow("select nosuchcol from dbo.tg")]
    [DataRow("select nosuchcol from inserted")]
    [DataRow("select nosuchcol from deleted")]
    public void TriggerBody_BinderError_FailsCreate(string body)
    {
        var sim = WithFixture();
        _ = sim.AssertSqlError($"create trigger dbo.trbind on dbo.tg after insert, update, delete as {body}", 207);
        AreEqual(0, ObjectCount(sim, "trbind"));
    }

    /// <summary>
    /// A DDL trigger's body binds the same way; <c>INSERTED</c> / <c>DELETED</c>
    /// don't exist there but a plain table reference still resolves.
    /// </summary>
    [TestMethod]
    public void DdlTriggerBody_BinderError_FailsCreate()
    {
        var sim = WithFixture();
        _ = sim.AssertSqlError("create trigger trddl on database for create_table as select nosuchcol from dbo.tg", 207);
        AreEqual(0, sim.ExecuteScalar<int>("select count(*) from sys.triggers where name = 'trddl'"));
    }

    // === What defers: missing objects ===

    [TestMethod]
    [DataRow("select * from dbo.missing_xyz")]
    [DataRow("select nocol from dbo.missing_xyz where alsonone = 1")]
    [DataRow("select m.anycol from dbo.missing_xyz m")]
    [DataRow("select nosuchcol from dbo.bt b, dbo.missing_xyz m")]
    [DataRow("select b.nosuchcol, m.x from dbo.bt b join dbo.missing_xyz m on b.id = m.id")]
    [DataRow("select * from dbo.missing_tvf_xyz(1)")]
    [DataRow("select dbo.missing_fn_xyz(1)")]
    [DataRow("exec dbo.missing_proc_xyz")]
    [DataRow("insert into dbo.missing_xyz (a, b) values (1, 2)")]
    [DataRow("drop table dbo.missing_xyz")]
    [DataRow("select * from #no_such_temp")]
    [DataRow("select * from no_such_db_xyz.dbo.t")]
    [DataRow("create table #made (a int); select nosuchcol from #made")]
    [DataRow("create table dbo.made (a int); select nosuchcol from dbo.made")]
    [DataRow("select id into dbo.copied from dbo.bt; select nosuchcol from dbo.copied")]
    [DataRow("exec dbo.callee 1, 2, 3")]
    [DataRow("exec sp_executesql N'select nosuchcol from dbo.bt'")]
    [DataRow("exec('select nosuchcol from dbo.bt')")]
    public void ProcedureBody_MissingObject_Defers(string body)
    {
        var sim = WithFixture();
        sim.ExecuteBatches($"create procedure dbo.pdefer as {body}");
        AreEqual(1, ObjectCount(sim, "pdefer"));
    }

    /// <summary>
    /// A body that names itself has to defer for a recursive module to be
    /// creatable at all — real's deferred resolution is what makes it legal.
    /// </summary>
    [TestMethod]
    public void RecursiveScalarFunction_Creates()
    {
        var sim = WithFixture();
        sim.ExecuteBatches("create function dbo.fact(@n int) returns int as begin if @n <= 1 return 1; return @n * dbo.fact(@n - 1); end");
        AreEqual(120, sim.ExecuteScalar("select dbo.fact(5)"));
    }

    /// <summary>
    /// A parameter stands in as a typed NULL while the body binds, so a write
    /// of one into a NOT NULL column must not report Msg 515 from the CREATE.
    /// </summary>
    [TestMethod]
    public void ProcedureBody_InsertsParameterIntoNotNullColumn_Creates()
    {
        var sim = WithFixture();
        sim.ExecuteBatches("create procedure dbo.pins @id int as insert dbo.bt (id) values (@id)");
        _ = sim.ExecuteNonQuery("exec dbo.pins 7");
        AreEqual(1, sim.ExecuteScalar("select count(*) from dbo.bt where id = 7"));
    }

    /// <summary>
    /// Binding must not call a scalar UDF: the FROM-less-SELECT fast path bakes
    /// projection values during the parse, which would otherwise run the body.
    /// </summary>
    [TestMethod]
    public void ProcedureBody_CallingScalarUdf_DoesNotRunItAtCreate()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.nums (n int, d int); insert dbo.nums values (1, 0)",
            "create function dbo.boom() returns int as begin return (select n / d from dbo.nums); end",
            "create procedure dbo.pcall as select dbo.boom()");
        AreEqual(1, ObjectCount(sim, "pcall"));
        _ = sim.AssertSqlError("exec dbo.pcall", 8134);
    }

    // === Statement granularity and the mixed cases ===

    /// <summary>
    /// A statement naming a missing object defers as a whole — the bad column
    /// beside it isn't reported. A later statement over existing objects still
    /// binds, in either order.
    /// </summary>
    [TestMethod]
    [DataRow("select * from dbo.missing_xyz; select nosuchcol from dbo.bt")]
    [DataRow("select nosuchcol from dbo.bt; select * from dbo.missing_xyz")]
    public void ProcedureBody_MissingObjectInOneStatement_StillBindsTheOther(string body)
    {
        var sim = WithFixture();
        _ = sim.AssertSqlError($"create procedure dbo.pbind as {body}", 207);
        AreEqual(0, ObjectCount(sim, "pbind"));
    }

    /// <summary>
    /// A missing DML target is the one deferral the simulator reaches by
    /// swallowing a raised Msg 208 rather than by a placeholder source, and its
    /// recovery scan leaves the parse cursor unreliable — so the bind stops
    /// there and the rest of the body binds at first invocation instead.
    /// </summary>
    [TestMethod]
    public void ProcedureBody_MissingDmlTarget_AbandonsTheRestOfTheBind()
    {
        var sim = WithFixture();
        sim.ExecuteBatches("create procedure dbo.pdefer as insert into dbo.missing_xyz values (1); select nosuchcol from dbo.bt");
        AreEqual(1, ObjectCount(sim, "pdefer"));
        _ = sim.AssertSqlError("exec dbo.pdefer", 208);
    }

    /// <summary>
    /// A bare (no BEGIN / END) IF whose branch names a missing DML target keeps
    /// its ELSE — the shape that used to lose the ELSE to the recovery scan.
    /// </summary>
    [TestMethod]
    public void ProcedureBody_BareIfElseOverMissingTarget_Creates()
    {
        var sim = WithFixture();
        sim.ExecuteBatches("""
            create procedure dbo.pdefer @x int as
            if @x = 1
                insert into dbo.missing_xyz values (1)
            else
                select 'else-ran' as v
            """);
        AreEqual(1, ObjectCount(sim, "pdefer"));
        AreEqual("else-ran", sim.ExecuteScalar("exec dbo.pdefer 0"));
    }

    // === Replacement: the previous body survives a failed bind ===

    [TestMethod]
    [DataRow("alter")]
    [DataRow("create or alter")]
    public void FailedRebind_LeavesThePreviousBodyStanding(string verb)
    {
        var sim = WithFixture();
        sim.ExecuteBatches("create procedure dbo.psurvive as select 'original' as v");
        _ = sim.AssertSqlError($"{verb} procedure dbo.psurvive as select nosuchcol from dbo.bt", 207);
        AreEqual("original", sim.ExecuteScalar("exec dbo.psurvive"));
    }

    /// <summary>
    /// The body error beats the name-collision gates: probe-confirmed that real
    /// reports it rather than Msg 2714 for a plain CREATE over a taken name, or
    /// Msg 208 for a bare ALTER of a name nothing holds.
    /// </summary>
    [TestMethod]
    public void BinderError_PrecedesTheNameCollisionGates()
    {
        var sim = WithFixture();
        sim.ExecuteBatches("create procedure dbo.ptaken as select 1 as v");
        _ = sim.AssertSqlError("create procedure dbo.ptaken as select nosuchcol from dbo.bt", 207);
        _ = sim.AssertSqlError("alter procedure dbo.pabsent as select nosuchcol from dbo.bt", 207);
    }

    // === Error shape ===

    /// <summary>
    /// A bind error carries the body's CREATE-relative line and the module's
    /// <em>unqualified</em> name — the opposite of the schema-qualified
    /// attribution an invocation-time procedure-body error carries.
    /// </summary>
    [TestMethod]
    public void BindError_ReportsCreateRelativeLineAndUnqualifiedName()
    {
        var sim = WithFixture();
        var ex = sim.AssertSqlError("""
            create procedure dbo.pshape
            as
            begin
                select 1;
                select nosuchcol from dbo.bt;
            end
            """, 207);
        AreEqual(5, ex.LineNumber);
        AreEqual("pshape", ex.Procedure);
        AreEqual(16, ex.Class);
        AreEqual(1, ex.State);
    }

    /// <summary>
    /// The error is an ordinary catchable one, so a CREATE issued through
    /// dynamic SQL inside TRY / CATCH lands in the CATCH with the module name
    /// on <c>ERROR_PROCEDURE()</c>.
    /// </summary>
    [TestMethod]
    public void BindError_IsCatchable()
    {
        var sim = WithFixture();
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand("""
            begin try
                exec('create procedure dbo.pcaught as select nosuchcol from dbo.bt');
            end try
            begin catch
                select error_number() as n, error_procedure() as p, error_line() as l;
            end catch
            """);
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(207, reader.GetInt32(0));
        AreEqual("pcaught", reader.GetString(1));
        AreEqual(1, reader.GetInt32(2));
    }

    // === Deferred errors still surface at invocation, unchanged ===

    [TestMethod]
    public void DeferredMissingObject_StillRaisesAtInvocationWithTheSameShape()
    {
        var sim = WithFixture();
        sim.ExecuteBatches("""
            create procedure dbo.pdefer as
            select 1;
            select * from dbo.missing_xyz;
            """);
        var ex = sim.AssertSqlError("exec dbo.pdefer", 208);
        AreEqual(3, ex.LineNumber);
        AreEqual("dbo.pdefer", ex.Procedure);
    }

    /// <summary>
    /// A deferred body works once the object it named exists — the whole point
    /// of deferring rather than rejecting.
    /// </summary>
    [TestMethod]
    public void DeferredBody_RunsOnceTheObjectExists()
    {
        var sim = WithFixture();
        sim.ExecuteBatches(
            "create procedure dbo.plater as select count(*) from dbo.made_later",
            "create table dbo.made_later (id int)",
            "insert dbo.made_later values (1), (2)");
        AreEqual(2, sim.ExecuteScalar("exec dbo.plater"));
    }

    /// <summary>
    /// An unmodeled feature in a body is a simulator gap rather than real's
    /// binder speaking, so it must not refuse a module real accepts — the
    /// <c>NotSupportedException</c> keeps surfacing at invocation.
    /// </summary>
    [TestMethod]
    public void UnmodeledFeatureInBody_DoesNotBlockTheCreate()
    {
        var sim = WithFixture();
        sim.ExecuteBatches("create procedure dbo.punmodeled as begin distributed transaction");
        AreEqual(1, ObjectCount(sim, "punmodeled"));
        _ = Throws<NotSupportedException>(() => sim.ExecuteNonQuery("exec dbo.punmodeled"));
    }

    /// <summary>
    /// Binding is a parse, not an execution: nothing the body would write, and
    /// no identity or sequence value it would burn, moves.
    /// </summary>
    [TestMethod]
    public void Binding_DoesNotMutateAnything()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.ids (id int identity(1, 1) primary key, v int not null)",
            "create sequence dbo.seq as int start with 1 increment by 1",
            "create procedure dbo.pwrite as insert dbo.ids (v) values (next value for dbo.seq)");
        _ = sim.ExecuteNonQuery("exec dbo.pwrite");
        AreEqual(1, sim.ExecuteScalar("select id from dbo.ids"));
        AreEqual(1, sim.ExecuteScalar("select v from dbo.ids"));
    }

    /// <summary>
    /// The bind resolves a DML statement's target and columns without walking
    /// the rows, so a per-row runtime failure over the table's current contents
    /// can't refuse the CREATE.
    /// </summary>
    [TestMethod]
    [DataRow("delete dbo.zero where id / d = 1")]
    [DataRow("update dbo.zero set id = id / d")]
    [DataRow("merge dbo.zero as tgt using (select 1 as id) as src on tgt.id = src.id / 0 when matched then update set d = 1;")]
    public void DmlBodyOverRowsThatWouldFail_StillCreates(string body)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.zero (id int not null, d int not null); insert dbo.zero values (1, 0)",
            $"create procedure dbo.pdml as {body}");
        AreEqual(1, ObjectCount(sim, "pdml"));
        _ = sim.AssertSqlError("exec dbo.pdml", 8134);
    }

    /// <summary>
    /// The column references a DML statement resolves at parse — an UPDATE's
    /// SET target, an INSERT's column list, an INSERT…SELECT's projection —
    /// bind, so the walk-free bind isn't a bind-free one.
    /// </summary>
    [TestMethod]
    [DataRow("update dbo.bt set nosuchcol = 1")]
    [DataRow("insert dbo.bt (nosuchcol) values (1)")]
    [DataRow("insert dbo.bt (id) select nosuchcol from dbo.bt")]
    public void DmlBodyWithBadColumn_FailsCreate(string body)
    {
        var sim = WithFixture();
        _ = sim.AssertSqlError($"create procedure dbo.pbind as {body}", 207);
        AreEqual(0, ObjectCount(sim, "pbind"));
    }

    /// <summary>
    /// The clauses whose column references used to resolve only per row —
    /// WHERE / HAVING / a MERGE's ON / the value side of a SET — bind through
    /// the static type path, so real's Msg 207 at CREATE arrives. The
    /// legacy-LOB argument gate rides the same walk (Msg 8116).
    /// </summary>
    [TestMethod]
    [DataRow("select id from dbo.bt where nosuchcol = 1", 207)]
    [DataRow("update dbo.bt set id = 1 where nosuchcol = 1", 207)]
    [DataRow("update dbo.bt set id = nosuchcol", 207)]
    [DataRow("delete dbo.bt where nosuchcol = 1", 207)]
    [DataRow("merge dbo.bt as tgt using (select 1 as id) as src on tgt.nosuchcol = src.id when matched then update set id = 1;", 207)]
    [DataRow("select len(nt) from dbo.bt", 8116)]
    [DataRow("select id from dbo.bt where len(nt) > 1", 8116)]
    public void PredicateAndSetValueReference_BindsAtCreate(string body, int expectedNumber)
    {
        var sim = WithFixture();
        _ = sim.AssertSqlError($"create procedure dbo.pbind as {body}", expectedNumber);
        AreEqual(0, ObjectCount(sim, "pbind"));
    }

    /// <summary>
    /// <strong>Divergence.</strong> An aggregate whose only column reference
    /// fails to resolve locally is taken for one over an enclosing query —
    /// unmodeled, so it raises <see cref="NotSupportedException"/>, which a
    /// bind swallows rather than refusing a module real accepts. Real reports
    /// Msg 207 at CREATE here.
    /// </summary>
    [TestMethod]
    public void HavingAggregateOverUnknownColumn_IsNotReachedByTheBind()
    {
        var sim = WithFixture();
        sim.ExecuteBatches("create procedure dbo.presidual as select id from dbo.bt group by id having max(nosuchcol) = 1");
        AreEqual(1, ObjectCount(sim, "presidual"));
    }
}
