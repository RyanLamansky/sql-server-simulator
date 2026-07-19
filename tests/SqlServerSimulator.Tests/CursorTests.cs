using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// T-SQL cursors: DECLARE … CURSOR / OPEN / FETCH / CLOSE / DEALLOCATE,
/// the STATIC / KEYSET / DYNAMIC sensitivity model, scroll fetches,
/// <c>@@FETCH_STATUS</c> / <c>@@CURSOR_ROWS</c> / <c>CURSOR_STATUS</c>, and
/// positioned <c>WHERE CURRENT OF</c> DML. Behavior probed against SQL Server
/// 2025. A cursor is session-scoped, so each lifecycle runs as one batch
/// string on a single connection.
/// </summary>
[TestClass]
public sealed class CursorTests
{
    private const string Seed =
        "create table t (id int primary key, name varchar(20)); " +
        "insert t values (1,'a'),(2,'b'),(3,'c');";

    [TestMethod]
    public void ForwardLoop_AccumulatesAllRowsInOrder()
        => AreEqual("1:a;2:b;3:c;", new Simulation().ExecuteScalar(Seed + """
            declare @id int, @name varchar(20), @log varchar(200) = '';
            declare c cursor for select id, name from t order by id;
            open c;
            fetch next from c into @id, @name;
            while @@fetch_status = 0
            begin
              set @log = @log + convert(varchar, @id) + ':' + @name + ';';
              fetch next from c into @id, @name;
            end
            close c; deallocate c;
            select @log
            """));

    [TestMethod]
    public void FetchPastEnd_FetchStatusIsMinusOne()
        => AreEqual(-1, ExecuteScalar<int>(Seed + """
            declare @id int, @name varchar(20);
            declare c cursor for select id, name from t;
            open c;
            while @@fetch_status = 0 fetch next from c into @id, @name;
            select @@fetch_status
            """));

    [TestMethod]
    public void FetchPastEnd_VariablesRetainLastFetchedValue()
        => AreEqual(3, ExecuteScalar<int>(Seed + """
            declare @id int, @name varchar(20);
            declare c cursor for select id, name from t order by id;
            open c;
            while @@fetch_status = 0 fetch next from c into @id, @name;
            select @id
            """));

    [TestMethod]
    public void CursorRows_Static_ReturnsRowCount()
        => AreEqual(3, ExecuteScalar<int>(Seed + """
            declare c cursor static for select id from t;
            open c;
            select @@cursor_rows
            """));

    [TestMethod]
    public void CursorRows_Dynamic_ReturnsMinusOne()
        => AreEqual(-1, ExecuteScalar<int>(Seed + """
            declare c cursor dynamic for select id from t;
            open c;
            select @@cursor_rows
            """));

    [TestMethod]
    public void OpenUndeclaredCursor_RaisesMsg16916()
        => new Simulation().AssertSqlError("open nope", 16916,
            "A cursor with the name 'nope' does not exist.");

    [TestMethod]
    public void DuplicateDeclare_RaisesMsg16915()
        => new Simulation().AssertSqlError(Seed + """
            declare dup cursor for select id from t;
            declare dup cursor for select id from t;
            """, 16915, "A cursor with the name 'dup' already exists.");

    [TestMethod]
    public void OpenAlreadyOpen_RaisesMsg16905()
        => new Simulation().AssertSqlError(Seed + """
            declare c cursor for select id from t;
            open c; open c;
            """, 16905, "The cursor is already open.");

    [TestMethod]
    public void CloseNotOpen_RaisesMsg16917()
        => new Simulation().AssertSqlError(Seed + """
            declare c cursor for select id from t;
            close c;
            """, 16917, "Cursor is not open.");

    [TestMethod]
    public void FetchNotOpen_RaisesMsg16917()
        => new Simulation().AssertSqlError(Seed + """
            declare @v int;
            declare c cursor for select id from t;
            fetch next from c into @v;
            """, 16917, "Cursor is not open.");

