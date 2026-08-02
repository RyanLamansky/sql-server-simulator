using SqlServerSimulator.Bacpac;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the <c>*PROPERTY</c> introspection family:
/// <c>COLUMNPROPERTY</c>, <c>INDEXPROPERTY</c>, <c>TYPEPROPERTY</c>, and
/// <c>OBJECTPROPERTYEX</c>. Property values are probe-confirmed against
/// SQL Server 2025 (2026-05-23); the common NULL-on-any-NULL-arg /
/// NULL-on-unknown-X convention is enforced uniformly.
/// </summary>
[TestClass]
public sealed class PropertyFunctionsTests
{
    // === COLUMNPROPERTY ===

    [TestMethod]
    public void ColumnProperty_AllowsNull_NullableCol_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "create table t (id int not null, name varchar(50) null); " +
            "select columnproperty(object_id('t'), 'name', 'AllowsNull')"));

    [TestMethod]
    public void ColumnProperty_AllowsNull_NotNullCol_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "create table t (id int not null, name varchar(50) null); " +
            "select columnproperty(object_id('t'), 'id', 'AllowsNull')"));

    [TestMethod]
    public void ColumnProperty_IsIdentity_IdentityCol_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "create table t (id int identity(1,1) primary key); " +
            "select columnproperty(object_id('t'), 'id', 'IsIdentity')"));

    [TestMethod]
    public void ColumnProperty_IsIdentity_RegularCol_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "create table t (id int identity(1,1), name varchar(50)); " +
            "select columnproperty(object_id('t'), 'name', 'IsIdentity')"));

    [TestMethod]
    public void ColumnProperty_IsComputed_Computed_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "create table t (id int, doubled as (id * 2) persisted); " +
            "select columnproperty(object_id('t'), 'doubled', 'IsComputed')"));

    [TestMethod]
    public void ColumnProperty_IsComputed_Regular_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select columnproperty(object_id('t'), 'id', 'IsComputed')"));

    [TestMethod]
    public void ColumnProperty_IsRowGuidCol_AlwaysZero()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select columnproperty(object_id('t'), 'id', 'IsRowGuidCol')"));

    [TestMethod]
    public void ColumnProperty_Precision_Int_Returns10()
        => AreEqual(10, new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select columnproperty(object_id('t'), 'id', 'Precision')"));

    [TestMethod]
    public void ColumnProperty_Precision_VarcharN_ReturnsN()
        => AreEqual(50, new Simulation().ExecuteScalar(
            "create table t (name varchar(50)); " +
            "select columnproperty(object_id('t'), 'name', 'Precision')"));

    [TestMethod]
    public void ColumnProperty_Precision_Money_Returns19()
        => AreEqual(19, new Simulation().ExecuteScalar(
            "create table t (amt money); " +
            "select columnproperty(object_id('t'), 'amt', 'Precision')"));

    [TestMethod]
    public void ColumnProperty_Scale_Money_Returns4()
        => AreEqual(4, new Simulation().ExecuteScalar(
            "create table t (amt money); " +
            "select columnproperty(object_id('t'), 'amt', 'Scale')"));

    [TestMethod]
    public void ColumnProperty_Scale_Int_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select columnproperty(object_id('t'), 'id', 'Scale')"));

    [TestMethod]
    public void ColumnProperty_CharMaxLen_VarcharN_ReturnsN()
        => AreEqual(50, new Simulation().ExecuteScalar(
            "create table t (name varchar(50)); " +
            "select columnproperty(object_id('t'), 'name', 'CharMaxLen')"));

    [TestMethod]
    public void ColumnProperty_CharMaxLen_OnInt_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select columnproperty(object_id('t'), 'id', 'CharMaxLen')"));

    [TestMethod]
    public void ColumnProperty_ColumnId_FirstColumn_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "create table t (id int, name varchar(50)); " +
            "select columnproperty(object_id('t'), 'id', 'ColumnId')"));

    [TestMethod]
    public void ColumnProperty_ColumnId_SecondColumn_Returns2()
        => AreEqual(2, new Simulation().ExecuteScalar(
            "create table t (id int, name varchar(50)); " +
            "select columnproperty(object_id('t'), 'name', 'ColumnId')"));

    [TestMethod]
    public void ColumnProperty_UsesAnsiTrim_Varchar_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "create table t (name varchar(50)); " +
            "select columnproperty(object_id('t'), 'name', 'UsesAnsiTrim')"));

    [TestMethod]
    public void ColumnProperty_UnknownProperty_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select columnproperty(object_id('t'), 'id', 'NotAProperty')"));

    [TestMethod]
    public void ColumnProperty_UnknownColumn_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select columnproperty(object_id('t'), 'no_such_col', 'AllowsNull')"));

    [TestMethod]
    public void ColumnProperty_UnknownObject_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select columnproperty(99999, 'id', 'AllowsNull')"));

    [TestMethod]
    public void ColumnProperty_NullId_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select columnproperty(null, 'id', 'AllowsNull')"));

    [TestMethod]
    public void ColumnProperty_NullColumn_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select columnproperty(object_id('t'), null, 'AllowsNull')"));

    [TestMethod]
    public void ColumnProperty_NullProperty_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select columnproperty(object_id('t'), 'id', null)"));

    [TestMethod]
    public void ColumnProperty_CaseInsensitiveProperty()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "create table t (id int not null); " +
            "select columnproperty(object_id('t'), 'id', 'allowsnull')"));

    [TestMethod]
    public void ColumnProperty_CaseInsensitiveColumnName()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "create table t (id int not null); " +
            "select columnproperty(object_id('t'), 'ID', 'AllowsNull')"));

    // === INDEXPROPERTY ===

    [TestMethod]
    public void IndexProperty_IsUnique_OnUniqueIndex_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "create table t (id int, name varchar(50)); " +
            "create unique index ix on t(name); " +
            "select indexproperty(object_id('t'), 'ix', 'IsUnique')"));

    [TestMethod]
    public void IndexProperty_IsUnique_OnNonUniqueIndex_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "create table t (id int, name varchar(50)); " +
            "create index ix on t(name); " +
            "select indexproperty(object_id('t'), 'ix', 'IsUnique')"));

    [TestMethod]
    public void IndexProperty_IsClustered_OnNonClustered_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "create table t (id int, name varchar(50)); " +
            "create index ix on t(name); " +
            "select indexproperty(object_id('t'), 'ix', 'IsClustered')"));

    [TestMethod]
    public void IndexProperty_IndexDepth_AlwaysZero()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "create table t (id int, name varchar(50)); " +
            "create index ix on t(name); " +
            "select indexproperty(object_id('t'), 'ix', 'IndexDepth')"));

    [TestMethod]
    public void IndexProperty_IndexFillFactor_AlwaysZero()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "create table t (id int, name varchar(50)); " +
            "create index ix on t(name); " +
            "select indexproperty(object_id('t'), 'ix', 'IndexFillFactor')"));

    [TestMethod]
    public void IndexProperty_UnknownIndex_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select indexproperty(object_id('t'), 'no_such_index', 'IsUnique')"));

    [TestMethod]
    public void IndexProperty_UnknownProperty_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int, name varchar(50)); " +
            "create index ix on t(name); " +
            "select indexproperty(object_id('t'), 'ix', 'NotAProperty')"));

    [TestMethod]
    public void IndexProperty_NullId_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select indexproperty(null, 'ix', 'IsUnique')"));

    [TestMethod]
    public void IndexProperty_CaseInsensitiveIndexName()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "create table t (id int, name varchar(50)); " +
            "create unique index ix on t(name); " +
            "select indexproperty(object_id('t'), 'IX', 'IsUnique')"));

    [TestMethod]
    public void IndexProperty_PKConstraint_AsIndex()
    {
        // PK / UNIQUE constraints surface in sys.indexes by their constraint
        // name; the simulator auto-allocates names like PK__<table8>__<hex>.
        // Look up the constraint by querying sys.indexes first.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int primary key)");
        var pkName = (string)sim.ExecuteScalar(
            "select name from sys.indexes where object_id = object_id('t') and is_primary_key = 1")!;
        IsNotNull(pkName);
        AreEqual(1, sim.ExecuteScalar($"select indexproperty(object_id('t'), '{pkName}', 'IsUnique')"));
    }

    // === TYPEPROPERTY ===

    /// <summary>
    /// The whole table, one row per type name and one column per property, as
    /// SQL Server 2025 answers it (2026-08-02). A property the type has no
    /// value for is NULL rather than 0, which is most of the table — and the
    /// two grammar synonyms <c>integer</c> / <c>rowversion</c> are names
    /// TYPEPROPERTY itself does not recognize, so every property is NULL.
    /// </summary>
    [TestMethod]
    [DataRow("bit", 1, null, 1, null)]
    [DataRow("int", 10, 0, 1, null)]
    [DataRow("xml", -1, null, 1, null)]
    [DataRow("char", 8000, null, 1, 1)]
    [DataRow("date", 10, 0, 1, null)]
    [DataRow("real", 24, null, 1, null)]
    [DataRow("text", 2147483647, null, 1, null)]
    [DataRow("time", 16, 7, 1, null)]
    [DataRow("float", 53, null, 1, null)]
    [DataRow("image", 2147483647, null, 1, null)]
    [DataRow("money", 19, 4, 1, null)]
    [DataRow("nchar", 4000, null, 1, null)]
    [DataRow("ntext", 1073741823, null, 1, null)]
    [DataRow("bigint", 19, 0, 1, null)]
    [DataRow("binary", 8000, null, 1, 1)]
    [DataRow("decimal", 38, 38, 1, null)]
    [DataRow("numeric", 38, 38, 1, null)]
    [DataRow("sysname", 128, null, 0, null)]
    [DataRow("tinyint", 3, 0, 1, null)]
    [DataRow("varchar", 8000, null, 1, 1)]
    [DataRow("datetime", 23, 3, 1, null)]
    [DataRow("nvarchar", 4000, null, 1, null)]
    [DataRow("smallint", 5, 0, 1, null)]
    [DataRow("datetime2", 27, 7, 1, null)]
    [DataRow("timestamp", 8, null, 0, null)]
    [DataRow("varbinary", 8000, null, 1, 1)]
    [DataRow("smallmoney", 10, 4, 1, null)]
    [DataRow("hierarchyid", 892, null, 1, null)]
    [DataRow("sql_variant", 0, null, 1, 1)]
    [DataRow("smalldatetime", 16, 0, 1, null)]
    [DataRow("datetimeoffset", 34, 7, 1, null)]
    [DataRow("uniqueidentifier", 16, null, 1, null)]
    [DataRow("integer", null, null, null, null)]
    [DataRow("rowversion", null, null, null, null)]
    public void TypeProperty_Table_MatchesSqlServer(string typeName, int? precision, int? scale, int? allowsNull, int? usesAnsiTrim)
    {
        var sim = new Simulation();
        foreach (var (property, expected) in new (string, int?)[]
        {
            ("Precision", precision), ("Scale", scale), ("AllowsNull", allowsNull), ("UsesAnsiTrim", usesAnsiTrim),
        })
        {
            AreEqual(
                expected is null ? DBNull.Value : expected,
                sim.ExecuteScalar($"select typeproperty('{typeName}', '{property}')"),
                $"{typeName}.{property}");
        }
    }

    [TestMethod]
    public void TypeProperty_UnknownType_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select typeproperty('no_such_type', 'Precision')"));

    [TestMethod]
    public void TypeProperty_UnknownProperty_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select typeproperty('int', 'NotAProperty')"));

    [TestMethod]
    public void TypeProperty_NullType_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select typeproperty(null, 'Precision')"));

    [TestMethod]
    public void TypeProperty_NullProperty_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select typeproperty('int', null)"));

    [TestMethod]
    public void TypeProperty_CaseInsensitiveType()
        => AreEqual(10, new Simulation().ExecuteScalar("select typeproperty('INT', 'Precision')"));

    [TestMethod]
    public void TypeProperty_CaseInsensitiveProperty()
        => AreEqual(10, new Simulation().ExecuteScalar("select typeproperty('int', 'precision')"));

    // === OBJECTPROPERTYEX ===

    /// <summary>
    /// OBJECTPROPERTYEX projects sql_variant; the boolean Is-X properties
    /// carry an int inner base type, which SqlClient unwraps to an int.
    /// </summary>
    [TestMethod]
    public void ObjectPropertyEx_IsTable_OnTable_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select objectpropertyex(object_id('t'), 'IsTable')"));

    [TestMethod]
    public void ObjectPropertyEx_BaseType_Table_Returns_U()
        => AreEqual("U ", new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select objectpropertyex(object_id('t'), 'BaseType')"));

    [TestMethod]
    public void ObjectPropertyEx_BaseType_View_Returns_V()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table t (id int)", "create view v as select id from t");
        AreEqual("V ", sim.ExecuteScalar("select objectpropertyex(object_id('v'), 'BaseType')"));
    }

    [TestMethod]
    public void ObjectPropertyEx_BaseType_Procedure_Returns_P()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure p as select 1");
        AreEqual("P ", sim.ExecuteScalar("select objectpropertyex(object_id('p'), 'BaseType')"));
    }

    /// <summary>
    /// SchemaId's inner base type is int.
    /// </summary>
    [TestMethod]
    public void ObjectPropertyEx_SchemaId_Dbo_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select objectpropertyex(object_id('t'), 'SchemaId')"));

    /// <summary>
    /// Cardinality's inner base type is bigint, which unwraps to a long.
    /// </summary>
    [TestMethod]
    public void ObjectPropertyEx_Cardinality_EmptyTable_Returns0()
        => AreEqual(0L, new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select objectpropertyex(object_id('t'), 'Cardinality')"));

    [TestMethod]
    public void ObjectPropertyEx_Cardinality_ThreeRows_Returns3()
        => AreEqual(3L, new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "insert t values (1), (2), (3); " +
            "select objectpropertyex(object_id('t'), 'Cardinality')"));

    [TestMethod]
    public void ObjectPropertyEx_TableHasIdentity_WithIdentity_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "create table t (id int identity(1,1) primary key); " +
            "select objectpropertyex(object_id('t'), 'TableHasIdentity')"));

    [TestMethod]
    public void ObjectPropertyEx_TableHasIdentity_NoIdentity_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select objectpropertyex(object_id('t'), 'TableHasIdentity')"));

    [TestMethod]
    public void ObjectPropertyEx_TableHasPrimaryKey_WithPK_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "create table t (id int primary key); " +
            "select objectpropertyex(object_id('t'), 'TableHasPrimaryKey')"));

    [TestMethod]
    public void ObjectPropertyEx_TableHasPrimaryKey_NoPK_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select objectpropertyex(object_id('t'), 'TableHasPrimaryKey')"));

    [TestMethod]
    public void ObjectPropertyEx_TableHasUniqueCnst_WithUnique_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "create table t (id int, name varchar(50), constraint uq unique (name)); " +
            "select objectpropertyex(object_id('t'), 'TableHasUniqueCnst')"));

    [TestMethod]
    public void ObjectPropertyEx_TableHasCheckCnst_WithCheck_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "create table t (id int, check (id > 0)); " +
            "select objectpropertyex(object_id('t'), 'TableHasCheckCnst')"));

    [TestMethod]
    public void ObjectPropertyEx_TableHasForeignKey_WithFK_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "create table p (id int primary key); " +
            "create table c (id int primary key, p_id int references p(id)); " +
            "select objectpropertyex(object_id('c'), 'TableHasForeignKey')"));

    [TestMethod]
    public void ObjectPropertyEx_TableHasForeignRef_WithIncoming_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "create table p (id int primary key); " +
            "create table c (id int primary key, p_id int references p(id)); " +
            "select objectpropertyex(object_id('p'), 'TableHasForeignRef')"));

    [TestMethod]
    public void ObjectPropertyEx_TableHasIndex_WithIndex_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "create table t (id int, name varchar(50)); " +
            "create index ix on t(name); " +
            "select objectpropertyex(object_id('t'), 'TableHasIndex')"));

    [TestMethod]
    public void ObjectPropertyEx_TableHasRowGuidCol_AlwaysZero()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select objectpropertyex(object_id('t'), 'TableHasRowGuidCol')"));

    [TestMethod]
    public void ObjectPropertyEx_ProjectsSqlVariant()
    {
        using var reader = new Simulation().ExecuteReader(
            "create table t (id int); " +
            "select objectpropertyex(object_id('t'), 'SchemaId')");
        IsTrue(reader.Read());
        AreEqual("sql_variant", reader.GetDataTypeName(0));
    }

    [TestMethod]
    public void ObjectPropertyEx_BaseType_InnerBaseTypeIsChar()
        => AreEqual("char", new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select sql_variant_property(objectpropertyex(object_id('t'), 'BaseType'), 'BaseType')"));

    [TestMethod]
    public void ObjectPropertyEx_SchemaId_InnerBaseTypeIsInt()
        => AreEqual("int", new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select sql_variant_property(objectpropertyex(object_id('t'), 'SchemaId'), 'BaseType')"));

    [TestMethod]
    public void ObjectPropertyEx_Cardinality_InnerBaseTypeIsBigInt()
        => AreEqual("bigint", new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select sql_variant_property(objectpropertyex(object_id('t'), 'Cardinality'), 'BaseType')"));

    [TestMethod]
    public void ObjectPropertyEx_TableHasIdentity_InnerBaseTypeIsInt()
        => AreEqual("int", new Simulation().ExecuteScalar(
            "create table t (id int identity(1,1)); " +
            "select sql_variant_property(objectpropertyex(object_id('t'), 'TableHasIdentity'), 'BaseType')"));

    [TestMethod]
    public void ObjectPropertyEx_UnknownProperty_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select objectpropertyex(object_id('t'), 'NotAProperty')"));

    [TestMethod]
    public void ObjectPropertyEx_NullId_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select objectpropertyex(null, 'IsTable')"));

    [TestMethod]
    public void ObjectPropertyEx_NullProperty_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "create table t (id int); " +
            "select objectpropertyex(object_id('t'), null)"));

    // === FILEPROPERTY ===
    // The current database's modeled files are <db>_Data (file_id 1, primary
    // data) and <db>_Log (file_id 2, log); the default simulation database is
    // "simulated". Values / NULL cases probed against SQL Server 2025
    // (2026-07-15). Returns int.

    [TestMethod]
    public void FileProperty_IsPrimaryFile_DataFile_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar<int>(
            "select fileproperty(N'simulated_Data', 'IsPrimaryFile')"));

    [TestMethod]
    public void FileProperty_IsPrimaryFile_LogFile_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar<int>(
            "select fileproperty(N'simulated_Log', 'IsPrimaryFile')"));

    [TestMethod]
    public void FileProperty_IsLogFile_LogFile_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar<int>(
            "select fileproperty(N'simulated_Log', 'IsLogFile')"));

    [TestMethod]
    public void FileProperty_IsLogFile_DataFile_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar<int>(
            "select fileproperty(N'simulated_Data', 'IsLogFile')"));

    [TestMethod]
    public void FileProperty_IsReadOnly_AlwaysZero()
        => AreEqual(0, new Simulation().ExecuteScalar<int>(
            "select fileproperty(N'simulated_Data', 'IsReadOnly')"));

    // SpaceUsed on the data file equals SUM(allocation_units.total_pages),
    // keeping SSMS's SpaceAvailable non-negative.
    [TestMethod]
    public void FileProperty_SpaceUsed_DataFile_MatchesAllocationUnitTotal()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key, filler nvarchar(2000) null)");
        _ = sim.ExecuteNonQuery("insert into t (id, filler) select value, replicate(N'x', 1000) from generate_series(1, 200)");
        var sumTotal = sim.ExecuteScalar<long>("""
            select sum(a.total_pages)
            from sys.partitions p join sys.allocation_units a on p.partition_id = a.container_id
            """);
        AreEqual((int)sumTotal, sim.ExecuteScalar<int>("select fileproperty(N'simulated_Data', 'SpaceUsed')"));
    }

    [TestMethod]
    public void FileProperty_SpaceUsed_LogFile_NonNull()
        => IsGreaterThan(0, new Simulation().ExecuteScalar<int>(
            "select fileproperty(N'simulated_Log', 'SpaceUsed')"));

    // Property name is case-insensitive and trailing-space insensitive (SQL
    // Server's internal = comparison).
    [TestMethod]
    public void FileProperty_Property_CaseAndTrailingSpaceInsensitive()
        => AreEqual(1, new Simulation().ExecuteScalar<int>(
            "select fileproperty(N'simulated_Data', 'isPRIMARYfile ')"));

    [TestMethod]
    public void FileProperty_UnknownProperty_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select fileproperty(N'simulated_Data', 'NotAProperty')"));

    [TestMethod]
    public void FileProperty_UnknownFile_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select fileproperty(N'NoSuchFile', 'SpaceUsed')"));

    [TestMethod]
    public void FileProperty_NullFile_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select fileproperty(null, 'SpaceUsed')"));

    [TestMethod]
    public void FileProperty_NullProperty_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select fileproperty(N'simulated_Data', null)"));

    // === FILE_ID / FILE_IDEX / FILE_NAME ===
    // The current database's two modeled files are <db>_Data (file_id 1) and
    // <db>_Log (file_id 2), consistent with sys.database_files; the default
    // simulation database is "simulated". Return types / NULL cases
    // probe-confirmed against SQL Server 2025 (2026-07-20): FILE_ID → smallint,
    // FILE_IDEX → int, FILE_NAME → nvarchar(128).

    [TestMethod]
    public void FileId_DataFile_Returns1()
        => AreEqual((short)1, new Simulation().ExecuteScalar<short>(
            "select file_id(N'simulated_Data')"));

    [TestMethod]
    public void FileId_LogFile_Returns2()
        => AreEqual((short)2, new Simulation().ExecuteScalar<short>(
            "select file_id(N'simulated_Log')"));

    [TestMethod]
    public void FileId_TrailingSpaceInsensitive()
        => AreEqual((short)1, new Simulation().ExecuteScalar<short>(
            "select file_id(N'simulated_Data ')"));

    [TestMethod]
    public void FileId_UnknownFile_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select file_id(N'NoSuchFile')"));

    [TestMethod]
    public void FileId_NullArgument_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select file_id(null)"));

    [TestMethod]
    public void FileId_ResultType_IsSmallInt()
        => AreEqual("smallint", new Simulation().ExecuteScalar(
            "select sql_variant_property(cast(file_id(N'simulated_Data') as sql_variant), 'BaseType')"));

    [TestMethod]
    public void FileIdEx_DataFile_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar<int>(
            "select file_idex(N'simulated_Data')"));

    [TestMethod]
    public void FileIdEx_UnknownFile_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select file_idex(N'NoSuchFile')"));

    [TestMethod]
    public void FileIdEx_ResultType_IsInt()
        => AreEqual("int", new Simulation().ExecuteScalar(
            "select sql_variant_property(cast(file_idex(N'simulated_Log') as sql_variant), 'BaseType')"));

    [TestMethod]
    public void FileName_File1_ReturnsDataFile()
        => AreEqual("simulated_Data", new Simulation().ExecuteScalar(
            "select file_name(1)"));

    [TestMethod]
    public void FileName_File2_ReturnsLogFile()
        => AreEqual("simulated_Log", new Simulation().ExecuteScalar(
            "select file_name(2)"));

    [TestMethod]
    public void FileName_UnknownId_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select file_name(99)"));

    [TestMethod]
    public void FileName_ZeroId_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select file_name(0)"));

    [TestMethod]
    public void FileName_NegativeId_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select file_name(-1)"));

    [TestMethod]
    public void FileName_NullArgument_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select file_name(null)"));

    // FILE_ID / FILE_NAME are exact inverses over the modeled file pair.
    [TestMethod]
    public void FileName_OfFileId_RoundTrips()
        => AreEqual("simulated_Data", new Simulation().ExecuteScalar(
            "select file_name(file_id(N'simulated_Data'))"));

    // === FILEGROUP_ID / FILEGROUP_NAME ===
    // Every database carries the PRIMARY filegroup at data_space_id 1; user
    // filegroups (bacpac SqlFilegroup) take 2, 3, …. Return types / NULL cases
    // probe-confirmed against SQL Server 2025 (2026-07-20): FILEGROUP_ID →
    // smallint, FILEGROUP_NAME → nvarchar(128).

    [TestMethod]
    public void FilegroupId_Primary_Returns1()
        => AreEqual((short)1, new Simulation().ExecuteScalar<short>(
            "select filegroup_id('PRIMARY')"));

    [TestMethod]
    public void FilegroupId_CaseInsensitive()
        => AreEqual((short)1, new Simulation().ExecuteScalar<short>(
            "select filegroup_id('primary')"));

    [TestMethod]
    public void FilegroupId_UnknownName_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select filegroup_id('NoSuchFilegroup')"));

    [TestMethod]
    public void FilegroupId_NullArgument_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select filegroup_id(null)"));

    [TestMethod]
    public void FilegroupId_ResultType_IsSmallInt()
        => AreEqual("smallint", new Simulation().ExecuteScalar(
            "select sql_variant_property(cast(filegroup_id('PRIMARY') as sql_variant), 'BaseType')"));

    [TestMethod]
    public void FilegroupName_Id1_ReturnsPrimary()
        => AreEqual("PRIMARY", new Simulation().ExecuteScalar(
            "select filegroup_name(1)"));

    [TestMethod]
    public void FilegroupName_ZeroId_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select filegroup_name(0)"));

    [TestMethod]
    public void FilegroupName_UnknownId_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select filegroup_name(99)"));

    [TestMethod]
    public void FilegroupName_NullArgument_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select filegroup_name(null)"));

    // A bacpac SqlFilegroup registers as data_space_id 2; the file/filegroup
    // scalars read it and stay consistent with sys.filegroups.
    [TestMethod]
    public void FilegroupId_UserFilegroup_FromBacpac_Returns2()
    {
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Item", t => t.Column("Id", "int").Row(1))
            .Filegroup("FG_Indexes")
            .Build();
        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out _);
        AreEqual((short)2, sim.ExecuteScalar<short>("select filegroup_id('FG_Indexes')"));
        AreEqual("FG_Indexes", sim.ExecuteScalar("select filegroup_name(2)"));
    }

    // === FILEGROUPPROPERTY ===
    // Returns int. PRIMARY and user-filegroup values probe-confirmed against
    // SQL Server 2025 (2026-07-20): PRIMARY is the default, not user-defined;
    // a user filegroup is user-defined, not the default; no read-only
    // filegroups modeled.

    [TestMethod]
    public void FilegroupProperty_IsDefault_Primary_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar<int>(
            "select filegroupproperty('PRIMARY', 'IsDefault')"));

    [TestMethod]
    public void FilegroupProperty_IsUserDefinedFG_Primary_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar<int>(
            "select filegroupproperty('PRIMARY', 'IsUserDefinedFG')"));

    [TestMethod]
    public void FilegroupProperty_IsReadOnly_Primary_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar<int>(
            "select filegroupproperty('PRIMARY', 'IsReadOnly')"));

    [TestMethod]
    public void FilegroupProperty_Property_CaseAndTrailingSpaceInsensitive()
        => AreEqual(1, new Simulation().ExecuteScalar<int>(
            "select filegroupproperty('PRIMARY', 'isDEFAULT ')"));

    [TestMethod]
    public void FilegroupProperty_UnknownProperty_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select filegroupproperty('PRIMARY', 'NotAProperty')"));

    [TestMethod]
    public void FilegroupProperty_UnknownFilegroup_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select filegroupproperty('NoSuchFilegroup', 'IsDefault')"));

    [TestMethod]
    public void FilegroupProperty_NullFilegroup_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select filegroupproperty(null, 'IsDefault')"));

    [TestMethod]
    public void FilegroupProperty_NullProperty_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select filegroupproperty('PRIMARY', null)"));

    // A user filegroup: user-defined, not the default (probe-confirmed against
    // a scratch database's ADD FILEGROUP on SQL Server 2025).
    [TestMethod]
    public void FilegroupProperty_UserFilegroup_IsUserDefined_NotDefault()
    {
        using var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Item", t => t.Column("Id", "int").Row(1))
            .Filegroup("FG_Indexes")
            .Build();
        var sim = new Simulation();
        sim.ImportBacpac(bacpac, out _);
        AreEqual(1, sim.ExecuteScalar<int>("select filegroupproperty('FG_Indexes', 'IsUserDefinedFG')"));
        AreEqual(0, sim.ExecuteScalar<int>("select filegroupproperty('FG_Indexes', 'IsDefault')"));
        AreEqual(0, sim.ExecuteScalar<int>("select filegroupproperty('FG_Indexes', 'IsReadOnly')"));
    }
}
