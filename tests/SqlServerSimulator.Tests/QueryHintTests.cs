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
            select count(*) from t with (updlock, readpast, rowlock)
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

    /// <summary>
    /// Real reports Msg 1065 at line 15 wherever the statement sits, and
    /// refuses the batch while compiling it, so nothing ahead of it runs
    /// (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void NoLockOnADmlTarget_IsLine15_AndRunsNothing()
    {
        var simulation = new Simulation();
        AreEqual(15, simulation.AssertSqlError("""
            create table t (id int);

            insert t with (nolock) values (1)
            """, 1065).LineNumber);
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select object_id('t')"));
    }

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

    [TestMethod]
    public void Select_IndexHint_KnownNamedArg_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key, v int);
            create index ix_v on t(v);
            insert t values (1, 10), (2, 20), (3, 30);
            select count(*) from t with (index(ix_v))
            """));

    [TestMethod]
    public void Select_IndexHint_UnknownNamedArg_RaisesMsg308()
        => new Simulation().AssertSqlError("""
            create table t (id int primary key);
            insert t values (1);
            select * from t with (index(IX_does_not_exist))
            """, 308, "Index 'IX_does_not_exist' on table 'dbo.t' (specified in the FROM clause) does not exist.");

    [TestMethod]
    public void Select_IndexHint_PkConstraintName_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int constraint pk_t primary key);
            insert t values (1), (2), (3);
            select count(*) from t with (index(pk_t))
            """));

    [TestMethod]
    public void Select_IndexHint_UqConstraintName_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key, u int constraint uq_u unique);
            insert t values (1, 10), (2, 20), (3, 30);
            select count(*) from t with (index(uq_u))
            """));

    [TestMethod]
    public void Select_IndexHint_NamedArg_CaseInsensitive_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key, v int);
            create index ix_v on t(v);
            insert t values (1, 10), (2, 20), (3, 30);
            select count(*) from t with (index(IX_V))
            """));

    [TestMethod]
    public void Select_IndexHint_EqForm_KnownName_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int constraint pk_t primary key);
            insert t values (1), (2), (3);
            select count(*) from t with (index = pk_t)
            """));

    [TestMethod]
    public void Select_IndexHint_EqForm_UnknownName_RaisesMsg308()
        => new Simulation().AssertSqlError("""
            create table t (id int primary key);
            insert t values (1);
            select * from t with (index = nope)
            """, 308, "Index 'nope' on table 'dbo.t' (specified in the FROM clause) does not exist.");

    [TestMethod]
    public void Select_IndexHint_BadIdOnPkTable_RaisesMsg307()
        => new Simulation().AssertSqlError("""
            create table t (id int primary key);
            insert t values (1);
            select * from t with (index(99))
            """, 307, "Index ID 99 on table 'dbo.t' (specified in the FROM clause) does not exist.");

    [TestMethod]
    public void Select_IndexHint_Id1OnHeapTable_RaisesMsg307()
        => new Simulation().AssertSqlError("""
            create table t (id int, v int);
            insert t values (1, 10);
            select * from t with (index(1))
            """, 307, "Index ID 1 on table 'dbo.t' (specified in the FROM clause) does not exist.");

    [TestMethod]
    public void Select_IndexHint_Id0OnHeapTable_ReturnsRows()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (id int, v int);
            insert t values (1, 10), (2, 20);
            select count(*) from t with (index(0))
            """));

    [TestMethod]
    public void Select_IndexHint_MixedGoodBadInOneList_RaisesMsg308_OnFirstBad()
        => new Simulation().AssertSqlError("""
            create table t (id int constraint pk_t primary key);
            insert t values (1);
            select * from t with (index(nope, pk_t))
            """, 308, "Index 'nope' on table 'dbo.t' (specified in the FROM clause) does not exist.");

    [TestMethod]
    public void Select_IndexHint_MultipleKnown_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int constraint pk_t primary key, v int);
            create index ix_v on t(v);
            insert t values (1, 10), (2, 20), (3, 30);
            select count(*) from t with (index(pk_t, ix_v))
            """));

    [TestMethod]
    public void Select_IndexHint_SchemaQualified_ErrorEmbedsQualifier()
    {
        var sim = new Simulation();
        _ = sim.WithSchemas("au").ExecuteNonQuery("""
            create table au.t (id int primary key);
            """);
        sim.AssertSqlError(
            "select * from au.t with (index(nope))",
            308, "Index 'nope' on table 'au.t' (specified in the FROM clause) does not exist.");
    }

    [TestMethod]
    public void Select_IndexHint_NegativeIntegerArg_RaisesMsg102()
        => new Simulation().AssertSqlError("""
            create table t (id int primary key);
            select * from t with (index(-1))
            """, 102);

    [TestMethod]
    public void Select_ForceSeek_BareForm_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t with (forceseek) where id > 0
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
    public void Option_UseHint_MultipleNames_AcceptedAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            select 1 option (use hint('FORCE_LEGACY_CARDINALITY_ESTIMATION', 'DISABLE_OPTIMIZED_NESTED_LOOP'))
            """));

    [TestMethod]
    public void Option_UseHint_LowercaseName_AcceptedCaseInsensitive()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            select 1 option (use hint('force_legacy_cardinality_estimation'))
            """));

    [TestMethod]
    public void Option_UseHint_UnicodeLiteral_AcceptedAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            select 1 option (use hint(N'FORCE_LEGACY_CARDINALITY_ESTIMATION'))
            """));

    [TestMethod]
    public void Option_UseHint_WithMaxDop_EitherOrder_AcceptedAsNoop()
    {
        // Probe-confirmed both orders combine with other OPTION hints.
        AreEqual(1, new Simulation().ExecuteScalar("select 1 option (maxdop 1, use hint('FORCE_LEGACY_CARDINALITY_ESTIMATION'))"));
        AreEqual(1, new Simulation().ExecuteScalar("select 1 option (use hint('FORCE_LEGACY_CARDINALITY_ESTIMATION'), maxdop 1)"));
    }

    [TestMethod]
    public void Option_UseHint_UnknownName_RaisesMsg10715()
        => new Simulation().AssertSqlError(
            "select 1 option (use hint('BANANA_NOT_A_HINT'))",
            10715, "'BANANA_NOT_A_HINT' is not a valid hint.");

    [TestMethod]
    public void Option_UseHint_EmptyParens_RaisesMsg102()
        => new Simulation().AssertSqlError("select 1 option (use hint())", 102);

    [TestMethod]
    public void Option_UseHint_NonStringArg_RaisesMsg102()
        => new Simulation().AssertSqlError("select 1 option (use hint(123))", 102);

    /// <summary>
    /// USE PLAN N'…' shares the USE first-word but isn't USE HINT — it must
    /// still fall through to the generic parse-and-discard skip.
    /// </summary>
    [TestMethod]
    public void Option_UsePlan_NotConfusedWithUseHint_AcceptedAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("select 1 option (use plan N'<ShowPlanXML />')"));

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
    public void Insert_HintAfterColumnList_RaisesMsg156()
        => new Simulation().AssertSqlError("""
            create table t (id int identity primary key, name nvarchar(50));
            insert into t (name) with (tablock) values (N'a')
            """, 156);

    [TestMethod]
    public void Insert_HintOnTableVariable_RaisesMsg156()
        => new Simulation().AssertSqlError("""
            declare @t table (id int, name nvarchar(50));
            insert into @t with (tablock) values (1, N'a')
            """, 156);

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
    public void Merge_AliasThenHint_RaisesMsg156()
        => new Simulation().AssertSqlError("""
            create table tgt (id int primary key, v int);
            create table src (id int primary key, v int);
            merge into tgt as t with (tablock)
            using (select id, v from src) as s on s.id = t.id
            when not matched by target then insert (id, v) values (s.id, s.v);
            """, 156);

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

    // --- the legacy bare-paren form: what it means turns on the alias ---

    private const string SeekTable = """
        create table t (a int, b int, d int, e int);
        create index ix_ab on t(a, b) include (d);
        insert t values (1, 2, 3, 4);
        """;

    [TestMethod]
    public void BareParen_RecognizedHint_NoAlias_IsStillAHintList()
        => AreEqual(1, new Simulation().ExecuteScalar($"{SeekTable} select count(*) from t (nolock)"));

    [TestMethod]
    public void BareParen_UnknownName_WithAlias_ReportsMsg321()
        => new Simulation().AssertSqlError(
            $"{SeekTable} select * from t x (unknown)",
            321,
            "\"unknown\" is not a recognized table hints option.");

    [TestMethod]
    public void BareParen_UnknownName_NoAlias_ReportsMsg207ThenMsg215()
    {
        var exception = new Simulation().AssertSqlError($"{SeekTable} select * from t (unknown)", 207);
        AreEqual(2, exception.Errors.Count);
        AreEqual("Invalid column name 'unknown'.", exception.Errors[0].Message);
        AreEqual(215, exception.Errors[1].Number);
        AreEqual(
            "Parameters supplied for object 't' which is not a function. If the parameters are intended as a table hint, a WITH keyword is required.",
            exception.Errors[1].Message);
    }

    [TestMethod]
    public void BareParen_AColumnOfTheSourceItself_StillReportsMsg207()
        // The source is not in scope for its own arguments, so even a name the
        // table carries is an unresolvable column reference.
        => AreEqual("Invalid column name 'a'.", new Simulation().AssertSqlError($"{SeekTable} select * from t (a)", 207).Errors[0].Message);

    [TestMethod]
    public void BareParen_SeveralUnknownNames_ReportOneMsg207Each()
        => AreEqual(3, new Simulation().AssertSqlError($"{SeekTable} select * from t (unknown, alsounknown)", 207).Errors.Count);

    [TestMethod]
    public void BareParen_ScalarArgument_ReportsMsg215Alone()
    {
        var exception = new Simulation().AssertSqlError($"{SeekTable} select * from t (1)", 215);
        AreEqual(1, exception.Errors.Count);
    }

    [TestMethod]
    public void BareParen_VariableArgument_ReportsMsg215Alone()
        => AreEqual(1, new Simulation().AssertSqlError($"{SeekTable} declare @z int = 1; select * from t (@z)", 215).Errors.Count);

    /// <summary>Msg 1018 names the hint as written (probed 2026-09-26).</summary>
    [TestMethod]
    [DataRow("index")]
    [DataRow("Index")]
    public void BareParen_IndexHint_ReportsMsg1018(string written)
        => new Simulation().AssertSqlError(
            $"{SeekTable} select * from t ({written}(ix_ab))",
            1018,
            $"Incorrect syntax near '{written}'. If this is intended as a part of a table hint, A WITH keyword and parenthesis are now required. See SQL Server Books Online for proper syntax.");

    /// <summary>
    /// A table variable takes no table hint: WITH ends the statement (Msg 319)
    /// and the legacy parenthesized form is Msg 1018 for a hint name, 102 for
    /// anything else (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select * from @t with (nolock)", 319)]
    [DataRow("select * from @t x with (nolock)", 319)]
    [DataRow("select * from @t (nolock)", 1018)]
    [DataRow("select * from @t x (tablock)", 1018)]
    [DataRow("select * from @t (bogus)", 102)]
    public void TableVariable_TakesNoHint(string sql, int number)
        => new Simulation().AssertSqlError($"declare @t table (a int); {sql}", number);

    // --- FORCESEEK's nested form validates its index and its seek columns ---

    [TestMethod]
    public void ForceSeek_LeadingKeyPrefix_IsAccepted()
        => AreEqual(1, new Simulation().ExecuteScalar($"{SeekTable} select count(*) from t with (forceseek(ix_ab(a))) where a = 1"));

    [TestMethod]
    public void ForceSeek_WholeKey_IsAccepted()
        => AreEqual(1, new Simulation().ExecuteScalar($"{SeekTable} select count(*) from t with (forceseek(ix_ab(a, b))) where a = 1 and b > 0"));

    [TestMethod]
    public void ForceSeek_MissingIndex_ReportsMsg308()
        => new Simulation().AssertSqlError(
            $"{SeekTable} select * from t with (forceseek(ix_nope(a)))",
            308,
            "Index 'ix_nope' on table 'dbo.t' (specified in the FROM clause) does not exist.");

    [TestMethod]
    public void ForceSeek_NonKeyColumn_ReportsMsg362()
        => new Simulation().AssertSqlError(
            $"{SeekTable} select * from t with (forceseek(ix_ab(nope)))",
            362,
            "The query processor could not produce a query plan because the name 'nope' in the FORCESEEK hint on table or view 't' did not match the key column names of the index 'ix_ab'.");

    [TestMethod]
    public void ForceSeek_IncludedColumn_ReportsMsg362()
        => new Simulation().AssertSqlError($"{SeekTable} select * from t with (forceseek(ix_ab(d)))", 362);

    [TestMethod]
    public void ForceSeek_KeyColumnOutOfOrder_ReportsMsg362NamingIt()
        => AreEqual(
            "The query processor could not produce a query plan because the name 'b' in the FORCESEEK hint on table or view 't' did not match the key column names of the index 'ix_ab'.",
            new Simulation().AssertSqlError($"{SeekTable} select * from t with (forceseek(ix_ab(b, a)))", 362).Message);

    [TestMethod]
    public void ForceSeek_SecondColumnWrong_NamesTheSecond()
        => AreEqual(
            "The query processor could not produce a query plan because the name 'nope' in the FORCESEEK hint on table or view 't' did not match the key column names of the index 'ix_ab'.",
            new Simulation().AssertSqlError($"{SeekTable} select * from t with (forceseek(ix_ab(a, nope)))", 362).Message);

    [TestMethod]
    public void ForceSeek_TooManySeekColumns_ReportsMsg365AheadOfTheNameCheck()
        => new Simulation().AssertSqlError(
            $"{SeekTable} select * from t with (forceseek(ix_ab(a, b, d)))",
            365,
            "The query processor could not produce a query plan because the FORCESEEK hint on table or view 't' specified more seek columns than the number of key columns in index 'ix_ab'.");

    [TestMethod]
    public void ForceSeek_NamesTheBaseTableNotTheAlias()
        => AreEqual(
            "The query processor could not produce a query plan because the name 'nope' in the FORCESEEK hint on table or view 't' did not match the key column names of the index 'ix_ab'.",
            new Simulation().AssertSqlError($"{SeekTable} select * from t v with (forceseek(ix_ab(nope)))", 362).Message);

    [TestMethod]
    public void ForceSeek_OnAConstraintBackedIndex_ValidatesItsKeyColumns()
        => new Simulation().AssertSqlError("""
            create table t (a int not null, b int not null, constraint pk_t primary key (a, b));
            select * from t with (forceseek(pk_t(b)))
            """, 362);

    [TestMethod]
    public void ForceSeek_ColumnNameMatchIsCollationDriven()
        => AreEqual(1, new Simulation().ExecuteScalar($"{SeekTable} select count(*) from t with (forceseek(ix_ab(A))) where a = 1"));

    [TestMethod]
    public void ForceSeek_WithoutTheNestedForm_SeeksAnyLeadingKey()
        => AreEqual(1, new Simulation().ExecuteScalar($"{SeekTable} select count(*) from t with (forceseek) where a = 1"));

    private const string HintTable = """
        create table t (id int not null constraint pk_t primary key, a int null);
        create index ix_a on t (a);
        insert t values (1, 10), (2, 20);
        """;

    /// <summary>
    /// The lock-hint pairs real refuses: two isolation levels, two
    /// granularities, UPDLOCK beside XLOCK and a dirty read beside a lock
    /// (Msg 1047), and READPAST beside a dirty read or SERIALIZABLE (Msg 650);
    /// the whole pair matrix probed 2026-10-05 against SQL Server 2025.
    /// </summary>
    [TestMethod]
    [DataRow("nolock, readcommitted", 1047)]
    [DataRow("readuncommitted, rowlock", 1047)]
    [DataRow("readcommitted, serializable", 1047)]
    [DataRow("repeatableread, holdlock", 1047)]
    [DataRow("readcommittedlock, readcommitted", 1047)]
    [DataRow("snapshot, nolock", 1047)]
    [DataRow("rowlock, paglock", 1047)]
    [DataRow("rowlock, tablock", 1047)]
    [DataRow("tablock, tablockx", 1047)]
    [DataRow("updlock, xlock", 1047)]
    [DataRow("nolock, readpast", 650)]
    [DataRow("holdlock, readpast", 650)]
    public void ConflictingLockHints_AreRefused(string hints, int number)
        => _ = new Simulation().AssertSqlError($"{HintTable} select count(*) from t with ({hints})", number);

    [TestMethod]
    [DataRow("serializable, holdlock")]
    [DataRow("nolock, nowait")]
    [DataRow("updlock, tablockx")]
    [DataRow("repeatableread, readpast")]
    [DataRow("xlock, rowlock")]
    public void CompatibleLockHints_AreAccepted(string hints)
        => AreEqual(2, new Simulation().ExecuteScalar($"{HintTable} select count(*) from t with ({hints})"));

    [TestMethod]
    [DataRow("serializable")]
    [DataRow("read uncommitted")]
    public void ReadPast_UnderASessionLevelOtherThanReadCommitted_IsMsg650(string level)
        => _ = new Simulation().AssertSqlError(
            $"{HintTable} set transaction isolation level {level}; select count(*) from t with (readpast)", 650);

    [TestMethod]
    public void ReadPast_WithAHintNamingItsOwnLevel_IgnoresTheSessionLevel()
        => AreEqual(2, new Simulation().ExecuteScalar(
            $"{HintTable} set transaction isolation level serializable; select count(*) from t with (readpast, readcommitted)"));

    [TestMethod]
    public void ReadOnly_IsNoTableHint()
        => new Simulation().AssertSqlError(
            $"{HintTable} select count(*) from t with (readonly)", 321, "\"readonly\" is not a recognized table hints option.");

    [TestMethod]
    public void HintsWrittenWithoutAComma_AreTaken()
        => AreEqual(2, new Simulation().ExecuteScalar($"{HintTable} select count(*) from t with (index(ix_a) nolock)"));

    [TestMethod]
    [DataRow("ignore_triggers")]
    [DataRow("ignore_constraints")]
    public void BulkLoadHint_OnARead_IsMsg8171(string hint)
        => new Simulation().AssertSqlError(
            $"{HintTable} select count(*) from dbo.t with ({hint})", 8171, $"Hint '{hint}' on object 'dbo.t' is invalid.");

    [TestMethod]
    public void KeepIdentity_OnARead_IsDiscarded()
        => AreEqual(2, new Simulation().ExecuteScalar($"{HintTable} select count(*) from t with (keepidentity, keepdefaults)"));

    [TestMethod]
    [DataRow("insert dbo.t with (keepdefaults) values (3, 3)", "keepdefaults")]
    [DataRow("insert dbo.t with (tablock, keepidentity) select 3, 3", "keepidentity")]
    [DataRow("update dbo.t with (ignore_triggers) set a = 1", "ignore_triggers")]
    [DataRow("delete dbo.t with (keepidentity)", "keepidentity")]
    [DataRow("merge dbo.t with (ignore_constraints) x using (select 1 k) s on s.k = x.id when matched then delete;", "ignore_constraints")]
    public void BulkLoadHint_OnAWriteTarget_IsMsg8171(string statement, string hint)
        => new Simulation().AssertSqlError($"{HintTable} {statement}", 8171, $"Hint '{hint}' on object 'dbo.t' is invalid.");

    [TestMethod]
    [DataRow("delete t with (forceseek)", 10724)]
    [DataRow("update t with (forceseek) set a = 1", 10724)]
    [DataRow("insert t with (forceseek) values (3, 3)", 10724)]
    [DataRow("delete t with (forcescan)", 10745)]
    [DataRow("update t with (index(ix_a)) set a = 1", 1069)]
    [DataRow("insert t with (readpast) select 3, 3", 4102)]
    public void WriteTargetHints_AreRefused(string statement, int number)
        => _ = new Simulation().AssertSqlError($"{HintTable} {statement}", number);

    [TestMethod]
    public void ReadPast_OnAnUpdateOrDeleteTarget_IsTaken()
        => AreEqual(0, new Simulation().ExecuteScalar($"{HintTable} update t with (readpast) set a = a where id = 9; delete t with (readpast) where id = 9; select @@rowcount"));

    [TestMethod]
    public void MergeTarget_TakesAnIndexHint_CheckedAgainstItsTable()
    {
        AreEqual(1, new Simulation().ExecuteScalar(
            $"{HintTable} merge t with (index(ix_a)) x using (select 1 k) s on s.k = x.id when matched then delete; select count(*) from t"));
        new Simulation().AssertSqlError(
            $"{HintTable} merge dbo.t with (index(nosuch)) x using (select 1 k) s on s.k = x.id when matched then delete;",
            308,
            "Index 'nosuch' on table 'dbo.t' (specified in the FROM clause) does not exist.");
    }

    [TestMethod]
    public void IndexHint_BracketedName_Resolves()
        => AreEqual(2, new Simulation().ExecuteScalar($"{HintTable} select count(*) from t with (index([ix_a]))"));

    [TestMethod]
    public void IndexHint_IdsFollowSysIndexes()
    {
        // A heap has no index 1, and its first nonclustered index is 2.
        const string heap = "create table h (k int, v int); create index nx on h (v); insert h values (1, 1);";
        AreEqual(1, new Simulation().ExecuteScalar($"{heap} select count(*) from h with (index(2))"));
        new Simulation().AssertSqlError(
            $"{heap} select count(*) from dbo.h with (index(1))", 307, "Index ID 1 on table 'dbo.h' (specified in the FROM clause) does not exist.");
    }

    [TestMethod]
    public void IndexHint_OnAnXmlIndex_IsMsg309()
        => new Simulation().AssertSqlError("""
            create table x (id int primary key, doc xml);
            create primary xml index px on x (doc);
            select count(*) from dbo.x with (index(px))
            """, 309, "Cannot use index \"px\" on table \"dbo.x\" in a hint. XML indexes are not allowed in hints.");

    [TestMethod]
    public void IndexHint_OnATempTable_NamesItAlone()
        => new Simulation().AssertSqlError(
            "create table #x (a int primary key); select a from #x with (index(nosuch))",
            308,
            "Index 'nosuch' on table '#x' (specified in the FROM clause) does not exist.");

    [TestMethod]
    public void IndexHint_InAModuleBody_IsCheckedWhenItRuns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(HintTable);
        _ = sim.ExecuteNonQuery("create view v as select id from t with (index(nosuch))");
        _ = sim.AssertSqlError("select id from v", 308);
    }

    [TestMethod]
    public void PagLock_WhereThePageLocksAreDisallowed_IsMsg651()
        => new Simulation().AssertSqlError(
            $"{HintTable} alter index pk_t on t set (allow_page_locks = off); select id from dbo.t with (paglock)",
            651,
            "Cannot use the PAGE granularity hint on the table \"dbo.t\" because locking at the specified granularity is inhibited.");

    [TestMethod]
    public void IndexHint_OnACatalogView_IsIgnoredWithMsg4430()
    {
        var messages = new List<string>();
        using var connection = (SimulatedDbConnection)new Simulation().CreateOpenConnection();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(error => error.Message));
        _ = connection.CreateCommand("select count(*) from sys.objects with (index(1))").ExecuteScalar();
        CollectionAssert.Contains(messages, "Warning: Index hints supplied for view 'sys.objects' will be ignored.");
    }

    [TestMethod]
    public void HintsOnACteReference_AreDiscarded()
        => AreEqual(2, new Simulation().ExecuteScalar($"{HintTable} with c as (select id from t) select count(*) from c with (nolock, index(ix_a))"));

    [TestMethod]
    [DataRow("update t set a = 0 where id = 1 option (recompile)")]
    [DataRow("update x set a = 0 from t x join t y on y.id = x.id where x.id = 1 option (hash join, maxdop 1)")]
    [DataRow("delete t where id = 1 option (maxdop 1)")]
    [DataRow("delete x from t x join t y on y.id = x.id where x.id = 1 option (loop join)")]
    [DataRow("insert t values (3, 30) option (recompile)")]
    [DataRow("merge t x using (select 1 k) s on s.k = x.id when matched then update set a = 0 option (loop join);")]
    public void WriteStatements_TakeAnOptionClause(string statement)
        => AreEqual(1, new Simulation().ExecuteScalar($"{HintTable} {statement} select @@rowcount"));

    /// <summary>
    /// The OPTION clause's own grammar and argument checks (probed 2026-10-05
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("maxdop 32768", 304)]
    [DataRow("maxdop -1", 102)]
    [DataRow("maxdop 'a'", 102)]
    [DataRow("maxdop 1, maxdop 2", 1042)]
    [DataRow("fast 1, fast 2", 1042)]
    [DataRow("fast 2147483648", 102)]
    [DataRow("maxrecursion 32768", 310)]
    [DataRow("maxrecursion 1, maxrecursion 2", 1042)]
    [DataRow("max_grant_percent = 101", 1042)]
    [DataRow("min_grant_percent = 1e1", 102)]
    [DataRow("label = 'a', label = 'b'", 10768)]
    [DataRow("label = 1", 102)]
    [DataRow("parameterization forced", 102)]
    [DataRow("remote join", 155)]
    [DataRow("hash join merge join", 156)]
    [DataRow("maxdop 1 recompile", 102)]
    [DataRow("optimize for ()", 102)]
    [DataRow("use plan N''", 8695)]
    [DataRow("use plan N'<x/>'", 6913)]
    public void OptionClause_ArgumentErrors(string hints, int number)
        => _ = new Simulation().AssertSqlError($"{HintTable} select id from t option ({hints})", number);

    [TestMethod]
    [DataRow("maxdop 1, maxdop 1")]
    [DataRow("maxdop 0, fast 2147483647")]
    [DataRow("min_grant_percent = 0, max_grant_percent = 10.5")]
    [DataRow("label = N'x'")]
    [DataRow("hash group, order group, concat union, merge union, hash union")]
    [DataRow("keep plan, keepfixed plan, robust plan, expand views, no_performance_spool, ignore_nonclustered_columnstore_index")]
    [DataRow("optimize for unknown, force order, recompile")]
    public void OptionClause_ValidHints_AreTaken(string hints)
        => AreEqual(2, new Simulation().ExecuteScalar($"{HintTable} select count(*) from t option ({hints})"));

    [TestMethod]
    [DataRow("(@q = 1)", 137, "Must declare the scalar variable \"@q\".")]
    [DataRow("(@p = 'x')", 4132, "The value specified for the variable \"@p\" in the OPTIMIZE FOR clause could not be implicitly converted to that variable's type.")]
    [DataRow("(@p unknown), optimize for (@p = 1)", 4131, "A compile-time literal value is specified more than once for the variable \"@p\" in one or more OPTIMIZE FOR clauses.")]
    [DataRow("(@p = @p)", 320, "The compile-time variable value for '@p' in the OPTIMIZE FOR clause must be a literal.")]
    [DataRow("(@d = 5)", 206, "Operand type clash: int is incompatible with date")]
    public void OptimizeFor_ChecksItsVariables(string clause, int number, string message)
        => new Simulation().AssertSqlError(
            $"{HintTable} declare @p int = 1, @d date; select id from t where a > @p and @d is null option (optimize for {clause})", number, message);

    [TestMethod]
    public void OptimizeFor_TakesLiteralsItsVariablesConvert()
        => AreEqual(2, new Simulation().ExecuteScalar(
            $"{HintTable} declare @p int = 1, @q int; select count(*) from t where a > @p or a = @q option (optimize for (@p = '5', @q unknown))"));

    [TestMethod]
    [DataRow("select id from t where id in (select id from t option (maxdop 1))")]
    [DataRow("select * from (select id from t option (maxdop 1)) d")]
    [DataRow("if exists (select 1 from t option (maxdop 1)) select 1")]
    [DataRow("select id from t option (maxdop 1) union select id from t")]
    [DataRow("select id from t option (maxdop 1) option (recompile)")]
    public void OptionClause_OnlyClosesTheStatementsOwnQuery(string statement)
        => _ = new Simulation().AssertSqlError($"{HintTable} {statement}", 156);

    [TestMethod]
    public void OptionClause_MayPrecedeForXml()
        => AreEqual("<row id=\"1\"/><row id=\"2\"/>", new Simulation().ExecuteScalar($"{HintTable} select id from t order by id option (maxdop 1) for xml raw"));

    /// <summary>
    /// A TABLE HINT names a source as the query exposes it — its alias when it
    /// has one — and may carry no semantic hint the source's own WITH clause
    /// lacks (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select id from dbo.t option (table hint(t, index(ix_a)))", 8723)]
    [DataRow("select x.id from dbo.t x option (table hint(dbo.t, index(ix_a)))", 8723)]
    [DataRow("select id from dbo.t option (table hint(dbo.t, index(ix_a)), table hint(dbo.t, index(ix_a)))", 8720)]
    [DataRow("select id from dbo.t option (table hint(dbo.t, nolock))", 8722)]
    [DataRow("select id from dbo.t option (table hint(dbo.t, index(nosuch)))", 308)]
    [DataRow("select id from dbo.t option (table hint(dbo.t, forcescan, forceseek))", 10746)]
    [DataRow("select id from dbo.t where a + 1 = 2 option (table hint(dbo.t, forceseek))", 8622)]
    public void TableHintClause_IsCheckedAgainstTheQuery(string statement, int number)
        => _ = new Simulation().AssertSqlError($"{HintTable} {statement}", number);

    [TestMethod]
    public void TableHintClause_MatchingTheExposedName_IsTaken()
        => AreEqual(1, new Simulation().ExecuteScalar(
            $"{HintTable} select count(*) from dbo.t x with (nolock) where x.a = 10 option (table hint(x, nolock, forceseek))"));
}
