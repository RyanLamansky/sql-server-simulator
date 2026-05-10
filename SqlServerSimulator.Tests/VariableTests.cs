using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

[TestClass]
public sealed class VariableTests
{
    [TestMethod]
    public void Declare_NoInit_VariableIsNull()
        => AreEqual(DBNull.Value, ExecuteScalar("declare @v int; select @v"));

    [TestMethod]
    public void Declare_WithInit_HoldsValue()
        => AreEqual(7, ExecuteScalar<int>("declare @v int = 7; select @v"));

    [TestMethod]
    public void Declare_AsKeywordOptional()
        => AreEqual(1, ExecuteScalar<int>("declare @v as int = 1; select @v"));

    [TestMethod]
    public void Declare_MultiVariable_AllReadable()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var cmd = conn.CreateCommand("declare @v int = 7, @w varchar(5) = 'abc'; select @v, @w");
        using var reader = cmd.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(7, reader.GetInt32(0));
        AreEqual("abc", reader.GetString(1));
    }

    [TestMethod]
    public void Set_Assigns()
        => AreEqual(42, ExecuteScalar<int>("declare @v int; set @v = 42; select @v"));

    [TestMethod]
    public void Set_StringToInt_Coerces()
        => AreEqual(42, ExecuteScalar<int>("declare @v int; set @v = '42'; select @v"));

    [TestMethod]
    public void Set_BadStringToInt_RaisesMsg245()
        => AssertSqlError("declare @v int; set @v = 'abc'; select @v", 245,
            "Conversion failed when converting the varchar value 'abc' to data type int.");

    [TestMethod]
    public void Set_StringTruncatesToVarcharLength()
        => AreEqual("hel", ExecuteScalar("declare @v varchar(3); set @v = 'hello'; select @v"));

    [TestMethod]
    public void Set_DecimalToInt_Truncates()
        => AreEqual(3, ExecuteScalar<int>("declare @v int; set @v = 3.7; select @v"));

    [TestMethod]
    public void Set_ScalarSubquery_Assigns()
        => AreEqual(42, ExecuteScalar<int>("declare @v int; set @v = (select 42); select @v"));

    [TestMethod]
    public void Set_MultiRowSubquery_RaisesMsg512()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int); insert t values (1),(2)");
        _ = sim.AssertSqlError("declare @v int; set @v = (select id from t); select @v", 512);
    }

    [TestMethod]
    public void Set_EmptySubquery_AssignsNull()
        => AreEqual(DBNull.Value, ExecuteScalar(
            "declare @v int = 99; set @v = (select 1 where 1 = 0); select @v"));

    [TestMethod]
    public void SelectAssign_Single()
        => AreEqual(42, ExecuteScalar<int>("declare @v int; select @v = 42; select @v"));

    [TestMethod]
    public void SelectAssign_LastRowWins()
    {
        using var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("create table t (id int); insert t values (1),(2),(3)").ExecuteNonQuery();
        AreEqual(3, conn.CreateCommand(
            "declare @v int; select @v = id from t order by id; select @v").ExecuteScalar());
    }

    [TestMethod]
    public void SelectAssign_EmptyResult_KeepsPriorValue()
    {
        using var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("create table t (id int); insert t values (1)").ExecuteNonQuery();
        AreEqual(99, conn.CreateCommand(
            "declare @v int = 99; select @v = id from t where id = 0; select @v").ExecuteScalar());
    }

    [TestMethod]
    public void SelectAssign_NullResult_AssignsNull()
        => AreEqual(DBNull.Value, ExecuteScalar(
            "declare @v int = 99; select @v = case when 1=0 then 1 else null end; select @v"));

    [TestMethod]
    public void SelectAssign_MultiVariable()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var cmd = conn.CreateCommand(
            "declare @v int, @w varchar(10); select @v = 1, @w = 'x'; select @v, @w");
        using var reader = cmd.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual("x", reader.GetString(1));
    }

    [TestMethod]
    public void SelectAssign_MultiVariableLastRowWins()
    {
        using var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("create table t (a int, b int); insert t values (1, 2), (3, 4)").ExecuteNonQuery();
        using var cmd = conn.CreateCommand(
            "declare @v int, @w int; select @v = a, @w = b from t order by a; select @v, @w");
        using var reader = cmd.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(3, reader.GetInt32(0));
        AreEqual(4, reader.GetInt32(1));
    }

    [TestMethod]
    public void SelectAssign_MixedWithProjection_RaisesMsg141()
        => AssertSqlError("declare @v int; select @v = 1, 2", 141,
            "A SELECT statement that assigns a value to a variable must not be combined with data-retrieval operations.");

    [TestMethod]
    public void Reference_BeforeDeclare_RaisesMsg137()
        => AssertSqlError("select @x", 137, "Must declare the scalar variable \"@x\".");

    [TestMethod]
    public void Declare_Duplicate_RaisesMsg134()
        => AssertSqlError("declare @v int; declare @v int; select @v", 134,
            "The variable name '@v' has already been declared. Variable names must be unique within a query batch or stored procedure.");

    [TestMethod]
    public void Declare_NameCollidesWithParameter_RaisesMsg134()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var cmd = conn.CreateCommand("declare @x int = 99; select @x", ("@x", 1));
        var ex = Throws<DbException>(cmd.ExecuteScalar);
        AreEqual("134", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void ReservedKeywordAsVariableName_Works()
        => AreEqual(1, ExecuteScalar<int>("declare @select int = 1; select @select"));

    [TestMethod]
    public void Declare_SelfReference_RaisesMsg137()
        => AssertSqlError("declare @v int = @v + 1; select @v", 137);

    [TestMethod]
    public void Declare_MultipleStatementsViaSemicolon()
        => AreEqual(3, ExecuteScalar<int>("declare @x int = 1; declare @y int = 2; select @x + @y"));

    [TestMethod]
    public void Variable_InWhereClause_FiltersRows()
        => IsNull(ExecuteScalar("declare @x int = 5; select 1 where 1 = @x"));

    [TestMethod]
    public void RowCount_AfterSelect()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var cmd = conn.CreateCommand(
            "select 1 union select 2 union select 3; select @@rowcount as rc");
        using var reader = cmd.ExecuteReader();
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(3, reader.GetInt32(0));
    }

    [TestMethod]
    public void RowCount_AfterDeclareWithInit_IsOne()
        => AreEqual(1, ExecuteScalar<int>(
            "declare @v int = 99; select @@rowcount"));

    [TestMethod]
    public void RowCount_AfterBareDeclare_PreservedFromPriorStatement()
    {
        // INSERT sets @@ROWCOUNT to 3; bare DECLARE without init does NOT reset.
        using var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("create table t (id int)").ExecuteNonQuery();
        using var cmd = conn.CreateCommand(
            "insert t values (1),(2),(3); declare @v int; select @@rowcount");
        AreEqual(3, cmd.ExecuteScalar());
    }

    [TestMethod]
    public void RowCount_AfterSet_IsOne()
        => AreEqual(1, ExecuteScalar<int>("declare @v int; set @v = 42; select @@rowcount"));

    [TestMethod]
    public void OutputParameter_WrittenBackAfterBatch()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var cmd = conn.CreateCommand("set @x = 999");
        var p = cmd.CreateParameter();
        p.ParameterName = "@x";
        p.DbType = System.Data.DbType.Int32;
        p.Direction = System.Data.ParameterDirection.InputOutput;
        p.Value = 5;
        _ = cmd.Parameters.Add(p);
        _ = cmd.ExecuteNonQuery();
        AreEqual(999, p.Value);
    }

    [TestMethod]
    public void InputParameter_NotWrittenBack()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var cmd = conn.CreateCommand("set @x = 999");
        var p = cmd.CreateParameter();
        p.ParameterName = "@x";
        p.DbType = System.Data.DbType.Int32;
        p.Direction = System.Data.ParameterDirection.Input;
        p.Value = 5;
        _ = cmd.Parameters.Add(p);
        _ = cmd.ExecuteNonQuery();
        AreEqual(5, p.Value);
    }
}
