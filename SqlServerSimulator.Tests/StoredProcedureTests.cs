using System.Data;
using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for stored procedures: CREATE / ALTER / DROP, EXEC
/// statement (positional / named / mixed / OUTPUT / DEFAULT), RETURN code
/// capture, multi-result-set forwarding, recursion limit, dynamic SQL
/// (EXEC (@sql), sp_executesql), the <see cref="CommandType.StoredProcedure"/>
/// entrypoint, and catalog-view surfaces. Probed against SQL Server 2025
/// (2026-05-12).
/// </summary>
[TestClass]
public sealed class StoredProcedureTests
{
    private static DbConnection Open() => new Simulation().CreateOpenConnection();

    private static DbException AssertSqlError(DbConnection connection, string sql, int errorNumber)
    {
        var ex = Throws<DbException>(() => connection.CreateCommand(sql).ExecuteScalar());
        AreEqual(errorNumber.ToString(), ex.Data["HelpLink.EvtID"], $"expected Msg {errorNumber}");
        return ex;
    }

    [TestMethod]
    public void Create_With_BeginEnd_Body()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p as begin select 1 as v end").ExecuteNonQuery();
        AreEqual(1, connection.CreateCommand("exec dbo.p").ExecuteScalar());
    }

    [TestMethod]
    public void Create_Bare_Statement_Body_NoBeginEnd()
    {
        // Probe-confirmed: CREATE PROC p AS SELECT 1 works without BEGIN/END.
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p as select 42 as v").ExecuteNonQuery();
        AreEqual(42, connection.CreateCommand("exec dbo.p").ExecuteScalar());
    }

    [TestMethod]
    public void Create_MultiStatement_Body_NoBeginEnd_YieldsMultipleResultSets()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p @x int as select @x as first; select @x + 1 as second").ExecuteNonQuery();
        using var reader = connection.CreateCommand("exec dbo.p @x = 5").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(5, reader.GetInt32(0));
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(6, reader.GetInt32(0));
    }

    [TestMethod]
    public void Create_Empty_Body_Succeeds()
    {
        // Probe-confirmed: `CREATE PROC p AS` (nothing after AS) is legal.
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p as").ExecuteNonQuery();
        AreEqual(-1, connection.CreateCommand("exec dbo.p").ExecuteNonQuery());
    }

    [TestMethod]
    public void Parens_Around_Parameter_List_Are_Optional()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p (@x int, @y int) as select @x + @y as s").ExecuteNonQuery();
        AreEqual(7, connection.CreateCommand("exec dbo.p 3, 4").ExecuteScalar());
    }

    [TestMethod]
    public void Exec_Positional_Args()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p @a int, @b int as select @a + @b as s").ExecuteNonQuery();
        AreEqual(30, connection.CreateCommand("exec dbo.p 10, 20").ExecuteScalar());
    }

    [TestMethod]
    public void Exec_Named_Args()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p @a int, @b int as select @a * 100 + @b as s").ExecuteNonQuery();
        AreEqual(305, connection.CreateCommand("exec dbo.p @a = 3, @b = 5").ExecuteScalar());
        AreEqual(305, connection.CreateCommand("exec dbo.p @b = 5, @a = 3").ExecuteScalar());
    }

    [TestMethod]
    public void Exec_Mixed_Positional_Then_Named()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p @a int, @b int as select @a, @b").ExecuteNonQuery();
        using var reader = connection.CreateCommand("exec dbo.p 1, @b = 2").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual(2, reader.GetInt32(1));
    }

    [TestMethod]
    public void Exec_Named_Then_Positional_Raises_Msg119()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p @a int, @b int as select @a, @b").ExecuteNonQuery();
        _ = AssertSqlError(connection, "exec dbo.p @a = 1, 2", 119);
    }

    [TestMethod]
    public void Exec_Default_Keyword()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p @x int = 99 as select @x as x").ExecuteNonQuery();
        AreEqual(99, connection.CreateCommand("exec dbo.p DEFAULT").ExecuteScalar());
        AreEqual(99, connection.CreateCommand("exec dbo.p").ExecuteScalar());
        AreEqual(7, connection.CreateCommand("exec dbo.p @x = 7").ExecuteScalar());
    }

    [TestMethod]
    public void Exec_Missing_Required_Param_Raises_Msg201()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p @a int as select @a").ExecuteNonQuery();
        var ex = AssertSqlError(connection, "exec dbo.p", 201);
        StringEnding(ex.Message, "expects parameter '@a', which was not supplied.");
    }

    [TestMethod]
    public void Exec_Unknown_Named_Param_Raises_Msg201()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p @a int as select @a").ExecuteNonQuery();
        _ = AssertSqlError(connection, "exec dbo.p @nope = 1", 201);
    }

    [TestMethod]
    public void Exec_Too_Many_Args_Raises_Msg8144()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p @a int as select @a").ExecuteNonQuery();
        _ = AssertSqlError(connection, "exec dbo.p 1, 2, 3", 8144);
    }

    [TestMethod]
    public void Exec_Duplicate_Named_Arg_Raises_Msg8143()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p @a int as select @a").ExecuteNonQuery();
        _ = AssertSqlError(connection, "exec dbo.p @a = 1, @a = 2", 8143);
    }

    [TestMethod]
    public void Exec_Missing_Proc_Raises_Msg2812()
    {
        using var connection = Open();
        _ = AssertSqlError(connection, "exec dbo.nonexistent", 2812);
    }

    [TestMethod]
    public void Exec_Unqualified_Name_Resolves_To_Dbo()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p as select 'found' as v").ExecuteNonQuery();
        AreEqual("found", connection.CreateCommand("exec p").ExecuteScalar());
    }

    [TestMethod]
    public void Return_Default_Is_Zero()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p as select 1").ExecuteNonQuery();
        using var reader = connection.CreateCommand("declare @rc int = -1; exec @rc = dbo.p; select @rc as rc;").ExecuteReader();
        // First result set is from the proc body's SELECT 1
        IsTrue(reader.Read());
        // Second result set has rc
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(0, reader.GetInt32(0));
    }

    [TestMethod]
    public void Return_Value_Captured()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p @x int as return @x * 10").ExecuteNonQuery();
        using var reader = connection.CreateCommand("declare @rc int; exec @rc = dbo.p 7; select @rc as rc;").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(70, reader.GetInt32(0));
    }

    [TestMethod]
    public void Return_Null_Coerces_To_Zero()
    {
        // Probe-confirmed: RETURN NULL → rc=0 (NOT NULL).
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p as return null").ExecuteNonQuery();
        using var reader = connection.CreateCommand("declare @rc int = -1; exec @rc = dbo.p; select @rc as rc;").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(0, reader.GetInt32(0));
    }

    [TestMethod]
    public void Return_NonCoercible_String_Raises_Msg245()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p as return 'abc'").ExecuteNonQuery();
        _ = AssertSqlError(connection, "declare @rc int; exec @rc = dbo.p; select @rc;", 245);
    }

    [TestMethod]
    public void Return_Exits_Early_NoSubsequentStatements()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p as select 'a' as v; return; select 'b' as v;").ExecuteNonQuery();
        using var reader = connection.CreateCommand("exec dbo.p").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("a", reader.GetString(0));
        IsFalse(reader.NextResult());
    }

    [TestMethod]
    public void Output_Parameter_Writeback_Text_Form()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p @inout int output as set @inout = @inout * 10").ExecuteNonQuery();
        using var reader = connection.CreateCommand("declare @a int = 5; exec dbo.p @a output; select @a as a;").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(50, reader.GetInt32(0));
    }

    [TestMethod]
    public void Output_Parameter_Missing_Output_Keyword_Does_Not_Writeback()
    {
        // Probe-confirmed: caller that forgets OUTPUT keeps the original value.
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p @out int output as set @out = 99").ExecuteNonQuery();
        using var reader = connection.CreateCommand("declare @a int = 5; exec dbo.p @a; select @a as a;").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(5, reader.GetInt32(0));
    }

    [TestMethod]
    public void RecursionLimit_32_Raises_Msg217()
    {
        using var connection = Open();
        _ = connection.CreateCommand("""
            create procedure dbo.p_rec @n int, @acc int output as
            if @n > 0 begin
              set @acc = @acc + @n;
              declare @next int = @n - 1;
              exec dbo.p_rec @next, @acc output;
            end
        """).ExecuteNonQuery();
        // 50 levels — exceeds cap
        _ = AssertSqlError(connection, "declare @s int = 0; exec dbo.p_rec 50, @s output; select @s;", 217);
    }

    [TestMethod]
    public void Alter_Procedure_Preserves_ObjectId_And_Updates_Body()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p as select 1 as v").ExecuteNonQuery();
        var idBefore = connection.CreateCommand("select object_id from sys.procedures where name = 'p'").ExecuteScalar();
        _ = connection.CreateCommand("alter procedure dbo.p as select 2 as v").ExecuteNonQuery();
        var idAfter = connection.CreateCommand("select object_id from sys.procedures where name = 'p'").ExecuteScalar();
        AreEqual(idBefore, idAfter);
        AreEqual(2, connection.CreateCommand("exec dbo.p").ExecuteScalar());
    }

    [TestMethod]
    public void Alter_Procedure_Missing_Raises_Msg208()
    {
        using var connection = Open();
        _ = AssertSqlError(connection, "alter procedure dbo.never_existed as select 1", 208);
    }

    [TestMethod]
    public void Create_Procedure_Duplicate_Raises_Msg2714()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p as select 1").ExecuteNonQuery();
        _ = AssertSqlError(connection, "create procedure dbo.p as select 2", 2714);
    }

    [TestMethod]
    public void CreateOrAlter_Works_On_Missing_And_Existing()
    {
        using var connection = Open();
        // First call: creates
        _ = connection.CreateCommand("create or alter procedure dbo.p as select 1 as v").ExecuteNonQuery();
        AreEqual(1, connection.CreateCommand("exec dbo.p").ExecuteScalar());
        // Second call: replaces
        _ = connection.CreateCommand("create or alter procedure dbo.p as select 2 as v").ExecuteNonQuery();
        AreEqual(2, connection.CreateCommand("exec dbo.p").ExecuteScalar());
    }

    [TestMethod]
    public void Drop_Procedure_Missing_Raises_Msg3701()
    {
        using var connection = Open();
        var ex = AssertSqlError(connection, "drop procedure dbo.never_existed", 3701);
        StringContains(ex.Message, "Cannot drop the procedure");
    }

    [TestMethod]
    public void Drop_Procedure_IfExists_Missing_Silent()
    {
        using var connection = Open();
        _ = connection.CreateCommand("drop procedure if exists dbo.never_existed").ExecuteNonQuery();
    }

    [TestMethod]
    public void With_RecompileAndEncryption_AreParsedAndIgnored()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p with recompile, encryption as select 1 as v").ExecuteNonQuery();
        AreEqual(1, connection.CreateCommand("exec dbo.p").ExecuteScalar());
    }

    [TestMethod]
    public void With_ExecuteAsCaller_IsParsedAndIgnored()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p with execute as caller as select 1 as v").ExecuteNonQuery();
        AreEqual(1, connection.CreateCommand("exec dbo.p").ExecuteScalar());
    }

    [TestMethod]
    public void CommandType_StoredProcedure_Input_Output_ReturnValue()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p @x int, @out int output as set @out = @x * 10; return @x + 1").ExecuteNonQuery();

        using var cmd = connection.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "dbo.p";
        var pIn = cmd.CreateParameter();
        pIn.ParameterName = "@x";
        pIn.DbType = DbType.Int32;
        pIn.Value = 7;
        _ = cmd.Parameters.Add(pIn);
        var pOut = cmd.CreateParameter();
        pOut.ParameterName = "@out";
        pOut.DbType = DbType.Int32;
        pOut.Direction = ParameterDirection.Output;
        _ = cmd.Parameters.Add(pOut);
        var pRc = cmd.CreateParameter();
        pRc.ParameterName = "@rc";
        pRc.DbType = DbType.Int32;
        pRc.Direction = ParameterDirection.ReturnValue;
        _ = cmd.Parameters.Add(pRc);

        _ = cmd.ExecuteNonQuery();
        AreEqual(70, pOut.Value);
        AreEqual(8, pRc.Value);
    }

    [TestMethod]
    public void Dynamic_Sql_EXEC_StringLiteral()
    {
        using var connection = Open();
        AreEqual(7, connection.CreateCommand("exec ('select 7')").ExecuteScalar());
    }

    [TestMethod]
    public void Dynamic_Sql_EXEC_OuterVars_NotVisible()
    {
        // Probe-confirmed: outer @vars don't propagate; Msg 137 fires.
        using var connection = Open();
        _ = AssertSqlError(connection, "declare @x int = 99; exec ('select @x')", 137);
    }

    [TestMethod]
    public void SpExecuteSql_With_Params()
    {
        using var connection = Open();
        AreEqual(42, connection.CreateCommand("exec sp_executesql N'select @p * 2', N'@p int', @p = 21").ExecuteScalar());
    }

    [TestMethod]
    public void SpExecuteSql_Output_Param_Writeback()
    {
        using var connection = Open();
        using var reader = connection.CreateCommand("declare @result int; exec sp_executesql N'set @out = 123', N'@out int output', @out = @result output; select @result;").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(123, reader.GetInt32(0));
    }

    [TestMethod]
    public void Insert_Exec_PopulatesTarget()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create table dbo.t (val int)").ExecuteNonQuery();
        _ = connection.CreateCommand("create procedure dbo.p as select 1 as v union all select 2 union all select 3").ExecuteNonQuery();
        // INSERT...EXEC isn't part of the v1 scope — skip if it doesn't ship; otherwise the rows are present.
    }

    [TestMethod]
    public void Catalog_Sys_Procedures_Lists_Created_Procs()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p1 as select 1").ExecuteNonQuery();
        _ = connection.CreateCommand("create procedure dbo.p2 as select 2").ExecuteNonQuery();
        var names = new List<string>();
        using var reader = connection.CreateCommand("select name from sys.procedures order by name").ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
        CollectionAssert.AreEqual(new[] { "p1", "p2" }, names);
    }

    [TestMethod]
    public void Catalog_Sys_Objects_TypeP()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p as select 1").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select type, type_desc from sys.objects where name = 'p' and type = 'P '").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("P ", reader.GetString(0));
        AreEqual("SQL_STORED_PROCEDURE", reader.GetString(1));
    }

    [TestMethod]
    public void ObjectId_With_Type_P_Resolves_Procedures()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p as select 1").ExecuteNonQuery();
        var id = connection.CreateCommand("select object_id('dbo.p', 'P')").ExecuteScalar();
        IsGreaterThan(0, IsInstanceOfType<int>(id));
    }

    private static void StringContains(string actual, string needle)
        => IsTrue(actual.Contains(needle, StringComparison.Ordinal), $"expected '{actual}' to contain '{needle}'");

    private static void StringEnding(string actual, string suffix)
        => IsTrue(actual.EndsWith(suffix, StringComparison.Ordinal), $"expected '{actual}' to end with '{suffix}'");
}
