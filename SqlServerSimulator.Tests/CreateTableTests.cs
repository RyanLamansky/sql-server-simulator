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
    {
        // 2016 int columns × 4 bytes = 8064 bytes, beyond 8060-byte in-row record size — Msg 1701.
        var columns = string.Join(", ", Enumerable.Range(0, 2016).Select(i => $"c{i} int"));
        var ex = Assert.Throws<System.Data.Common.DbException>(() => new Simulation().ExecuteNonQuery($"create table t ( {columns} )"));
        Assert.Contains("row size", ex.Message);
        Assert.Contains("8060", ex.Message);
    }

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
    {
        // 2015 int columns × 4 bytes = 8060 bytes — exactly at the limit.
        var columns = string.Join(", ", Enumerable.Range(0, 2015).Select(i => $"c{i} int"));
        Assert.AreEqual(-1, new Simulation().ExecuteNonQuery($"create table t ( {columns} )"));
    }

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
}
