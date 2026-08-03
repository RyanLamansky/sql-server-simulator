using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// A T-SQL KEYSET cursor keys on a row locator every participating base table
/// has to carry: a PRIMARY KEY / UNIQUE constraint, an unfiltered unique index,
/// or a clustered index. A table with none converts the cursor to a read-only
/// snapshot, whichever route reached KEYSET — probed against SQL Server 2025
/// via <c>sys.dm_exec_cursors(@@SPID).properties</c>, which reports
/// <c>Keyset | Optimistic</c> for a qualifying table and
/// <c>Snapshot | Read Only</c> otherwise, and behaviorally via the Msg 16929 a
/// positioned <c>WHERE CURRENT OF</c> then raises.
/// </summary>
[TestClass]
public sealed class CursorKeysetIdentityTests
{
    /// <summary>
    /// The cursor's effective sensitivity, read off the two observations that
    /// separate the three: <c>@@CURSOR_ROWS</c> is <c>-1</c> only for DYNAMIC,
    /// and a positioned UPDATE reaches the row for KEYSET while STATIC refuses
    /// it with Msg 16929.
    /// </summary>
    private static string Sensitivity(string seed, string declaration)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(seed);
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand($"{declaration} open c;").ExecuteNonQuery();
        if ((int)connection.CreateCommand("select @@cursor_rows").ExecuteScalar()! == -1)
            return "DYNAMIC";

