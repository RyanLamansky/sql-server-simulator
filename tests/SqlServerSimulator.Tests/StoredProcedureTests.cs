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
    public void CommandType_StoredProcedure_UnnamedParameters_BindPositionally()
    {
        // Native DB-Library RPC callers (pymssql / FreeTDS) send procedure
        // arguments positionally with empty names — probe-confirmed against the
        // reference over the wire that real binds them to the declared
        // parameters by position, including OUTPUT write-back. Empty ParameterName
        // is the in-process equivalent of that wire shape.
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p @x int, @out int output as set @out = @x + 1; return @x + 2").ExecuteNonQuery();

        using var cmd = connection.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "dbo.p";
        var pIn = cmd.CreateParameter();
        pIn.DbType = DbType.Int32;
        pIn.Value = 5;
        _ = cmd.Parameters.Add(pIn);
        var pOut = cmd.CreateParameter();
        pOut.DbType = DbType.Int32;
        pOut.Direction = ParameterDirection.Output;
        _ = cmd.Parameters.Add(pOut);
        var pRc = cmd.CreateParameter();
        pRc.DbType = DbType.Int32;
        pRc.Direction = ParameterDirection.ReturnValue;
        _ = cmd.Parameters.Add(pRc);

        _ = cmd.ExecuteNonQuery();
        AreEqual(6, pOut.Value);   // @out = @x + 1, bound by position
        AreEqual(7, pRc.Value);    // return @x + 2
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
    public void Catalog_Sys_Procedures_FullModeledColumnSet_Resolves()
    {
        // The SMO StoredProcedure property-bag reads is_auto_executed (projected
        // as [Startup]) alongside the rest; a missing column fails the whole bag
        // query Msg 207 and every StoredProcedure property errors. Startup procs
        // (sp_procoption) aren't modeled, so is_auto_executed is a constant 0.
        using var connection = Open();
        _ = connection.CreateCommand("create procedure dbo.p as select 1").ExecuteNonQuery();
        using var reader = connection.CreateCommand("""
            select object_id, name, schema_id, type, type_desc, create_date,
                   modify_date, is_ms_shipped, is_auto_executed
            from sys.procedures where name = 'p'
            """).ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("p", reader.GetString(1));
        IsFalse(reader.GetBoolean(7));  // is_ms_shipped
        IsFalse(reader.GetBoolean(8));  // is_auto_executed
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

    // -- WITH NATIVE_COMPILATION / SCHEMABINDING + BEGIN ATOMIC body --
    // Tests cover the natively-compiled procedure shape SqlPackage emits in
    // bacpacs (WWI-Full's Website.RecordColdRoomTemperatures is the canonical
    // example). The simulator's semantic model doesn't change: NATIVE_COMPILATION
    // is parse-and-discard, BEGIN ATOMIC is parsed as a regular block whose
    // WITH (...) options block is consumed without enforcement.

    [TestMethod]
    public void Create_With_NativeCompilation_Schemabinding_Parses()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create procedure dbo.p
            with native_compilation, schemabinding, execute as owner
            as
            begin atomic with (transaction isolation level = snapshot, language = N'English')
                select 1;
            end
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.procedures where name = 'p'"));
    }

    [TestMethod]
    public void BeginAtomic_Body_Runs()
    {
        // Verify the BEGIN ATOMIC body actually dispatches: a procedure with
        // a body that inserts into a table should land the row when EXEC'd.
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int primary key, v int)",
            """
            create procedure dbo.add_row @id int, @v int
            with native_compilation, schemabinding, execute as owner
            as
            begin atomic with (transaction isolation level = snapshot, language = N'us_english')
                insert into t (id, v) values (@id, @v);
            end
            """,
            "exec dbo.add_row 1, 100",
            "exec dbo.add_row 2, 200");
        AreEqual(2, sim.ExecuteScalar("select count(*) from t"));
        AreEqual(200, sim.ExecuteScalar("select v from t where id = 2"));
    }

    [TestMethod]
    public void BeginAtomic_With_TryCatch_Body()
    {
        // Mirrors WWI-Full's Website.RecordColdRoomTemperatures shape:
        // BEGIN ATOMIC WITH (...) wrapping a BEGIN TRY / BEGIN CATCH block.
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table failures (msg nvarchar(200))",
            """
            create procedure dbo.try_or_log @raise bit
            with native_compilation, schemabinding, execute as owner
            as
            begin atomic with (transaction isolation level = snapshot, language = N'English')
                begin try
                    if @raise = 1
                        throw 51000, N'boom', 1;
                end try
                begin catch
                    insert into failures (msg) values (error_message());
                end catch
            end
            """,
            "exec dbo.try_or_log 0",
            "exec dbo.try_or_log 1");
        AreEqual(1, sim.ExecuteScalar("select count(*) from failures"));
        AreEqual("boom", sim.ExecuteScalar("select msg from failures"));
    }

    [TestMethod]
    public void BeginAtomic_Without_With_Options_Block_Parses()
    {
        // The grammar allows BEGIN ATOMIC without a WITH (...) options
        // block (future ATOMIC use cases outside natively-compiled procs).
        // Verify the path doesn't reject.
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int)",
            """
            create procedure dbo.p
            as
            begin atomic
                insert into t values (42);
            end
            """,
            "exec dbo.p");
        AreEqual(42, sim.ExecuteScalar("select id from t"));
    }

    [TestMethod]
    public void BeginAtomic_Empty_Body_RaisesSyntax_On_Exec()
    {
        // Empty atomic body should be rejected (matches the regular
        // BEGIN…END empty-body rule). The procedure CREATE captures the
        // body as opaque text; the rejection fires when EXEC re-parses
        // and dispatches the body. ExecuteBatches keeps the CREATE and
        // EXEC in separate batches so the body text doesn't accidentally
        // include the EXEC.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create procedure dbo.p
            as
            begin atomic with (transaction isolation level = snapshot, language = N'English')
            end
            """);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("exec dbo.p"));
        AreEqual("102", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void CommandType_StoredProcedure_UnknownProc_RaisesMsg2812()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "no_such_proc";
        var ex = Throws<DbException>(() => cmd.ExecuteNonQuery());
        AreEqual("2812", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void CommandType_StoredProcedure_NonNameCommandText_RaisesMsg2812()
    {
        // Leading non-identifier token in CommandText doesn't tokenize as a
        // Name — the dispatch's first guard rejects before name parsing.
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "123_not_a_name";
        var ex = Throws<DbException>(() => cmd.ExecuteNonQuery());
        AreEqual("2812", ex.Data["HelpLink.EvtID"]);
    }

    private static void StringContains(string actual, string needle)
        => IsTrue(actual.Contains(needle, StringComparison.Ordinal), $"expected '{actual}' to contain '{needle}'");

    private static void StringEnding(string actual, string suffix)
        => IsTrue(actual.EndsWith(suffix, StringComparison.Ordinal), $"expected '{actual}' to end with '{suffix}'");
}
