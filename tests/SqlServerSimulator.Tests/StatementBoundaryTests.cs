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
    // parse-before-bind entry in backlog.md.
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
}
