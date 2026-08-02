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

    /// <summary>
    /// An index keying one non-persisted computed column and INCLUDE-ing
    /// another must report each column's own <c>column_id</c>. Both share
    /// storage ordinal -1 (no row-storage slot), which used to collapse the
    /// catalog mapping onto the first computed column — WWI's
    /// <c>IX_Sales_Invoices_ConfirmedDeliveryTime</c> exported
    /// <c>INCLUDE</c> of its own key column, and real SQL Server rejects
    /// that script at bacpac import with Msg 1909 (duplicate column names
    /// in index). Mirrors the Sales.Invoices shape.
    /// </summary>
    [TestMethod]
    public void IndexColumns_ComputedKeyAndInclude_ReportDistinctColumnIds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (
                id int not null primary key,
                payload nvarchar(max),
                c1 as (json_value(payload, '$.a')),
                c2 as (json_value(payload, '$.b')));
            create index ix_c1 on t (c1) include (c2)
            """);
        using var reader = sim.ExecuteReader("""
            select c.name, ic.is_included_column
            from sys.index_columns ic
            join sys.indexes i on i.object_id = ic.object_id and i.index_id = ic.index_id
            join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id
            where i.name = 'ix_c1'
            order by ic.index_column_id
            """);
        IsTrue(reader.Read());
        AreEqual("c1", reader.GetString(0));
        IsFalse(reader.GetBoolean(1));
        IsTrue(reader.Read());
        AreEqual("c2", reader.GetString(0));
        IsTrue(reader.GetBoolean(1));
        IsFalse(reader.Read());
    }

    /// <summary>
    /// A clustered index numbers <c>index_column_id</c> in table column order
    /// while <c>key_ordinal</c> carries the key order, so
    /// <c>create clustered index ix on t(b, a)</c> reports column <c>a</c> at
    /// index_column_id 1 / key_ordinal 2 (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void IndexColumns_Clustered_NumbersInTableColumnOrder()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (a int not null, b int not null, c int not null, d int not null);
            create clustered index ix on t (c, a)
            """);
        using var reader = sim.ExecuteReader("""
            select col_name(ic.object_id, ic.column_id), ic.index_column_id, ic.key_ordinal
            from sys.index_columns ic
            join sys.indexes i on i.object_id = ic.object_id and i.index_id = ic.index_id
            where i.name = 'ix'
            order by ic.index_column_id
            """);
        IsTrue(reader.Read());
        AreEqual("a", reader.GetString(0));
        AreEqual(1, reader.GetInt32(1));
        AreEqual((byte)2, reader.GetByte(2));
        IsTrue(reader.Read());
        AreEqual("c", reader.GetString(0));
        AreEqual(2, reader.GetInt32(1));
        AreEqual((byte)1, reader.GetByte(2));
        IsFalse(reader.Read());
    }

    /// <summary>A clustered PRIMARY KEY numbers its columns the same way.</summary>
    [TestMethod]
    public void IndexColumns_ClusteredPrimaryKey_NumbersInTableColumnOrder()
        => AreEqual("a=1/2 c=2/1", new Simulation().ExecuteScalar("""
            create table t (a int not null, b int not null, c int not null,
                            constraint pk primary key clustered (c, a));
            select string_agg(
                       concat(col_name(ic.object_id, ic.column_id), '=',
                              ic.index_column_id, '/', ic.key_ordinal), ' ')
                       within group (order by ic.index_column_id)
            from sys.index_columns ic where ic.object_id = object_id('t')
            """));

    /// <summary>
    /// A nonclustered index keeps key order, its INCLUDE columns continuing
    /// past the key count.
    /// </summary>
    [TestMethod]
    public void IndexColumns_Nonclustered_NumbersInKeyOrder()
        => AreEqual("c=1/1 a=2/2 b=3/0", new Simulation().ExecuteScalar("""
            create table t (a int not null, b int not null, c int not null);
            create index ix on t (c, a) include (b);
            select string_agg(
                       concat(col_name(ic.object_id, ic.column_id), '=',
                              ic.index_column_id, '/', ic.key_ordinal), ' ')
                       within group (order by ic.index_column_id)
            from sys.index_columns ic where ic.object_id = object_id('t')
            """));

    /// <summary>
    /// <c>sys.stats_columns.stats_column_id</c> tracks the sibling
    /// index_column_id, so DacFx's join of the two on (stats_column_id,
    /// column_id) still pairs up for a clustered index.
    /// </summary>
    [TestMethod]
    public void StatsColumns_Clustered_TrackIndexColumnId()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (a int not null, b int not null, c int not null);
            create clustered index ix on t (c, a);
            select count(*) from sys.index_columns ic
            join sys.stats_columns sc on sc.object_id = ic.object_id and sc.stats_id = ic.index_id
                 and sc.stats_column_id = ic.index_column_id and sc.column_id = ic.column_id
            where ic.object_id = object_id('t')
            """));

    /// <summary>
    /// One declaration's key constraints take their index ids in reverse
    /// declaration order — real's own allocation order (probe-confirmed), so
    /// an inline nonclustered PRIMARY KEY declared before a UNIQUE lands at
    /// index_id 3 with the UNIQUE at 2.
    /// </summary>
    [TestMethod]
    public void IndexIds_InlineKeyConstraints_AllocateInReverseDeclarationOrder()
        => AreEqual("uq2=2 uq1=3 pk=4", new Simulation().ExecuteScalar("""
            create table t (id int not null constraint pk primary key nonclustered,
                            u int not null constraint uq1 unique,
                            v int not null constraint uq2 unique);
            select string_agg(concat(name, '=', index_id), ' ') within group (order by index_id)
            from sys.indexes where object_id = object_id('t') and index_id > 0
            """));

    /// <summary>
    /// The clustered constraint takes index_id 1 wherever it's declared, and
    /// the rest still reverse (probe-confirmed with the clustered PK in the
    /// middle of the declaration).
    /// </summary>
    [TestMethod]
    public void IndexIds_ClusteredConstraintFirst_ThenReverseDeclarationOrder()
        => AreEqual("pk=1 uq2=2 uq1=3", new Simulation().ExecuteScalar("""
            create table t (a int not null constraint uq1 unique,
                            b int not null constraint pk primary key clustered,
                            c int not null constraint uq2 unique);
            select string_agg(concat(name, '=', index_id), ' ') within group (order by index_id)
            from sys.indexes where object_id = object_id('t') and index_id > 0
            """));

    /// <summary>
    /// A table-level UNIQUE participates in the same reverse ordering as an
    /// inline one, and <c>ALTER TABLE ADD CONSTRAINT</c> afterwards takes the
    /// next id up.
    /// </summary>
    [TestMethod]
    public void IndexIds_TableLevelAndLaterAlter_FollowTheSameOrder()
        => AreEqual("pk=1 uq2=2 uq1=3 uq3=4", new Simulation().ExecuteScalar("""
            create table t (a int not null, b int not null, c int not null,
                            constraint pk primary key (a),
                            constraint uq1 unique (b),
                            constraint uq2 unique (c));
            alter table t add constraint uq3 unique (b, c);
            select string_agg(concat(name, '=', index_id), ' ') within group (order by index_id)
            from sys.indexes where object_id = object_id('t') and index_id > 0
            """));
}
