using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Table hints (<c>WITH (NOLOCK [, …])</c>) on FROM sources / JOIN-RHS /
/// UPDATE / DELETE targets, plus statement-level <c>OPTION (…)</c> hints —
/// parsed and discarded for grammar compatibility (no locking / isolation
/// modeling). Probe-confirmed rejection wording: unknown table-hint → Msg
/// 321 verbatim; unknown OPTION hint → generic Msg 102 (matches probe
/// surprise that the OPTION clause has no dedicated unknown-hint code).
/// </summary>
[TestClass]
public sealed class QueryHintTests
{
    [TestMethod]
    public void Select_WithNoLock_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t with (nolock)
            """));

    [TestMethod]
    public void Select_WithMultipleHints_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t with (holdlock, readpast, rowlock)
            """));

    [TestMethod]
    public void Select_NoLockWithHoldLock_RaisesMsg1047_ConflictingHints()
        => new Simulation().AssertSqlError("""
            create table t (id int);
            select * from t with (nolock, holdlock)
            """, 1047, "Conflicting locking hints specified.");

    [TestMethod]
    public void Select_NoLockWithXLock_RaisesMsg1047_ConflictingHints()
        => new Simulation().AssertSqlError("""
            create table t (id int);
            select * from t with (nolock, xlock)
            """, 1047, "Conflicting locking hints specified.");

    [TestMethod]
    public void Select_NoLockWithUpdLock_RaisesMsg1047_ConflictingHints()
        => new Simulation().AssertSqlError("""
            create table t (id int);
            select * from t with (nolock, updlock)
            """, 1047, "Conflicting locking hints specified.");

    [TestMethod]
    public void Update_WithNoLock_RaisesMsg1065()
        => new Simulation().AssertSqlError("""
            create table t (id int);
            update t with (nolock) set id = 1
            """, 1065, "The NOLOCK and READUNCOMMITTED lock hints are not allowed for target tables of INSERT, UPDATE, DELETE or MERGE statements.");

    [TestMethod]
    public void Delete_WithReadUncommitted_RaisesMsg1065()
        => new Simulation().AssertSqlError("""
            create table t (id int);
            delete from t with (readuncommitted)
            """, 1065, "The NOLOCK and READUNCOMMITTED lock hints are not allowed for target tables of INSERT, UPDATE, DELETE or MERGE statements.");

    [TestMethod]
    public void Insert_WithNoLock_RaisesMsg1065()
        => new Simulation().AssertSqlError("""
            create table t (id int);
            insert t with (nolock) values (1)
            """, 1065, "The NOLOCK and READUNCOMMITTED lock hints are not allowed for target tables of INSERT, UPDATE, DELETE or MERGE statements.");

    [TestMethod]
    public void Update_WithIndexHint_RaisesMsg1069()
        => new Simulation().AssertSqlError("""
            create table t (id int);
            update t with (index(0)) set id = 1
            """, 1069, "Index hints are only allowed in a FROM or OPTION clause.");

    [TestMethod]
    public void Delete_WithIndexHint_RaisesMsg1069()
        => new Simulation().AssertSqlError("""
            create table t (id int);
            delete from t with (index(0))
            """, 1069, "Index hints are only allowed in a FROM or OPTION clause.");

    [TestMethod]
    public void Select_LegacyParenForm_NoWith_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t (nolock)
            """));

    [TestMethod]
    public void Select_HintAfterAlias_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t as x with (nolock)
            """));

    [TestMethod]
    public void Select_HintAfterBareAlias_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t x with (nolock)
            """));

    [TestMethod]
    public void Select_IndexHint_NumericArg_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t with (index(0))
            """));

    /// <summary>
    /// Simulator doesn't model indexes, so the named-index reference parses
    /// and is discarded. Real SQL Server raises Msg 308 on a wrong name;
    /// the parse-and-ignore stance is the consistent posture for hints.
    /// </summary>
    [TestMethod]
    public void Select_IndexHint_NamedArg_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t with (index(IX_does_not_exist))
            """));

    [TestMethod]
    public void Select_ForceSeek_BareForm_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t with (forceseek)
            """));

    [TestMethod]
    public void Select_SpatialWindowMaxCells_EqForm_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t with (SPATIAL_WINDOW_MAX_CELLS = 1024)
            """));

    [TestMethod]
    public void Select_UnknownHint_RaisesMsg321()
        => new Simulation().AssertSqlError("""
            create table t (id int primary key);
            select * from t with (banana)
            """, 321, "\"banana\" is not a recognized table hints option.");

    [TestMethod]
    public void Select_UnknownHint_AmongValidOnes_RaisesMsg321()
        => new Simulation().AssertSqlError("""
            create table t (id int primary key);
            select * from t with (nolock, foobar, readpast)
            """, 321, "\"foobar\" is not a recognized table hints option.");

    [TestMethod]
    public void Join_RhsHint_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*)
            from t a
            inner join t b with (nolock) on a.id = b.id
            """));

    [TestMethod]
    public void Join_LegacyParenRhs_NoWith_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*)
            from t a
            left join t b (nolock) on a.id = b.id
            """));

    [TestMethod]
    public void Update_WithHint_AppliesChange()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int primary key, v int);
            insert t values (1, 100), (2, 200);
            update t with (rowlock) set v = v + 1
            """);
        AreEqual(101, sim.ExecuteScalar("select v from t where id = 1"));
        AreEqual(201, sim.ExecuteScalar("select v from t where id = 2"));
    }

    [TestMethod]
    public void Update_LegacyParenForm_RaisesMsg102()
        => new Simulation().AssertSqlError("""
            create table t (id int primary key, v int);
            insert t values (1, 100);
            update t (tablock) set v = 999
            """, 102);

    [TestMethod]
    public void Delete_LegacyParenForm_RaisesMsg102()
        => new Simulation().AssertSqlError("""
            create table t (id int primary key);
            insert t values (1);
            delete from t (tablock) where id = 1
            """, 102);

    [TestMethod]
    public void Update_UnknownHint_RaisesMsg321()
        => new Simulation().AssertSqlError("""
            create table t (id int primary key, v int);
            insert t values (1, 100);
            update t with (banana) set v = 999
            """, 321, "\"banana\" is not a recognized table hints option.");

    [TestMethod]
    public void Delete_BareForm_WithHint_RemovesRows()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            delete from t with (tablock) where id = 2
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void Delete_AliasForm_WithHint_RemovesRows()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            delete a from t a with (tablock) where a.id = 2
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void Delete_UnknownHint_RaisesMsg321()
        => new Simulation().AssertSqlError("""
            create table t (id int primary key);
            delete from t with (banana)
            """, 321, "\"banana\" is not a recognized table hints option.");

    [TestMethod]
    public void Option_Recompile_AcceptedAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("select 1 option (recompile)"));

    [TestMethod]
    public void Option_MaxDop_AcceptedAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("select 1 option (maxdop 4)"));

    [TestMethod]
    public void Option_Fast_AcceptedAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("select 1 option (fast 100)"));

    [TestMethod]
    public void Option_LoopJoin_AcceptedAsNoop()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t a inner join t b on a.id = b.id option (loop join)
            """));

    [TestMethod]
    public void Option_HashJoin_AcceptedAsNoop()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t a inner join t b on a.id = b.id option (hash join)
            """));

    [TestMethod]
    public void Option_ForceOrder_AcceptedAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("select 1 option (force order)"));

    [TestMethod]
    public void Option_KeepFixedPlan_AcceptedAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("select 1 option (keepfixed plan)"));

    [TestMethod]
    public void Option_OptimizeForUnknown_AcceptedAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("select 1 option (optimize for unknown)"));

    [TestMethod]
    public void Option_UseHint_QuotedArg_AcceptedAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            select 1 option (use hint('FORCE_LEGACY_CARDINALITY_ESTIMATION'))
            """));

    [TestMethod]
    public void Option_MultipleHints_AcceptedAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("select 1 option (recompile, maxdop 4)"));

    [TestMethod]
    public void Option_UnknownHint_RaisesMsg102()
        => new Simulation().AssertSqlError("select 1 option (banana)", 102);

    [TestMethod]
    public void Option_MaxRecursionStillWorks()
    {
        // Closed-list parser preserves MAXRECURSION's runtime effect — the
        // CTE recursion limit override path is the only OPTION hint with
        // observable behavior.
        var ex = new Simulation().AssertSqlError("""
            with c as (
                select 1 as n
                union all
                select n + 1 from c where n < 200
            )
            select count(*) from c option (maxrecursion 50)
            """, 530);
        Contains("50", ex.Message);
    }

    [TestMethod]
    public void Select_BareIdentifier_NotHint_ParsesAsAlias()
    {
        // `FROM t nolock` (no WITH, no parens) is alias parsing, not a
        // deprecated hint shape — nolock / readpast / etc. aren't reserved
        // keywords, so they're valid bare aliases. Same path as `FROM t a`.
        // Real SQL Server treats this identically; the simulator's bare-alias
        // branch in ConsumeOptionalAlias matches by Name token, which
        // UnquotedString satisfies.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table h1 (id int primary key, v int);
            insert h1 values (1, 100), (2, 200)
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from h1 nolock"));
        AreEqual(100, sim.ExecuteScalar("select nolock.v from h1 nolock where nolock.id = 1"));
        AreEqual(2, sim.ExecuteScalar("select count(*) from h1 banana"));
        AreEqual(100, sim.ExecuteScalar("select banana.v from h1 banana where banana.id = 1"));
    }

    [TestMethod]
    public void Update_BareAlias_HintAfter_AppliesChange()
    {
        // UPDATE target-hint position is between target name and SET, not
        // between alias and SET — the alias form `UPDATE t a WITH (...)`
        // isn't probe-confirmed and not modeled here. This test pins the
        // unambiguous shape.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int primary key, v int);
            insert t values (1, 100);
            update t with (tablock) set v = 999
            """);
        AreEqual(999, sim.ExecuteScalar("select v from t where id = 1"));
    }

    // --------------- INSERT target hints ---------------
    //
    // Probe-confirmed (2026-05-14): INSERT accepts WITH (hint [, …]) only,
    // between target name and column list / VALUES. The legacy bare-paren
    // form `INSERT t (TABLOCK) …` is always a column list — probe surfaces
    // Msg 207 'Invalid column name TABLOCK' rather than parsing it as a
    // hint. Hint after column list raises Msg 156 / Msg 102. Table-variable
    // targets reject hints entirely.

    [TestMethod]
    public void Insert_WithHint_NoColumnList_AcceptsAsNoop()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int identity primary key, name nvarchar(50));
            insert into t with (tablock) values (N'a'), (N'b')
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void Insert_WithHint_ExplicitColumnList_AcceptsAsNoop()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int identity primary key, name nvarchar(50));
            insert into t with (tablock) (name) values (N'a')
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void Insert_WithHint_NoInto_AcceptsAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int identity primary key, name nvarchar(50));
            insert t with (tablock) values (N'a');
            select count(*) from t
            """));

    [TestMethod]
    public void Insert_WithHint_MultipleHints_AcceptsAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int identity primary key, name nvarchar(50));
            insert into t with (tablock, holdlock) values (N'a');
            select count(*) from t
            """));

    [TestMethod]
    public void Insert_WithHint_OutputClause_AcceptsAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int identity primary key, name nvarchar(50));
            insert into t with (tablock) output inserted.id values (N'a')
            """));

    [TestMethod]
    public void Insert_WithHint_UnknownHint_RaisesMsg321()
        => new Simulation().AssertSqlError("""
            create table t (id int identity primary key, name nvarchar(50));
            insert into t with (banana) values (N'a')
            """, 321, "\"banana\" is not a recognized table hints option.");

    [TestMethod]
    public void Insert_LegacyParenForm_ParsesAsColumnList_RaisesMsg207()
    {
        // Probe-confirmed: real SQL Server parses `(TABLOCK)` as a column
        // list and raises Msg 207. The simulator's column resolver throws
        // InvalidColumnName from ResolveInsertTargetColumn — matching code
        // and wording.
        var ex = new Simulation().AssertSqlError("""
            create table t (id int identity primary key, name nvarchar(50));
            insert into t (TABLOCK) values (N'a')
            """, 207);
        Contains("TABLOCK", ex.Message);
    }

    [TestMethod]
    public void Insert_HintAfterColumnList_RaisesMsg102()
        => new Simulation().AssertSqlError("""
            create table t (id int identity primary key, name nvarchar(50));
            insert into t (name) with (tablock) values (N'a')
            """, 102);

    [TestMethod]
    public void Insert_HintOnTableVariable_RaisesMsg102()
        => new Simulation().AssertSqlError("""
            declare @t table (id int, name nvarchar(50));
            insert into @t with (tablock) values (1, N'a')
            """, 102);

    [TestMethod]
    public void Insert_HintOnTempTable_AcceptsAsNoop()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table #tmp (id int);
            insert into #tmp with (tablock) values (1), (2);
            select count(*) from #tmp
            """));

    // --------------- MERGE target hints ---------------
    //
    // Probe-confirmed (2026-05-14): MERGE target uses hint-then-alias
    // placement — `MERGE INTO t WITH (TABLOCK) AS x USING …` works,
    // `MERGE INTO t AS x WITH (TABLOCK) …` raises Msg 156. Opposite of
    // FROM / UPDATE / DELETE which are alias-then-hint. Legacy bare-paren
    // form rejected with Msg 102.

    [TestMethod]
    public void Merge_TargetWithHint_AliasAfter_AcceptsAsNoop()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table tgt (id int primary key, v int);
            create table src (id int primary key, v int);
            insert tgt values (1, 10);
            insert src values (1, 100), (2, 200);
            merge into tgt with (tablock) as t
            using (select id, v from src) as s on s.id = t.id
            when matched then update set v = s.v
            when not matched by target then insert (id, v) values (s.id, s.v);
            """);
        AreEqual(100, sim.ExecuteScalar("select v from tgt where id = 1"));
        AreEqual(200, sim.ExecuteScalar("select v from tgt where id = 2"));
    }

    [TestMethod]
    public void Merge_TargetWithHint_NoAlias_AcceptsAsNoop()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table tgt (id int primary key, v int);
            create table src (id int primary key, v int);
            insert src values (1, 100);
            merge into tgt with (tablock)
            using (select id, v from src) as s on s.id = tgt.id
            when not matched by target then insert (id, v) values (s.id, s.v);
            """);
        AreEqual(100, sim.ExecuteScalar("select v from tgt where id = 1"));
    }

    [TestMethod]
    public void Merge_TargetMultipleHints_AcceptsAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table tgt (id int primary key, v int);
            create table src (id int primary key, v int);
            insert src values (1, 100);
            merge into tgt with (tablock, holdlock) as t
            using (select id, v from src) as s on s.id = t.id
            when not matched by target then insert (id, v) values (s.id, s.v);
            select count(*) from tgt
            """));

    [TestMethod]
    public void Merge_AliasThenHint_RaisesMsg102()
        => new Simulation().AssertSqlError("""
            create table tgt (id int primary key, v int);
            create table src (id int primary key, v int);
            merge into tgt as t with (tablock)
            using (select id, v from src) as s on s.id = t.id
            when not matched by target then insert (id, v) values (s.id, s.v);
            """, 102);

    [TestMethod]
    public void Merge_LegacyParenForm_RaisesMsg102()
        => new Simulation().AssertSqlError("""
            create table tgt (id int primary key, v int);
            create table src (id int primary key, v int);
            merge into tgt (tablock) as t
            using (select id, v from src) as s on s.id = t.id
            when not matched by target then insert (id, v) values (s.id, s.v);
            """, 102);

    [TestMethod]
    public void Merge_UnknownHint_RaisesMsg321()
        => new Simulation().AssertSqlError("""
            create table tgt (id int primary key, v int);
            create table src (id int primary key, v int);
            merge into tgt with (banana) as t
            using (select id, v from src) as s on s.id = t.id
            when not matched by target then insert (id, v) values (s.id, s.v);
            """, 321, "\"banana\" is not a recognized table hints option.");
}
