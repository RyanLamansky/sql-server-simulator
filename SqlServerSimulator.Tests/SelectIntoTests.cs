using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>SELECT … INTO target</c>. Covers schema inference
/// (direct refs preserve nullability + identity, expressions / aggregates
/// / CAST always nullable, ISNULL non-null when either arg is non-null,
/// CASE all-branches-non-null), target routing (temp vs regular tables),
/// FROM-less SELECT INTO, INSERT-then-read round-trip, identity high-water
/// mark propagation, error cases (Msg 2705 duplicate name, Msg 1038
/// unnamed column, Msg 2714 target exists). Behavior probed against SQL
/// Server 2025 (2026-05-11).
/// </summary>
[TestClass]
public sealed class SelectIntoTests
{
    /// <summary>
    /// Source table for SELECT INTO inference tests. Interpolated into each
    /// test's command-specific SQL so the whole batch runs as a single
    /// command — required because temp-table targets (<c>#t</c>) live in
    /// the connection that created them, and <c>Simulation.ExecuteNonQuery</c>
    /// opens a fresh connection per call.
    /// </summary>
    private const string Seed = """
        create table src (id int identity primary key, a int not null, b int null, cs varchar(10) not null, ds varchar(10) null);
        insert src (a, b, cs, ds) values (1, NULL, 'x', NULL), (2, 20, 'y', 'q');
        """;

    [TestMethod]
    public void BasicProjection_CopiesAllRows()
        => AreEqual(2, new Simulation().ExecuteScalar<int>($"""
            {Seed}
            select id, a into #t from src;
            select count(*) from #t
            """));

    /// <summary>
    /// Identity propagated through a direct column ref from a single
    /// non-joined source: inserting a row without supplying <c>id</c>
    /// auto-generates the next value past the source's max (2 → 3).
    /// </summary>
    [TestMethod]
    public void DirectColumnRef_PreservesIdentity()
        => AreEqual(3, new Simulation().ExecuteScalar<int>($"""
            {Seed}
            select id, a into #t from src;
            insert #t (a) values (99);
            select max(id) from #t
            """));

    /// <summary>
    /// An expression wrapper (<c>id + 0</c>) disqualifies identity
    /// propagation — the dest's <c>id_expr</c> is a plain int, so explicit
    /// values insert without IDENTITY_INSERT.
    /// </summary>
    [TestMethod]
    public void ExpressionColumn_DropsIdentity()
        => AreEqual(3, new Simulation().ExecuteScalar<int>($"""
            {Seed}
            select id + 0 as id_expr, a into #t from src;
            insert #t values (100, 50);
            select count(*) from #t
            """));

    /// <summary>
    /// Any join drops identity from every projected column (probe-confirmed,
    /// even when only one branch has identity). Inserting an explicit value
    /// without IDENTITY_INSERT works.
    /// </summary>
    [TestMethod]
    public void Join_DropsIdentity()
        => AreEqual(3, new Simulation().ExecuteScalar<int>($"""
            {Seed}
            create table src2 (k int);
            insert src2 values (1), (2);
            select s.id, s.a into #t from src s join src2 s2 on s.id = s2.k;
            insert #t values (50, 999);
            select count(*) from #t
            """));

    /// <summary><c>a</c> is NOT NULL in source → NOT NULL in dest → Msg 515 on NULL insert.</summary>
    [TestMethod]
    public void DirectColumnRef_PreservesNotNull()
        => new Simulation().AssertSqlError($"""
            {Seed}
            select a, b into #t from src;
            insert #t (a, b) values (NULL, 5)
            """, 515);

    /// <summary><c>b</c> is NULL allowed in source → NULL allowed in dest → NULL insert succeeds.</summary>
    [TestMethod]
    public void DirectColumnRef_PreservesNullable()
        => AreEqual(3, new Simulation().ExecuteScalar<int>($"""
            {Seed}
            select a, b into #t from src;
            insert #t (a, b) values (10, NULL);
            select count(*) from #t
            """));

