using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for scalar user-defined functions: CREATE / DROP /
/// invocation grammar, body dispatch (DECLARE / SET / IF / WHILE / RETURN),
/// recursion limit (Msg 217 at 32), argument coercion, DEFAULT keyword,
/// and the WITH RETURNS NULL ON NULL INPUT short-circuit.
/// </summary>
[TestClass]
public sealed class ScalarFunctionTests
{
    private static DbConnection Open() => new Simulation().CreateOpenConnection();

    private static DbException AssertSqlError(DbConnection connection, string sql, int errorNumber)
    {
        var ex = Throws<DbException>(() => connection.CreateCommand(sql).ExecuteScalar());
        AreEqual(errorNumber.ToString(), ex.Data["HelpLink.EvtID"], $"expected Msg {errorNumber}");
        return ex;
    }

    [TestMethod]
    public void Create_And_Call_SingleStatementBody_ReturnsExpression()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.f(@x int) returns int as begin return @x + 1 end").ExecuteNonQuery();
        AreEqual(6, connection.CreateCommand("select dbo.f(5)").ExecuteScalar());
    }

    [TestMethod]
    public void Create_And_Call_MultiStatementBody_WithDeclareAndIf()
    {
        using var connection = Open();
        _ = connection.CreateCommand("""
            create function dbo.f(@x int) returns int as begin
                declare @r int = @x * 2
                if @x < 0 set @r = 0
                return @r
            end
        """).ExecuteNonQuery();
        AreEqual(10, connection.CreateCommand("select dbo.f(5)").ExecuteScalar());
        AreEqual(0, connection.CreateCommand("select dbo.f(-3)").ExecuteScalar());
    }

    [TestMethod]
    public void Body_WithWhileLoop_ExecutesIteration()
    {
        using var connection = Open();
        _ = connection.CreateCommand("""
            create function dbo.sum_to(@n int) returns int as begin
                declare @s int = 0
                declare @i int = 1
                while @i <= @n begin
                    set @s = @s + @i
                    set @i = @i + 1
                end
                return @s
            end
        """).ExecuteNonQuery();
        AreEqual(15, connection.CreateCommand("select dbo.sum_to(5)").ExecuteScalar());
        AreEqual(5050, connection.CreateCommand("select dbo.sum_to(100)").ExecuteScalar());
    }

    [TestMethod]
    public void BareName_Without_SchemaQualifier_Raises_Msg195()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.f(@x int) returns int as begin return @x end").ExecuteNonQuery();
        var ex = AssertSqlError(connection, "select f(5)", 195);
        Contains("'f' is not a recognized built-in function name.", ex.Message);
    }

    [TestMethod]
    public void SchemaQualified_Missing_Raises_Msg4121()
    {
        using var connection = Open();
        _ = AssertSqlError(connection, "select dbo.does_not_exist(5)", 4121);
    }

    [TestMethod]
    public void TooFewArguments_Raises_Msg313()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.f(@x int, @y int) returns int as begin return @x + @y end").ExecuteNonQuery();
        _ = AssertSqlError(connection, "select dbo.f(5)", 313);
    }

    [TestMethod]
    public void TooManyArguments_Raises_Msg8144()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.f(@x int) returns int as begin return @x end").ExecuteNonQuery();
        _ = AssertSqlError(connection, "select dbo.f(1, 2)", 8144);
    }

    [TestMethod]
    public void Recursion_AtLimit_Succeeds_Beyond_Raises_Msg217()
    {
        using var connection = Open();
        _ = connection.CreateCommand("""
            create function dbo.depth(@n int) returns int as begin
                if @n <= 0 return 0
                return 1 + dbo.depth(@n - 1)
            end
        """).ExecuteNonQuery();
        AreEqual(31, connection.CreateCommand("select dbo.depth(31)").ExecuteScalar());
        _ = AssertSqlError(connection, "select dbo.depth(32)", 217);
    }

    [TestMethod]
    public void ReturnsNullOnNullInput_SkipsBody_OnNullArg()
    {
        using var connection = Open();
        _ = connection.CreateCommand("""
            create function dbo.f(@x int) returns int
            with returns null on null input
            as begin
                if @x is null return -1
                return @x * 10
            end
        """).ExecuteNonQuery();
        // Body would return -1 on NULL — short-circuit returns NULL instead.
        IsTrue(connection.CreateCommand("select dbo.f(null)").ExecuteScalar() is DBNull);
        AreEqual(50, connection.CreateCommand("select dbo.f(5)").ExecuteScalar());
    }

    [TestMethod]
    public void Without_Option_NullArg_RunsBody_Normally()
    {
        using var connection = Open();
        _ = connection.CreateCommand("""
            create function dbo.f(@x int) returns int as begin
                if @x is null return -1
                return @x * 10
            end
        """).ExecuteNonQuery();
        AreEqual(-1, connection.CreateCommand("select dbo.f(null)").ExecuteScalar());
        AreEqual(50, connection.CreateCommand("select dbo.f(5)").ExecuteScalar());
    }

    [TestMethod]
    public void Default_Keyword_Substitutes_Declared_Default()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.f(@x int = 99) returns int as begin return @x + 1 end").ExecuteNonQuery();
        AreEqual(100, connection.CreateCommand("select dbo.f(default)").ExecuteScalar());
        AreEqual(8, connection.CreateCommand("select dbo.f(7)").ExecuteScalar());
    }

    [TestMethod]
    public void BareOmission_Even_With_DeclaredDefault_Raises_Msg313()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.f(@x int = 99) returns int as begin return @x + 1 end").ExecuteNonQuery();
        _ = AssertSqlError(connection, "select dbo.f()", 313);
    }

    [TestMethod]
    public void Drop_Function_RemovesEntry()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.f(@x int) returns int as begin return @x end").ExecuteNonQuery();
        AreEqual(5, connection.CreateCommand("select dbo.f(5)").ExecuteScalar());
        _ = connection.CreateCommand("drop function dbo.f").ExecuteNonQuery();
        _ = AssertSqlError(connection, "select dbo.f(5)", 4121);
    }

    [TestMethod]
    public void Drop_Missing_Raises_Msg3701_With_Function_Wording()
    {
        using var connection = Open();
        var ex = Throws<DbException>(() => connection.CreateCommand("drop function dbo.does_not_exist").ExecuteNonQuery());
        AreEqual("3701", ex.Data["HelpLink.EvtID"]);
        Contains("Cannot drop the function 'dbo.does_not_exist'", ex.Message);
    }

    [TestMethod]
    public void Drop_If_Exists_Silently_Succeeds_OnMissing()
    {
        using var connection = Open();
        _ = connection.CreateCommand("drop function if exists dbo.does_not_exist").ExecuteNonQuery();
    }

    [TestMethod]
    public void Duplicate_Function_Name_Raises_Msg2714()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.f(@x int) returns int as begin return @x end").ExecuteNonQuery();
        var ex = Throws<DbException>(() =>
            connection.CreateCommand("create function dbo.f(@x int) returns int as begin return @x end").ExecuteNonQuery());
        AreEqual("2714", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void FunctionCall_InProjection_Per_Row()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.inc(@x int) returns int as begin return @x + 1 end").ExecuteNonQuery();
        _ = connection.CreateCommand("create table t (id int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (1),(2),(3)").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select id, dbo.inc(id) from t order by id").ExecuteReader();
        var rows = new List<(int, int)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        HasCount(3, rows);
        AreEqual((1, 2), rows[0]);
        AreEqual((2, 3), rows[1]);
        AreEqual((3, 4), rows[2]);
    }

    [TestMethod]
    public void Body_String_Return_Concatenation_Works()
    {
        using var connection = Open();
        _ = connection.CreateCommand("""
            create function dbo.label(@x int) returns varchar(20) as begin
                if @x is null return 'NULL'
                return 'val=' + cast(@x as varchar(10))
            end
        """).ExecuteNonQuery();
        AreEqual("val=5", connection.CreateCommand("select dbo.label(5)").ExecuteScalar());
        AreEqual("NULL", connection.CreateCommand("select dbo.label(null)").ExecuteScalar());
    }

    [TestMethod]
    public void ReturnWithValue_Outside_Udf_Still_Raises_Msg178()
    {
        using var connection = Open();
        _ = AssertSqlError(connection, "return 5", 178);
    }

    [TestMethod]
    public void Arg_Coercion_Widens_To_Parameter_Type()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.f(@x bigint) returns bigint as begin return @x * 2 end").ExecuteNonQuery();
        // tinyint argument coerces to bigint, multiplication stays in bigint range.
        AreEqual(10L, connection.CreateCommand("select dbo.f(cast(5 as tinyint))").ExecuteScalar());
    }

    [TestMethod]
    public void SysObjects_Surfaces_FN_Type_Rows()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.f(@x int) returns int as begin return @x end").ExecuteNonQuery();
        using var reader = connection.CreateCommand(
            "select name, type, type_desc from sys.objects where type = 'FN' and is_ms_shipped = 0").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("f", reader.GetString(0));
        AreEqual("FN", reader.GetString(1));
        AreEqual("SQL_SCALAR_FUNCTION", reader.GetString(2));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void SysParameters_Surfaces_ReturnType_AsParamId0()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.f(@x int, @y bigint) returns int as begin return @x end").ExecuteNonQuery();
        using var reader = connection.CreateCommand("""
            select name, parameter_id, is_output, is_nullable
            from sys.parameters
            where object_id = object_id('dbo.f','FN')
            order by parameter_id
        """).ExecuteReader();
        var rows = new List<(string Name, int Id, bool IsOutput, bool IsNullable)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetBoolean(2), reader.GetBoolean(3)));
        HasCount(3, rows);
        AreEqual(("", 0, true, true), rows[0]);   // return type row
        AreEqual(("@x", 1, false, true), rows[1]);
        AreEqual(("@y", 2, false, true), rows[2]);
    }

    [TestMethod]
    public void ObjectId_With_FN_Filter_Routes_To_Function()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.f(@x int) returns int as begin return @x end").ExecuteNonQuery();
        var idAsFunction = connection.CreateCommand("select object_id('dbo.f','FN')").ExecuteScalar();
        _ = IsInstanceOfType<int>(idAsFunction);
        // 'U' filter on a UDF should return NULL.
        IsTrue(connection.CreateCommand("select object_id('dbo.f','U')").ExecuteScalar() is DBNull);
    }

    [TestMethod]
    public void ObjectId_Without_Type_Filter_Finds_Function()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.f(@x int) returns int as begin return @x end").ExecuteNonQuery();
        var id = connection.CreateCommand("select object_id('dbo.f')").ExecuteScalar();
        _ = IsInstanceOfType<int>(id);
    }

    /// <summary>WITH SCHEMABINDING parse-and-discard — accepted, no runtime semantic effect.</summary>
    [TestMethod]
    public void CreateFunction_WithSchemabinding_Accepted()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create function dbo.lpad(@v int) returns varchar(8) with schemabinding as begin return right('0000000' + cast(@v as varchar(8)), 8) end");
        AreEqual("00000042", simulation.ExecuteScalar("select dbo.lpad(42)"));
    }

    /// <summary>WITH EXECUTE AS OWNER parse-and-discard — no principal model.</summary>
    [TestMethod]
    public void CreateFunction_WithExecuteAsOwner_Accepted()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create function dbo.double_it(@v int) returns int with execute as owner as begin return @v * 2 end");
        AreEqual(84, simulation.ExecuteScalar("select dbo.double_it(42)"));
    }

    /// <summary>WITH SCHEMABINDING, EXECUTE AS OWNER — comma-separated multi-option shape.</summary>
    [TestMethod]
    public void CreateFunction_WithMultipleOptions_Accepted()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create function dbo.tripled(@v int) returns int with schemabinding, execute as owner as begin return @v * 3 end");
        AreEqual(15, simulation.ExecuteScalar("select dbo.tripled(5)"));
    }

    /// <summary>WITH RETURNS NULL ON NULL INPUT + SCHEMABINDING — order-independent.</summary>
    [TestMethod]
    public void CreateFunction_ReturnsNullOnNullInput_PairsWithSchemabinding()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create function dbo.safe_add(@a int, @b int) returns int with returns null on null input, schemabinding as begin return @a + @b end");
        _ = IsInstanceOfType<DBNull>(simulation.ExecuteScalar("select dbo.safe_add(null, 5)"));
        AreEqual(7, simulation.ExecuteScalar("select dbo.safe_add(3, 4)"));
    }

    /// <summary>Unknown WITH option still raises NotSupportedException — closed accept-list enforced.</summary>
    [TestMethod]
    public void CreateFunction_UnknownWithOption_Rejected()
        => Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery(
            "create function dbo.bad(@v int) returns int with native_compilation as begin return @v end"));
}
