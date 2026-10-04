using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for indexed (materialized) views: <c>CREATE UNIQUE
/// CLUSTERED INDEX ON &lt;view&gt;</c>, the schema-binding / unique-clustered
/// gates (Msg 1939 / 1940 / 1941), create-time duplicate rejection (Msg 1505),
/// live base-DML uniqueness enforcement (Msg 2601), the <c>sys.indexes</c> /
/// <c>sys.index_columns</c> / <c>sys.stats</c> catalog surface, and
/// <c>is_schema_bound</c>. Probe-confirmed against SQL Server 2025
/// (2026-07-17).
/// </summary>
[TestClass]
public sealed class IndexedViewTests
{
    /// <summary>
    /// Base table + a schema-bound projection view + a unique clustered index
    /// on the view's <c>Id</c> column, with three seed rows (Ids 1/2/3).
    /// </summary>
    private static Simulation SeedIndexedView()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.b (id int not null, grp int not null, val int not null); insert dbo.b values (1,10,100),(2,20,200),(3,30,300)",
            "create view dbo.v with schemabinding as select id, grp, val from dbo.b",
            "create unique clustered index ix_v on dbo.v(id)");
        return sim;
    }

    // --- Gates ---

    [TestMethod]
    public void CreateIndex_OnNonSchemaBoundView_Msg1939()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.b (id int not null, grp int not null)",
            "create view dbo.v as select id, grp from dbo.b");
        var ex = sim.AssertSqlError("create unique clustered index ix on dbo.v(id)", 1939);
        AreEqual("Cannot create index on view 'v' because the view is not schema bound.", ex.Message);
    }

    [TestMethod]
    public void CreateIndex_NonUniqueClusteredOnView_Msg1941()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.b (id int not null, grp int not null)",
            "create view dbo.v with schemabinding as select id, grp from dbo.b");
        var ex = sim.AssertSqlError("create clustered index ix on dbo.v(id)", 1941);
        Contains("only unique clustered indexes are allowed", ex.Message);
    }

    [TestMethod]
    public void CreateIndex_NonClusteredFirstOnView_Msg1940()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.b (id int not null, grp int not null)",
            "create view dbo.v with schemabinding as select id, grp from dbo.b");
        var ex = sim.AssertSqlError("create nonclustered index ix on dbo.v(id)", 1940);
        AreEqual("Cannot create index on view 'dbo.v'. It does not have a unique clustered index.", ex.Message);
    }

    [TestMethod]
    public void CreateIndex_UniqueNonClusteredFirstOnView_Msg1940()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.b (id int not null, grp int not null)",
            "create view dbo.v with schemabinding as select id, grp from dbo.b");
        _ = sim.AssertSqlError("create unique nonclustered index ix on dbo.v(id)", 1940);
    }

    [TestMethod]
    public void CreateIndex_MissingViewColumn_Msg1911()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.b (id int not null, grp int not null)",
            "create view dbo.v with schemabinding as select id, grp from dbo.b");
        _ = sim.AssertSqlError("create unique clustered index ix on dbo.v(nope)", 1911);
    }

    // --- Create-time validation ---

    [TestMethod]
    public void CreateUniqueClusteredIndex_OverDuplicateViewRows_Msg1505()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.b (id int not null, grp int not null); insert dbo.b values (1,10),(2,10),(3,20)",
            "create view dbo.v with schemabinding as select grp from dbo.b");
        var ex = sim.AssertSqlError("create unique clustered index ix on dbo.v(grp)", 1505);
        Contains("'dbo.v'", ex.Message);
        Contains("(10)", ex.Message);
    }

    [TestMethod]
    public void CreateUniqueClusteredIndex_Succeeds()
        => AreEqual(3L, SeedIndexedView().ExecuteScalar("select count_big(*) from dbo.v"));

    // --- Enforcement ---

    [TestMethod]
    public void Insert_ProducingDuplicateViewKey_Msg2601_NamesViewAndIndex()
    {
        var ex = SeedIndexedView().AssertSqlError("insert dbo.b values (1, 99, 999)", 2601);
        AreEqual("Cannot insert duplicate key row in object 'dbo.v' with unique index 'ix_v'. The duplicate key value is (1).", ex.Errors[0].Message);
    }

    [TestMethod]
    public void Insert_ProducingDuplicateViewKey_RollsBackStatement()
    {
        var sim = SeedIndexedView();
        _ = sim.AssertSqlError("insert dbo.b values (1, 99, 999)", 2601);
        AreEqual(3, sim.ExecuteScalar("select count(*) from dbo.b"));
    }

    [TestMethod]
    public void Update_ProducingDuplicateViewKey_Msg2601()
    {
        var sim = SeedIndexedView();
        var ex = sim.AssertSqlError("update dbo.b set id = 2 where id = 3", 2601);
        Contains("'dbo.v'", ex.Message);
        AreEqual(3, sim.ExecuteScalar("select count(*) from dbo.b where id in (1,2,3)"));
    }

    /// <summary>A MERGE's inserted and updated rows are judged as an INSERT's and an UPDATE's are.</summary>
    [TestMethod]
    [DataRow("merge dbo.b as t using (values (1)) s(id) on 1 = 0 when not matched then insert values (s.id, 99, 999);")]
    [DataRow("merge dbo.b as t using (values (3)) s(id) on t.id = s.id when matched then update set id = 2;")]
    public void Merge_ProducingDuplicateViewKey_Msg2601(string merge)
    {
        var sim = SeedIndexedView();
        Contains("'dbo.v'", sim.AssertSqlError(merge, 2601).Message);
        AreEqual("1,2,3", sim.ExecuteScalar("select string_agg(id, ',') within group (order by id) from dbo.b"));
    }

    [TestMethod]
    public void Insert_NonDuplicate_Succeeds()
    {
        var sim = SeedIndexedView();
        _ = sim.ExecuteNonQuery("insert dbo.b values (4, 40, 400)");
        AreEqual(4, sim.ExecuteScalar("select count(*) from dbo.b"));
    }

    [TestMethod]
    public void Delete_NeverViolatesViewUniqueness()
    {
        var sim = SeedIndexedView();
        _ = sim.ExecuteNonQuery("delete dbo.b where id = 3");
        AreEqual(2, sim.ExecuteScalar("select count(*) from dbo.b"));
    }

    // --- is_schema_bound ---

    [TestMethod]
    public void SchemaBinding_SurfacesThroughSqlModulesAndObjectProperty()
    {
        var sim = SeedIndexedView();
        IsTrue((bool)sim.ExecuteScalar("select is_schema_bound from sys.sql_modules where object_id = object_id('dbo.v')")!);
        AreEqual(1, sim.ExecuteScalar("select objectproperty(object_id('dbo.v'), 'IsSchemaBound')"));
    }

    [TestMethod]
    public void NonSchemaBoundView_ReportsIsSchemaBoundFalse()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.b (id int not null)",
            "create view dbo.v as select id from dbo.b");
        IsFalse((bool)sim.ExecuteScalar("select is_schema_bound from sys.sql_modules where object_id = object_id('dbo.v')")!);
    }

    // --- Catalog surface ---

    [TestMethod]
    public void ViewIndex_SurfacesInSysIndexes_AsClusteredUnique()
    {
        var sim = SeedIndexedView();
        AreEqual(1, sim.ExecuteScalar(
            "select index_id from sys.indexes where object_id = object_id('dbo.v') and name = 'ix_v' and type_desc = 'CLUSTERED' and is_unique = 1 and is_primary_key = 0 and is_unique_constraint = 0"));
        // No HEAP row for a view (no index_id 0).
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.indexes where object_id = object_id('dbo.v') and index_id = 0"));
    }

    [TestMethod]
    public void PlainView_HasNoSysIndexesRows()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.b (id int not null)",
            "create view dbo.v as select id from dbo.b");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.indexes where object_id = object_id('dbo.v')"));
    }

    [TestMethod]
    public void ViewIndex_SurfacesInSysIndexColumns_WithViewColumnId()
    {
        // Two key columns: id (column_id 1) and grp (column_id 2).
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.b (id int not null, grp int not null, val int not null); insert dbo.b values (1,10,100)",
            "create view dbo.v with schemabinding as select id, grp, val from dbo.b",
            "create unique clustered index ix_v on dbo.v(id, grp)");
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.index_columns where object_id = object_id('dbo.v') and is_included_column = 0"));
        AreEqual(1, sim.ExecuteScalar("select column_id from sys.index_columns where object_id = object_id('dbo.v') and key_ordinal = 1"));
        AreEqual(2, sim.ExecuteScalar("select column_id from sys.index_columns where object_id = object_id('dbo.v') and key_ordinal = 2"));
    }

    [TestMethod]
    public void ViewIndex_SurfacesInSysStats()
    {
        var sim = SeedIndexedView();
        AreEqual(1, sim.ExecuteScalar("select stats_id from sys.stats where object_id = object_id('dbo.v') and name = 'ix_v'"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.stats_columns where object_id = object_id('dbo.v') and stats_id = 1"));
    }

    /// <summary>
    /// The three body re-parses <c>CREATE INDEX</c> runs over a view — the
    /// qualifying-battery shape scan, the duplicate-key materialization, and
    /// the base-table dependency collection — each execute outside the
    /// dispatch loop, so each has to release its own statement schema locks.
    /// A leak there leaves a Sch-S on the base table that outlives the
    /// connection, and the next connection's Sch-M waits forever; the
    /// separate-connection <c>ALTER TABLE</c> here is the regression guard
    /// (<c>ADD COLUMN</c> is deliberately un-gated by schema binding, so the
    /// statement's only obstacle would be the stale lock).
    /// </summary>
    [TestMethod]
    public void CreateViewIndex_ReleasesBaseTableSchemaLocks()
    {
        var sim = SeedIndexedView();
        _ = sim.ExecuteNonQuery("alter table dbo.b add extra int null");
        AreEqual(4, sim.ExecuteScalar("select count(*) from sys.columns where object_id = object_id('dbo.b')"));
    }

    // --- NOEXPAND ---

    [TestMethod]
    public void Select_FromIndexedView_WithNoExpand_Accepted()
        => AreEqual(3L, SeedIndexedView().ExecuteScalar("select count_big(*) from dbo.v with (noexpand)"));

    // ---- Qualifying battery (Msg 10100-series / 1947 / 1949 / 8662) ----

    /// <summary>
    /// Base tables for the battery: <c>b</c> carries a nullable column so the
    /// SUM rule can be exercised, and both are referenced two-part so the view
    /// bodies schema-bind.
    /// </summary>
    private static Simulation BatteryTables()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table dbo.t (id int not null primary key, a int not null, b int null);
            create table dbo.u (id int not null primary key, tid int not null, c int not null)
            """);
        return sim;
    }

    /// <summary>
    /// Every shape real SQL Server refuses to index. Each message is verbatim
    /// from SQL Server 2025 — note the quoting is not uniform across the
    /// family (10116 / 10138 / 1949 single-quote the view, the rest use
    /// double), which is real's inconsistency, not a transcription slip.
    /// The views themselves all create without complaint; real binds this
    /// battery at CREATE INDEX only, and so does the simulator.
    /// </summary>
    [TestMethod]
    [DataRow("select distinct a from dbo.t", "a", 10100,
        "Cannot create index on view \"simulated.dbo.v\" because it contains the DISTINCT keyword. Consider removing DISTINCT from the view or not indexing the view. Alternatively, consider replacing DISTINCT with GROUP BY or COUNT_BIG(*) to simulate DISTINCT on grouping columns.")]
    [DataRow("select top 5 id, a from dbo.t order by id", "id", 10101,
        "Cannot create index on view \"simulated.dbo.v\" because it contains the TOP or OFFSET keyword. Consider removing the TOP or OFFSET or not indexing the view.")]
    [DataRow("select t.id, u.c from dbo.t left join dbo.u on u.tid = t.id", "id", 10113,
        "Cannot create index on view \"simulated.dbo.v\" because it uses a LEFT, RIGHT, or FULL OUTER join, and no OUTER joins are allowed in indexed views. Consider using an INNER join instead.")]
    [DataRow("select id, a from dbo.t union all select id, c from dbo.u", "id", 10116,
        "Cannot create index on view 'simulated.dbo.v' because it contains one or more UNION, INTERSECT, or EXCEPT operators. Consider creating a separate indexed view for each query that is an input to the UNION, INTERSECT, or EXCEPT operators of the original view.")]
    [DataRow("select a, avg(c) av, count_big(*) cb from dbo.u join dbo.t on t.id = u.tid group by a", "a", 10125,
        "Cannot create index on view \"simulated.dbo.v\" because it uses aggregate \"AVG\". Consider eliminating the aggregate, not indexing the view, or using alternate aggregates. For example, for AVG substitute SUM and COUNT_BIG, or for COUNT, substitute COUNT_BIG.")]
    [DataRow("select id, a from dbo.t where id in (select tid from dbo.u)", "id", 10127,
        "Cannot create index on view \"simulated.dbo.v\" because it contains one or more subqueries. Consider changing the view to use only joins instead of subqueries. Alternatively, consider not indexing this view.")]
    [DataRow("select a, count(*) n from dbo.t group by a", "a", 10136,
        "Cannot create index on view \"simulated.dbo.v\" because it uses the aggregate COUNT. Use COUNT_BIG instead.")]
    [DataRow("select a, sum(b) sb from dbo.t group by a", "a", 10138,
        "Cannot create index on view 'simulated.dbo.v' because its select list does not include a proper use of COUNT_BIG. Consider adding COUNT_BIG(*) to select list.")]
    [DataRow("select id, dateadd(day, 1, getdate()) d from dbo.t", "id", 1949,
        "Cannot create index on view 'simulated.dbo.v'. The function 'getdate' yields nondeterministic results. Use a deterministic system function, or modify the user-defined function to return deterministic results.")]
    [DataRow("select id, newid() g from dbo.t", "id", 1949,
        "Cannot create index on view 'simulated.dbo.v'. The function 'newid' yields nondeterministic results. Use a deterministic system function, or modify the user-defined function to return deterministic results.")]
    [DataRow("select t1.id, t2.a from dbo.t t1 join dbo.t t2 on t2.id = t1.id", "id", 1947,
        "Cannot create index on view \"simulated.dbo.v\". The view contains a self join on \"simulated.dbo.t\".")]
    [DataRow("with c as (select id, a from dbo.t) select id, a from c", "id", 10137,
        "Cannot create index on view \"simulated.dbo.v\" because it references common table expression \"c\". Views referencing common table expressions cannot be indexed. Consider not indexing the view, or removing the common table expression from the view definition.")]
    public void NonQualifyingShape_RejectedAtCreateIndex(string body, string keyColumn, int errorNumber, string message)
    {
        var sim = BatteryTables();
        // The view itself is always accepted — only indexing it fails.
        _ = sim.ExecuteNonQuery($"create view dbo.v with schemabinding as {body}");

        var ex = sim.AssertSqlError($"create unique clustered index ix on dbo.v({keyColumn})", errorNumber);
        AreEqual(message, ex.Message);
    }

    /// <summary>
    /// Msg 8662 is the odd one out: it names the <em>index</em> as well as the
    /// view and carries State 0 where the rest of the family carries State 1.
    /// </summary>
    [TestMethod]
    public void SumOverNullableColumn_Raises8662_NamingTheIndex()
    {
        var sim = BatteryTables();
        _ = sim.ExecuteNonQuery(
            "create view dbo.v with schemabinding as select a, sum(b) sb, count_big(*) cb from dbo.t group by a");

        var ex = sim.AssertSqlError("create unique clustered index ix_sum on dbo.v(a)", 8662);
        AreEqual(
            "Cannot create the clustered index \"ix_sum\" on view \"simulated.dbo.v\" because the view references an unknown value (SUM aggregate of nullable expression). Consider referencing only non-nullable values in SUM. ISNULL() may be useful for this.",
            ex.Message);
        AreEqual(0, ex.State);
    }

    /// <summary>
    /// Msg 10137 names the <em>first declared</em> CTE, not the one the body's
    /// SELECT reads — and a CTE nothing reads still names itself. It is also
    /// the one gate whose precedence is probe-confirmed: a CTE-bearing body
    /// reports 10137 ahead of the DISTINCT rejection it also violates.
    /// </summary>
    [TestMethod]
    [DataRow("with c1 as (select id, a from dbo.t), c2 as (select id, a from c1) select id, a from c2", "c1")]
    [DataRow("with unread as (select 1 as x) select id, a from dbo.t", "unread")]
    [DataRow("with c as (select distinct id, a from dbo.t) select id, a from c", "c")]
    public void CteBearingView_Raises10137_NamingTheFirstDeclaredCte(string body, string cteName)
    {
        var sim = BatteryTables();
        _ = sim.ExecuteNonQuery($"create view dbo.v with schemabinding as {body}");

        var ex = sim.AssertSqlError("create unique clustered index ix on dbo.v(id)", 10137);
        AreEqual(
            $"Cannot create index on view \"simulated.dbo.v\" because it references common table expression \"{cteName}\". Views referencing common table expressions cannot be indexed. Consider not indexing the view, or removing the common table expression from the view definition.",
            ex.Message);
    }

    /// <summary>
    /// The shapes that must keep working — the battery's job is to reject what
    /// real rejects, not to narrow what already ships. The inner-join
    /// projection is the shape AdventureWorks' two indexed views take, and the
    /// grouped form is the canonical SUM + COUNT_BIG aggregate view.
    /// </summary>
    [TestMethod]
    [DataRow("select t.id, t.a, u.c from dbo.t join dbo.u on u.tid = t.id", "id")]
    [DataRow("select a, sum(c) sc, count_big(*) cb from dbo.u join dbo.t on t.id = u.tid group by a", "a")]
    [DataRow("select id, a from dbo.t where a > 0", "id")]
    public void QualifyingShape_IndexesCleanly(string body, string keyColumn)
    {
        var sim = BatteryTables();
        _ = sim.ExecuteNonQuery($"create view dbo.v with schemabinding as {body}");
        _ = sim.ExecuteNonQuery($"create unique clustered index ix on dbo.v({keyColumn})");

        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.indexes where name = 'ix'"));
    }

    // === The rest of the qualifying battery, IsIndexable, and the SET-option gates ===

    private static Simulation BatteryFixture()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (id int primary key, g int not null, v int not null, s varchar(10) not null, f float not null, d datetime not null)",
            "create table dbo.u (id int primary key, w int not null)",
            "create table dbo.xt (id int primary key, x xml)",
            "create function dbo.fnd(@x int) returns datetime with schemabinding as begin return dateadd(day, @x, getdate()) end",
            "create function dbo.tf() returns table with schemabinding as return select id from dbo.t",
            "create view dbo.vin with schemabinding as select id, v from dbo.t");
        return sim;
    }

    [TestMethod]
    [DataRow("select x.id, x.v from (select id, v from dbo.t) x", "id", 10109)]
    [DataRow("select g, count_big(*) cb from dbo.t group by g having count_big(*) > 0", "g", 10121)]
    [DataRow("select id, row_number() over (order by id) rn from dbo.t", "id", 10143)]
    [DataRow("select t.id, x.w from dbo.t t cross apply (select w from dbo.u where u.id = t.id) x", "id", 10142)]
    [DataRow("select [1] a, [2] b from (select g, v from dbo.t) s pivot (sum(v) for g in ([1], [2])) p", "a", 10114)]
    [DataRow("select g, count_big(*) cb from dbo.t group by g with rollup", "g", 10119)]
    [DataRow("select g, count_big(*) cb, sum(v) * 2 sv2 from dbo.t group by g", "g", 8668)]
    [DataRow("select count_big(*) cb, sum(v) sv from dbo.t group by g", "cb", 8660)]
    [DataRow("select id, dbo.fnd(v) fv from dbo.t", "id", 1956)]
    [DataRow("select f, count_big(*) cb from dbo.t group by f", "f", 1962)]
    [DataRow("select id, convert(datetime, '1/2/2020') cd from dbo.t", "id", 1963)]
    [DataRow("select id, datediff(day, d, '20210101') dd from dbo.t", "id", 10139)]
    [DataRow("select id, datename(month, d) dn from dbo.t", "id", 1949)]
    [DataRow("select id, v from dbo.vin", "id", 1937)]
    [DataRow("select id, v from dbo.t with (nolock)", "id", 10140)]
    [DataRow("select id, binary_checksum(*) ck from dbo.t", "id", 10117)]
    [DataRow("select id from dbo.tf()", "id", 10129)]
    [DataRow("select [key] k from openjson('[1,2]')", "k", 10148)]
    [DataRow("select id, x.value('(/a)[1]', 'int') a from dbo.xt", "id", 1985)]
    [Description("Every shape refuses CREATE INDEX with real's own message and reads IsIndexable 0.")]
    public void QualifyingBattery_RefusesTheShape(string body, string key, int number)
    {
        var sim = BatteryFixture();
        sim.ExecuteBatches($"create view dbo.iv with schemabinding as {body}");
        AreEqual(0, sim.ExecuteScalar("select objectproperty(object_id('dbo.iv'), 'IsIndexable')"));
        _ = sim.AssertSqlError($"create unique clustered index cx on dbo.iv ({key})", number);
    }

    [TestMethod]
    [DataRow("select g, count_big(*) cb, sum(v * 2) sv from dbo.t group by g", "g")]
    [DataRow("select id, rand(1) r from dbo.t", "id")]
    [DataRow("select id, f from dbo.t", "id")]
    [Description("A SUM over arithmetic on NOT NULL columns and a seeded RAND both index, reading IsIndexable 1.")]
    public void QualifyingBattery_AcceptsTheShape(string body, string key)
    {
        var sim = BatteryFixture();
        sim.ExecuteBatches($"create view dbo.iv with schemabinding as {body}");
        AreEqual(1, sim.ExecuteScalar("select objectproperty(object_id('dbo.iv'), 'IsIndexable')"));
        _ = sim.ExecuteNonQuery($"create unique clustered index cx on dbo.iv ({key})");
    }

    [TestMethod]
    [Description("A computed float key column is Msg 1901, a second clustered index Msg 1902, a filtered nonclustered one Msg 10610.")]
    public void ViewIndexKeysAndKinds_Refusals()
    {
        var sim = BatteryFixture();
        sim.ExecuteBatches(
            "create view dbo.iv with schemabinding as select id, v, f * 2 f2 from dbo.t",
            "create unique clustered index cx on dbo.iv (id)");
        _ = sim.AssertSqlError("create unique index ux on dbo.iv (f2)", 1901);
        sim.AssertSqlError("create unique clustered index cx2 on dbo.iv (v)", 1902,
            "Cannot create more than one clustered index on view 'dbo.iv'. Drop the existing clustered index 'cx' before creating another.");
        _ = sim.AssertSqlError("create index nx on dbo.iv (v) where v > 0", 10610);
    }

    [TestMethod]
    [Description("A view, table or function created under ANSI_NULLS OFF is Msg 1935 at CREATE INDEX.")]
    public void AnsiNullsOffAtCreation_IsMsg1935()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "set ansi_nulls off",
            "create table dbo.t (id int primary key, v int not null)");
        using (var connection = sim.CreateOpenConnection())
        {
            _ = connection.CreateCommand("set ansi_nulls on").ExecuteNonQuery();
            _ = connection.CreateCommand("create view dbo.iv with schemabinding as select id, v from dbo.t").ExecuteNonQuery();
        }
        sim.AssertSqlError("create unique clustered index cx on dbo.iv (id)", 1935,
            "Cannot create index. Object 't' was created with the following SET options off: 'ANSI_NULLS'.");
    }

    [TestMethod]
    [Description("A write to an indexed view's base table under a refused SET option is Msg 1934, which ends the batch.")]
    public void WriteUnderARefusedOption_EndsTheBatch()
    {
        var sim = SeedIndexedView();
        using var connection = sim.CreateOpenConnection();
        var error = Throws<SimulatedSqlException>(() => connection.CreateCommand("set ansi_nulls off; insert dbo.b values (9, 9, 9); select 1 / 0").ExecuteNonQuery());
        AreEqual(1934, error.Number);
        AreEqual(1, error.Errors.Count);
    }

    [TestMethod]
    [Description("TRUNCATE of an indexed view's base table is Msg 3729 state 2 naming the view.")]
    public void TruncateOfABaseTable_IsMsg3729()
    {
        var sim = SeedIndexedView();
        sim.AssertSqlError("truncate table dbo.b", 3729, "Cannot TRUNCATE TABLE 'dbo.b' because it is being referenced by object 'v'.");
    }

    [TestMethod]
    [Description("NOEXPAND names an indexed view: on a plain view or a table it is Msg 8171 at the line of the object's name, on a write's target state 1; an index hint without it is the warning Msg 4430.")]
    public void NoExpandAndIndexHints()
    {
        var sim = SeedIndexedView();
        sim.ExecuteBatches("create view dbo.pv as select id from dbo.b");
        var error = sim.AssertSqlError("select id from dbo.pv with (noexpand)", 8171);
        AreEqual(1, error.Errors[0].LineNumber);
        AreEqual((byte)2, error.State);
        AreEqual(3, sim.AssertSqlError("select 1\nselect id\nfrom dbo.pv\nwith (noexpand)", 8171).Errors[0].LineNumber);
        _ = sim.AssertSqlError("select id from dbo.b with (noexpand)", 8171);
        AreEqual((byte)1, sim.AssertSqlError("update dbo.v with (noexpand) set val = 0", 8171).State);
        sim.AssertSqlError("select id from dbo.v with (noexpand, index(nosuch))", 308, "Index 'nosuch' on table 'dbo.v' (specified in the FROM clause) does not exist.");

        using var connection = sim.CreateOpenConnection();
        var messages = new List<int>();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(static entry => entry.Number));
        AreEqual(3, connection.CreateCommand("select count(*) from dbo.v with (index(ix_v))").ExecuteScalar());
        CollectionAssert.AreEqual(new[] { 4430 }, messages);
    }

    [TestMethod]
    [Description("DROP INDEX of a view's clustered index takes the rest and the maintenance with it; ALTER INDEX DISABLE makes NOEXPAND invalid until a REBUILD; a rollback undoes CREATE INDEX.")]
    public void ViewIndexLifecycle()
    {
        var sim = SeedIndexedView();
        sim.ExecuteBatches(
            "create index nx on dbo.v (val)",
            "alter index ix_v on dbo.v disable");
        _ = sim.AssertSqlError("select id from dbo.v with (noexpand)", 8171);
        sim.ExecuteBatches("alter index ix_v on dbo.v rebuild");
        AreEqual(3, sim.ExecuteScalar("select count(*) from dbo.v with (noexpand)"));
        sim.ExecuteBatches("drop index ix_v on dbo.v");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.indexes where object_id = object_id('dbo.v')"));
        _ = sim.ExecuteNonQuery("insert dbo.b values (1, 1, 1)");
        _ = sim.ExecuteNonQuery("begin tran; create unique clustered index cx on dbo.v (val); rollback");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.indexes where object_id = object_id('dbo.v')"));
    }
}