    [TestMethod]
    public void FetchIntoColumnCountMismatch_RaisesMsg16924()
        => new Simulation().AssertSqlError(Seed + """
            declare @only int;
            declare c cursor for select id, name from t;
            open c;
            fetch next from c into @only;
            """, 16924, "Cursorfetch: The number of variables declared in the INTO list must match that of selected columns.");

    [TestMethod]
    public void DeallocateUndeclared_RaisesMsg16916()
        => new Simulation().AssertSqlError("deallocate nope", 16916,
            "A cursor with the name 'nope' does not exist.");

    [TestMethod]
    public void ScrollFetch_AllDirections()
        => AreEqual("3|1|2|1|3", ExecuteScalar(Seed + """
            declare @s int, @log varchar(50) = '';
            declare c cursor scroll for select id from t order by id;
            open c;
            fetch last from c into @s;        set @log = convert(varchar,@s);
            fetch first from c into @s;       set @log = @log + '|' + convert(varchar,@s);
            fetch absolute 2 from c into @s;  set @log = @log + '|' + convert(varchar,@s);
            fetch prior from c into @s;       set @log = @log + '|' + convert(varchar,@s);
            fetch relative 2 from c into @s;  set @log = @log + '|' + convert(varchar,@s);
            close c; deallocate c;
            select @log
            """));

    [TestMethod]
    public void ScrollFetchOnForwardOnlyCursor_RaisesMsg16925()
        => new Simulation().AssertSqlError(Seed + """
            declare @v int;
            declare c cursor forward_only for select id from t;
            open c;
            fetch absolute 2 from c into @v;
            """, 16925, "The fetch type Absolute cannot be used with dynamic cursors.");

    [TestMethod]
    public void StaticCursor_ImmuneToColumnChangeMidLoop()
        => AreEqual("a;b;c;", new Simulation().ExecuteScalar(Seed + """
            declare @id int, @name varchar(20), @log varchar(200) = '';
            declare c cursor static for select id, name from t order by id;
            open c;
            fetch next from c into @id, @name;
            update t set name = 'CHANGED' where id = 2;
            while @@fetch_status = 0
            begin
              set @log = @log + @name + ';';
              fetch next from c into @id, @name;
            end
            select @log
            """));

    [TestMethod]
    public void DynamicCursor_SeesColumnChangeMidLoop()
        => AreEqual("a;CHANGED;c;", new Simulation().ExecuteScalar(Seed + """
            declare @id int, @name varchar(20), @log varchar(200) = '';
            declare c cursor dynamic for select id, name from t order by id;
            open c;
            fetch next from c into @id, @name;
            update t set name = 'CHANGED' where id = 2;
            while @@fetch_status = 0
            begin
              set @log = @log + @name + ';';
              fetch next from c into @id, @name;
            end
            select @log
            """));

    [TestMethod]
    public void DynamicCursor_SkipsRowDeletedAhead()
        => AreEqual("1;3;", new Simulation().ExecuteScalar(Seed + """
            declare @id int, @name varchar(20), @log varchar(200) = '';
            declare c cursor dynamic for select id, name from t order by id;
            open c;
            fetch next from c into @id, @name;
            delete t where id = 2;
            while @@fetch_status = 0
            begin
              set @log = @log + convert(varchar,@id) + ';';
              fetch next from c into @id, @name;
            end
            select @log
            """));

    [TestMethod]
    public void DynamicCursor_SeesRowInsertedAhead()
        => AreEqual("1;2;3;9;", new Simulation().ExecuteScalar(Seed + """
            declare @id int, @name varchar(20), @log varchar(200) = '';
            declare c cursor dynamic for select id, name from t order by id;
            open c;
            fetch next from c into @id, @name;
            insert t values (9, 'i');
            while @@fetch_status = 0
            begin
              set @log = @log + convert(varchar,@id) + ';';
              fetch next from c into @id, @name;
            end
            select @log
            """));

