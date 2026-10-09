using System.Data;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public class ParameterTests
{
    [TestMethod]
    [DataRow(DbType.Boolean, true)]
    [DataRow(DbType.Boolean, false)]
    [DataRow(DbType.Byte, (byte)0)]
    [DataRow(DbType.Byte, (byte)200)]
    [DataRow(DbType.Int16, (short)-1)]
    [DataRow(DbType.Int16, (short)32000)]
    [DataRow(DbType.AnsiString, "ansi")]
    [DataRow(DbType.String, "unicodeé")]
    public void TypedParameter_RoundTrips(DbType dbType, object value)
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "select @p";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "p";
        parameter.DbType = dbType;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);

        AreEqual(value, command.ExecuteScalar());
    }

    [TestMethod]
    [DataRow(DbType.Boolean)]
    [DataRow(DbType.Byte)]
    [DataRow(DbType.Int16)]
    [DataRow(DbType.AnsiString)]
    [DataRow(DbType.String)]
    public void TypedNullParameter_ReturnsDBNull(DbType dbType)
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "select @p";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "p";
        parameter.DbType = dbType;
        parameter.Value = DBNull.Value;
        _ = command.Parameters.Add(parameter);

        AreEqual(DBNull.Value, command.ExecuteScalar());
    }

    private static SimulatedDbParameter Unsupplied(string name, DbType dbType, int size = 0, ParameterDirection direction = ParameterDirection.Input) =>
        new() { ParameterName = name, DbType = dbType, Size = size, Direction = direction, Value = null };

    [TestMethod]
    [DataRow(DbType.Int32, 0, "(@a int)select @a")]
    [DataRow(DbType.String, 0, "(@a nvarchar(4000))select @a")]
    [DataRow(DbType.String, 50, "(@a nvarchar(50))select @a")]
    [DataRow(DbType.String, -1, "(@a nvarchar(max) )select @a")]
    [DataRow(DbType.String, 5000, "(@a nvarchar(max) )select @a")]
    [DataRow(DbType.AnsiString, 20, "(@a varchar(20))select @a")]
    [DataRow(DbType.AnsiStringFixedLength, 0, "(@a char(8000))select @a")]
    [DataRow(DbType.Binary, -1, "(@a varbinary(max) )select @a")]
    [DataRow(DbType.Decimal, 0, "(@a decimal(29,0))select @a")]
    [DataRow(DbType.DateTime2, 0, "(@a datetime2(7))select @a")]
    [DataRow(DbType.Currency, 0, "(@a money)select @a")]
    [DataRow(DbType.Object, 0, "(@a sql_variant)select @a")]
    public void CSharpNullValue_IsNotSupplied(DbType dbType, int size, string query)
    {
        using var command = new Simulation().CreateOpenConnection().CreateCommand();
        command.CommandText = "select @a";
        _ = command.Parameters.Add(Unsupplied("a", dbType, size));

        var error = Throws<SimulatedSqlException>(() => _ = command.ExecuteScalar());
        AreEqual(8178, error.Number);
        AreEqual(0, error.LineNumber);
        AreEqual($"The parameterized query '{query}' expects the parameter '@a', which was not supplied.", error.Message);
    }

    [TestMethod]
    public void CSharpNullValue_NamesTheFirstUnsupplied_InDeclarationOrder()
    {
        using var command = new Simulation().CreateOpenConnection().CreateCommand();
        command.CommandText = "select 1";
        _ = command.Parameters.Add(Unsupplied("@o", DbType.Int32, direction: ParameterDirection.Output));
        _ = command.Parameters.Add(new SimulatedDbParameter { ParameterName = "@n", Value = DBNull.Value, DbType = DbType.Int32 });
        _ = command.Parameters.Add(Unsupplied("@b", DbType.Int32, direction: ParameterDirection.InputOutput));
        _ = command.Parameters.Add(Unsupplied("@r", DbType.Int32, direction: ParameterDirection.ReturnValue));
        _ = command.Parameters.Add(Unsupplied("@c", DbType.Int32));

        AreEqual(
            "The parameterized query '(@o int output,@n int,@b int output,@c int)select 1' expects the parameter '@b', which was not supplied.",
            Throws<SimulatedSqlException>(() => _ = command.ExecuteNonQuery()).Message);
    }

    [TestMethod]
    public void CSharpNullValue_QuotesTheFirst64Characters()
    {
        using var command = new Simulation().CreateOpenConnection().CreateCommand();
        command.CommandText = "select 1 /*" + new string('x', 100) + "*/";
        _ = command.Parameters.Add(Unsupplied("a", DbType.Int32));

        AreEqual(
            "The parameterized query '(@a int)select 1 /*" + new string('x', 45) + "' expects the parameter '@a', which was not supplied.",
            Throws<SimulatedSqlException>(() => _ = command.ExecuteNonQuery()).Message);
    }

    [TestMethod]
    public void CSharpNullValue_OutputOnly_IsSuppliedAsNull()
    {
        using var command = new Simulation().CreateOpenConnection().CreateCommand();
        command.CommandText = "select @o = isnull(@o, 0) + 1";
        var output = Unsupplied("o", DbType.Int32, direction: ParameterDirection.Output);
        _ = command.Parameters.Add(output);

        _ = command.ExecuteNonQuery();
        AreEqual(1, output.Value);
    }

    [TestMethod]
    public void CSharpNullValue_StoredProcedure_TakesItsDefaultOrRaises201()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create procedure zzd @x int = 7 output as begin select @x; set @x = 9 end",
            "create procedure zznpp @x int as select @x");
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "zzd";
        _ = command.Parameters.Add(Unsupplied("@x", DbType.Int32));
        AreEqual(7, command.ExecuteScalar());

        command.CommandText = "zzd";
        var inOut = Unsupplied("@x", DbType.Int32, direction: ParameterDirection.InputOutput);
        command.Parameters.Clear();
        _ = command.Parameters.Add(inOut);
        AreEqual(7, command.ExecuteScalar());
        AreEqual(9, inOut.Value);

        command.CommandText = "zznpp";
        command.Parameters.Clear();
        _ = command.Parameters.Add(Unsupplied("@x", DbType.Int32));
        var error = Throws<SimulatedSqlException>(() => _ = command.ExecuteScalar());
        AreEqual(201, error.Number);
        AreEqual(4, error.State);
        AreEqual(0, error.LineNumber);
        AreEqual("zznpp", error.Procedure);
        AreEqual("Procedure or function 'zznpp' expects parameter '@x', which was not supplied.", error.Message);
    }

    [TestMethod]
    public void ParameterArithmetic()
    {
        var result = new Simulation()
            .CreateOpenConnection()
            .CreateCommand("select @p0 + 1", ("p0", 41))
            .ExecuteScalar();

        AreEqual(42, result);
    }

    [TestMethod]
    public void DbType_NullValue_DefaultsToString()
    {
        using var cmd = new Simulation().CreateOpenConnection().CreateCommand();
        var p = cmd.CreateParameter();
        p.Value = null;
        AreEqual(DbType.String, p.DbType);
    }

    [TestMethod]
    public void DbType_UnsupportedValueType_ThrowsArgumentException()
    {
        using var cmd = new Simulation().CreateOpenConnection().CreateCommand();
        var p = cmd.CreateParameter();
        p.Value = new Dictionary<string, string>();
        _ = Throws<ArgumentException>(() => _ = p.DbType);
    }

    [TestMethod]
    public void SourceColumn_DefaultsToEmptyAndRoundTrips()
    {
        using var cmd = new Simulation().CreateOpenConnection().CreateCommand();
        var p = cmd.CreateParameter();
        AreEqual("", p.SourceColumn);
        p.SourceColumn = "x";
        AreEqual("x", p.SourceColumn);
    }

    [TestMethod]
    public void SourceColumnNullMapping_DefaultsToFalseAndRoundTrips()
    {
        using var cmd = new Simulation().CreateOpenConnection().CreateCommand();
        var p = cmd.CreateParameter();
        IsFalse(p.SourceColumnNullMapping);
        p.SourceColumnNullMapping = true;
        IsTrue(p.SourceColumnNullMapping);
    }

    [TestMethod]
    public void ResetDbType_ClearsExplicitOverride()
    {
        using var cmd = new Simulation().CreateOpenConnection().CreateCommand();
        var p = cmd.CreateParameter();
        p.Value = 1;
        p.DbType = DbType.Int64;
        AreEqual(DbType.Int64, p.DbType);
        p.ResetDbType();
        AreEqual(DbType.Int32, p.DbType);
    }
}
