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
        var ex = Throws<SimulatedSqlException>(() => conn.CreateCommand("select * from #t").ExecuteNonQuery());
        AreEqual(208, ex.Number);
    }

    [TestMethod]
    public void EmptySource_CreatesEmptyDest()
        => AreEqual(0, new Simulation().ExecuteScalar<int>($"""
            {Seed}
            select id, a into #t from src where 1 = 0;
            select count(*) from #t
            """));

    [TestMethod]
    public void GlobalTempTarget_LandsInGlobalTempDict_RoundTrips()
    {
        // SELECT INTO ##g goes to Simulation.GlobalTempTables (instance-wide,
        // owned by the connection that ran the SELECT INTO) — same routing
        // as CREATE TABLE ##g. Probe-confirmed that real SQL Server accepts
        // this shape.
        var sim = new Simulation();
        using var conn = sim.CreateOpenConnection();
        _ = conn.CreateCommand($"""
            {Seed}
            select a into ##g from src
            """).ExecuteNonQuery();
        AreEqual(2, conn.CreateCommand("select count(*) from ##g").ExecuteScalar());
        // Visible from another session on the same Simulation.
        using var other = sim.CreateOpenConnection();
        AreEqual(2, other.CreateCommand("select count(*) from ##g").ExecuteScalar());
    }

    // ---- the IDENTITY() function (probed 2026-09-24 against SQL Server 2025) ----

    [TestMethod]
    public void IdentityFunction_NumbersRowsInTheQuerysOrder()
        => AreEqual("5:2,7:1", new Simulation().ExecuteScalar("""
            select identity(bigint, 5, 2) as i, x into t from (values (1), (2)) v(x) order by x desc;
            select string_agg(concat(i, ':', x), ',') within group (order by i) from t
            """));

    [TestMethod]
    public void IdentityFunction_MakesARealIdentityColumn()
        => AreEqual("q|0|1|int|10|3|5", new Simulation().ExecuteScalar("""
            select identity(int, 3, 1) 'q' into t from (values (1), (2)) v(x);
            insert t default values;
            select concat(c.name, '|', c.is_nullable, '|', c.is_identity, '|', type_name(c.system_type_id), '|', c.precision, '|',
                          cast(ic.seed_value as int), '|', ident_current('t'))
            from sys.columns c join sys.identity_columns ic on ic.object_id = c.object_id and ic.column_id = c.column_id
            where c.object_id = object_id('t')
            """));

    [TestMethod]
    [DataRow("select identity(int) as i into t", "1")]
    [DataRow("select i = identity(int, -1, -1) into t from (values (1), (2)) v(x)", "-2")]
    [DataRow("select identity(numeric(5, 0), 1, 1) as i into t from (values (1), (2)) v(x)", "2")]
    [DataRow("select identity(int, - 1, 1) as i into #t; select * into t from #t", "-1")]
    public void IdentityFunction_Forms(string sql, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"{sql}; select cast(min(i) as varchar) from t where abs(i) = (select max(abs(i)) from t)"));

    [TestMethod]
    public void IdentityFunction_EmptyResult_LeavesIdentCurrentAtTheSeed()
        => AreEqual(7m, new Simulation().ExecuteScalar("select identity(int, 7, 1) i into t from (values (1)) v(x) where 1 = 0; select ident_current('t')"));

    [TestMethod]
    [DataRow("select identity(int, 1, 1) as i", 177)]
    [DataRow("create table d (i int); insert d select identity(int, 1, 1) as i", 177)]
    [DataRow("select identity(int, 1, 1) as i, x into t from (values (1)) v(x) union all select 5, 6", 1057)]
    [DataRow("select identity(int, 1, 1) i, identity(int, 1, 1) j into t", 8109)]
    [DataRow("select identity(int, 1, 1) + 1 as x into t", 102)]
    [DataRow("select identity(int, 1, 1), 2 as x into t", 102)]
    [DataRow("select identity(int, 1, 1) into t", 156)]
    [DataRow("select identity(int, 1) as i into t", 102)]
    [DataRow("select identity(int, 1e0, 1) as i into t", 102)]
    [DataRow("declare @s int = 1; select identity(int, @s, 1) as i into t", 102)]
    [DataRow("select * into t from (select identity(int, 1, 1) as i) d", 156)]
    [DataRow("select x into t from (values (1)) v(x) where identity(int, 1, 1) > 0", 156)]
    [DataRow("select identity(tinyint, -1, 1) as i into t", 2752)]
    [DataRow("select identity(int, 1.5, 1) as i into t", 2752)]
    [DataRow("select identity(int, 1, 0) as i into t", 2753)]
    [DataRow("select identity(int, 1, 2.0) as i into t", 2753)]
    public void IdentityFunction_Refused(string sql, int number)
        => new Simulation().AssertSqlError(sql, number);

    [TestMethod]
    [DataRow("varchar(10)", "varchar")]
    [DataRow("decimal(10, 2)", "decimal")]
    public void IdentityFunction_OtherType_RaisesMsg2749NamingIt(string type, string name)
        => new Simulation().AssertSqlError($"select identity({type}, 1, 1) as i into t", 2749,
            $"Identity column '{name}' must be of data type int, bigint, smallint, tinyint, or decimal or numeric with a scale of 0, unencrypted, and constrained to be nonnullable.");

    [TestMethod]
    public void IdentityFunction_BesideAnInheritedIdentity_RaisesMsg8108()
        => new Simulation().AssertSqlError("""
            create table s (id int identity, v int);
            select identity(int, 1, 1) as i, id into t from s
            """, 8108, "Cannot add identity column, using the SELECT INTO statement, to table 't', which already has column 'id' that inherits the identity property.");

    [TestMethod]
    public void IdentityFunction_TinyIntOverflow_RaisesMsg8115()
        => new Simulation().AssertSqlError(
            "select identity(tinyint, 250, 5) as i into t from (values (1), (2), (3)) v(x)",
            8115, "Arithmetic overflow error converting IDENTITY to data type tinyint.");

    [TestMethod]
    public void IdentityFunction_RerunInALoop_NumbersEachTableFromTheSeed()
        => AreEqual(4, new Simulation().ExecuteScalar("""
            declare @n int = 0, @total int = 0;
            while @n < 2
            begin
                select identity(int, 1, 1) as i into #t from (values (1), (2)) v(x);
                set @total += (select max(i) from #t);
                drop table #t;
                set @n += 1;
            end
            select @total
            """));

    // IDENTITY()'s type is a bare system type name (probed 2026-09-24 against
    // SQL Server 2025).
    [TestMethod]
    public void IdentityFunction_SchemaQualifiedType_IsMsg102AtTheDot()
        => new Simulation().ValidateSyntaxError("select identity(dbo.foo, 1, 1) as id into #t", ".");

    [TestMethod]
    public void CharacterColumns_KeepTheirCollationInSysColumns()
        => AreEqual("Latin1_General_BIN;Japanese_CI_AS;SQL_Latin1_General_CP1_CI_AS|Latin1_General_BIN", new Simulation().ExecuteScalar("""
            create table t (a varchar(10) collate Latin1_General_BIN, b nvarchar(5) collate Japanese_CI_AS, c varchar(3));
            select a, b, c into u from t;
            select a into #v from t;
            select (select string_agg(collation_name, ';') within group (order by column_id) from sys.columns where object_id = object_id('u'))
                + '|' + (select collation_name from tempdb.sys.columns where object_id = object_id('tempdb..#v'))
            """));
}