    [TestMethod]
    public void KeysetCursor_SeesColumnChangeButNotInsert()
        => AreEqual("a;K2;c;", new Simulation().ExecuteScalar(Seed + """
            declare @id int, @name varchar(20), @log varchar(200) = '';
            declare c cursor keyset for select id, name from t order by id;
            open c;
            fetch next from c into @id, @name;
            update t set name = 'K2' where id = 2;
            insert t values (9, 'i');
            while @@fetch_status = 0
            begin
              set @log = @log + @name + ';';
              fetch next from c into @id, @name;
            end
            select @log
            """));

    [TestMethod]
    public void KeysetCursor_DeletedMember_FetchStatusMinusTwo()
        => AreEqual(-2, ExecuteScalar<int>(Seed + """
            declare @id int, @name varchar(20);
            declare c cursor keyset for select id, name from t order by id;
            open c;
            fetch next from c into @id, @name;   -- id 1
            delete t where id = 2;
            fetch next from c into @id, @name;   -- keyset member 2, now deleted
            select @@fetch_status
            """));

    [TestMethod]
    public void CursorStatus_OpenReturnsOne()
        => AreEqual((short)1, ExecuteScalar<short>(Seed + """
            declare c cursor for select id from t;
            open c;
            select cursor_status('global', 'c')
            """));

    [TestMethod]
    public void CursorStatus_NonexistentReturnsMinusThree()
        => AreEqual((short)-3, ExecuteScalar<short>("select cursor_status('local', 'ghost')"));

    [TestMethod]
    public void WhereCurrentOf_UpdatesPositionedRow()
        => AreEqual("POS", new Simulation().ExecuteScalar(Seed + """
            declare @id int;
            declare c cursor for select id from t order by id;
            open c;
            fetch next from c into @id;        -- on id 1
            fetch next from c into @id;        -- on id 2
            update t set name = 'POS' where current of c;
            close c; deallocate c;
            select name from t where id = 2
            """));

    [TestMethod]
    public void WhereCurrentOf_DeletesPositionedRow()
        => AreEqual(0, ExecuteScalar<int>(Seed + """
            declare @id int;
            declare c cursor for select id from t order by id;
            open c;
            fetch next from c into @id;        -- on id 1
            delete from t where current of c;
            close c; deallocate c;
            select count(*) from t where id = 1
            """));

    [TestMethod]
    public void WhereCurrentOf_ReadOnlyStaticCursor_RaisesMsg16929()
        => new Simulation().AssertSqlError(Seed + """
            declare @id int;
            declare c cursor static for select id from t order by id;
            open c;
            fetch next from c into @id;
            update t set name = 'x' where current of c;
            """, 16929, "The cursor is READ ONLY.");

    [TestMethod]
    public void WhereCurrentOf_BeforeAnyFetch_RaisesMsg16931()
        => new Simulation().AssertSqlError(Seed + """
            declare c cursor for select id from t order by id;
            open c;
            update t set name = 'x' where current of c;
            """, 16931, "There are no rows in the current fetch buffer.");

    [TestMethod]
    public void DeallocateWhileOpen_IsAllowed()
        => AreEqual((short)-3, ExecuteScalar<short>(Seed + """
            declare c cursor for select id from t;
            open c;
            deallocate c;
            select cursor_status('local', 'c')
            """));

    [TestMethod]
    public void NonUpdatableQuery_ForcedToStaticEvenWhenDynamicRequested()
        => AreEqual(1, ExecuteScalar<int>(Seed + """
            declare c cursor dynamic for select count(*) from t;
            open c;
            select @@cursor_rows
            """));

    /// <summary>
    /// Cursor variables are modeled — see CursorBreadthTests for the full
    /// lifecycle. A bare DECLARE registers an unallocated slot.
    /// </summary>
    [TestMethod]
    public void CursorVariable_DeclareRegistersUnallocatedSlot()
        => AreEqual((short)-2, ExecuteScalar<short>("declare @c cursor; select cursor_status('variable','@c')"));

