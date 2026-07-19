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

    [TestMethod]
    public void TypeProperty_Precision_Int_Returns10()
        => AreEqual(10, new Simulation().ExecuteScalar("select typeproperty('int', 'Precision')"));

    [TestMethod]
    public void TypeProperty_Precision_BigInt_Returns19()
        => AreEqual(19, new Simulation().ExecuteScalar("select typeproperty('bigint', 'Precision')"));

    [TestMethod]
    public void TypeProperty_Precision_TinyInt_Returns3()
        => AreEqual(3, new Simulation().ExecuteScalar("select typeproperty('tinyint', 'Precision')"));

    [TestMethod]
    public void TypeProperty_Precision_SmallInt_Returns5()
        => AreEqual(5, new Simulation().ExecuteScalar("select typeproperty('smallint', 'Precision')"));

    [TestMethod]
    public void TypeProperty_Precision_Varchar_Returns8000()
        => AreEqual(8000, new Simulation().ExecuteScalar("select typeproperty('varchar', 'Precision')"));

    [TestMethod]
    public void TypeProperty_Precision_Decimal_Returns38()
        => AreEqual(38, new Simulation().ExecuteScalar("select typeproperty('decimal', 'Precision')"));

    [TestMethod]
    public void TypeProperty_Scale_Decimal_Returns38()
        => AreEqual(38, new Simulation().ExecuteScalar("select typeproperty('decimal', 'Scale')"));

    [TestMethod]
    public void TypeProperty_Scale_Int_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar("select typeproperty('int', 'Scale')"));

    [TestMethod]
    public void TypeProperty_Scale_Money_Returns4()
        => AreEqual(4, new Simulation().ExecuteScalar("select typeproperty('money', 'Scale')"));

    [TestMethod]
    public void TypeProperty_AllowsNull_Int_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar("select typeproperty('int', 'AllowsNull')"));

    [TestMethod]
    public void TypeProperty_AllowsNull_RowVersion_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar("select typeproperty('rowversion', 'AllowsNull')"));

    [TestMethod]
    public void TypeProperty_UsesAnsiTrim_Varchar_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar("select typeproperty('varchar', 'UsesAnsiTrim')"));

    [TestMethod]
    public void TypeProperty_UsesAnsiTrim_Int_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar("select typeproperty('int', 'UsesAnsiTrim')"));

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
}
