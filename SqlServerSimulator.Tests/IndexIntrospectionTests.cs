using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>INDEX_COL</c> / <c>INDEXKEY_PROPERTY</c> /
/// <c>STATS_DATE</c>. Index_id resolution follows the same emission order
/// as <c>sys.indexes</c> (PK=1, then UQ/named indexes sorted by ObjectId).
/// Behaviors are probe-confirmed against SQL Server 2025 (2026-05-23);
/// <c>STATS_DATE</c> intentionally diverges from real (which returns NULL
/// when no auto-stats has run) — the simulator returns the table's
/// <c>CreateDate</c> as a fake-but-realistic placeholder.
/// </summary>
[TestClass]
public sealed class IndexIntrospectionTests
{
    // === INDEX_COL ===

    [TestMethod]
    public void IndexCol_PK_FirstKeyColumn()
        => AreEqual("id", new Simulation().ExecuteScalar(
            "create table t (id int primary key, name varchar(50)); " +
            "select index_col('t', 1, 1)"));

    [TestMethod]
    public void IndexCol_PK_OutOfRangeKeyId_Null()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int primary key); " +
            "select index_col('t', 1, 2)"));

    [TestMethod]
    public void IndexCol_NamedIndex_FirstColumn()
        => AreEqual("name", new Simulation().ExecuteScalar(
            "create table t (id int primary key, name varchar(50)); " +
            "create index ix_name on t(name); " +
            "select index_col('t', 2, 1)"));

    [TestMethod]
    public void IndexCol_CompositeIndex_BothColumns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(
            "create table t (id int primary key, a int, b int); " +
            "create index ix_ab on t(a, b)");
        AreEqual("a", sim.ExecuteScalar("select index_col('t', 2, 1)"));
        AreEqual("b", sim.ExecuteScalar("select index_col('t', 2, 2)"));
    }

    [TestMethod]
    public void IndexCol_IncludeColumn_NotReachable()
    {
        // INCLUDE columns appear in sys.index_columns but INDEX_COL returns
        // NULL for them (key positions only). Probe-confirmed.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(
            "create table t (id int primary key, a int, b int, c int); " +
            "create index ix_a on t(a) include (b, c)");
        AreEqual("a", sim.ExecuteScalar("select index_col('t', 2, 1)"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select index_col('t', 2, 2)"));
    }

    [TestMethod]
    public void IndexCol_UnknownIndexId_Null()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select index_col('t', 99, 1)"));

    [TestMethod]
    public void IndexCol_UnknownTable_Null()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select index_col('no_such_table', 1, 1)"));

    [TestMethod]
    public void IndexCol_NullTable_Null()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select index_col(null, 1, 1)"));

    [TestMethod]
    public void IndexCol_NullIndexId_Null()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int primary key); " +
            "select index_col('t', null, 1)"));

    [TestMethod]
    public void IndexCol_NullKeyId_Null()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int primary key); " +
            "select index_col('t', 1, null)"));

    [TestMethod]
    public void IndexCol_KeyIdZero_Null()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int primary key); " +
            "select index_col('t', 1, 0)"));

    [TestMethod]
    public void IndexCol_KeyIdNegative_Null()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int primary key); " +
            "select index_col('t', 1, -1)"));

    [TestMethod]
    public void IndexCol_BracketedTableName()
        => AreEqual("id", new Simulation().ExecuteScalar(
            "create table t (id int primary key); " +
            "select index_col('[dbo].[t]', 1, 1)"));

    [TestMethod]
    public void IndexCol_SchemaQualified()
        => AreEqual("id", new Simulation().ExecuteScalar(
            "create table t (id int primary key); " +
            "select index_col('dbo.t', 1, 1)"));

    [TestMethod]
    public void IndexCol_DescendingKey_StillReturnsName()
    {
        // The DESC flag doesn't affect the returned column name.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(
            "create table t (id int primary key, b varchar(20)); " +
            "create unique index ix_b on t(b desc)");
        AreEqual("b", sim.ExecuteScalar("select index_col('t', 2, 1)"));
    }

    // === INDEXKEY_PROPERTY ===

    [TestMethod]
    public void IndexKeyProperty_ColumnId_PK()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "create table t (id int primary key); " +
            "select indexkey_property(object_id('t'), 1, 1, 'ColumnId')"));

    [TestMethod]
    public void IndexKeyProperty_ColumnId_SecondaryIndex()
        => AreEqual(2, new Simulation().ExecuteScalar(
            "create table t (id int primary key, name varchar(50)); " +
            "create index ix on t(name); " +
            "select indexkey_property(object_id('t'), 2, 1, 'ColumnId')"));

    [TestMethod]
    public void IndexKeyProperty_IsDescending_Ascending_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "create table t (id int primary key, name varchar(50)); " +
            "create index ix on t(name); " +
            "select indexkey_property(object_id('t'), 2, 1, 'IsDescending')"));

    [TestMethod]
    public void IndexKeyProperty_IsDescending_Descending_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "create table t (id int primary key, name varchar(50)); " +
            "create index ix on t(name desc); " +
            "select indexkey_property(object_id('t'), 2, 1, 'IsDescending')"));

    [TestMethod]
    public void IndexKeyProperty_IsDescending_PK_AlwaysZero()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "create table t (id int primary key); " +
            "select indexkey_property(object_id('t'), 1, 1, 'IsDescending')"));

    [TestMethod]
    public void IndexKeyProperty_CompositeIndex_SecondKeyDescending()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "create table t (id int primary key, a int, b int); " +
            "create index ix on t(a, b desc); " +
            "select indexkey_property(object_id('t'), 2, 2, 'IsDescending')"));

    [TestMethod]
    public void IndexKeyProperty_CompositeIndex_FirstKeyAscending()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "create table t (id int primary key, a int, b int); " +
            "create index ix on t(a, b desc); " +
            "select indexkey_property(object_id('t'), 2, 1, 'IsDescending')"));

    [TestMethod]
    public void IndexKeyProperty_OutOfRangeKeyId_Null()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int primary key, name varchar(50)); " +
            "create index ix on t(name); " +
            "select indexkey_property(object_id('t'), 2, 99, 'ColumnId')"));

    [TestMethod]
    public void IndexKeyProperty_UnknownIndexId_Null()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select indexkey_property(object_id('t'), 99, 1, 'ColumnId')"));

    [TestMethod]
    public void IndexKeyProperty_UnknownObjectId_Null()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select indexkey_property(99999, 1, 1, 'ColumnId')"));

    [TestMethod]
    public void IndexKeyProperty_UnknownProperty_Null()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int primary key); " +
            "select indexkey_property(object_id('t'), 1, 1, 'NotAProperty')"));

    [TestMethod]
    public void IndexKeyProperty_NullObjectId_Null()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select indexkey_property(null, 1, 1, 'ColumnId')"));

    [TestMethod]
    public void IndexKeyProperty_NullIndexId_Null()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int primary key); " +
            "select indexkey_property(object_id('t'), null, 1, 'ColumnId')"));

    [TestMethod]
    public void IndexKeyProperty_NullKeyId_Null()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int primary key); " +
            "select indexkey_property(object_id('t'), 1, null, 'ColumnId')"));

    [TestMethod]
    public void IndexKeyProperty_NullProperty_Null()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int primary key); " +
            "select indexkey_property(object_id('t'), 1, 1, null)"));

    [TestMethod]
    public void IndexKeyProperty_CaseInsensitiveProperty()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "create table t (id int primary key); " +
            "select indexkey_property(object_id('t'), 1, 1, 'columnid')"));

    [TestMethod]
    public void IndexKeyProperty_IncludeColumn_NotReachable()
    {
        // INCLUDE columns appear in sys.index_columns but
        // INDEXKEY_PROPERTY returns NULL for key positions beyond the
        // actual key column count.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(
            "create table t (id int primary key, a int, b int); " +
            "create index ix on t(a) include (b)");
        AreEqual(DBNull.Value, sim.ExecuteScalar(
            "select indexkey_property(object_id('t'), 2, 2, 'ColumnId')"));
    }

    // === STATS_DATE ===

    [TestMethod]
    public void StatsDate_PK_ReturnsNonNullDateTime()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int primary key)");
        var v = sim.ExecuteScalar("select stats_date(object_id('t'), 1)");
        _ = IsInstanceOfType<DateTime>(v);
        IsGreaterThan(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), (DateTime)v!);
    }

    [TestMethod]
    public void StatsDate_NamedIndex_MatchesTableCreateDate()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(
            "create table t (id int primary key, a int); " +
            "create index ix on t(a)");
        var pk = (DateTime)sim.ExecuteScalar("select stats_date(object_id('t'), 1)")!;
        var ix = (DateTime)sim.ExecuteScalar("select stats_date(object_id('t'), 2)")!;
        AreEqual(pk, ix);
    }

    [TestMethod]
    public void StatsDate_UnknownIndexId_Null()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select stats_date(object_id('t'), 99)"));

    [TestMethod]
    public void StatsDate_UnknownObjectId_Null()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select stats_date(99999, 1)"));

    [TestMethod]
    public void StatsDate_NullObjectId_Null()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select stats_date(null, 1)"));

    [TestMethod]
    public void StatsDate_NullStatsId_Null()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int primary key); " +
            "select stats_date(object_id('t'), null)"));

    [TestMethod]
    public void StatsDate_HeapTable_NoIndex_Returns_Null()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int); " +  // no PK = heap, index_id 0 is HEAP
            "select stats_date(object_id('t'), 1)"));
}
