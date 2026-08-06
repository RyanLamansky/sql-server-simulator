using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>MERGE</c>'s WHEN-clause family expansion —
/// <c>WHEN MATCHED THEN UPDATE</c> / <c>DELETE</c>, <c>WHEN NOT MATCHED
/// [BY TARGET] THEN INSERT</c>, <c>WHEN NOT MATCHED BY SOURCE THEN
/// UPDATE</c> / <c>DELETE</c>, source subqueries / set-ops / CTEs,
/// AND-conditioned per-WHEN clause selection, the <c>$action</c>
/// pseudo-column in OUTPUT, the multi-match <c>UPDATE</c> guard (Msg
/// 8672), and the action-kind / ordering grammar checks (Msg 10710 /
/// 10711 / 10714 / 5324). The EF SaveChanges single-clause shape is
/// covered separately by <c>OutputClauseTests</c>. All probed against
/// SQL Server 2025 (2026-05-13).
/// <para>
/// The <c>HashMatch_*</c> group re-asserts those same semantics over an
/// unindexed target, where the match phase hashes the source by the ON's
/// equality keys rather than scanning target × source or seeking the target
/// per source row.
/// </para>
/// </summary>
[TestClass]
public sealed class MergeTests
{
    [TestMethod]
    public void Matched_Update_NotMatchedByTarget_Insert_Upsert()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int primary key, v int);
            insert t values (1, 100), (2, 200);
            merge t using (values (1, 11), (3, 33)) as s (id, v) on t.id = s.id
            when matched then update set v = s.v
            when not matched by target then insert (id, v) values (s.id, s.v);
            """);
        using var reader = simulation.ExecuteReader("select id, v from t order by id");
        var rows = new List<(int Id, int V)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        CollectionAssert.AreEqual(new[] { (1, 11), (2, 200), (3, 33) }, rows);
    }

    [TestMethod]
    public void Matched_Delete_RemovesMatchedRows()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int primary key, v int);
            insert t values (1, 100), (2, 200), (3, 300);
            merge t using (values (1), (3)) as s (id) on t.id = s.id
            when matched then delete;
            """);
        using var reader = simulation.ExecuteReader("select id, v from t order by id");
        var rows = new List<(int, int)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        CollectionAssert.AreEqual(new[] { (2, 200) }, rows);
    }

    [TestMethod]
    public void NotMatchedBySource_Delete_RemovesUnmatchedTargets()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int primary key, v int);
            insert t values (1, 100), (2, 200), (3, 300);
            merge t using (values (2)) as s (id) on t.id = s.id
            when not matched by source then delete;
            """);
        using var reader = simulation.ExecuteReader("select id from t order by id");
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 2 }, ids);
    }

    [TestMethod]
    public void NotMatchedBySource_Update_FlagsUnmatchedTargets()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int primary key, status nvarchar(10));
            insert t values (1, 'on'), (2, 'on'), (3, 'on');
            merge t using (values (2)) as s (id) on t.id = s.id
            when not matched by source then update set status = 'off';
            """);
        using var reader = simulation.ExecuteReader("select id, status from t order by id");
        var rows = new List<(int Id, string Status)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetString(1)));
        CollectionAssert.AreEqual(new[] { (1, "off"), (2, "on"), (3, "off") }, rows);
    }

    [TestMethod]
    public void AllFourBranches_TogetherInOneStatement()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int primary key, v int);
            insert t values (1, 100), (2, 200), (3, 300);
            merge t using (values (1, 11), (2, 22), (4, 44)) as s (id, v) on t.id = s.id
            when matched and s.v > 15 then update set v = s.v
            when matched then delete
            when not matched by target then insert (id, v) values (s.id, s.v)
            when not matched by source then update set v = -1;
            """);
        using var reader = simulation.ExecuteReader("select id, v from t order by id");
        var rows = new List<(int, int)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        // 1 matches: AND fails (s.v=11 not > 15), falls through to delete → row 1 deleted
        // 2 matches: AND passes (s.v=22 > 15), update → (2, 22)
        // 3 not matched by source → update v = -1 → (3, -1)
        // 4 not matched by target → insert → (4, 44)
        CollectionAssert.AreEqual(new[] { (2, 22), (3, -1), (4, 44) }, rows);
    }

    [TestMethod]
    public void AndSearchCondition_FirstMatchWins()
    {
        // First WHEN clause with AND filters by predicate; second (unconditional) catches the rest.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int primary key, v int);
            insert t values (1, 100), (2, 200);
            merge t using (values (1, 999), (2, 5)) as s (id, v) on t.id = s.id
            when matched and s.v > 500 then delete
            when matched then update set v = s.v;
            """);
        using var reader = simulation.ExecuteReader("select id, v from t order by id");
        var rows = new List<(int, int)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        // row 1: AND matched (s.v=999 > 500) → DELETE
        // row 2: AND failed, falls to UPDATE → (2, 5)
        CollectionAssert.AreEqual(new[] { (2, 5) }, rows);
    }

    [TestMethod]
    public void DollarAction_OutputProjectsActionVerb()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int primary key, v int);
            insert t values (1, 100), (2, 200);
            """);
        using var reader = simulation.ExecuteReader("""
            merge t using (values (1, 11), (3, 33)) as s (id, v) on t.id = s.id
            when matched then update set v = s.v
            when not matched by target then insert (id, v) values (s.id, s.v)
            when not matched by source then delete
            output $action, isnull(inserted.id, deleted.id);
            """);
        var rows = new List<(string Action, int Id)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt32(1)));
        rows.Sort((a, b) => a.Id.CompareTo(b.Id));
        CollectionAssert.AreEqual(new[] { ("UPDATE", 1), ("DELETE", 2), ("INSERT", 3) }, rows);
    }

    [TestMethod]
    public void DollarAction_TypeIsNVarchar()
    {
        // $action surface is nvarchar — probe-confirmed against SQL Server 2025.
        using var reader = new Simulation().ExecuteReader("""
            create table t (id int);
            merge t using (values (1)) as s (id) on 1 = 0
            when not matched then insert (id) values (s.id)
            output $action as a;
            """);
        Assert.AreEqual("a", reader.GetName(0));
        Assert.AreEqual(typeof(string), reader.GetFieldType(0));
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("INSERT", reader.GetString(0));
    }

    [TestMethod]
    public void MultiMatchUpdate_RaisesMsg8672()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int primary key, v int);
            insert t values (1, 100);
            merge t using (values (1, 10), (1, 20)) as s (id, v) on t.id = s.id
            when matched then update set v = s.v;
            """, 8672);

    [TestMethod]
    public void MultiMatchDelete_DoesNotRaise()
    {
        // Probe-confirmed: a target row matched by multiple source rows raises
        // 8672 only for UPDATE; DELETE silently collapses.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int primary key, v int);
            insert t values (1, 100);
            merge t using (values (1, 10), (1, 20)) as s (id, v) on t.id = s.id
            when matched then delete;
            """);
        using var reader = simulation.ExecuteReader("select count(*) from t");
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(0, reader.GetInt32(0));
    }

    [TestMethod]
    public void InsertInWhenMatched_RaisesMsg10711()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int, v int);
            merge t using (values (1, 10)) as s (id, v) on t.id = s.id
            when matched then insert (id, v) values (s.id, s.v);
            """, 10711);

    [TestMethod]
    public void InsertInWhenNotMatchedBySource_RaisesMsg10711()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int, v int);
            merge t using (values (1, 10)) as s (id, v) on t.id = s.id
            when not matched by source then insert (id, v) values (0, 0);
            """, 10711);

    /// <summary>
    /// A MERGE insert action's value count is measured the way an ordinary
    /// INSERT's is: against a written column list (Msg 110 for a surplus
    /// value, Msg 109 for a missing one), and against the target's own
    /// definition when no list is written (Msg 213). Probed against SQL
    /// Server 2025.
    /// </summary>
    [TestMethod]
    [DataRow("insert (id, v) values (s.id, s.v, 3)", 110)]
    [DataRow("insert (id, v, w) values (s.id, s.v)", 109)]
    [DataRow("insert values (s.id, s.v)", 213)]
    [DataRow("insert values (s.id, s.v, 3, 4)", 213)]
    public void InsertActionWidthMismatch_ReportsTheInsertStatementError(string action, int errorNumber)
        => _ = new Simulation().AssertSqlError($"""
            create table t (id int, v int, w int);
            merge t using (values (1, 10)) as s (id, v) on t.id = s.id
            when not matched then {action};
            """, errorNumber);

    [TestMethod]
    public void UpdateInWhenNotMatchedByTarget_RaisesMsg10710()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int, v int);
            merge t using (values (1, 10)) as s (id, v) on t.id = s.id
            when not matched then update set v = 0;
            """, 10710);

    [TestMethod]
    public void DeleteInWhenNotMatchedByTarget_RaisesMsg10710()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int, v int);
            merge t using (values (1, 10)) as s (id, v) on t.id = s.id
            when not matched then delete;
            """, 10710);

    [TestMethod]
    public void MultipleWhenNotMatchedByTarget_RaisesMsg10714()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int, v int);
            merge t using (values (1, 10)) as s (id, v) on t.id = s.id
            when not matched then insert (id, v) values (s.id, s.v)
            when not matched and s.v > 5 then insert (id, v) values (s.id, s.v);
            """, 10714);

    [TestMethod]
    public void MatchedAndAfterUnconditional_RaisesMsg5324()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int, v int);
            merge t using (values (1, 10)) as s (id, v) on t.id = s.id
            when matched then update set v = 0
            when matched and s.v > 5 then delete;
            """, 5324);

    [TestMethod]
    public void NotMatchedBySourceAndAfterUnconditional_RaisesMsg5324()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int, v int);
            merge t using (values (1, 10)) as s (id, v) on t.id = s.id
            when not matched by source then delete
            when not matched by source and t.id > 0 then update set v = 0;
            """, 5324);

    [TestMethod]
    public void MissingTerminatingSemicolon_RaisesMsg10713()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int, v int)

            merge t using (values (1, 10)) as s (id, v) on 1 = 0
            when not matched then insert (id, v) values (s.id, s.v)
            select 1
            """, 10713);

    [TestMethod]
    public void SourceSubquery_FlowsRowsToWhenClauses()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table src (id int, v int);
            create table t (id int primary key, v int);
            insert src values (1, 99), (2, 22), (3, 5);
            insert t values (1, 100);
            merge t using (select id, v from src where v > 10) as s on t.id = s.id
            when matched then update set v = s.v
            when not matched by target then insert (id, v) values (s.id, s.v);
            """);
        using var reader = simulation.ExecuteReader("select id, v from t order by id");
        var rows = new List<(int, int)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        // src filtered to v>10: (1, 99), (2, 22). 3 doesn't enter.
        // (1, 99) matches existing → UPDATE → (1, 99)
        // (2, 22) doesn't match → INSERT → (2, 22)
        CollectionAssert.AreEqual(new[] { (1, 99), (2, 22) }, rows);
    }

    [TestMethod]
    public void SourceSetOp_UnionAllInUsingClause()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int primary key, v int);
            merge t using (select 1 as id, 10 as v union all select 2, 20) as s on t.id = s.id
            when not matched by target then insert (id, v) values (s.id, s.v);
            """);
        using var reader = simulation.ExecuteReader("select id, v from t order by id");
        var rows = new List<(int, int)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        CollectionAssert.AreEqual(new[] { (1, 10), (2, 20) }, rows);
    }

    [TestMethod]
    public void Triggers_FireOncePerKindInInsertUpdateDeleteOrder()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table tgt (id int primary key, v int);
            create table trace (seq int identity primary key, action varchar(20), id int);
            insert tgt values (1, 100), (2, 200), (3, 300);
            """);
        // Per-action triggers must be in separate batches because
        // CREATE TRIGGER's body extends to the next statement.
        _ = simulation.ExecuteNonQuery("create trigger t_ins on tgt after insert as insert trace(action, id) select 'INS', id from inserted;");
        _ = simulation.ExecuteNonQuery("create trigger t_upd on tgt after update as insert trace(action, id) select 'UPD', id from inserted;");
        _ = simulation.ExecuteNonQuery("create trigger t_del on tgt after delete as insert trace(action, id) select 'DEL', id from deleted;");

        _ = simulation.ExecuteNonQuery("""
            merge tgt using (values (1, 11), (4, 44)) as s (id, v) on tgt.id = s.id
            when matched then update set v = s.v
            when not matched by target then insert (id, v) values (s.id, s.v)
            when not matched by source then delete;
            """);

        using var reader = simulation.ExecuteReader("select seq, action, id from trace order by seq");
        var actions = new List<string>();
        while (reader.Read())
            actions.Add(reader.GetString(1));
        // Probe-confirmed against SQL Server 2025: triggers fire in
        // INSERT → UPDATE → DELETE order, each kind once per MERGE.
        Assert.AreEqual("INS", actions[0]);
        Assert.AreEqual("UPD", actions[1]);
        Assert.AreEqual("DEL", actions[2]);
        Assert.AreEqual("DEL", actions[3]);
    }

    [TestMethod]
    public void AtomicRollback_ConstraintViolationCancelsAllWrites()
    {
        // INSERT failing on PK collision must roll back the other
        // queued inserts as well (single statement atomicity).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int primary key, v int);
            insert t values (5, 500);
            """);
        try
        {
            _ = simulation.ExecuteNonQuery("""
                merge t using (values (1, 10), (5, 50), (3, 30)) as s (id, v) on 1 = 0
                when not matched by target then insert (id, v) values (s.id, s.v);
                """);
            Assert.Fail("expected PK violation");
        }
        catch (System.Data.Common.DbException)
        {
            // expected
        }
        using var reader = simulation.ExecuteReader("select count(*) from t");
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(1, reader.GetInt32(0));
    }

    // --------------- USING bare-table source ---------------
    //
    // Probe-confirmed 2026-05-14: `USING tbl [AS] alias` is grammar-equivalent
    // to a FROM-source heap table — works for regular heap tables, views,
    // temp-tables, table-variables, schema-qualified names. Alias is
    // optional (defaults to the table's leaf name on the source side);
    // optional WITH (...) hints sit alias-then-hint (same placement as
    // FROM); column-rename list trailing the alias parses as a hint
    // clause and surfaces Msg 321 with the first column-name as the
    // would-be hint name.

    [TestMethod]
    public void UsingBareTable_WithAlias_Upserts()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table tgt (id int primary key, v int);
            create table src (id int primary key, v int);
            insert tgt values (1, 10);
            insert src values (1, 100), (2, 200);
            merge into tgt as t
            using src as s on s.id = t.id
            when matched then update set v = s.v
            when not matched by target then insert (id, v) values (s.id, s.v);
            """);
        using var reader = simulation.ExecuteReader("select id, v from tgt order by id");
        var rows = new List<(int, int)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        CollectionAssert.AreEqual(new[] { (1, 100), (2, 200) }, rows);
    }

    [TestMethod]
    public void UsingBareTable_NoAs_BareAlias_Works()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table tgt (id int primary key, v int);
            create table src (id int primary key, v int);
            insert tgt values (1, 10);
            insert src values (1, 999);
            merge into tgt t
            using src s on s.id = t.id
            when matched then update set v = s.v;
            """);
        Assert.AreEqual(999, simulation.ExecuteScalar<int>("select v from tgt where id = 1"));
    }

    [TestMethod]
    public void UsingBareTable_NoAlias_ReferenceByTableName()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table tgt (id int primary key, v int);
            create table src (id int primary key, v int);
            insert tgt values (1, 10);
            insert src values (1, 100), (2, 200);
            merge into tgt
            using src on src.id = tgt.id
            when matched then update set v = src.v
            when not matched by target then insert (id, v) values (src.id, src.v);
            """);
        Assert.AreEqual(2, simulation.ExecuteScalar<int>("select count(*) from tgt"));
        Assert.AreEqual(100, simulation.ExecuteScalar<int>("select v from tgt where id = 1"));
    }

    [TestMethod]
    public void UsingBareTable_SchemaQualified_Works()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create schema au;
            create table tgt (id int primary key, v int);
            create table au.src (id int primary key, v int);
            insert au.src values (5, 500);
            merge into tgt as t
            using au.src as s on s.id = t.id
            when not matched by target then insert (id, v) values (s.id, s.v);
            """);
        Assert.AreEqual(500, simulation.ExecuteScalar<int>("select v from tgt where id = 5"));
    }

    [TestMethod]
    public void UsingBareTable_AliasThenWithHint_AcceptsAsNoop()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table tgt (id int primary key, v int);
            create table src (id int primary key, v int);
            insert tgt values (1, 10);
            insert src values (1, 100);
            merge into tgt as t
            using src as s with (nolock) on s.id = t.id
            when matched then update set v = s.v;
            """);
        Assert.AreEqual(100, simulation.ExecuteScalar<int>("select v from tgt where id = 1"));
    }

    [TestMethod]
    public void UsingBareTable_LegacyParenHint_AcceptsAsNoop()
    {
        // Mirrors FROM-source behavior: the legacy bare-paren `(NOLOCK)`
        // form after alias is also accepted (probe-confirmed indirectly
        // via the column-rename rejection — the parser routes the
        // trailing `(…)` through ParseOptionalTableHints regardless of
        // whether the first inner token is a hint name).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table tgt (id int primary key, v int);
            create table src (id int primary key, v int);
            insert tgt values (1, 10);
            insert src values (1, 100);
            merge into tgt as t
            using src as s (nolock) on s.id = t.id
            when matched then update set v = s.v;
            """);
        Assert.AreEqual(100, simulation.ExecuteScalar<int>("select v from tgt where id = 1"));
    }

    [TestMethod]
    public void UsingBareTable_ColumnRenameList_RaisesMsg321()
        => new Simulation().AssertSqlError("""
            create table tgt (id int primary key, v int);
            create table src (id int primary key, v int);
            insert src values (1, 100);
            merge into tgt as t
            using src as s (x, y) on s.x = t.id
            when matched then update set v = s.y;
            """, 321, "\"x\" is not a recognized table hints option.");

    [TestMethod]
    public void UsingBareTable_TempTable_Works()
    {
        // Temp table source on the same connection.
        Assert.AreEqual(2, new Simulation().ExecuteScalar<int>("""
            create table tgt (id int primary key, v int);
            create table #src (id int, v int);
            insert tgt values (1, 10);
            insert #src values (1, 100), (2, 200);
            merge into tgt as t
            using #src as s on s.id = t.id
            when matched then update set v = s.v
            when not matched by target then insert (id, v) values (s.id, s.v);
            select count(*) from tgt;
            """));
    }

    [TestMethod]
    public void UsingBareTable_TableVariable_Works()
    {
        Assert.AreEqual(100, new Simulation().ExecuteScalar<int>("""
            create table tgt (id int primary key, v int);
            insert tgt values (1, 10);
            declare @tv table (id int, v int);
            insert @tv values (1, 100);
            merge into tgt as t
            using @tv as s on s.id = t.id
            when matched then update set v = s.v;
            select v from tgt where id = 1;
            """));
    }

    [TestMethod]
    public void UsingBareTable_View_Works()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table tgt (id int primary key, v int);
            create table src (id int primary key, v int);
            insert tgt values (1, 10);
            insert src values (1, 100), (2, 200);
            """,
            "create view vsrc as select id, v from src",
            """
            merge into tgt as t
            using vsrc as s on s.id = t.id
            when matched then update set v = s.v
            when not matched by target then insert (id, v) values (s.id, s.v);
            """);
        Assert.AreEqual(2, simulation.ExecuteScalar<int>("select count(*) from tgt"));
        Assert.AreEqual(100, simulation.ExecuteScalar<int>("select v from tgt where id = 1"));
    }

    [TestMethod]
    public void UsingBareTable_MissingTable_RaisesMsg208()
        => new Simulation().AssertSqlError("""
            create table tgt (id int primary key, v int);
            merge into tgt as t
            using nosuch as s on s.id = t.id
            when matched then update set v = 1;
            """, 208);

    [TestMethod]
    public void UsingBareTable_MissingTableVariable_RaisesMsg1087()
        => new Simulation().AssertSqlError("""
            create table tgt (id int primary key, v int);
            merge into tgt as t
            using @nosuch as s on s.id = t.id
            when matched then update set v = 1;
            """, 1087);

    /// <summary>
    /// A subquery inside the ON predicate correlates to the MERGE's own source
    /// and target, which is what the two-sided outer type resolver exists for.
    /// Probe-confirmed against SQL Server 2025 (2026-07-30).
    /// </summary>
    [TestMethod]
    public void OnPredicate_CorrelatedSubquery_BindsMergeColumns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(MergeCorrelationSetup);
        _ = sim.ExecuteNonQuery("""
            merge tgt as t using src as s on t.id = s.id and exists (select 1 from lookup u where u.v = s.val)
            when matched then update set t.x = s.val;
            """);
        Assert.AreEqual(100, sim.ExecuteScalar("select x from tgt where id = 1"));
        Assert.AreEqual(20, sim.ExecuteScalar("select x from tgt where id = 2"));
    }

    /// <inheritdoc cref="OnPredicate_CorrelatedSubquery_BindsMergeColumns"/>
    [TestMethod]
    public void WhenClause_CorrelatedSubquery_BindsMergeColumns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(MergeCorrelationSetup);
        _ = sim.ExecuteNonQuery("""
            merge tgt as t using src as s on t.id = s.id
            when matched and exists (select 1 from lookup u where u.v = s.val) then update set t.x = s.val
            when not matched by target then insert (id, x) values (s.id, s.val);
            """);
        Assert.AreEqual(100, sim.ExecuteScalar("select x from tgt where id = 1"));
        Assert.AreEqual(20, sim.ExecuteScalar("select x from tgt where id = 2"));
        Assert.AreEqual(300, sim.ExecuteScalar("select x from tgt where id = 3"));
    }

    /// <inheritdoc cref="OnPredicate_CorrelatedSubquery_BindsMergeColumns"/>
    [TestMethod]
    public void UpdateSet_ScalarSubquery_BindsMergeColumns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(MergeCorrelationSetup);
        _ = sim.ExecuteNonQuery("""
            merge tgt as t using src as s on t.id = s.id
            when matched then update set t.x = (select max(u.v) from lookup u where u.v >= s.val);
            """);
        Assert.AreEqual(100, sim.ExecuteScalar("select x from tgt where id = 1"));
        Assert.AreEqual(DBNull.Value, sim.ExecuteScalar("select x from tgt where id = 2"));
    }

    /// <summary>
    /// A subquery whose own projection is an outer MERGE column forces the
    /// column's <i>static</i> type to resolve through the two-sided outer
    /// resolver, where the predicate cases above only need its runtime value.
    /// Probe-confirmed against SQL Server 2025 (2026-07-30) — note real
    /// compiles a batch against the tables that existed before it, so the
    /// setup has to run as its own batch to reproduce.
    /// </summary>
    [TestMethod]
    public void Subquery_ProjectingOuterColumn_ResolvesStaticType()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(MergeCorrelationSetup);
        _ = sim.ExecuteNonQuery("""
            merge tgt as t using src as s on t.id = s.id
            when matched then update set t.x = (select top 1 s.val from lookup u);
            """);
        Assert.AreEqual(100, sim.ExecuteScalar("select x from tgt where id = 1"));
        Assert.AreEqual(200, sim.ExecuteScalar("select x from tgt where id = 2"));
    }

    /// <inheritdoc cref="Subquery_ProjectingOuterColumn_ResolvesStaticType"/>
    [TestMethod]
    public void Subquery_ProjectingOuterTargetColumn_ResolvesStaticType()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(MergeCorrelationSetup);
        _ = sim.ExecuteNonQuery("""
            merge tgt as t using src as s on t.id = (select top 1 s.id from lookup u)
            when matched then update set t.x = (select top 1 val from lookup u);
            """);
        Assert.AreEqual(100, sim.ExecuteScalar("select x from tgt where id = 1"));
        Assert.AreEqual(200, sim.ExecuteScalar("select x from tgt where id = 2"));
    }

    /// <summary>
    /// An unindexed target can't be seeked per source row, so the match phase
    /// hashes the source by the ON's equality keys and probes per target row
    /// instead. Every semantic the target × source scan carried has to survive
    /// that: which rows match, in which source order, and what the NOT MATCHED
    /// branches see. Each test here uses a heap target (no PRIMARY KEY, no
    /// index) so the seek path can't take the statement instead.
    /// </summary>
    [TestMethod]
    public void HashMatch_UnindexedTarget_Upsert()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, v int);
            insert t values (1, 100), (2, 200);
            merge t using (values (1, 11), (3, 33)) as s (id, v) on t.id = s.id
            when matched then update set v = s.v
            when not matched by target then insert (id, v) values (s.id, s.v);
            """);
        AreEqual(3, sim.ExecuteScalar("select count(*) from t"));
        AreEqual(11, sim.ExecuteScalar("select v from t where id = 1"));
        AreEqual(200, sim.ExecuteScalar("select v from t where id = 2"));
        AreEqual(33, sim.ExecuteScalar("select v from t where id = 3"));
    }

    /// <inheritdoc cref="HashMatch_UnindexedTarget_Upsert"/>
    [TestMethod]
    public void HashMatch_MultiMatchUpdate_RaisesMsg8672()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int, v int);
            insert t values (1, 100);
            merge t using (values (1, 10), (1, 20)) as s (id, v) on t.id = s.id
            when matched then update set v = s.v;
            """, 8672);

    /// <summary>
    /// Msg 8672 fires while the match phase runs, before any mutation is queued
    /// — so the target is untouched and no trigger fired, on the hashed path as
    /// on the scan.
    /// </summary>
    [TestMethod]
    public void HashMatch_MultiMatchUpdate_LeavesNoSideEffects()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int, v int); create table audit (v int); insert t values (1, 100)",
            "create trigger tr on t after update as insert audit select v from inserted");
        _ = sim.AssertSqlError("""
            merge t using (values (1, 10), (1, 20)) as s (id, v) on t.id = s.id
            when matched then update set v = s.v;
            """, 8672);
        AreEqual(100, sim.ExecuteScalar("select v from t where id = 1"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from audit"));
    }

    /// <inheritdoc cref="HashMatch_UnindexedTarget_Upsert"/>
    [TestMethod]
    public void HashMatch_MultiMatchDelete_DoesNotRaise()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, v int);
            insert t values (1, 100), (2, 200);
            merge t using (values (1, 10), (1, 20)) as s (id, v) on t.id = s.id
            when matched then delete;
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from t"));
        AreEqual(200, sim.ExecuteScalar("select v from t where id = 2"));
    }

    /// <summary>
    /// Several source rows matching one target row are collected in ascending
    /// source order, and the first of them is the one the WHEN clause reads —
    /// the bucket chain has to walk in build order for that to hold. (Real
    /// leaves the pick unspecified for a multi-matched DELETE; the simulator
    /// commits to first-source-wins, and the fast path must not change it.)
    /// </summary>
    [TestMethod]
    public void HashMatch_MultipleMatches_FirstSourceRowDrivesTheClause()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, v int);
            insert t values (1, 100);
            merge t using (values (1, 10), (1, 1)) as s (id, v) on t.id = s.id
            when matched and s.v > 5 then delete;
            """);
        AreEqual(0, sim.ExecuteScalar("select count(*) from t"));
    }

    /// <summary>
    /// A NULL key equi-matches nothing (<c>NULL = NULL</c> is UNKNOWN), on
    /// either side: the NULL-keyed target row falls to WHEN NOT MATCHED BY
    /// SOURCE and the NULL-keyed source row to WHEN NOT MATCHED BY TARGET.
    /// </summary>
    [TestMethod]
    public void HashMatch_NullJoinKeys_NeverMatch()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, v int);
            insert t values (1, 100), (null, 99);
            merge t using (values (1, 11), (null, 22)) as s (id, v) on t.id = s.id
            when matched then update set v = s.v
            when not matched by target then insert (id, v) values (s.id, s.v)
            when not matched by source then update set v = -1;
            """);
        AreEqual(3, sim.ExecuteScalar("select count(*) from t"));
        AreEqual(11, sim.ExecuteScalar("select v from t where id = 1"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from t where id is null and v = -1"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from t where id is null and v = 22"));
    }

    /// <summary>
    /// The ON's non-equality conjuncts stay a residual filter re-checked per
    /// probed pair, so a candidate the hash produced but the residual rejects is
    /// no match at all — the target row falls to WHEN NOT MATCHED BY SOURCE.
    /// </summary>
    [TestMethod]
    public void HashMatch_ResidualConjunct_FiltersProbedCandidates()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (k int, flag int, v int);
            insert t values (1, 5, 100), (2, 5, 200);
            merge t using (values (1, 1, 11), (2, 9, 22)) as s (k, flag, v) on t.k = s.k and t.flag > s.flag
            when matched then update set v = s.v
            when not matched by source then update set v = -1;
            """);
        AreEqual(11, sim.ExecuteScalar("select v from t where k = 1"));
        AreEqual(-1, sim.ExecuteScalar("select v from t where k = 2"));
    }

    /// <inheritdoc cref="HashMatch_UnindexedTarget_Upsert"/>
    [TestMethod]
    public void HashMatch_CompositeKey_MatchesOnBothColumns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (a int, b int, v int);
            insert t values (1, 1, 10), (1, 2, 20);
            merge t using (values (1, 2, 99)) as s (a, b, v) on t.a = s.a and t.b = s.b
            when matched then update set v = s.v;
            """);
        AreEqual(10, sim.ExecuteScalar("select v from t where a = 1 and b = 1"));
        AreEqual(99, sim.ExecuteScalar("select v from t where a = 1 and b = 2"));
    }

    /// <summary>
    /// Key values are coerced to the promotion type the <c>=</c> operator itself
    /// would reach before hashing, so a bigint target column and an int source
    /// column land in the same bucket.
    /// </summary>
    [TestMethod]
    public void HashMatch_MixedNumericKeyTypes_Match()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id bigint, v int);
            insert t values (1, 100), (2, 200);
            merge t using (values (2, 22)) as s (id, v) on t.id = s.id
            when matched then update set v = s.v;
            """);
        AreEqual(100, sim.ExecuteScalar("select v from t where id = 1"));
        AreEqual(22, sim.ExecuteScalar("select v from t where id = 2"));
    }

    /// <summary>
    /// String keys hash under the column's own collation, so a case-insensitive
    /// collation matches the pair its <c>=</c> would — and a <c>char</c> key's
    /// trailing-space padding is folded the same way equality folds it.
    /// </summary>
    [TestMethod]
    public void HashMatch_StringKeys_FollowCollationAndPadding()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (k varchar(10), c char(5), v int);
            insert t values ('abc', 'ab', 1), ('def', 'de', 2);
            merge t using (values ('ABC', 'ab   ', 11)) as s (k, c, v) on t.k = s.k and t.c = s.c
            when matched then update set v = s.v;
            """);
        AreEqual(11, sim.ExecuteScalar("select v from t where k = 'abc'"));
        AreEqual(2, sim.ExecuteScalar("select v from t where k = 'def'"));
    }

    /// <summary>
    /// An unqualified ON operand reads the target first and the source only when
    /// the target has no such column — the same rule the runtime resolver
    /// applies, so the two sides of a hashed key never swap.
    /// </summary>
    [TestMethod]
    public void HashMatch_UnqualifiedOnOperands_ResolveTargetFirst()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, v int);
            insert t values (1, 100);
            merge t using (values (1, 11), (2, 22)) as s (sid, sv) on id = sid
            when matched then update set v = sv
            when not matched by target then insert (id, v) values (sid, sv);
            """);
        AreEqual(11, sim.ExecuteScalar("select v from t where id = 1"));
        AreEqual(22, sim.ExecuteScalar("select v from t where id = 2"));
    }

    /// <summary>
    /// An ON with no <c>target = source</c> conjunct keeps the target × source
    /// scan, which runs the whole predicate per pair.
    /// </summary>
    [TestMethod]
    public void NonEquiOn_KeepsTheScanAndMatchesEveryPair()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, v int);
            insert t values (1, 1), (2, 2), (3, 3);
            merge t using (values (2)) as s (id) on t.id < s.id
            when matched then update set v = 0
            when not matched by source then update set v = -1;
            """);
        AreEqual(0, sim.ExecuteScalar("select v from t where id = 1"));
        AreEqual(-1, sim.ExecuteScalar("select v from t where id = 2"));
        AreEqual(-1, sim.ExecuteScalar("select v from t where id = 3"));
    }

    /// <summary>
    /// An equality between two columns of the <i>same</i> side isn't a key —
    /// it's an ordinary filter, and stays one.
    /// </summary>
    [TestMethod]
    public void OnEqualityWithinOneSide_StaysAFilter()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, v int);
            insert t values (1, 1), (2, 9);
            merge t using (values (1, 11), (2, 22)) as s (id, v) on t.id = s.id and t.id = t.v
            when matched then update set v = s.v
            when not matched by source then update set v = -1;
            """);
        AreEqual(11, sim.ExecuteScalar("select v from t where id = 1"));
        AreEqual(-1, sim.ExecuteScalar("select v from t where id = 2"));
    }

    /// <summary>
    /// The source materializes once whatever the match strategy, so an empty
    /// source leaves every target row to WHEN NOT MATCHED BY SOURCE and an empty
    /// target leaves every source row to WHEN NOT MATCHED BY TARGET.
    /// </summary>
    [TestMethod]
    public void HashMatch_EmptySides_TakeTheNotMatchedBranches()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, v int);
            create table empty_src (id int, v int);
            insert t values (1, 1), (2, 2);
            merge t using (select id, v from empty_src) as s on t.id = s.id
            when matched then update set v = 0
            when not matched by source then update set v = -1;
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from t where v = -1"));

        _ = sim.ExecuteNonQuery("""
            create table t2 (id int, v int);
            merge t2 using (values (1, 11), (2, 22)) as s (id, v) on t2.id = s.id
            when matched then update set v = 0
            when not matched by target then insert (id, v) values (s.id, s.v);
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from t2"));
        AreEqual(22, sim.ExecuteScalar("select v from t2 where id = 2"));
    }

    private const string MergeCorrelationSetup = """
        create table tgt (id int primary key, x int);
        create table src (id int, val int);
        create table lookup (v int);
        insert tgt values (1, 10), (2, 20);
        insert src values (1, 100), (2, 200), (3, 300);
        insert lookup values (100);
        """;
}
