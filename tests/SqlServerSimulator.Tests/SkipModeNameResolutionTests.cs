using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Deferred name resolution in skipped control-flow branches. Real SQL Server
/// binds base object names lazily, so an un-taken IF / WHILE branch (or a block
/// skipped after BREAK / CONTINUE / RETURN) that references a nonexistent table
/// / view / function compiles fine and is discarded. The simulator resolves
/// names inline with parsing, so a skip-mode FROM / function-call miss
/// substitutes placeholder metadata and the statement parses to completion (no
/// Msg 208 / 4121). Deferral is scoped to the missing base object, though: a
/// missing column on a <em>resolvable</em> table binds eagerly and raises
/// Msg 207 even in a dead branch (probe-confirmed against SQL Server 2025), and
/// a taken branch still raises. Syntax / structural errors in skipped branches
/// still raise (only name resolution of a missing object defers).
/// </summary>
[TestClass]
public sealed class SkipModeNameResolutionTests
{
    [TestMethod]
    public void IfFalse_UnknownTable_ThenFollowingStatementsRun()
        => AreEqual("ok", new Simulation().ExecuteScalar(
            "if 1 = 0 begin select * from nosuchtable end select 'ok'"));

    [TestMethod]
    public void IfFalse_UnknownTable_NoBlock()
        => AreEqual("ok", new Simulation().ExecuteScalar(
            "if 1 = 0 select * from nosuchtable select 'ok'"));

    [TestMethod]
    public void IfTrue_UnknownTable_StillRaises208()
        => new Simulation().AssertSqlError("if 1 = 1 select * from nosuchtable", 208);

