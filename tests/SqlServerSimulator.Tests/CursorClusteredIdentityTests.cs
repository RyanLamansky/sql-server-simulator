using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// What a cursor keys and orders each base table's rows on, and how
/// FAST_FORWARD reads them — probed 2026-09-29 against SQL Server 2025. A
/// table with a clustered index keys on that index's key and a cursor walks it
/// in key order; a non-unique one completes the key with a uniquifier, which an
/// UPDATE assigning any key column redraws even when the value stands still.
/// A heap keys on its PRIMARY KEY, else a UNIQUE constraint, else a unique
/// index, and a cursor walks it in write order.
/// </summary>
[TestClass]
public sealed class CursorClusteredIdentityTests
{
    private const string NonUnique = "create table t (a int not null, b int); create clustered index ix on t (a); insert t values (1, 10), (2, 20), (3, 30);";

    /// <summary>
    /// Runs <paramref name="body"/> against a cursor <c>c</c> over
    /// <c>select a, b from t</c> declared with <paramref name="options"/>,
    /// where <c>{next}</c> / <c>{prior}</c> / <c>{first}</c> / <c>{same}</c>
    /// fetch in that direction into <c>@a, @b</c> and append
    /// <c>status:a,b;</c> to the trace this returns.
    /// </summary>
    private static string Trace(string seed, string options, string body, string query = "select a, b from t")
    {
        static string Fetch(string direction) =>
            $"fetch {direction} from c into @a, @b; set @s += concat(@@fetch_status, ':', @a, ',', @b, ';');";
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(seed);
        return (string)simulation.ExecuteScalar($"""
            declare @a int, @b int, @s varchar(max) = '';
            declare c cursor {options} for {query};
            open c;
            {body.Replace("{next}", Fetch("next")).Replace("{prior}", Fetch("prior")).Replace("{first}", Fetch("first")).Replace("{same}", Fetch("relative 0"))}
            close c; deallocate c;
            select @s
            """)!;
    }

    // ---- KEYSET identity ----

    /// <summary>
    /// Any UPDATE assigning a column of a non-unique clustered key unmakes the
    /// member, whatever it assigns and whichever statement does it; a column
    /// outside the key doesn't, and a rolled-back move leaves the member whole.
    /// A deleted member re-inserted under the same key is a new row.
    /// </summary>
    [TestMethod]
    [DataRow("update t set a = 5 where a = 2", "0:1,10;-2:0,;0:3,30;")]
    [DataRow("update t set a = a where a = 2", "0:1,10;-2:0,;0:3,30;")]
    [DataRow("update t set a = a, b = b where b = 20", "0:1,10;-2:0,;0:3,30;")]
    [DataRow("update t set b = 99 where a = 2", "0:1,10;0:2,99;0:3,30;")]
    [DataRow("begin tran; update t set a = 5 where a = 2; rollback", "0:1,10;0:2,20;0:3,30;")]
    [DataRow("begin tran; update t set a = a where a = 2; rollback", "0:1,10;0:2,20;0:3,30;")]
    [DataRow("delete t where a = 2; insert t values (2, 22)", "0:1,10;-2:0,;0:3,30;")]
    [DataRow("merge t using (select 2 k) s on t.a = s.k when matched then update set a = t.a;", "0:1,10;-2:0,;0:3,30;")]
    public void NonUniqueClusteredKey_AssignmentUnmakesTheMember(string change, string expected)
        => AreEqual(expected, Trace(NonUnique, "keyset", "{next} " + change + "; {next} {next}"));

