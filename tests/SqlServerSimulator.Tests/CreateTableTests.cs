namespace SqlServerSimulator;

[TestClass]
public class CreateTableTests
{
    private static int Create(string columnsSpec) => new Simulation().ExecuteNonQuery($"create table t ( {columnsSpec} )");

    [TestMethod]
    public void CreateTableMinimal() => Assert.AreEqual(-1, Create("v int"));

    [TestMethod]
    public void InvalidTypeName()
        => new Simulation().AssertSqlError("create table t ( v intz )", 2715, "Column, parameter, or variable #1: Cannot find data type intz.");

    [TestMethod]
    public void CreateTableNull() => Assert.AreEqual(-1, Create("v int null"));

    [TestMethod]
    public void CreateTableNotNull() => Assert.AreEqual(-1, Create("v int not null"));

    [TestMethod]
    [DataRow("varchar(50)")]
    [DataRow("VARCHAR(50)")]
    [DataRow("nvarchar(50)")]
    [DataRow("NVARCHAR(50)")]
    public void CreateTableVarcharWithLength(string typeSpec) => Assert.AreEqual(-1, Create($"v {typeSpec}"));

    [TestMethod]
    public void CreateTableVarcharWithLengthAndNullability() => Assert.AreEqual(-1, Create("v varchar(50) not null"));

    [TestMethod]
    public void CreateTableVarcharWithoutLengthDefaultsToOne()
        => Assert.AreEqual(-1, Create("v varchar"));

    [TestMethod]
    public void CreateTableVarcharMaxAccepted() => Assert.AreEqual(-1, Create("v varchar(max)"));

    [TestMethod]
    public void CreateTableVarcharSizeExceedsMaximum()
        => new Simulation().AssertSqlError("create table t ( v varchar(8001) )", 131,
            "The size (8001) given to the column 'v' exceeds the maximum allowed for any data type (8000).");

    [TestMethod]
    public void CreateTableNVarcharSizeExceedsMaximum()
        => new Simulation().AssertSqlError("create table t ( v nvarchar(4001) )", 2717,
            "The size (4001) given to the parameter 'v' exceeds the maximum allowed (4000).");

    [TestMethod]
    public void CreateTableLengthOnNonLengthType()
        => new Simulation().AssertSqlError("create table t ( v int(4) )", 2716,
            "Column, parameter, or variable #1: Cannot specify a column width on data type int.");

    [TestMethod]
    public void CreateTableFixedWidthSumExceedsRowSizeMax()
        // 8054 bytes of fixed-width data and 7 of row overhead pass the
        // 8060-byte in-row record size — Msg 1701 (probed 2026-10-01).
        => new Simulation().AssertSqlError("create table t ( a char(8000), b char(54) )", 1701,
            "Creating or altering table 't' failed because the minimum row size would be 8061, including 7 bytes of internal overhead. This exceeds the maximum allowable table row size of 8060 bytes.");

    /// <summary>
    /// The smallest row packs <c>bit</c> columns eight to a byte and counts a
    /// null-bitmap byte per eight columns, while a variable-length column adds
    /// nothing (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("a char(8000), b char(50), c bit, d bit, e bit, f bit, g bit, h bit, i bit, j bit, k bit", -1)]
    [DataRow("a char(8000), b char(60), v varchar(10), w varchar(10)", 1701)]
    [DataRow("a char(8000), b char(40), c int, d int, e int, f int, g int, h int, i int", 1701)]
    [DataRow("a nchar(4000), b char(100) sparse", -1)]
    public void CreateTableMinimumRowSize(string columns, int expected)
    {
        if (expected < 0)
            Assert.AreEqual(-1, new Simulation().ExecuteNonQuery($"create table t ( {columns} )"));
        else
            _ = new Simulation().AssertSqlError($"create table t ( {columns} )", expected);
    }

    [TestMethod]
    public void CreateTableMoreThan1024Columns_RaisesMsg1702()
        => new Simulation().AssertSqlError($"create table t ( {string.Join(", ", Enumerable.Range(1, 1025).Select(i => $"c{i} int"))} )", 1702,
            "CREATE TABLE failed because column 'c1025' in table 't' exceeds the maximum of 1024 columns.");

