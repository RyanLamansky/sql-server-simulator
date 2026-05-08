using System.Data.Common;

namespace SqlServerSimulator;

[TestClass]
public class CreateTableTests
{
    [TestMethod]
    public void CreateTableMinimal()
    {
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand("create table t ( v int )");

        connection.Open();
        Assert.AreEqual(-1, command.ExecuteNonQuery());
    }

    [TestMethod]
    public void InvalidTypeName()
    {
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand("create table t ( v intz )");

        connection.Open();
        var x = Assert.Throws<DbException>(() => command.ExecuteNonQuery());
        Assert.AreEqual("Column, parameter, or variable #1: Cannot find data type intz.", x.Message);
    }

    [TestMethod]
    public void CreateTableNull()
    {
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand("create table t ( v int null )");

        connection.Open();
        Assert.AreEqual(-1, command.ExecuteNonQuery());
    }

    [TestMethod]
    public void CreateTableNotNull()
    {
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand("create table t ( v int not null )");

        connection.Open();
        Assert.AreEqual(-1, command.ExecuteNonQuery());
    }

    [TestMethod]
    [DataRow("varchar(50)")]
    [DataRow("VARCHAR(50)")]
    [DataRow("nvarchar(50)")]
    [DataRow("NVARCHAR(50)")]
    public void CreateTableVarcharWithLength(string typeSpec)
    {
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand($"create table t ( v {typeSpec} )");

        connection.Open();
        Assert.AreEqual(-1, command.ExecuteNonQuery());
    }

    [TestMethod]
    public void CreateTableVarcharWithLengthAndNullability()
    {
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand("create table t ( v varchar(50) not null )");

        connection.Open();
        Assert.AreEqual(-1, command.ExecuteNonQuery());
    }

    [TestMethod]
    public void CreateTableVarcharWithoutLengthDefaultsToOne()
    {
        // SQL Server treats `varchar` (no parens) as `varchar(1)` with a warning;
        // the simulator accepts the same form silently for fidelity.
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand("create table t ( v varchar )");

        connection.Open();
        Assert.AreEqual(-1, command.ExecuteNonQuery());
    }

    [TestMethod]
    public void CreateTableVarcharMaxAccepted()
    {
        // varchar(MAX) declares a LOB-eligible column; the row encoder routes
        // values that don't fit inline through the table's LOB-chain pages.
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand("create table t ( v varchar(max) )");

        connection.Open();
        Assert.AreEqual(-1, command.ExecuteNonQuery());
    }

    [TestMethod]
    public void CreateTableVarcharSizeExceedsMaximum()
    {
        // Msg 131 in column-declaration form: SQL Server names the column,
        // not the type, and adds the "for any data type" suffix.
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand("create table t ( v varchar(8001) )");

        connection.Open();
        var x = Assert.Throws<DbException>(() => command.ExecuteNonQuery());
        Assert.AreEqual("The size (8001) given to the column 'v' exceeds the maximum allowed for any data type (8000).", x.Message);
    }

    [TestMethod]
    public void CreateTableNVarcharSizeExceedsMaximum()
    {
        // nvarchar takes a separate error path: Msg 2717 with "parameter"
        // wording even though it's a column, no "for any data type" suffix.
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand("create table t ( v nvarchar(4001) )");

        connection.Open();
        var x = Assert.Throws<DbException>(() => command.ExecuteNonQuery());
        Assert.AreEqual("The size (4001) given to the parameter 'v' exceeds the maximum allowed (4000).", x.Message);
    }

    [TestMethod]
    public void CreateTableLengthOnNonLengthType()
    {
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand("create table t ( v int(4) )");

        connection.Open();
        var x = Assert.Throws<DbException>(() => command.ExecuteNonQuery());
        Assert.AreEqual("Column, parameter, or variable #1: Cannot specify a column width on data type int.", x.Message);
    }

    [TestMethod]
    public void CreateTableFixedWidthSumExceedsRowSizeMax()
    {
        // 2016 bigint columns × 8 bytes = 16128 bytes of fixed-width data, far
        // beyond SQL Server's 8060-byte in-row record size; CREATE TABLE must
        // refuse this schema (Msg 1701).
        var simulation = new Simulation();

        var columns = string.Join(", ", Enumerable.Range(0, 2016).Select(i => $"c{i} int"));
        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand($"create table t ( {columns} )");

        connection.Open();
        var x = Assert.Throws<DbException>(() => command.ExecuteNonQuery());
        Assert.Contains("row size", x.Message);
        Assert.Contains("8060", x.Message);
    }

    [TestMethod]
    [DataRow("varbinary(50)")]
    [DataRow("VARBINARY(8000)")]
    public void CreateTableVarbinaryWithLength(string typeSpec)
    {
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand($"create table t ( v {typeSpec} )");

        connection.Open();
        Assert.AreEqual(-1, command.ExecuteNonQuery());
    }

    [TestMethod]
    public void CreateTableVarbinaryMaxAccepted()
    {
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand("create table t ( v varbinary(max) )");

        connection.Open();
        Assert.AreEqual(-1, command.ExecuteNonQuery());
    }