    /// <summary>Through a view, and through an ON UPDATE CASCADE landing on
    /// the child's clustered key, the assignment unmakes the member too.</summary>
    [TestMethod]
    public void NonUniqueClusteredKey_ViewAndCascadeAssignmentsUnmakeTheMember()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table p (id int primary key); insert p values (1), (2);" +
            "create table t (a int not null references p (id) on update cascade, b int); create clustered index ix on t (a); insert t values (1, 10), (2, 20);",
            "create view v as select a, b from t");
        AreEqual("0:1,10;-2:0,;-2:0,;", simulation.ExecuteScalar("""
            declare @a int, @b int, @s varchar(max) = '';
            declare c cursor keyset for select a, b from t;
            open c;
            fetch next from c into @a, @b; set @s += concat(@@fetch_status, ':', @a, ',', @b, ';');
            update v set a = a where a = 1;
            fetch first from c into @a, @b; set @s += concat(@@fetch_status, ':', @a, ',', @b, ';');
            update p set id = 5 where id = 2;
            fetch next from c into @a, @b; set @s += concat(@@fetch_status, ':', @a, ',', @b, ';');
            close c; deallocate c;
            select @s
            """));
    }

    /// <summary>Assigning one column of a composite non-unique clustered key
    /// is enough.</summary>
    [TestMethod]
    public void NonUniqueCompositeClusteredKey_OneColumnAssignedUnmakesTheMember()
        => AreEqual("0:1,1;-2:0,0;", Trace(
            "create table t (a int not null, b int not null, v int); create clustered index ix on t (a, b); insert t values (1, 1, 10), (1, 2, 20);",
            "keyset",
            "{next} update t set b = b where v = 20; {next}",
            "select a, b from t"));

    /// <summary>A positioned UPDATE moving the key leaves the cursor on a
    /// member it can no longer find.</summary>
    [TestMethod]
    public void NonUniqueClusteredKey_PositionedKeyUpdateLeavesAHole()
        => AreEqual("0:1,10;-2:0,;0:2,20;", Trace(NonUnique, "keyset", "{next} update t set a = 50 where current of c; {same} {next}"));

    /// <summary>
    /// A unique key is the whole identity: assigning it its own value keeps the
    /// member and a new value unmakes it. A nonclustered PRIMARY KEY beside a
    /// non-unique clustered index is not what the cursor keys on, and among a
    /// heap's unique keys the one created first is.
    /// </summary>
    [TestMethod]
    [DataRow("create table t (a int not null, b int, u int); create unique clustered index ix on t (a);", "update t set a = a where a = 2", "0:1,10;0:2,20;")]
    [DataRow("create table t (a int not null, b int, u int); create unique clustered index ix on t (a);", "update t set a = 7 where a = 2", "0:1,10;-2:0,;")]
    [DataRow("create table t (a int not null primary key, b int, u int);", "update t set a = a where a = 2", "0:1,10;0:2,20;")]
    [DataRow("create table t (a int not null, b int, u int not null primary key nonclustered); create clustered index ix on t (a);", "update t set u = 9 where a = 2", "0:1,10;0:2,20;")]
    [DataRow("create table t (a int not null, b int, u int); create unique index ux on t (a);", "update t set a = 5 where a = 2", "0:1,10;-2:0,;")]
    [DataRow("create table t (a int not null, b int, u int not null unique); create unique index ux on t (a);", "update t set a = 5 where a = 2", "0:1,10;0:5,20;")]
    [DataRow("create table t (a int not null, b int, u int not null); create unique index ux on t (a); alter table t add constraint uq unique (u);", "update t set a = 5 where a = 2", "0:1,10;-2:0,;")]
    public void UniqueKeys_KeyTheMemberByValue(string create, string change, string expected)
        => AreEqual(expected, Trace(create + "insert t values (1, 10, 1), (2, 20, 2);", "keyset", "{next} " + change + "; {next}"));

    // ---- order ----

    /// <summary>
    /// A cursor walks a clustered table in key order — NULL first under an
    /// ascending key, reversed under a descending one — and a heap in write
    /// order, for KEYSET and DYNAMIC alike.
    /// </summary>
    [TestMethod]
    [DataRow("create table t (a int not null primary key, b int); insert t values (3, 30), (1, 10), (2, 20);", "keyset", "0:1,10;0:2,20;0:3,30;")]
    [DataRow("create table t (a int not null primary key, b int); insert t values (3, 30), (1, 10), (2, 20);", "dynamic", "0:1,10;0:2,20;0:3,30;")]
    [DataRow("create table t (a int not null, b int); create clustered index ix on t (a desc); insert t values (1, 10), (3, 30), (2, 20);", "keyset", "0:3,30;0:2,20;0:1,10;")]
    [DataRow("create table t (a int null, b int); create clustered index ix on t (a); insert t values (3, 30), (null, 0), (1, 10);", "dynamic", "0:,0;0:1,10;0:3,30;")]
    [DataRow("create table t (a int not null primary key nonclustered, b int); insert t values (3, 30), (1, 10), (2, 20);", "keyset", "0:3,30;0:1,10;0:2,20;")]
    public void RowsWalkInClusteredKeyOrder(string seed, string options, string expected)
        => AreEqual(expected, Trace(seed, options, "{next} {next} {next}"));

    /// <summary>An ORDER BY's ties fall to the clustered key.</summary>
    [TestMethod]
    public void OrderByTiesFallToTheClusteredKey()
        => AreEqual("0:1,10;0:3,10;0:2,20;", Trace(
            "create table t (a int not null, b int); create clustered index ix on t (a); insert t values (3, 10), (1, 10), (2, 20);",
            "keyset",
            "{next} {next} {next}",
            "select a, b from t order by b"));

    /// <summary>
    /// A DYNAMIC cursor meets a row whose key an UPDATE moved ahead of it
    /// again, and a row moved behind it doesn't come back.
    /// </summary>
    [TestMethod]
    [DataRow("{next} update t set a = 5 where a = 2; {next} {next} {next}", "0:1,10;0:3,30;0:5,20;-1:5,20;")]
    [DataRow("{next} update t set a = 25 where current of c; {next} {next} {next}", "0:1,10;0:2,20;0:3,30;0:25,10;")]
    [DataRow("{next} {next} update t set a = 0 where a = 3; {next} {prior} {prior} {prior}", "0:1,10;0:2,20;-1:2,20;0:2,20;0:1,10;0:0,30;")]
    public void Dynamic_FollowsTheLiveKeyOrder(string body, string expected)
        => AreEqual(expected, Trace(NonUnique, "dynamic", body));

    /// <summary>
    /// Duplicates of a non-unique clustered key walk in the order their
    /// uniquifiers were drawn: insertion order, with a row an UPDATE moved onto
    /// the key after the ones already there.
    /// </summary>
    [TestMethod]
    [DataRow("insert t values (1, 10), (2, 20), (2, 21), (3, 30);", "{next} {next} insert t values (2, 5); {next} {next} {next}", "0:1,10;0:2,20;0:2,21;0:2,5;0:3,30;")]
    [DataRow("insert t values (1, 10), (3, 30), (3, 31), (4, 40);", "{next} update t set a = 3 where a = 1; {next} {next} {next} {next}", "0:1,10;0:3,30;0:3,31;0:3,10;0:4,40;")]
    public void Dynamic_DuplicateKeysWalkInUniquifierOrder(string rows, string body, string expected)
        => AreEqual(expected, Trace("create table t (a int not null, b int); create clustered index ix on t (a); " + rows, "dynamic", body));

    /// <summary>A join walks each source in its own key order, nested.</summary>
    [TestMethod]
    public void JoinWalksEachSourceInKeyOrder()
        => AreEqual("0:1,10;0:1,20;0:2,30;0:3,10;", Trace(
            "create table t (a int not null primary key, b int); insert t values (3, 1), (1, 1), (2, 2);" +
            "create table u (x int not null primary key, y int); insert u values (20, 1), (10, 1), (30, 2);",
            "dynamic",
            "{next} {next} {next} {next}",
            "select a, x from t join u on u.y = t.b"));

    // ---- FAST_FORWARD ----

    /// <summary>
    /// FAST_FORWARD reads live rows as the fetches go — an insert ahead
    /// appears, an update shows, a delete ahead vanishes, a row whose key moved
    /// ahead comes round again — and forward-only read-only cursors naming no
    /// sensitivity are FAST_FORWARD ones.
    /// </summary>
    [TestMethod]
    [DataRow("fast_forward")]
    [DataRow("forward_only read_only")]
    [DataRow("read_only")]
    public void FastForward_ReadsLiveRows(string options)
        => AreEqual("0:1,10;0:2,99;0:4,40;0:10,10;-1:10,10;", Trace(
            "create table t (a int not null primary key, b int); insert t values (1, 10), (2, 20), (3, 30);",
            options,
            "{next} insert t values (4, 40); update t set b = 99 where a = 2; delete t where a = 3; update t set a = 10 where a = 1; {next} {next} {next} {next}"));

    /// <summary>A plan that has to sort or limit rows settles them at OPEN,
    /// values included.</summary>
    [TestMethod]
    [DataRow("select a, b from t order by b", "0:3,10;0:2,20;")]
    [DataRow("select top 2 a, b from t order by a", "0:1,30;0:2,20;")]
    public void FastForward_SortOrLimitSettlesAtOpen(string query, string expected)
        => AreEqual(expected, Trace(
            "create table t (a int not null primary key, b int); insert t values (1, 30), (2, 20), (3, 10);",
            "fast_forward",
            "update t set b = 99 where a = 2; {next} {next}",
            query));

    /// <summary><c>@@CURSOR_ROWS</c> reads -1 and <c>CURSOR_STATUS</c> 1 for
    /// an open FAST_FORWARD cursor, a settled or empty one included.</summary>
    [TestMethod]
    [DataRow("select a from t")]
    [DataRow("select a from t order by b")]
    [DataRow("select a from t where a > 9")]
    [DataRow("select value from string_split('x,y', ',')")]
    public void FastForward_ReportsNoRowCount(string query)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int not null primary key, b int); insert t values (1, 30), (2, 20);");
        AreEqual("-1,1", simulation.ExecuteScalar($"declare c cursor fast_forward for {query}; open c; select concat(@@cursor_rows, ',', cursor_status('global', 'c'))"));
    }

    /// <summary>ABSOLUTE on FAST_FORWARD is the forward-only refusal, not the
    /// dynamic one.</summary>
    [TestMethod]
    public void FastForward_AbsoluteIsForwardOnlyRefusal()
        => _ = new Simulation().AssertSqlError(
            "create table t (a int primary key); declare c cursor fast_forward for select a from t; open c; fetch absolute 1 from c",
            16911);

    /// <summary>A positioned write through a read-only cursor ends its
    /// statement with Msg 3621 after the Msg 16929.</summary>
    [TestMethod]
    [DataRow("fast_forward", "update t set b = 1 where current of c")]
    [DataRow("static", "delete t where current of c")]
    [DataRow("dynamic read_only", "update t set b = 1 where current of c")]
    public void ReadOnlyCursor_PositionedWriteIsTerminated(string options, string write)
    {
        var ex = new Simulation().AssertSqlError(
            $"create table t (a int primary key, b int); insert t values (1, 1); declare c cursor {options} for select a, b from t; open c; fetch next from c; {write}",
            16929);
        AreEqual(3621, ex.Errors[^1].Number);
    }
}