    [TestMethod]
    [DataRow("varbinary(50)")]
    [DataRow("VARBINARY(8000)")]
    public void CreateTableVarbinaryWithLength(string typeSpec) => Assert.AreEqual(-1, Create($"v {typeSpec}"));

    [TestMethod]
    public void CreateTableVarbinaryMaxAccepted() => Assert.AreEqual(-1, Create("v varbinary(max)"));

    [TestMethod]
    public void CreateTableVarbinarySizeExceedsMaximum()
        => new Simulation().AssertSqlError("create table t ( v varbinary(8001) )", 131,
            "The size (8001) given to the column 'v' exceeds the maximum allowed for any data type (8000).");

    [TestMethod]
    public void CreateTableFixedWidthSumAtRowSizeMax()
        // 8053 bytes of fixed-width data and 7 of overhead — exactly at the limit.
        => Assert.AreEqual(-1, new Simulation().ExecuteNonQuery("create table t ( a char(8000), b char(53) )"));

    [TestMethod]
    [DataRow("date")]
    [DataRow("DATE")]
    [DataRow("Date")]
    public void CreateTableDate(string typeSpec) => Assert.AreEqual(-1, Create($"v {typeSpec}"));

    [TestMethod]
    public void CreateTableDateRejectsLengthSpecifier()
        => new Simulation().AssertSqlError("create table t ( v date(3) )", 2716,
            "Column, parameter, or variable #1: Cannot specify a column width on data type date.");

    [TestMethod]
    [DataRow("datetime2")]
    [DataRow("DATETIME2")]
    [DataRow("datetime2(0)")]
    [DataRow("datetime2(3)")]
    [DataRow("datetime2(7)")]
    public void CreateTableDateTime2(string typeSpec) => Assert.AreEqual(-1, Create($"v {typeSpec}"));

    [TestMethod]
    [DataRow(8)]
    [DataRow(99)]
    public void CreateTableDateTime2_PrecisionOutOfRange(int precision)
        => new Simulation().AssertSqlError($"create table t ( v datetime2({precision}) )", 1002,
            $"Line 1: Specified scale {precision} is invalid.");

    [TestMethod]
    [DataRow("time")]
    [DataRow("TIME")]
    [DataRow("time(0)")]
    [DataRow("time(3)")]
    [DataRow("time(7)")]
    public void CreateTableTime(string typeSpec) => Assert.AreEqual(-1, Create($"v {typeSpec}"));

    [TestMethod]
    [DataRow(8)]
    [DataRow(99)]
    public void CreateTableTime_PrecisionOutOfRange(int precision)
        => new Simulation().AssertSqlError($"create table t ( v time({precision}) )", 1002,
            $"Line 1: Specified scale {precision} is invalid.");

    [TestMethod]
    [DataRow("datetimeoffset")]
    [DataRow("DATETIMEOFFSET")]
    [DataRow("datetimeoffset(0)")]
    [DataRow("datetimeoffset(3)")]
    [DataRow("datetimeoffset(7)")]
    public void CreateTableDateTimeOffset(string typeSpec) => Assert.AreEqual(-1, Create($"v {typeSpec}"));

    [TestMethod]
    [DataRow(8)]
    [DataRow(99)]
    public void CreateTableDateTimeOffset_PrecisionOutOfRange(int precision)
        => new Simulation().AssertSqlError($"create table t ( v datetimeoffset({precision}) )", 1002,
            $"Line 1: Specified scale {precision} is invalid.");

    /// <summary>
    /// Identifiers matching contextual keywords (OUTPUT, USING, MATCHED, MAX, etc.)
    /// must work as column names — contextual keywords are classified at parse time
    /// in keyword-expecting positions only.
    /// </summary>
    [TestMethod]
    [DataRow("Output")]
    [DataRow("Using")]
    [DataRow("Matched")]
    [DataRow("Max")]
    [DataRow("Configuration")]
    public void ContextualKeywordsAsColumnNames_RoundTrip(string columnName)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t ( {columnName} int )");
        _ = simulation.ExecuteNonQuery($"insert t ({columnName}) values (42)");