    [TestMethod]
    public void CreateTableVarbinarySizeExceedsMaximum()
    {
        // varbinary follows the same Msg 131 column form as varchar.
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand("create table t ( v varbinary(8001) )");

        connection.Open();
        var x = Assert.Throws<DbException>(() => command.ExecuteNonQuery());
        Assert.AreEqual("The size (8001) given to the column 'v' exceeds the maximum allowed for any data type (8000).", x.Message);
    }

    [TestMethod]
    public void CreateTableFixedWidthSumAtRowSizeMax()
    {
        // 2015 int columns × 4 bytes = 8060 bytes of fixed-width data: exactly
        // at the limit, which should succeed.
        var simulation = new Simulation();

        var columns = string.Join(", ", Enumerable.Range(0, 2015).Select(i => $"c{i} int"));
        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand($"create table t ( {columns} )");

        connection.Open();
        Assert.AreEqual(-1, command.ExecuteNonQuery());
    }

    [TestMethod]
    [DataRow("date")]
    [DataRow("DATE")]
    [DataRow("Date")]
    public void CreateTableDate(string typeSpec)
    {
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand($"create table t ( v {typeSpec} )");

        connection.Open();
        Assert.AreEqual(-1, command.ExecuteNonQuery());
    }

    [TestMethod]
    public void CreateTableDateRejectsLengthSpecifier()
    {
        // date is fixed-length; SQL Server rejects date(N) with Msg 2716.
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand("create table t ( v date(3) )");

        connection.Open();
        var x = Assert.Throws<DbException>(() => command.ExecuteNonQuery());
        Assert.AreEqual("Column, parameter, or variable #1: Cannot specify a column width on data type date.", x.Message);
    }

    [TestMethod]
    [DataRow("datetime2")]              // default precision = 7
    [DataRow("DATETIME2")]
    [DataRow("datetime2(0)")]
    [DataRow("datetime2(3)")]
    [DataRow("datetime2(7)")]
    public void CreateTableDateTime2(string typeSpec)
    {
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand($"create table t ( v {typeSpec} )");

        connection.Open();
        Assert.AreEqual(-1, command.ExecuteNonQuery());
    }

    [TestMethod]
    [DataRow(8)]
    [DataRow(99)]
    public void CreateTableDateTime2_PrecisionOutOfRange(int precision)
    {
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand($"create table t ( v datetime2({precision}) )");

        connection.Open();
        var x = Assert.Throws<DbException>(() => command.ExecuteNonQuery());
        Assert.AreEqual($"Line 1: Specified scale {precision} is invalid.", x.Message);
    }

    [TestMethod]
    [DataRow("time")]                   // default precision = 7
    [DataRow("TIME")]
    [DataRow("time(0)")]
    [DataRow("time(3)")]
    [DataRow("time(7)")]
    public void CreateTableTime(string typeSpec)
    {
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand($"create table t ( v {typeSpec} )");

        connection.Open();
        Assert.AreEqual(-1, command.ExecuteNonQuery());
    }

    [TestMethod]
    [DataRow(8)]
    [DataRow(99)]
    public void CreateTableTime_PrecisionOutOfRange(int precision)
    {
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand($"create table t ( v time({precision}) )");

        connection.Open();
        var x = Assert.Throws<DbException>(() => command.ExecuteNonQuery());
        Assert.AreEqual($"Line 1: Specified scale {precision} is invalid.", x.Message);
    }

    [TestMethod]
    [DataRow("datetimeoffset")]         // default precision = 7
    [DataRow("DATETIMEOFFSET")]
    [DataRow("datetimeoffset(0)")]
    [DataRow("datetimeoffset(3)")]
    [DataRow("datetimeoffset(7)")]
    public void CreateTableDateTimeOffset(string typeSpec)
    {
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand($"create table t ( v {typeSpec} )");

        connection.Open();
        Assert.AreEqual(-1, command.ExecuteNonQuery());
    }

    [TestMethod]
    [DataRow(8)]
    [DataRow(99)]
    public void CreateTableDateTimeOffset_PrecisionOutOfRange(int precision)
    {
        var simulation = new Simulation();

        using var connection = simulation.CreateDbConnection();
        using var command = connection.CreateCommand($"create table t ( v datetimeoffset({precision}) )");

        connection.Open();
        var x = Assert.Throws<DbException>(() => command.ExecuteNonQuery());
        Assert.AreEqual($"Line 1: Specified scale {precision} is invalid.", x.Message);
    }

    /// <summary>
    /// Identifiers that match contextual keywords (the parser-side enum
    /// covering OUTPUT, USING, MATCHED, MAX, etc.) must still work as column
    /// names — the architecture classifies contextual keywords at parse time
    /// in keyword-expecting positions only, so identifier positions are
    /// unaffected. <c>Output</c>, <c>Using</c>, <c>Matched</c>, and <c>Max</c>
    /// are the most likely real-world collisions.
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
        _ = simulation.ExecuteNonQuery($"insert into t ({columnName}) values (42)");

        using var reader = simulation
            .CreateCommand($"select {columnName} from t where {columnName} = 42")
            .ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(42, reader.GetInt32(0));
    }

    [TestMethod]
    public void CreateTable_DuplicateName_RaisesMsg2714()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table dup (a int)");
        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("create table dup (a int)"));
        Assert.AreEqual("There is already an object named 'dup' in the database.", ex.Message);
        Assert.AreEqual("2714", ex.Data["HelpLink.EvtID"]);
    }
}