    [TestMethod]
    public void JoinCursor_ForwardLoopOverMultipleSources()
        => AreEqual("x-p;x-r;y-q;", new Simulation().ExecuteScalar("""
            create table a (id int primary key, name varchar(20));
            create table b (id int primary key, a_id int, tag varchar(20));
            insert a values (1,'x'),(2,'y');
            insert b values (10,1,'p'),(11,2,'q'),(12,1,'r');
            declare @name varchar(20), @tag varchar(20), @log varchar(200) = '';
            declare c cursor for
              select a.name, b.tag from a join b on b.a_id = a.id order by a.name, b.tag;
            open c;
            fetch next from c into @name, @tag;
            while @@fetch_status = 0
            begin
              set @log = @log + @name + '-' + @tag + ';';
              fetch next from c into @name, @tag;
            end
            close c; deallocate c;
            select @log
            """));

    [TestMethod]
    public void MultiSourceCursor_ForcedStatic_KnownDivergence()
    {
        // KNOWN DIVERGENCE (probed against SQL Server 2025): a cursor whose
        // source isn't a direct single base table — JOIN, derived table, or
        // view — is DYNAMIC on the real server (@@CURSOR_ROWS = -1, sees
        // mid-loop changes, WHERE CURRENT OF updates the named base table).
        // The simulator forces it to a read-only STATIC snapshot: the rowset
        // is correct, but @@CURSOR_ROWS reports the materialized count and a
        // base change after OPEN isn't reflected. See docs/claude/cursors.md.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table a (id int primary key, name varchar(20));
            create table b (id int primary key, a_id int, tag varchar(20));
            insert a values (1,'x'),(2,'y');
            insert b values (10,1,'p'),(11,2,'q');
            """);
        AreEqual(2, sim.ExecuteScalar<int>("""
            declare c cursor for select a.name, b.tag from a join b on b.a_id = a.id;
            open c;
            select @@cursor_rows
            """));
        // Snapshot taken at OPEN ignores a mid-loop UPDATE to a joined column.
        AreEqual("p;q;", new Simulation().ExecuteScalar("""
            create table a (id int primary key, name varchar(20));
            create table b (id int primary key, a_id int, tag varchar(20));
            insert a values (1,'x'),(2,'y');
            insert b values (10,1,'p'),(11,2,'q');
            declare @name varchar(20), @tag varchar(20), @log varchar(200) = '';
            declare c cursor for select a.name, b.tag from a join b on b.a_id = a.id order by b.id;
            open c;
            fetch next from c into @name, @tag;
            update b set tag = 'CHANGED';
            while @@fetch_status = 0
            begin
              set @log = @log + @tag + ';';
              fetch next from c into @name, @tag;
            end
            select @log
            """));
    }

    [TestMethod]
    public void ReopenAfterClose_RestartsFromFirstRow()
        => AreEqual(1, ExecuteScalar<int>(Seed + """
            declare @id int;
            declare c cursor for select id from t order by id;
            open c; fetch next from c into @id; fetch next from c into @id;  -- on id 2
            close c;
            open c; fetch next from c into @id;                              -- back to id 1
            select @id
            """));

    [TestMethod]
    public void NestedCursors_InnerLoopsPerOuterRow()
        => AreEqual("1/10;1/12;2/11;", new Simulation().ExecuteScalar("""
            create table a (id int primary key);
            create table b (id int primary key, a_id int);
            insert a values (1),(2);
            insert b values (10,1),(11,2),(12,1);
            declare @ai int, @bi int, @log varchar(200) = '';
            declare outer_c cursor for select id from a order by id;
            open outer_c; fetch next from outer_c into @ai;
            while @@fetch_status = 0
            begin
              declare inner_c cursor for select id from b where a_id = @ai order by id;
              open inner_c; fetch next from inner_c into @bi;
              while @@fetch_status = 0
              begin
                set @log = @log + convert(varchar,@ai) + '/' + convert(varchar,@bi) + ';';
                fetch next from inner_c into @bi;
              end
              close inner_c; deallocate inner_c;
              fetch next from outer_c into @ai;
            end
            close outer_c; deallocate outer_c;
            select @log
            """));

    [TestMethod]
    public void CursorOverTempTable_ForwardLoop()
        => AreEqual("a;b;", new Simulation().ExecuteScalar("""
            create table #tmp (id int primary key, v varchar(10));
            insert #tmp values (1,'a'),(2,'b');
            declare @id int, @v varchar(10), @log varchar(100) = '';
            declare c cursor for select id, v from #tmp order by id;
            open c; fetch next from c into @id, @v;
            while @@fetch_status = 0
            begin
              set @log = @log + @v + ';';
              fetch next from c into @id, @v;
            end
            close c; deallocate c;
            select @log
            """));

    [TestMethod]
    public void CursorOverUnion_ForcedStaticWithCount()
        => AreEqual(4, ExecuteScalar<int>(Seed + """
            declare c cursor for select id from t union select 99;
            open c;
            select @@cursor_rows
            """));

    [TestMethod]
    public void KeysetCursor_KeyColumnChanged_FetchStatusMinusTwo()
        => AreEqual(-2, ExecuteScalar<int>(Seed + """
            declare @id int, @name varchar(20);
            declare c cursor keyset for select id, name from t order by id;
            open c;
            fetch next from c into @id, @name;     -- id 1
            update t set id = 99 where id = 2;     -- key of not-yet-fetched member changes
            fetch next from c into @id, @name;     -- keyset member 2 no longer found
            select @@fetch_status
            """));

    [TestMethod]
    public void DerivedTableCursor_ForwardLoop()
        => AreEqual("2;4;6;", new Simulation().ExecuteScalar(Seed + """
            declare @d int, @log varchar(200) = '';
            declare c cursor for
              select doubled from (select id * 2 as doubled from t) s order by doubled;
            open c;
            fetch next from c into @d;
            while @@fetch_status = 0
            begin
              set @log = @log + convert(varchar, @d) + ';';
              fetch next from c into @d;
            end
            select @log
            """));

    [TestMethod]
    public void FetchAbsoluteZero_PositionsBeforeFirst()
        => AreEqual(-1, ExecuteScalar<int>(Seed + """
            declare @id int;
            declare c cursor scroll for select id from t order by id;
            open c;
            fetch absolute 0 from c into @id;
            select @@fetch_status
            """));

    [TestMethod]
    public void FetchAbsoluteNegative_CountsFromEnd()
        => AreEqual(3, ExecuteScalar<int>(Seed + """
            declare @id int;
            declare c cursor scroll for select id from t order by id;
            open c;
            fetch absolute -1 from c into @id;
            select @id
            """));

    [TestMethod]
    public void FetchAbsolute_VariableOffset()
        => AreEqual(2, ExecuteScalar<int>(Seed + """
            declare @id int, @n int = 2;
            declare c cursor scroll for select id from t order by id;
            open c;
            fetch absolute @n from c into @id;
            select @id
            """));

    [TestMethod]
    public void OrderByDesc_LoopDescends()
        => AreEqual("3;2;1;", new Simulation().ExecuteScalar(Seed + """
            declare @id int, @log varchar(100) = '';
            declare c cursor for select id from t order by id desc;
            open c; fetch next from c into @id;
            while @@fetch_status = 0
            begin
              set @log = @log + convert(varchar,@id) + ';';
              fetch next from c into @id;
            end
            select @log
            """));

    [TestMethod]
    public void CursorInProcedureBody_LoopsOverRows()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int primary key, name varchar(20)); insert t values (1,'a'),(2,'b'),(3,'c')",
            """
            create procedure cproc as
            begin
              declare @id int, @log varchar(100) = '';
              declare c cursor for select id from t order by id;
              open c; fetch next from c into @id;
              while @@fetch_status = 0
              begin
                set @log = @log + convert(varchar,@id) + ';';
                fetch next from c into @id;
              end
              close c; deallocate c;
              select @log;
            end
            """);
        AreEqual("1;2;3;", sim.ExecuteScalar("exec cproc"));
    }

    [TestMethod]
    public void CursorOverView_ForwardLoopReturnsCorrectRows()
    {
        // A view source isn't a direct single base table, so the cursor is
        // forced to STATIC (see MultiSourceCursor_ForcedStatic_KnownDivergence) —
        // but the rowset is still correct. The view excludes id 2.
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int primary key, name varchar(20)); insert t values (1,'a'),(2,'b'),(3,'c')",
            "create view v as select id, name from t where id <> 2");
        AreEqual("a;c;", sim.ExecuteScalar("""
            declare @id int, @name varchar(20), @log varchar(100) = '';
            declare c cursor for select id, name from v order by id;
            open c; fetch next from c into @id, @name;
            while @@fetch_status = 0
            begin
              set @log = @log + @name + ';';
              fetch next from c into @id, @name;
            end
            close c; deallocate c;
            select @log
            """));
    }

    private const string NoKeySeed =
        "create table h (id int not null, name varchar(20) not null); " +
        "insert h values (1,'a'),(2,'b'),(3,'c');";

    [TestMethod]
    public void KeysetCursor_NoUniqueKeyHeap_SeesUpdatedValues()
        => AreEqual("a;NEW;c;", new Simulation().ExecuteScalar(NoKeySeed + """
            declare @id int, @name varchar(20), @log varchar(200) = '';
            declare c cursor keyset for select id, name from h order by id;
            open c; fetch next from c into @id, @name;
            update h set name = 'NEW' where id = 2;        -- non-key UPDATE on no-key heap
            while @@fetch_status = 0
            begin
              set @log = @log + @name + ';';
              fetch next from c into @id, @name;
            end
            close c; deallocate c;
            select @log
            """));

    [TestMethod]
    public void WhereCurrentOf_NoUniqueKeyHeap_UpdatesPositionedRow()
        => AreEqual("POS", new Simulation().ExecuteScalar(NoKeySeed + """
            declare @id int;
            declare c cursor for select id from h order by id;
            open c;
            fetch next from c into @id;        -- id 1
            fetch next from c into @id;        -- id 2 (positioned)
            update h set name = 'POS' where current of c;
            close c; deallocate c;
            select name from h where id = 2
            """));

    [TestMethod]
    public void WhereCurrentOf_NoUniqueKeyHeap_DeletesPositionedRow()
        => AreEqual(2, ExecuteScalar<int>(NoKeySeed + """
            declare @id int;
            declare c cursor for select id from h order by id;
            open c;
            fetch next from c into @id;
            fetch next from c into @id;
            delete h where current of c;
            close c; deallocate c;
            select count(*) from h
            """));

    /// <summary>
    /// Force the UPDATE to forward (new name longer than old) — the cursor
    /// must still find the row by its stable address on re-fetch.
    /// </summary>
    [TestMethod]
    public void ForwardedUpdate_PreservesRowAddressForCursorRefetch()
        => AreEqual("AAA", new Simulation().ExecuteScalar(NoKeySeed + """
            declare @id int, @name varchar(20);
            declare c cursor keyset for select id, name from h order by id;
            open c;
            fetch next from c into @id, @name;        -- id 1
            update h set name = replicate('A', 20) where id = 1;   -- grows the row → forward
            update h set name = 'AAA' where id = 1;               -- second update on the same (now-forwarded) row
            fetch first from c into @id, @name;
            select @name
            """));

    [TestMethod]
    public void FetchWithoutInto_YieldsSingleRowResultSet()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int primary key, name varchar(20)); insert t values (1,'a'),(2,'b')");
        using var conn = sim.CreateOpenConnection();
        _ = conn.CreateCommand("declare c cursor for select id, name from t order by id; open c").ExecuteNonQuery();
        using var reader = conn.CreateCommand("fetch next from c").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual("a", reader.GetString(1));
        IsFalse(reader.Read());
    }
}