        using var reader = simulation
            .CreateCommand($"select {columnName} from t where {columnName} = 42")
            .ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(42, reader.GetInt32(0));
    }

    [TestMethod]
    public void CreateTable_DuplicateName_RaisesMsg2714()
    {
        new Simulation().AssertSqlError("""
            create table dup (a int);
            create table dup (a int)
            """, 2714, "There is already an object named 'dup' in the database.");
    }

    [TestMethod]
    public void InlineTableLevelIndex_CreatesIndexVisibleInSysIndexes()
        => Assert.AreEqual("ix", new Simulation().ExecuteScalar("""
            create table zz (id int, INDEX ix (id));
            select name from sys.indexes where object_id = object_id('zz') and name = 'ix'
            """));

    [TestMethod]
    public void InlineColumnLevelIndex_Nonclustered_CreatesIndex()
        => Assert.AreEqual("ix", new Simulation().ExecuteScalar("""
            create table zz (id int, name varchar(10) INDEX ix NONCLUSTERED);
            select name from sys.indexes where object_id = object_id('zz') and name = 'ix'
            """));

    [TestMethod]
    public void InlineColumnLevelIndex_AlongsidePrimaryKey_BothCreated()
        => Assert.AreEqual(2, new Simulation().ExecuteScalar("""
            create table zz (id int primary key nonclustered, a int INDEX ixa);
            select count(*) from sys.indexes where object_id = object_id('zz') and name is not null
            """));

    [TestMethod]
    public void InlineTableLevelIndex_MultiColumn_CreatesIndexOverBothKeys()
        => Assert.AreEqual(2, new Simulation().ExecuteScalar("""
            create table zz (a int, b int, INDEX ix (a, b desc));
            select count(*) from sys.index_columns where object_id = object_id('zz')
                and index_id = (select index_id from sys.indexes where object_id = object_id('zz') and name = 'ix')
            """));

    [TestMethod]
    public void InlineIndex_UnknownColumn_RaisesAndRollsBack()
        => new Simulation().AssertSqlError("create table zz (id int, INDEX ix (nope))", 1911);

    [TestMethod]
    public void CreateTable_TempNameOver116Characters_RaisesMsg193()
        => _ = new Simulation().AssertSqlError($"create table #{new string('t', 116)} (a int)", 193);

    [TestMethod]
    public void CreateTable_CollateOnANonStringColumn_RaisesMsg447()
        => new Simulation().AssertSqlError("create table t (b int collate Latin1_General_CS_AS)", 447, "Expression type int is invalid for COLLATE clause.");

    /// <summary>
    /// An unnamed CHECK, DEFAULT or FOREIGN KEY is thirty characters at most:
    /// with a column the table and column share fourteen, the table keeping at
    /// least nine; without one the table keeps sixteen (probed 2026-10-01
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("create table abcdefghijklmnopqrstuvwxyz (abcdefghijklmnopqrstuvwxyz int default 1)", "DF__abcdefghi__abcde__")]
    [DataRow("create table abcdefghijklmnopqrstuvwxyz (x int check (x > 0))", "CK__abcdefghijklm__x__")]
    [DataRow("create table t (abcdefghijklmnopqrstuvwxyz int default 1)", "DF__t__abcdefghijklm__")]
    [DataRow("create table tt123456 (col1234567 int default 1)", "DF__tt123456__col123__")]
    [DataRow("create table abcdefghijklmnopqrstuvwxyz (a int, b int, check (a < b))", "CK__abcdefghijklmnop__")]
    public void CreateTable_AutoConstraintNameLengths(string sql, string prefix)
    {
        var name = (string?)new Simulation().ExecuteScalar($"{sql}; select name from sys.objects where type in ('C', 'D')");
        Assert.AreEqual(prefix, name![..^8]);
    }
}