    /// <summary>
    /// A missing column on a <em>resolvable</em> table is not deferred — real
    /// SQL Server binds an existing table's columns at compile time and raises
    /// Msg 207 even from an un-taken branch (probe-confirmed SQL Server 2025,
    /// 2026-07-17). Contrast <see cref="IfFalse_UnknownTable_NoBlock"/>, where
    /// the missing base object defers.
    /// </summary>
    [TestMethod]
    public void IfFalse_UnknownColumnInKnownTable_StillRaises207()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        _ = sim.AssertSqlError("if 1 = 0 begin select bad_col from t end select 'ok'", 207);
    }

    [TestMethod]
    public void IfTrue_UnknownColumn_StillRaises207()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        _ = sim.AssertSqlError("if 1 = 1 select bad_col from t", 207);
    }

    [TestMethod]
    public void IfFalse_UnknownDatabaseQualifier_Tolerated()
        => AreEqual("ok", new Simulation().ExecuteScalar(
            "if 1 = 0 select * from nosuchdb.dbo.t select 'ok'"));

    /// <summary>
    /// A malformed FROM (a keyword where a table source is required) is a
    /// structural error, not name resolution — it fires (Msg 102) before
    /// any name lookup, even in a skipped branch.
    /// </summary>
    [TestMethod]
    public void SkippedBranch_SyntaxErrorStillRaises()
        => new Simulation().AssertSqlError("if 1 = 0 select * from select 'ok'", 156);

    [TestMethod]
    public void SkippedBranch_UnknownInsertTarget_Tolerated()
        => AreEqual("ok", new Simulation().ExecuteScalar(
            "if 1 = 0 insert into nosuchtable values (1) select 'ok'"));

    [TestMethod]
    public void SkippedBranch_UnknownUpdateTarget_Tolerated()
        => AreEqual("ok", new Simulation().ExecuteScalar(
            "if 1 = 0 update nosuchtable set x = 1 select 'ok'"));

    [TestMethod]
    public void SkippedBranch_UnknownDeleteTarget_Tolerated()
        => AreEqual("ok", new Simulation().ExecuteScalar(
            "if 1 = 0 delete from nosuchtable select 'ok'"));

    [TestMethod]
    public void WhileFalse_UnknownTable_Tolerated()
        => AreEqual("ok", new Simulation().ExecuteScalar(
            "while 1 = 0 begin select * from nosuchtable end select 'ok'"));

    [TestMethod]
    public void ElseBranchSkipped_UnknownTable_Tolerated()
        => AreEqual("taken", new Simulation().ExecuteScalar(
            "if 1 = 1 select 'taken' else select * from nosuchtable"));

    [TestMethod]
    public void NestedIf_InsideSkippedBlock_Tolerated()
        => AreEqual("ok", new Simulation().ExecuteScalar(
            "if 1 = 0 begin if 1 = 1 select * from nosuchtable end select 'ok'"));

    // The taken outer branch runs; the un-taken nested IF's unknown-table
    // reference must not raise, and the nested taken statement executes.
    [TestMethod]
    public void NestedIf_TakenOuter_SkippedInner_Tolerated()
        => AreEqual("inner", new Simulation().ExecuteScalar(
            "if 1 = 1 begin if 1 = 0 select * from nosuchtable select 'inner' end"));

    // Variable declarations are compile-scoped batch-wide (probe-confirmed
    // against SQL Server 2025): a DECLARE in an un-taken branch registers
    // its slot for the whole batch, only the initializer is suppressed, and
    // duplicate names raise Msg 134 even across dead branches. SSMS's
    // server-properties batch relies on this — its Managed-Instance-only
    // block declares variables the following statements assign.
    [TestMethod]
    public void DeclareInSkippedBranch_VariableUsableAfter()
        => AreEqual(5, new Simulation().ExecuteScalar(
            "if 1 = 0 begin declare @x int end set @x = 5 select @x"));

    [TestMethod]
    public void DeclareInSkippedBranch_InitializerSuppressed()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "if 1 = 0 begin declare @y int = 42 end select @y";
        AreEqual(DBNull.Value, command.ExecuteScalar());
    }

    [TestMethod]
    public void DuplicateDeclare_AcrossDeadBranch_StillRaises134()
        => new Simulation().AssertSqlError(
            "declare @z int = 1 if 1 = 0 begin declare @z int end select @z", 134);

    [TestMethod]
    public void TableVariableDeclaredInSkippedBranch_UsableAfter()
        => AreEqual(3, new Simulation().ExecuteScalar(
            "if 1 = 0 begin declare @t table(a int) end insert @t values (3) select a from @t"));

    /// <summary>
    /// Variable-name resolution is NOT deferred (unlike table/column
    /// names) — real SQL Server raises Msg 137 at compile even for a
    /// dead branch.
    /// </summary>
    [TestMethod]
    public void SetUndeclaredVariableInSkippedBranch_StillRaises137()
        => new Simulation().AssertSqlError("if 1 = 0 begin set @never = 1 end select 'ok'", 137);

    // ---- Placeholder parse-continuation (probe matrix, 2026-07-17) ----

    /// <summary>
    /// A missing schema-qualified scalar function in an un-taken branch defers
    /// (probe-confirmed: real SQL Server binds user functions lazily too, unlike
    /// a bare 1-part call which is a compile-time Msg 195). The call parses to
    /// completion and is discarded.
    /// </summary>
    [TestMethod]
    public void IfFalse_UnknownQualifiedFunction_Tolerated()
        => AreEqual("ok", new Simulation().ExecuteScalar(
            "if 1 = 0 select dbo.no_such_fn(1, 2) select 'ok'"));

    /// <summary>
    /// A bare (1-part) unresolved function is NOT deferred — real SQL Server
    /// treats it as a missing built-in and raises Msg 195 at compile time, even
    /// in a dead branch.
    /// </summary>
    [TestMethod]
    public void IfFalse_UnknownBareFunction_StillRaises195()
        => new Simulation().AssertSqlError("if 1 = 0 select no_such_fn(1) select 'ok'", 195);

    /// <summary>
    /// The un-taken THEN branch defers, so its trailing ELSE runs — the missing
    /// function must not orphan the ELSE into a spurious Msg 102.
    /// </summary>
    [TestMethod]
    public void IfFalse_UnknownFunctionThenElse_ElseRuns()
        => AreEqual("else", new Simulation().ExecuteScalar(
            "if 1 = 0 select dbo.no_such_fn(1) as r else select 'else' as r"));

    /// <summary>
    /// The SSMS Query Store / server-properties shape: a missing table behind an
    /// EXISTS in an un-taken outer branch. Regression for the orphaned-fragment
    /// cascade — the recovery scan used to abandon this mid-parse and re-dispatch
    /// the inner branch as a bare statement (spurious Msg 102 / 156).
    /// </summary>
    [TestMethod]
    public void SkippedOuter_ExistsMissingTableWithInnerElse_Tolerated()
        => AreEqual("after", new Simulation().ExecuteScalar("""
            if 1 = 0 begin if exists(select * from missing) select 1 as r else select 2 as r end
            select 'after' as r
            """));

    /// <summary>
    /// The same EXISTS-behind-a-missing-table shape at top level: the un-taken
    /// THEN's inner IF/ELSE parses to completion and the following statement
    /// runs.
    /// </summary>
    [TestMethod]
    public void SkippedIf_ExistsMissingTableWithElse_Tolerated()
        => AreEqual("after", new Simulation().ExecuteScalar(
            "if 1 = 0 if exists(select * from missing) select 1 as r else select 2 as r select 'after' as r"));

    /// <summary>
    /// A missing table plus a second missing table inside a scalar subquery,
    /// both in the un-taken THEN — the whole statement defers, the ELSE runs.
    /// </summary>
    [TestMethod]
    public void SkippedIf_MissingTableWithMissingSubqueryTable_ElseRuns()
        => AreEqual("else", new Simulation().ExecuteScalar(
            "if 1 = 0 select * from missing where a = (select b from other) else select 'else' as r"));

    /// <summary>
    /// A missing table behind a CASE WHEN EXISTS in a skipped block, with the
    /// block's own ELSE — parses to completion and the ELSE runs.
    /// </summary>
    [TestMethod]
    public void SkippedBlock_CaseWhenExistsMissing_ElseRuns()
        => AreEqual("else", new Simulation().ExecuteScalar("""
            if 1 = 0 begin select case when exists(select * from missing) then 1 else 2 end as r end
            else select 'else' as r
            """));

    /// <summary>
    /// A missing TVF invoked in the FROM clause of an un-taken branch defers
    /// (the argument list is parsed and discarded); the ELSE runs.
    /// </summary>
    [TestMethod]
    public void SkippedIf_MissingTvfInFrom_ElseRuns()
        => AreEqual("else", new Simulation().ExecuteScalar(
            "if 1 = 0 select * from dbo.no_such_tvf(1, 2) else select 'else' as r"));

    /// <summary>
    /// ORDER BY referencing a column of a missing table defers along with the
    /// table — no compile error in the dead branch.
    /// </summary>
    [TestMethod]
    public void SkippedIf_MissingTableOrderByMissingColumn_Tolerated()
        => AreEqual("ok", new Simulation().ExecuteScalar(
            "if 1 = 0 select * from missing order by also_missing select 'ok'"));

    /// <summary>
    /// ORDER BY referencing a missing column of a <em>resolvable</em> table is
    /// not deferred — Msg 207 fires even in the dead branch.
    /// </summary>
    [TestMethod]
    public void SkippedIf_KnownTableOrderByMissingColumn_StillRaises207()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        _ = sim.AssertSqlError("if 1 = 0 select id from t order by nope select 'ok'", 207);
    }

    /// <summary>
    /// A missing table in one statement of a skipped block plus a missing column
    /// on a resolvable table in the next: real SQL Server binds the resolvable
    /// table's columns eagerly, so Msg 207 wins and aborts the batch — the
    /// block's ELSE never runs.
    /// </summary>
    [TestMethod]
    public void SkippedBlock_MissingTableThenBadColumnOnRealTable_Raises207()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        _ = sim.AssertSqlError("""
            if 1 = 0 begin select * from missing; select bad_col from t end
            else select 'else' as r
            select 'after' as r
            """, 207);
    }

    /// <summary>
    /// A qualified column reference against a placeholder (missing) table
    /// resolves leniently, so the whole SELECT parses to completion and the ELSE
    /// runs.
    /// </summary>
    [TestMethod]
    public void SkippedIf_QualifiedColumnsOnMissingTable_ElseRuns()
        => AreEqual("else", new Simulation().ExecuteScalar(
            "if 1 = 0 select m.foo, m.bar from missing m else select 'else' as r"));

    /// <summary>
    /// A syntax error past a DML statement's missing target or a missing
    /// source's <c>FOR SYSTEM_TIME</c> clause outranks the missing object, as
    /// real parses the whole batch before binding it (probed 2026-09-29
    /// against SQL Server 2025); the well-formed statement stays Msg 208.
    /// </summary>
    [TestMethod]
    [DataRow("update missing set a = 1 where", 102)]
    [DataRow("update missing set a = 1 where b = 1 foo", 102)]
    [DataRow("update missing set a = 1 output inserted.a where", 102)]
    [DataRow("delete from missing where", 102)]
    [DataRow("delete missing where a = 1 foo", 102)]
    [DataRow("delete from missing option (maxdop 1) where", 156)]
    [DataRow("insert missing values (", 102)]
    [DataRow("insert missing (a, b values (1, 2)", 156)]
    [DataRow("insert missing (a) values (1) foo", 102)]
    [DataRow("insert missing select 1 from other where", 102)]
    [DataRow("update m set a = 1 from missing m where", 102)]
    [DataRow("delete m from other m where a in (select 1 from", 102)]
    [DataRow("select 1 from missing for system_time all where", 102)]
    [DataRow("select 1 from missing for system_time as of '2020-01-01' where", 102)]
    [DataRow("update missing set a = 1 where a = 1", 208)]
    [DataRow("update missing set a = 1 where a = 1 option (maxdop 1)", 208)]
    [DataRow("delete from missing where a = 1;", 208)]
    [DataRow("insert missing (a, b) values (1, 2)", 208)]
    [DataRow("insert missing default values", 208)]
    [DataRow("update m set a = 1 from missing m where m.a = 1", 208)]
    [DataRow("select 1 from missing for system_time all where a = 1", 208)]
    public void MissingDmlTarget_SyntaxErrorOutranksTheMissingObject(string sql, int number)
        => _ = new Simulation().AssertSqlError(sql, number);

    /// <summary>A dead branch over a missing DML target still parses whole and lets the ELSE run.</summary>
    [TestMethod]
    [DataRow("if 1 = 0 update missing set a = 1 where a = 1 else select 'else'")]
    [DataRow("if 1 = 0 delete from missing where a in (select b from other) else select 'else'")]
    [DataRow("if 1 = 0 insert missing (a) values (1) else select 'else'")]
    [DataRow("if 1 = 0 update m set a = 1 from missing m where m.a = 1 else select 'else'")]
    public void DeadBranchOverMissingDmlTarget_ElseRuns(string sql)
        => AreEqual("else", new Simulation().ExecuteScalar(sql));

    private static Simulation MergeFixture()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.r (a int, b int); insert dbo.r values (1, 2)",
            "create procedure dbo.pr @x int = 0 as select @x");
        return sim;
    }

    /// <summary>
    /// A dead branch over a missing <c>MERGE</c> target or an <c>INSERT … EXEC</c>
    /// into one parses to its end — every <c>WHEN</c> clause, <c>OUTPUT</c>,
    /// <c>OPTION</c> and the procedure's argument list — so the ELSE runs and
    /// the EXEC doesn't (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("if 1 = 0 merge missing as t using (select 1 a) s on t.a = s.a when matched then update set t.a = 1; else select 'else'")]
    [DataRow("if 1 = 0 merge missing as t using dbo.r s on t.a = s.a when matched then update set a = 1 when not matched then insert (a) values (s.a) when not matched by source then delete output $action, inserted.*; else select 'else'")]
    [DataRow("if 1 = 0 merge into missing with (holdlock) as t using (values (1)) s(a) on t.a = s.a when matched and s.a > 0 then update set t.a = s.a, b = 2 when not matched by target then insert values (s.a, default) when not matched by source and t.a = 3 then update set a = 4; else select 'else'")]
    [DataRow("if 1 = 0 merge top (5) missing t using dbo.r s on t.a = s.a when matched then delete output deleted.a into dbo.r (a); else select 'else'")]
    [DataRow("if 1 = 0 merge missing t using (select 1 a) s on t.a = s.a when matched then update set t.a = (select max(a) from dbo.r), b = s.a + 1; else select 'else'")]
    [DataRow("if 1 = 0 merge missing using dbo.r on missing.a = r.a when matched then delete; else select 'else'")]
    [DataRow("if 1 = 0 merge missing t using missing2 s on t.a = s.a when matched then delete; else select 'else'")]
    [DataRow("if 1 = 0 merge missing t using (values (1)) s(a) on 1 = 1 when not matched then insert values (1) option (recompile); else select 'else'")]
    [DataRow("if 1 = 0 insert missing exec dbo.pr 1; else select 'else'")]
    [DataRow("if 1 = 0 insert missing exec('select 1'); else select 'else'")]
    [DataRow("if 1 = 0 insert missing (a) exec dbo.pr @x = 1; else select 'else'")]
    [DataRow("if 1 = 0 insert missing exec sp_executesql N'select 1'; else select 'else'")]
    [DataRow("if 1 = 0 insert into missing with (tablock) exec dbo.pr @x = 1 with recompile; else select 'else'")]
    public void DeadBranchOverMissingMergeOrInsertExecTarget_ElseRuns(string sql)
    {
        using var reader = MergeFixture().ExecuteReader(sql);
        IsTrue(reader.Read());
        AreEqual("else", reader.GetValue(0));
        IsFalse(reader.Read());
        IsFalse(reader.NextResult());
    }

    /// <summary>
    /// Past a missing <c>MERGE</c> target or <c>INSERT … EXEC</c> target a syntax
    /// error stops the batch while it compiles, before the statement ahead of it
    /// runs; a well-formed statement runs what precedes it and then raises Msg
    /// 208 — for the <c>USING</c> source ahead of the target when both are
    /// missing (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("merge missing as t using (select 1 a) s on t.a = s.a when matched then update set a = ;", 102, null)]
    [DataRow("merge missing t using dbo.r s on t.a = s.a when matched then delete zz;", 102, null)]
    [DataRow("merge missing t using dbo.r s on t.a = s.a when matched then delete", 10713, null)]
    [DataRow("merge missing t using dbo.r s on t.a = s.a;", 102, null)]
    [DataRow("insert missing exec dbo.pr 1,;", 102, null)]
    [DataRow("insert missing exec dbo.pr 1 zz;", 102, null)]
    [DataRow("merge missing as t using (select 1 a) s on t.a = s.a when matched then update set t.a = 1;", 208, "missing")]
    [DataRow("merge missing t using dbo.r s on t.a = s.nosuch when matched then delete;", 208, "missing")]
    [DataRow("merge missing t using dbo.r s on t.a = s.a when matched then delete option (maxdop 1);", 208, "missing")]
    [DataRow("merge missing t using missing2 s on t.a = s.a when matched then delete;", 208, "missing2")]
    [DataRow("insert missing exec dbo.pr 1;", 208, "missing")]
    [DataRow("insert missing exec dbo.nosuchproc 1;", 208, "missing")]
    public void MissingMergeOrInsertExecTarget_CompileErrorsOutrankTheMissingObject(string statement, int number, string? missing)
    {
        using var connection = MergeFixture().CreateOpenConnection();
        using var command = connection.CreateCommand("select 'before'; " + statement);
        var rows = 0;
        var error = ThrowsExactly<SimulatedSqlException>(() =>
        {
            using var reader = command.ExecuteReader();
            do
            {
                while (reader.Read())
                    rows++;
            }
            while (reader.NextResult());
        });
        AreEqual(number, error.Number);
        AreEqual(missing is null ? 0 : 1, rows);
        if (missing is not null)
            AreEqual($"Invalid object name '{missing}'.", error.Message);
    }

    /// <summary>A stray word where a MERGE's closing semicolon belongs is Msg 102 at the word, over a real target too.</summary>
    [TestMethod]
    public void MergeWithAStrayWordForItsSemicolon_Msg102()
        => MergeFixture().AssertSqlError("merge dbo.r t using dbo.r s on t.a = s.a when matched then delete zz;", 102, "Incorrect syntax near 'zz'.");
}
