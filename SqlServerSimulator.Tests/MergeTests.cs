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
}