    /// <summary>
    /// Integer arithmetic projects as NULL allowed even when both operands
    /// are NOT NULL — real SQL Server's documented rule (overflow potential).
    /// </summary>
    [TestMethod]
    public void IntegerArithmetic_AlwaysNullable()
        => AreEqual(3, new Simulation().ExecuteScalar<int>($"""
            {Seed}
            select a + 1 as v into #t from src;
            insert #t values (NULL);
            select count(*) from #t
            """));

    /// <summary>
    /// <c>ISNULL(x, y)</c> projects as NOT NULL when either operand is
    /// non-null — asymmetric with COALESCE which is always nullable.
    /// </summary>
    [TestMethod]
    public void IsNull_NonNullWhenEitherArgNonNull()
        => new Simulation().AssertSqlError($"""
            {Seed}
            select isnull(b, 0) as v into #t from src;
            insert #t values (NULL)
            """, 515);

    /// <summary>
    /// COALESCE projects as nullable even when one operand is a non-null
    /// constant — surprising but probe-confirmed against SQL Server 2025.
    /// </summary>
    [TestMethod]
    public void Coalesce_AlwaysNullable()
        => AreEqual(3, new Simulation().ExecuteScalar<int>($"""
            {Seed}
            select coalesce(b, 0) as v into #t from src;
            insert #t values (NULL);
            select count(*) from #t
            """));

    /// <summary>CASE with both THEN and ELSE non-null projects as NOT NULL.</summary>
    [TestMethod]
    public void Case_NonNullWhenAllBranchesNonNull()
        => new Simulation().AssertSqlError($"""
            {Seed}
            select case when a > 0 then 1 else 2 end as v into #t from src;
            insert #t values (NULL)
            """, 515);

    /// <summary>Missing ELSE acts as implicit <c>ELSE NULL</c> → projects as nullable.</summary>
    [TestMethod]
    public void Case_NullableWithoutElse()
        => AreEqual(3, new Simulation().ExecuteScalar<int>($"""
            {Seed}
            select case when a > 0 then 1 end as v into #t from src;
            insert #t values (NULL);
            select count(*) from #t
            """));

    /// <summary>Any branch referencing a nullable source column makes the CASE result nullable.</summary>
    [TestMethod]
    public void Case_NullableWhenAnyBranchNullable()
        => AreEqual(3, new Simulation().ExecuteScalar<int>($"""
            {Seed}
            select case when a > 0 then 1 else b end as v into #t from src;
            insert #t values (NULL);
            select count(*) from #t
            """));

    [TestMethod]
    public void Literal_NonNull()
        => new Simulation().AssertSqlError($"""
            {Seed}
            select 42 as v into #t from src;
            insert #t values (NULL)
            """, 515);

    /// <summary>Bare <c>NULL</c> literal → typed as int, nullable.</summary>
    [TestMethod]
    public void BareNullLiteral_Nullable()
        => AreEqual(3, new Simulation().ExecuteScalar<int>($"""
            {Seed}
            select null as v into #t from src;
            insert #t values (NULL);
            select count(*) from #t
            """));

    [TestMethod]
    public void Cast_AlwaysNullable()
        => AreEqual(3, new Simulation().ExecuteScalar<int>($"""
            {Seed}
            select cast(a as bigint) as v into #t from src;
            insert #t values (NULL);
            select count(*) from #t
            """));

    /// <summary>
    /// COUNT projects as nullable in real SQL Server (despite the runtime
    /// guarantee that COUNT never returns NULL). Probe-confirmed.
    /// </summary>
    [TestMethod]
    public void Aggregate_AlwaysNullable()
        => AreEqual(2, new Simulation().ExecuteScalar<int>($"""
            {Seed}
            select count(*) as c into #t from src;
            insert #t values (NULL);
            select count(*) from #t
            """));

    /// <summary>FROM-less SELECT INTO works; literal <c>'hello'</c> is varchar(5) NOT NULL.</summary>
    [TestMethod]
    public void NoFromClause_Works()
        => new Simulation().AssertSqlError("""
            select 42 as x, 'hello' as y into #t;
            insert #t values (NULL, NULL)
            """, 515);