        try
        {
            _ = connection.CreateCommand("""
                declare @id int, @v int;
                fetch next from c into @id, @v;
                update t set v = 99 where current of c;
                """).ExecuteNonQuery();
            return "KEYSET";
        }
        catch (DbException ex) when (ex.Data["HelpLink.EvtID"] is "16929")
        {
            return "STATIC";
        }
    }

    private const string Rows = " insert t (id, v) values (1, 10), (2, 20);";

    // ---- which tables carry a keyset row locator ----

    /// <summary>
    /// Probe-confirmed matrix. Mere existence anywhere on the table is the
    /// test — the key's columns need not be projected, nor referenced at all.
    /// A <em>disabled</em> unique index still qualifies (real reads the
    /// metadata, not the index's usability), and a clustered index qualifies
    /// whether or not it is unique, since a non-unique one carries a
    /// uniquifier. A filtered unique index covers only part of the table and
    /// does not, and neither IDENTITY nor <c>rowversion</c> stands in for one.
    /// </summary>
    [TestMethod]
    [DataRow("create table t (id int primary key, v int);", "KEYSET")]
    [DataRow("create table t (id int primary key nonclustered, v int);", "KEYSET")]
    [DataRow("create table t (id int unique, v int);", "KEYSET")]
    [DataRow("create table t (id int, v int); create unique index ux on t (id);", "KEYSET")]
    [DataRow("create table t (id int, v int); create unique clustered index ux on t (id);", "KEYSET")]
    [DataRow("create table t (id int, v int); create clustered index ix on t (id);", "KEYSET")]
    [DataRow("create table t (id int, v int);", "STATIC")]
    [DataRow("create table t (id int, v int); create index ix on t (id);", "STATIC")]
    [DataRow("create table t (id int, v int, rv rowversion);", "STATIC")]
    public void KeysetOverTable_ConvertsWithoutARowLocator(string seed, string expected)
        => AreEqual(expected, Sensitivity(seed + Rows, "declare c cursor keyset for select id, v from t;"));

    /// <summary>An IDENTITY column is unique in practice but carries no index,
    /// so it is no row locator and the cursor converts.</summary>
    [TestMethod]
    public void KeysetOverIdentityWithoutAKey_Converts()
        => AreEqual("STATIC", Sensitivity(
            "create table t (id int identity, v int); insert t (v) values (10), (20);",
            "declare c cursor keyset for select id, v from t;"));

    /// <summary>A disabled unique index keeps the cursor KEYSET — real reads
    /// the index's presence in metadata rather than its usability.</summary>
    [TestMethod]
    public void KeysetOverDisabledUniqueIndex_StaysKeyset()
        => AreEqual("KEYSET", Sensitivity(
            "create table t (id int, v int); create unique index ux on t (id);" + Rows + " alter index ux on t disable;",
            "declare c cursor keyset for select id, v from t;"));

    /// <summary>A filtered unique index covers only part of the table, so it
    /// is no row locator and the cursor converts.</summary>
    [TestMethod]
    public void KeysetOverFilteredUniqueIndexOnly_Converts()
        => AreEqual("STATIC", Sensitivity(
            "create table t (id int, v int); create unique index ux on t (id) where v > 5;" + Rows,
            "declare c cursor keyset for select id, v from t;"));

    /// <summary>The locator need not be projected — a cursor selecting only
    /// the unkeyed column still keys on the table's unique index.</summary>
    [TestMethod]
    public void KeysetOverUnprojectedUniqueColumn_StaysKeyset()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, v int); create unique index ux on t (id);" + Rows);
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("declare c cursor keyset for select v from t; open c;").ExecuteNonQuery();
        _ = connection.CreateCommand("""
            declare @v int;
            fetch next from c into @v;
            update t set v = 99 where current of c;
            """).ExecuteNonQuery();
        AreEqual(99, connection.CreateCommand("select v from t where id = 1").ExecuteScalar());
    }

    /// <summary>
    /// Documented divergence. A table whose only locator is a clustered index
    /// carries no PK / UNIQUE, so <c>CursorUniqueKeyOrdinals</c> finds nothing
    /// and the KEYSET rides addresses alone: moving the clustered key leaves
    /// the member matchable and the re-fetch reports status <c>0</c> with the
    /// new values. Real keys such a keyset on the clustered key plus its
    /// uniquifier, which the simulator has no equivalent of, and reports
    /// <c>@@FETCH_STATUS = -2</c> with the INTO variables NULLed
    /// (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void AddressIdentityKeyset_ClusteredKeyChange_StaysMatchable()
        => AreEqual("0|5", new Simulation().ExecuteScalar("""
            create table t (id int not null, v int not null);
            create clustered index ix_t on t (id);
            insert t values (1, 10), (2, 20);
            declare @id int, @v int;
            declare c cursor keyset scroll for select id, v from t order by id;
            open c; fetch next from c into @id, @v;
            update t set id = 5 where id = 1;
            fetch first from c into @id, @v;
            select cast(@@fetch_status as varchar(4)) + '|' + isnull(cast(@id as varchar(9)), 'null')
            """));

    // ---- every route to KEYSET takes the gate ----

    private const string KeylessSeed = "create table t (id int, v int);" + Rows;

    /// <summary>
    /// KEYSET is reached explicitly, through <c>SCROLL</c> (which implies it),
    /// and through the two caps a shape imposes on DYNAMIC — a row limit and an
    /// ORDER BY no index delivers. All four convert over a keyless table, while
    /// DYNAMIC (explicit or the forward-only default) keeps its live walk,
    /// which needs no locator.
    /// </summary>
    [TestMethod]
    [DataRow("declare c cursor keyset for select id, v from t;", "STATIC")]
    [DataRow("declare c cursor scroll for select id, v from t;", "STATIC")]
    [DataRow("declare c cursor for select top 2 id, v from t;", "STATIC")]
    [DataRow("declare c cursor for select id, v from t order by v;", "STATIC")]
    [DataRow("declare c cursor dynamic for select id, v from t order by v;", "STATIC")]
    [DataRow("declare c cursor for select id, v from t;", "DYNAMIC")]
    [DataRow("declare c cursor dynamic for select id, v from t;", "DYNAMIC")]
    public void RouteToKeyset_OverKeylessTable_Converts(string declaration, string expected)
        => AreEqual(expected, Sensitivity(KeylessSeed, declaration));

    /// <summary>A cursor variable's <c>SET @c = CURSOR KEYSET …</c> form runs
    /// the same resolution, so it converts too.</summary>
    [TestMethod]
    public void CursorVariableKeyset_OverKeylessTable_Converts()
        => AssertSqlError(KeylessSeed + """
            declare @id int, @v int;
            declare @c cursor;
            set @c = cursor keyset for select id, v from t;
            open @c; fetch next from @c into @id, @v;
            update t set v = 99 where current of @c;
            select 1
            """, 16929, "The cursor is READ ONLY.");

    /// <summary>A keyless <c>#temp</c> table converts like a permanent
    /// one.</summary>
    [TestMethod]
    public void KeysetOverKeylessTempTable_Converts()
        => AssertSqlError("""
            create table #t (id int, v int);
            insert #t values (1, 10), (2, 20);
            declare @id int, @v int;
            declare c cursor keyset for select id, v from #t;
            open c; fetch next from c into @id, @v;
            update #t set v = 99 where current of c;
            select 1
            """, 16929, "The cursor is READ ONLY.");

    /// <summary>The gate follows a deferred body down to its base tables, so a
    /// view over a keyless table converts.</summary>
    [TestMethod]
    public void KeysetThroughView_OverKeylessTable_Converts()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(KeylessSeed, "create view vt as select id, v from t;");
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("declare c cursor keyset for select id, v from vt; open c;").ExecuteNonQuery();
        var ex = Throws<DbException>(() => connection.CreateCommand("""
            declare @id int, @v int;
            fetch next from c into @id, @v;
            update vt set v = 99 where current of c;
            """).ExecuteNonQuery());
        AreEqual("16929", ex.Data["HelpLink.EvtID"]);
    }

    /// <summary>Every participating table must qualify: a join with one keyless
    /// side converts, and the same join keyed on both stays KEYSET
    /// (probe-confirmed).</summary>
    [TestMethod]
    [DataRow("create table u (id int, w int);", "STATIC")]
    [DataRow("create table u (id int primary key, w int);", "KEYSET")]
    public void KeysetOverJoin_ConvertsWhenEitherSideIsKeyless(string otherSeed, string expected)
        => AreEqual(expected, Sensitivity(
            "create table t (id int primary key, v int);" + Rows + otherSeed + " insert u values (1, 100), (2, 200);",
            "declare c cursor keyset for select t.id, t.v from t join u on t.id = u.id;"));

    // ---- TYPE_WARNING ----

    /// <summary>
    /// The conversion is a downgrade, so <c>TYPE_WARNING</c> reports Msg 16956
    /// for it — for an explicit KEYSET, for the KEYSET a plain <c>SCROLL</c>
    /// implies, and for a DYNAMIC the ORDER BY cap already pushed to KEYSET
    /// (all three probe-confirmed). A qualifying table warns about nothing.
    /// </summary>
    [TestMethod]
    [DataRow("create table t (id int, v int);", "declare c cursor keyset type_warning for select id, v from t;", 1)]
    [DataRow("create table t (id int, v int);", "declare c cursor scroll type_warning for select id, v from t;", 1)]
    [DataRow("create table t (id int, v int);", "declare c cursor dynamic type_warning for select id, v from t order by v;", 1)]
    [DataRow("create table t (id int primary key, v int);", "declare c cursor keyset type_warning for select id, v from t;", 0)]
    public void TypeWarning_ReportsTheKeylessDowngrade(string seed, string declaration, int expected)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(seed + Rows);
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        var messages = new List<SimulatedError>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors);
        _ = connection.CreateCommand(declaration).ExecuteNonQuery();
        HasCount(expected, messages);
        if (expected > 0)
        {
            AreEqual(16956, messages[0].Number);
            AreEqual("The created cursor is not of the requested type.", messages[0].Message);
        }
    }
}
