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
}
