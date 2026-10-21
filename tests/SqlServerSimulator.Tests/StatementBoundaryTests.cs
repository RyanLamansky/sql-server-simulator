using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Semicolon-less statement sequences: SQL Server accepts one statement
/// immediately followed by another with no separating <c>;</c>, and SSMS
/// generates such batches. The simulator recognizes the second statement's
/// leading keyword through a single shared predicate
/// (<c>Simulation.IsStatementBoundary</c>) that every consumer — the dispatch
/// loop, the SELECT projection-list terminator, the EXEC-argument scanner, and
/// the principal-DDL parse-and-discard tail — routes through, so the full
/// statement-keyword set (including <c>EXEC</c>/<c>EXECUTE</c>, <c>USE</c>,
/// <c>GRANT</c>, <c>FETCH</c>, …) terminates the preceding statement uniformly.
/// The cross-product below pairs each kind of preceding statement with each
/// kind of following statement, joined by a newline only, and proves the batch
/// parses and runs to the trailing sentinel <c>SELECT 42</c>.
/// </summary>
[TestClass]
public sealed class StatementBoundaryTests
{
    // Shared prelude gives every combination a table, a declared+opened
    // cursor, and an @rc slot so FETCH / GRANT / EXEC @rc all have targets.
    // The load-bearing junction is the newline-only join between the
    // preceding and following statements — no `;` separates them. The trailing
    // SELECT 42 sentinel is read from the LAST result set (a following such as
    // SELECT / FETCH emits its own result set first), proving the whole batch
    // parsed and ran to completion; pre-fix these threw Msg 102.
    private static int RunPair(string preceding, string following)
    {
        using var reader = new Simulation().ExecuteReader($"""
            create table t (id int not null);
            insert into t values (1);
            declare @rc int;
            declare c cursor for select id from t;
            open c;
            {preceding}
            {following};
            select 42
            """);
        object? last = null;
        do
        {
            while (reader.Read())
                last = reader[0];
        }
        while (reader.NextResult());
        return (int)last!;
    }

    [TestMethod]
    [DataRow("select 1")]
    [DataRow("declare @x int")]
    [DataRow("if 1 = 1 select 1")]
    [DataRow("set nocount on")]
    [DataRow("insert into t values (2)")]
    public void FollowedByExecProc_NoSemicolon_Parses(string preceding)
        => AreEqual(42, RunPair(preceding, "exec xp_qv N'a', N'b'"));

    [TestMethod]
    [DataRow("select 1")]
    [DataRow("declare @x int")]
    [DataRow("if 1 = 1 select 1")]
    [DataRow("set nocount on")]
    [DataRow("insert into t values (2)")]
    public void FollowedByExecReturnCapture_NoSemicolon_Parses(string preceding)
        => AreEqual(42, RunPair(preceding, "exec @rc = xp_qv N'a', N'b'"));

    [TestMethod]
    [DataRow("select 1")]
    [DataRow("declare @x int")]
    [DataRow("if 1 = 1 select 1")]
    [DataRow("set nocount on")]
    [DataRow("insert into t values (2)")]
    public void FollowedByUse_NoSemicolon_Parses(string preceding)
        => AreEqual(42, RunPair(preceding, "use master"));

    [TestMethod]
    [DataRow("select 1")]
    [DataRow("declare @x int")]
    [DataRow("if 1 = 1 select 1")]
    [DataRow("set nocount on")]
    [DataRow("insert into t values (2)")]
    public void FollowedByGrant_NoSemicolon_Parses(string preceding)
        => AreEqual(42, RunPair(preceding, "grant select on t to public"));

    [TestMethod]
    [DataRow("select 1")]
    [DataRow("declare @x int")]
    [DataRow("if 1 = 1 select 1")]
    [DataRow("set nocount on")]
    [DataRow("insert into t values (2)")]
    public void FollowedByFetch_NoSemicolon_Parses(string preceding)
        => AreEqual(42, RunPair(preceding, "fetch c"));

    [TestMethod]
    [DataRow("select 1")]
    [DataRow("declare @x int")]
    [DataRow("if 1 = 1 select 1")]
    [DataRow("set nocount on")]
    [DataRow("insert into t values (2)")]
    public void FollowedBySelect_NoSemicolon_Parses(string preceding)
        => AreEqual(42, RunPair(preceding, "select 1"));

    // WITH after a SELECT projection stays the specific Msg 319 ("Incorrect
    // syntax near … expects a preceding statement terminated with a
    // semicolon") rather than being swallowed as a generic boundary — the
    // CTE-continuation case is checked ahead of the shared boundary predicate.
    [TestMethod]
    public void SelectFollowedByCte_WithoutSemicolon_RaisesMsg319()
        => new Simulation().AssertSqlError(
            "select 1 with x as (select 1 as n) select n from x",
            319);

