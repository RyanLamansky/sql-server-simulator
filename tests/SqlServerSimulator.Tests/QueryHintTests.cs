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
        _ = sim.ExecuteNonQuery("""
            create schema au;
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
        => AreEqual("Invalid column name 'a'.", new Simulation().AssertSqlError($"{SeekTable} select * from t (a)", 207).Message);

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

    [TestMethod]
    public void BareParen_IndexHint_ReportsMsg1018()
        => new Simulation().AssertSqlError(
            $"{SeekTable} select * from t (index(ix_ab))",
            1018,
            "Incorrect syntax near 'INDEX'. If this is intended as a part of a table hint, A WITH keyword and parenthesis are now required. See SQL Server Books Online for proper syntax.");

    // --- FORCESEEK's nested form validates its index and its seek columns ---

    [TestMethod]
    public void ForceSeek_LeadingKeyPrefix_IsAccepted()
        => AreEqual(1, new Simulation().ExecuteScalar($"{SeekTable} select count(*) from t with (forceseek(ix_ab(a))) where a = 1"));

    [TestMethod]
    public void ForceSeek_WholeKey_IsAccepted()
        => AreEqual(1, new Simulation().ExecuteScalar($"{SeekTable} select count(*) from t with (forceseek(ix_ab(a, b))) where a = 1"));

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
    public void ForceSeek_WithoutTheNestedForm_StaysParseAndDiscard()
        => AreEqual(1, new Simulation().ExecuteScalar($"{SeekTable} select count(*) from t with (forceseek)"));
}