    /// <summary>Identity propagates to a regular (non-temp) destination too.</summary>
    [TestMethod]
    public void RegularTableTarget_Works()
        => AreEqual(3, new Simulation().ExecuteScalar<int>($"""
            {Seed}
            select id, a into dest_reg from src;
            insert dest_reg (a) values (99);
            select max(id) from dest_reg
            """));

    /// <summary>
    /// Temp-table target lives in the creating session — a second
    /// connection on the same Simulation can't see it.
    /// </summary>
    [TestMethod]
    public void TempTableTarget_AutoDroppedOnClose()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table src (a int);
            insert src values (1), (2);
            select a into #t from src
            """);
        _ = sim.AssertSqlError("select * from #t", 208);
    }

    [TestMethod]
    public void TargetAlreadyExists_RaisesMsg2714()
        => new Simulation().AssertSqlError($"""
            {Seed}
            create table #t (id int);
            select id into #t from src
            """, 2714);

    [TestMethod]
    public void UnnamedColumn_RaisesMsg1038()
        => new Simulation().AssertSqlError($"""
            {Seed}
            select a + 1 into #t from src
            """, 1038);

    [TestMethod]
    public void DuplicateColumnName_RaisesMsg2705()
        => new Simulation().AssertSqlError($"""
            {Seed}
            select a, a into #t from src
            """, 2705, "Column names in each table must be unique. Column name 'a' in table '#t' is specified more than once.");

    /// <summary>
    /// Real SQL Server propagates identity through a simple CTE; the
    /// simulator drops both identity and nullability because CTE bindings
    /// synthesize wrapper columns with nullable=true and no identity.
    /// Documented divergence; the inserted explicit row succeeds without
    /// IDENTITY_INSERT because the column lost its identity property.
    /// </summary>
    [TestMethod]
    public void CTE_DropsIdentityAndNullabilityInSimulator()
        => AreEqual(3, new Simulation().ExecuteScalar<int>($"""
            {Seed}
            with cte as (select id, a from src) select id, a into #t from cte;
            insert #t values (50, 99);
            select count(*) from #t
            """));

    [TestMethod]
    public void Where_PreservesIdentity()
        => AreEqual(3, new Simulation().ExecuteScalar<int>($"""
            {Seed}
            select id, a into #t from src where a > 0;
            insert #t (a) values (99);
            select max(id) from #t
            """));

    [TestMethod]
    public void OrderBy_PreservesIdentity()
        => AreEqual(3, new Simulation().ExecuteScalar<int>($"""
            {Seed}
            select id, a into #t from src order by a desc;
            insert #t (a) values (99);
            select max(id) from #t
            """));

    /// <summary>TOP 1 picks the first row (id=1) → next auto-generated identity is 2.</summary>
    [TestMethod]
    public void Top_PreservesIdentity()
        => AreEqual(2, new Simulation().ExecuteScalar<int>($"""
            {Seed}
            select top 1 id, a into #t from src;
            insert #t (a) values (99);
            select max(id) from #t
            """));

    /// <summary>
    /// Transaction lifecycle requires a held DbConnection — can't densify
    /// into a single batch because ROLLBACK is invoked via the
    /// <see cref="DbTransaction"/> API, not inline SQL.
    /// </summary>
    [TestMethod]
    public void Transaction_RollbackUndoesSelectIntoTemp()
    {
        using var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand(Seed).ExecuteNonQuery();
        using (var tx = conn.BeginTransaction())
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "select id into #t from src";
            _ = cmd.ExecuteNonQuery();
            tx.Rollback();
        }
        var ex = Throws<DbException>(() => conn.CreateCommand("select * from #t").ExecuteNonQuery());
        AreEqual("208", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void EmptySource_CreatesEmptyDest()
        => AreEqual(0, new Simulation().ExecuteScalar<int>($"""
            {Seed}
            select id, a into #t from src where 1 = 0;
            select count(*) from #t
            """));

    [TestMethod]
    public void GlobalTempTarget_NotSupported()
        => _ = Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery($"""
            {Seed}
            select id into ##g from src
            """));
}