    // The exact SSMS Object-Explorer AlwaysOn availability probe: three
    // statements, only the middle two separated by a semicolon. xp_qv returns
    // status 2 (AlwaysOn *available* — the edition-capability answer for the
    // simulated Enterprise EngineEdition; distinct from IsHadrEnabled = 0
    // meaning not-configured), so ISNULL(@alwayson, -1) yields 2. SMO's
    // Databases enumeration is HADR-aware and requires this.
    [TestMethod]
    public void SsmsAlwaysOnProbe_ReturnsTwo()
        => AreEqual(2, new Simulation().ExecuteScalar<int>("""
            DECLARE @alwayson INT
            EXECUTE @alwayson = master.dbo.xp_qv N'3641190370', @@SERVICENAME;
            SELECT ISNULL(@alwayson,-1) AS [AlwaysOn]
            """));

    [TestMethod]
    [DataRow("exec xp_qv N'x', N'y'")]
    [DataRow("exec dbo.xp_qv N'x', N'y'")]
    [DataRow("exec master.dbo.xp_qv N'x', N'y'")]
    public void XpQv_YieldsNoResultSet(string call)
    {
        using var reader = new Simulation().ExecuteReader(call);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void XpQv_ReturnStatus_IsTwo()
        => AreEqual(2, new Simulation().ExecuteScalar<int>("""
            declare @rc int;
            exec @rc = xp_qv N'x', N'y';
            select @rc
            """));

    // --- The other half: input the preceding statement does NOT own ---
    //
    // The dispatch loop advances one token past a parser that stopped on its
    // last consumed token, which silently swallowed a stray token after any
    // parser that had already stopped past its own input: `DECLARE @x int = 1
    // zzz` ran clean where real raises Msg 102. A statement kind whose parser
    // stops at its first un-consumed token now says so, and a non-boundary
    // token after one of those is rejected. Every case below is
    // probe-confirmed against SQL Server 2025 (2026-08-06).

    [TestMethod]
    [DataRow("declare @x int = 1 zzz")]
    [DataRow("declare @x int zzz")]
    [DataRow("declare @x int = 1, @y int = 2 zzz")]
    [DataRow("print 'x' zzz")]
    [DataRow("insert into t values (2) zzz")]
    [DataRow("update t set a = 1 zzz")]
    [DataRow("delete from t zzz")]
    // Wrapped in a transaction so COMMIT succeeds: a statement that raises an
    // error of its own reports that error first, since the simulator executes
    // as it parses where real parses the whole batch first — see the
    // batch-compilation section of control-flow.md.
    [DataRow("begin transaction commit zzz")]
    [DataRow("waitfor delay '00:00:00' zzz")]
    [DataRow("raiserror('m', 0, 1) zzz")]
    [DataRow("throw 50000, 'm', 1 zzz")]
    [DataRow("revert zzz")]
    [DataRow("reconfigure zzz")]
    [DataRow("use master zzz")]
    [DataRow("grant select on t to public zzz")]
    [DataRow("revoke select on t from public zzz")]
    [DataRow("deny select on t to public zzz")]
    [DataRow("drop table t zzz")]
    public void TrailingTokenAfterAStatementThatOwnsItsInput_RaisesMsg102(string statement)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int null); insert t values (1)");
        var ex = sim.AssertSqlError(statement, 102);
        Assert.Contains("zzz", ex.Message);
    }

    /// <summary>
    /// The rejection must not reach a statement kind whose parser genuinely
    /// ends on its last consumed token — those still need the advance, and a
    /// following statement is not a stray token.
    /// </summary>
    [TestMethod]
    [DataRow("set nocount on", "select 42")]
    [DataRow("declare @x int = 1", "select 42")]
    [DataRow("print 'x'", "select 42")]
    [DataRow("insert into t values (2)", "select 42")]
    [DataRow("begin transaction commit", "select 42")]
    [DataRow("declare @y int set @y = 1", "select 42")]
    public void AStatementFollowingAnotherIsNotATrailingToken(string preceding, string following)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int null); insert t values (1)");
        AreEqual(42, sim.ExecuteScalar($"{preceding}\n{following}"));
    }

    /// <summary>
    /// The marker is per-statement: one statement declaring that it owns its
    /// input must not make the <em>next</em> statement's legitimate tail a
    /// syntax error. Before the flag was cleared unconditionally, an INSERT
    /// followed by `set nocount on` reported Msg 102 near 'on'.
    /// </summary>
    [TestMethod]
    public void TheMarkerDoesNotLeakToTheFollowingStatement()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int null)");
        AreEqual(42, sim.ExecuteScalar("insert into t values (1)\nset nocount on\nselect 42"));
    }

    /// <summary>
    /// A comma left after a complete SELECT is unconsumed input, reported at
    /// the comma (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select 1 where 1 in (null), 2 where 1 not in (2, null)")]
    [DataRow("select a from (values (1)) v (a) where a = 1, 5")]
    public void SelectFollowedByAComma_RaisesMsg102AtTheComma(string sql)
        => new Simulation().AssertSqlError(sql, 102, "Incorrect syntax near ','.");

    /// <summary>A select list's comma with no element before it is Msg 102 at itself (probed 2026-10-21 against SQL Server 2025).</summary>
    [TestMethod]
    [DataRow("select 1, , 2")]
    [DataRow("select 1 a, , 2")]
    [DataRow("select , 1")]
    public void ASelectListCommaWithNoElement_RaisesMsg102AtTheComma(string sql)
        => new Simulation().AssertSqlError(sql, 102, "Incorrect syntax near ','.");

    [TestMethod]
    [DataRow("select 1 where exists (select 1) = 1", "=")]
    [DataRow("select 1 where 'a' like 'a' = 1", "=")]
    [DataRow("select 1 where 1 = 1 <", "<")]
    [DataRow("select 1 from (values (1)) v(a) =", "=")]
    [DataRow("select 1 a where 1 = 1 option (recompile) >", ">")]
    public void AnOperatorAfterAQuery_IsASyntaxErrorAtTheOperator(string batch, string near)
        => new Simulation().AssertSqlError(batch, 102, $"Incorrect syntax near '{near}'.");

    [TestMethod]
    [DataRow("create index zix on zt (b) zzbogus", "zzbogus")]
    [DataRow("create index zix on zt (b) with (fillfactor zzbogus = 80)", "zzbogus")]
    [DataRow("create sequence zs as int cycle zzbogus", "zzbogus")]
    [DataRow("create sequence zs zzbogus as int", "zzbogus")]
    [DataRow("create type ztt as table (a int) zzbogus", "zzbogus")]
    [DataRow("alter table zt alter column b varchar(20) not null zzbogus", "zzbogus")]
    [DataRow("create synonym zsy for dbo.zt zzbogus", "zzbogus")]
    [DataRow("create role zr authorization dbo zzbogus", "zzbogus")]
    [DataRow("create role zr dbo", "dbo")]
    [DataRow("create user zu without login zzbogus", "zzbogus")]
    [DataRow("create statistics zst on zt (b) with fullscan zzbogus", "zzbogus")]
    [DataRow("alter index all on zt rebuild zzbogus", "zzbogus")]
    [DataRow("begin tran zzbogus zx", "zx")]
    [DataRow("create xml schema collection zxc as N'<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"e\"/></xs:schema>' zzbogus", "zzbogus")]
    [DataRow("while 1 = 0 begin break end zzbogus", "zzbogus")]
    [DataRow("while 1 = 0 begin break zzbogus end", "zzbogus")]
    [DataRow("create table zv (id int, unique (id)) zzbogus", "zzbogus")]
    [DataRow("create table zv (id int) )", ")")]
    [DataRow("create table zv (id int) with (data_compression = page) zzbogus", "zzbogus")]
    [DataRow("alter table zt drop column b zzbogus", "zzbogus")]
    [DataRow("alter table zt drop column missing b", "b")]
    [DataRow("alter table zt drop constraint zck zzbogus", "zzbogus")]
    [DataRow("alter table zt zzbogus add c int", "zzbogus")]
    [DataRow("create index zix on zt (b) with (fillfactor = 80 zzbogus)", "zzbogus")]
    [DataRow("create index zix on zt (b) with (= 80)", "=")]
    [DataRow("create index zix on zt (b) with (fillfactor = 80 80)", "80")]
    [DataRow("begin try select 1 end try begin catch end catch zzbogus", "zzbogus")]
    public void AStrayTokenAfterTheStatement_IsASyntaxErrorAtIt(string statement, string near)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table zt (a int not null primary key, b varchar(10), constraint zck check (a > 0))");
        sim.AssertSqlError(statement, 102, $"Incorrect syntax near '{near}'.");
    }

    /// <summary>
    /// An <c>ALTER TABLE</c> word that opens no action is the syntax error at
    /// it, and <c>DROP</c> takes a constraint's name without its keyword,
    /// though <c>IF EXISTS</c> takes the keyword (probed 2026-10-21 against
    /// SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("alter table zt column b", 156, "Incorrect syntax near the keyword 'column'.")]
    [DataRow("alter table zt drop drop column b", 156, "Incorrect syntax near the keyword 'drop'.")]
    [DataRow("alter table zt drop if exists zck", 156, "Incorrect syntax near the keyword 'if'.")]
    [DataRow("alter table zt drop b", 3728, "'b' is not a constraint.")]
    public void AlterTable_ActionWords(string statement, int number, string message)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table zt (a int not null primary key, b varchar(10), constraint zck check (a > 0))");
        sim.AssertSqlError(statement, number, message);
    }

    [TestMethod]
    public void AlterTableDrop_WithoutTheConstraintKeyword_DropsTheConstraints()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table zt (a int, b int, constraint zck check (a > 0), constraint zck2 check (a < 9));
            alter table zt drop zck, [zck2], column b;
            select count(*) from sys.columns where object_id = object_id('zt') and not exists (select 1 from sys.check_constraints)
            """));

    [TestMethod]
    [DataRow("create zzbogus procedure zp as select 1")]
    [DataRow("drop zzbogus index zix on zt")]
    [DataRow("alter zzbogus index all on zt rebuild")]
    public void AWordNamingNoObjectType_IsMsg343(string statement)
        => new Simulation().AssertSqlError(statement, 343, "Unknown object type 'zzbogus' used in a CREATE, DROP, or ALTER statement.");
}
