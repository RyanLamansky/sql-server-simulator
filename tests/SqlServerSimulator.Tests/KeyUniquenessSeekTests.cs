using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// PRIMARY KEY / UNIQUE enforcement on multi-page tables. Enforcement answers
/// "does a live row already carry this key?" by seeking the shared per-heap
/// cache, falling back to a full scan only for a NULL key component; every other
/// key-constraint suite works on tables of a few rows, which never span pages.
/// These pin the same Msg 2627 / 2601 wording and the same NULLs-collide and
/// collation rules once a table does, and pin that the incrementally-maintained
/// cache stays truthful across the mutations that move rows underneath it
/// (DELETE, ROLLBACK, mass key shift, DROP COLUMN).
/// </summary>
[TestClass]
public sealed class KeyUniquenessSeekTests
{
    /// <summary>
    /// A <c>char(500)</c> row packs roughly fifteen to an 8 KB page, so 200 rows
    /// spans a dozen or so — enough that the seek's bucket index and its journal
    /// replay carry real content rather than a single page's worth. No test here
    /// depends on the exact count.
    /// </summary>
    private const int SeededRows = 200;

    private static Simulation Seeded(string createTable, string insertColumns = "(id, pad)", string valueExpression = "value, 'x'")
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"""
            {createTable};
            insert t {insertColumns} select {valueExpression} from generate_series(1, {SeededRows})
            """);
        return simulation;
    }

    private static string ErrorNumber(DbException exception) => (string)exception.Data["HelpLink.EvtID"]!;

    private static int RowCount(Simulation simulation)
    {
        using var reader = simulation.ExecuteReader("select id from t");
        var count = 0;
        while (reader.Read())
            count++;
        return count;
    }

    [TestMethod]
    public void PrimaryKey_Duplicate_RaisesMsg2627()
    {
        var simulation = Seeded("create table t (id int constraint pk_t primary key, pad char(500))");
        simulation.AssertSqlError("insert t values (137, 'x')", 2627,
            "Violation of PRIMARY KEY constraint 'pk_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (137).");
    }

    [TestMethod]
    public void PrimaryKey_FreshKey_Inserts()
    {
        var simulation = Seeded("create table t (id int constraint pk_t primary key, pad char(500))");
        _ = simulation.ExecuteNonQuery($"insert t values ({SeededRows + 1}, 'x')");
        AreEqual(SeededRows + 1, RowCount(simulation));
    }

    [TestMethod]
    public void PrimaryKey_Composite_DuplicateFullKeyRaises_PartialKeyInserts()
    {
        var simulation = Seeded(
            "create table t (id int not null, tag nvarchar(20) not null, pad char(500), constraint pk_t primary key (id, tag))",
            "(id, tag, pad)",
            "value, 'a', 'x'");

        // Sharing only the leading component is not a collision.
        _ = simulation.ExecuteNonQuery("insert t values (137, 'b', 'x')");

        simulation.AssertSqlError("insert t values (137, 'a', 'x')", 2627,
            "Violation of PRIMARY KEY constraint 'pk_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (137, a).");
    }

    [TestMethod]
    public void Unique_SecondNull_Raises()
    {
        // A NULL key component can't be answered from the seek's NULL-free
        // buckets, so this is the fall-back-to-scan path — the one condition
        // that still declines a seek. SQL Server's NULLs-collide rule has to
        // survive it on a table this size.
        var simulation = Seeded("create table t (id int constraint uq_t unique, pad char(500))");
        _ = simulation.ExecuteNonQuery("insert t values (null, 'x')");
        simulation.AssertSqlError("insert t values (null, 'x')", 2627,
            "Violation of UNIQUE KEY constraint 'uq_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (<NULL>).");
    }

    [TestMethod]
    public void Unique_CaseInsensitiveCollation_DuplicateRaises()
    {
        // The seek buckets by hash and verifies by equality, so a case-
        // insensitive duplicate only collides if the two agree — the reason
        // SqlValue.GetHashCode folds case and trailing spaces the way
        // SqlValue.Equals compares them.
        var simulation = Seeded(
            "create table t (id nvarchar(20) constraint uq_t unique, pad char(500))",
            "(id, pad)",
            "concat('key', value), 'x'");
        simulation.AssertSqlError("insert t values ('KEY137', 'x')", 2627,
            "Violation of UNIQUE KEY constraint 'uq_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (KEY137).");
    }

    [TestMethod]
    public void Unique_TrailingSpaces_DuplicateRaises()
    {
        // ANSI trailing-space padding likewise has to survive the hash.
        var simulation = Seeded(
            "create table t (id nvarchar(20) constraint uq_t unique, pad char(500))",
            "(id, pad)",
            "concat('key', value), 'x'");
        simulation.AssertSqlError("insert t values ('key137   ', 'x')", 2627,
            "Violation of UNIQUE KEY constraint 'uq_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (key137   ).");
    }

    [TestMethod]
    public void UniqueIndex_Duplicate_RaisesMsg2601()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"""
            create table t (id int not null primary key, a int not null, pad char(500));
            create unique index ix_a on t(a);
            insert t (id, a, pad) select value, value, 'x' from generate_series(1, {SeededRows})
            """);
        var exception = Throws<DbException>(() => simulation.ExecuteNonQuery($"insert t values ({SeededRows + 1}, 137, 'x')"));
        AreEqual("2601", ErrorNumber(exception));
    }

    [TestMethod]
    public void UniqueIndex_Filtered_HonorsFilterOnBothSides()
    {
        // The seek narrows by key; the filter is then evaluated on each
        // candidate's own row, so a colliding key excluded by the filter is not
        // a violation while one inside it is.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"""
            create table t (id int not null primary key, a int not null, pad char(500));
            create unique index ix_a on t(a) where a > 0;
            insert t (id, a, pad) select value, -value, 'x' from generate_series(1, {SeededRows});
            insert t (id, a, pad) select value + 1000, value, 'x' from generate_series(1, {SeededRows})
            """);

        // -137 collides with an existing row, but both sides sit outside the filter.
        _ = simulation.ExecuteNonQuery("insert t values (5001, -137, 'x')");

        var exception = Throws<DbException>(() => simulation.ExecuteNonQuery("insert t values (5002, 137, 'x')"));
        AreEqual("2601", ErrorNumber(exception));
    }

    [TestMethod]
    public void PrimaryKey_ReinsertAfterDelete_Succeeds()
    {
        // The cache maintains itself from the heap's mutation journal; a delete
        // has to retire the key, not leave a phantom that rejects the re-insert.
        var simulation = Seeded("create table t (id int constraint pk_t primary key, pad char(500))");
        _ = simulation.ExecuteNonQuery("delete t where id = 137");
        _ = simulation.ExecuteNonQuery("insert t values (137, 'x')");
        AreEqual(SeededRows, RowCount(simulation));
    }

    [TestMethod]
    public void PrimaryKey_ReinsertAfterRollback_Succeeds()
    {
        var simulation = Seeded("create table t (id int constraint pk_t primary key, pad char(500))");
        _ = simulation.ExecuteNonQuery("""
            begin transaction;
            insert t values (9001, 'x');
            rollback transaction
            """);
        _ = simulation.ExecuteNonQuery("insert t values (9001, 'x')");
        AreEqual(SeededRows + 1, RowCount(simulation));
    }

    [TestMethod]
    public void PrimaryKey_DuplicateAfterRolledBackDelete_Raises()
    {
        var simulation = Seeded("create table t (id int constraint pk_t primary key, pad char(500))");
        _ = simulation.ExecuteNonQuery("""
            begin transaction;
            delete t where id = 137;
            rollback transaction
            """);
        simulation.AssertSqlError("insert t values (137, 'x')", 2627,
            "Violation of PRIMARY KEY constraint 'pk_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (137).");
    }

    [TestMethod]
    public void Update_IntoExistingKey_RaisesMsg2627()
    {
        var simulation = Seeded("create table t (id int constraint pk_t primary key, pad char(500))");
        simulation.AssertSqlError("update t set id = 42 where id = 137", 2627,
            "Violation of PRIMARY KEY constraint 'pk_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (42).");
    }

    [TestMethod]
    public void Update_NonKeyColumn_DoesNotCollideWithItself()
    {
        // The affected rows are excluded from their own comparison; without that
        // every row would read as a duplicate of its pre-update self.
        var simulation = Seeded("create table t (id int constraint pk_t primary key, pad char(500))");
        AreEqual(SeededRows, simulation.ExecuteNonQuery("update t set pad = 'y'"));
    }

    [TestMethod]
    public void Update_MassKeyShift_Succeeds()
    {
        // Every key moves at once, so each new key overlaps some other affected
        // row's pre-update key — a true violation only if it collides with a row
        // the statement isn't touching.
        var simulation = Seeded("create table t (id int constraint pk_t primary key, pad char(500))");
        AreEqual(SeededRows, simulation.ExecuteNonQuery("update t set id = id + 1"));
        AreEqual(SeededRows + 1, simulation.ExecuteScalar("select max(id) from t"));
    }

    [TestMethod]
    public void Update_MassKeyShift_IntoUntouchedRow_Raises()
    {
        var simulation = Seeded("create table t (id int constraint pk_t primary key, pad char(500))");
        // Rows 1..100 shift up by 100, landing on the untouched 101..200.
        simulation.AssertSqlError("update t set id = id + 100 where id <= 100", 2627,
            "Violation of PRIMARY KEY constraint 'pk_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (101).");
    }

    [TestMethod]
    public void Update_MovingKeyOntoAStandingAffectedRow_Raises()
    {
        // Row 1 is affected but its key doesn't move, so it skips its own check;
        // row 5 moves onto it. A standing row still has to be a comparison
        // target, or the skip would swallow this collision.
        var simulation = Seeded("create table t (id int constraint pk_t primary key, pad char(500))");
        simulation.AssertSqlError("update t set id = case when id = 5 then 1 else id end where id in (1, 5)", 2627,
            "Violation of PRIMARY KEY constraint 'pk_t'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (1).");
    }

    [TestMethod]
    public void Update_MovingIntoFilteredSetWithStandingKey_Raises()
    {
        // The key stands still while the row moves into the filtered index's
        // set, where it collides — which is why the standing-key skip is only
        // taken for an unfiltered index.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"""
            create table t (id int not null primary key, a int not null, flag int not null, pad char(500));
            create unique index ix_a on t(a) where flag = 1;
            insert t (id, a, flag, pad) select value, value, 1, 'x' from generate_series(1, {SeededRows});
            insert t values (9001, 7, 0, 'x')
            """);
        var exception = Throws<DbException>(() => simulation.ExecuteNonQuery("update t set flag = 1 where id = 9001"));
        AreEqual("2601", ErrorNumber(exception));
    }

    [TestMethod]
    public void Merge_UpdateIntoExistingKey_Raises()
    {
        var simulation = Seeded("create table t (id int constraint pk_t primary key, pad char(500))");
        var exception = Throws<DbException>(() => simulation.ExecuteNonQuery("""
            merge t as target
            using (values (137, 42)) as source (findId, newId)
            on target.id = source.findId
            when matched then update set id = source.newId;
            """));
        AreEqual("2627", ErrorNumber(exception));
    }

    [TestMethod]
    public void DropMiddleColumn_ShiftsSeekOrdinals()
    {
        // DROP COLUMN shifts every later storage slot down. The seek reads the
        // key's storage ordinals from a projection built at index construction,
        // so it has to be remapped with the rest or the seek decodes the wrong
        // column — which surfaces as a duplicate that isn't caught, or a decode
        // that walks off the row.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"""
            create table t (id int not null primary key, mid int, b int not null, pad char(500));
            create unique index ix_b on t(b);
            insert t (id, mid, b, pad) select value, value, value, 'x' from generate_series(1, {SeededRows});
            alter table t drop column mid
            """);
        var exception = Throws<DbException>(() => simulation.ExecuteNonQuery($"insert t values ({SeededRows + 1}, 137, 'x')"));
        AreEqual("2601", ErrorNumber(exception));
    }
}
