using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for inline table-valued functions: CREATE / DROP, FROM-
/// clause invocation, parameter binding (including DEFAULT keyword), the
/// always-lateral correlation pattern under CROSS APPLY / OUTER APPLY,
/// catalog-view surface (sys.objects with type 'IF', sys.columns for
/// output projection, sys.parameters with no return-row), and the error
/// paths probe-confirmed against SQL Server 2025 (Msg 487 / 4514 / 4506
/// / 208 / 4121 / 313 / 8144).
/// </summary>
[TestClass]
public sealed class InlineTvfTests
{
    private static DbConnection Open() => new Simulation().CreateOpenConnection();

    private static DbException AssertSqlError(DbConnection connection, string sql, int errorNumber)
    {
        var ex = Throws<DbException>(() => connection.CreateCommand(sql).ExecuteScalar());
        AreEqual(errorNumber.ToString(), ex.Data["HelpLink.EvtID"], $"expected Msg {errorNumber}");
        return ex;
    }

    [TestMethod]
    public void Create_And_Call_BasicTvf_ReturnsRowFromParameter()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.tvf(@x int) returns table as return (select @x as a, @x * 2 as b)").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select a, b from dbo.tvf(5)").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(5, reader.GetInt32(0));
        AreEqual(10, reader.GetInt32(1));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Body_WithoutParens_IsAccepted()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.tvf(@x int) returns table as return select @x as v").ExecuteNonQuery();
        AreEqual(7, connection.CreateCommand("select v from dbo.tvf(7)").ExecuteScalar());
    }

    [TestMethod]
    public void ZeroArg_Tvf_Works()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.tvf() returns table as return (select 1 as a, 'hi' as b)").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select * from dbo.tvf()").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual("hi", reader.GetString(1));
    }

    [TestMethod]
    public void WithSchemabinding_ParsesAndIgnores()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.tvf(@x int) returns table with schemabinding as return (select @x as v)").ExecuteNonQuery();
        AreEqual(11, connection.CreateCommand("select v from dbo.tvf(11)").ExecuteScalar());
    }

    [TestMethod]
    public void WithEncryption_ParsesAndIgnores()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.tvf(@x int) returns table with encryption as return (select @x as v)").ExecuteNonQuery();
        AreEqual(13, connection.CreateCommand("select v from dbo.tvf(13)").ExecuteScalar());
    }

    [TestMethod]
    public void WithSchemabindingAndEncryption_Combined_ParsesAndIgnores()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.tvf(@x int) returns table with encryption, schemabinding as return (select @x as v)").ExecuteNonQuery();
        AreEqual(17, connection.CreateCommand("select v from dbo.tvf(17)").ExecuteScalar());
    }

    [TestMethod]
    public void With_ReturnsNullOnNullInput_OnTvf_Raises_Msg487()
    {
        using var connection = Open();
        var ex = AssertSqlError(connection, "create function dbo.tvf(@x int) returns table with returns null on null input as return (select @x as v)", 487);
        Contains("CREATE/ALTER FUNCTION", ex.Message);
    }

    [TestMethod]
    public void DefaultParam_WithDefaultKeyword_Materializes()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.tvf(@x int = 99) returns table as return (select @x as v)").ExecuteNonQuery();
        AreEqual(99, connection.CreateCommand("select v from dbo.tvf(default)").ExecuteScalar());
        AreEqual(5, connection.CreateCommand("select v from dbo.tvf(5)").ExecuteScalar());
    }

    [TestMethod]
    public void BareOmission_OfDefaultParam_Raises_Msg313()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.tvf(@x int = 99) returns table as return (select @x as v)").ExecuteNonQuery();
        _ = AssertSqlError(connection, "select v from dbo.tvf()", 313);
    }

    [TestMethod]
    public void Insufficient_Args_Raises_Msg313()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.tvf(@x int, @y int) returns table as return (select @x + @y as v)").ExecuteNonQuery();
        _ = AssertSqlError(connection, "select v from dbo.tvf(1)", 313);
    }

    [TestMethod]
    public void Too_Many_Args_Raises_Msg8144()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.tvf(@x int) returns table as return (select @x as v)").ExecuteNonQuery();
        _ = AssertSqlError(connection, "select v from dbo.tvf(1, 2)", 8144);
    }

    [TestMethod]
    public void Unnamed_Projection_Column_Raises_Msg4514()
    {
        using var connection = Open();
        var ex = AssertSqlError(connection, "create function dbo.tvf(@x int) returns table as return (select @x + 1)", 4514);
        Contains("column 1", ex.Message);
    }

    [TestMethod]
    public void Duplicate_Column_Names_Raises_Msg4506()
    {
        using var connection = Open();
        var ex = AssertSqlError(connection, "create function dbo.tvf(@x int) returns table as return (select @x as a, @x as a)", 4506);
        Contains("'a'", ex.Message);
        Contains("'tvf'", ex.Message);
    }

    [TestMethod]
    public void Scalar_UDF_Called_In_FROM_Raises_Msg208()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.scalar_f(@x int) returns int as begin return @x * 10 end").ExecuteNonQuery();
        _ = AssertSqlError(connection, "select * from dbo.scalar_f(5)", 208);
    }

    [TestMethod]
    public void Inline_TVF_Used_As_Scalar_Raises_Msg4121()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.tvf(@x int) returns table as return (select @x as v)").ExecuteNonQuery();
        _ = AssertSqlError(connection, "select dbo.tvf(5)", 4121);
    }

    [TestMethod]
    public void Missing_TVF_In_FROM_Raises_Msg208()
    {
        using var connection = Open();
        _ = AssertSqlError(connection, "select * from dbo.does_not_exist(1)", 208);
    }

    [TestMethod]
    public void CrossApply_WithCorrelatedArg_BindsPerRow()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create table #nums(n int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert #nums values (1), (2), (3)").ExecuteNonQuery();
        _ = connection.CreateCommand("create function dbo.tvf(@x int) returns table as return (select @x as a, @x * 10 as b)").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select n.n, t.a, t.b from #nums n cross apply dbo.tvf(n.n) t order by n.n").ExecuteReader();
        var pairs = new List<(int n, int a, int b)>();
        while (reader.Read())
            pairs.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)));
        HasCount(3, pairs);
        AreEqual((1, 1, 10), pairs[0]);
        AreEqual((2, 2, 20), pairs[1]);
        AreEqual((3, 3, 30), pairs[2]);
    }

    [TestMethod]
    public void OuterApply_Empty_Tvf_NullFills()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create table #nums(n int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert #nums values (1), (2)").ExecuteNonQuery();
        // The body filters its rows on a parameter-dependent predicate so the
        // TVF returns zero rows for the test input — exercising OUTER APPLY's
        // null-fill path without depending on no-FROM WHERE support.
        _ = connection.CreateCommand("create table #src(v int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert #src values (10), (20)").ExecuteNonQuery();
        _ = connection.CreateCommand("create function dbo.tvf_empty(@x int) returns table as return (select v from #src where v = @x * 1000)").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select n.n, t.v from #nums n outer apply dbo.tvf_empty(n.n) t order by n.n").ExecuteReader();
        var rows = new List<(int n, int? v)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt32(1)));
        HasCount(2, rows);
        AreEqual((1, null), rows[0]);
        AreEqual((2, null), rows[1]);
    }

    [TestMethod]
    public void DropFunction_Removes_Tvf()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.tvf(@x int) returns table as return (select @x as v)").ExecuteNonQuery();
        AreEqual(5, connection.CreateCommand("select v from dbo.tvf(5)").ExecuteScalar());
        _ = connection.CreateCommand("drop function dbo.tvf").ExecuteNonQuery();
        _ = AssertSqlError(connection, "select v from dbo.tvf(5)", 208);
    }

    [TestMethod]
    public void DropFunction_IfExists_NoOps()
    {
        using var connection = Open();
        _ = connection.CreateCommand("drop function if exists dbo.does_not_exist").ExecuteNonQuery();
    }

    [TestMethod]
    public void Tvf_Body_References_Real_Table()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create table dbo.t(id int, label varchar(20))").ExecuteNonQuery();
        _ = connection.CreateCommand("insert dbo.t values (1, 'a'), (2, 'b'), (3, 'c')").ExecuteNonQuery();
        _ = connection.CreateCommand("create function dbo.tvf(@min int) returns table as return (select id, label from dbo.t where id >= @min)").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select id, label from dbo.tvf(2) order by id").ExecuteReader();
        var rows = new List<(int id, string label)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetString(1)));
        HasCount(2, rows);
        AreEqual((2, "b"), rows[0]);
        AreEqual((3, "c"), rows[1]);
    }

    [TestMethod]
    public void SysObjects_HasInlineTvfRow_WithTypeIF()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.tvf(@x int) returns table as return (select @x as v)").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select name, type, type_desc from sys.objects where name = 'tvf'").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("tvf", reader.GetString(0));
        AreEqual("IF", reader.GetString(1));
        AreEqual("SQL_INLINE_TABLE_VALUED_FUNCTION", reader.GetString(2));
    }

    [TestMethod]
    public void SysColumns_Emits_TvfOutputProjection()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.tvf(@x int) returns table as return (select @x as a, @x * 2 as b)").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select name, column_id from sys.columns where object_id = object_id('dbo.tvf', 'IF') order by column_id").ExecuteReader();
        var cols = new List<(string name, int id)>();
        while (reader.Read())
            cols.Add((reader.GetString(0), reader.GetInt32(1)));
        HasCount(2, cols);
        AreEqual(("a", 1), cols[0]);
        AreEqual(("b", 2), cols[1]);
    }

    [TestMethod]
    public void SysParameters_TvfHasNoReturnRow()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.tvf(@x int) returns table as return (select @x as v)").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select name, parameter_id, is_output from sys.parameters where object_id = object_id('dbo.tvf', 'IF') order by parameter_id").ExecuteReader();
        var rows = new List<(string name, int id, bool isOut)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetBoolean(2)));
        // Only the declared parameter — no synthetic return row.
        HasCount(1, rows);
        AreEqual(("@x", 1, false), rows[0]);
    }

    [TestMethod]
    public void ObjectId_WithIFFilter_ResolvesInlineTvfOnly()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.tvf(@x int) returns table as return (select @x as v)").ExecuteNonQuery();
        _ = connection.CreateCommand("create function dbo.scalar_f(@x int) returns int as begin return @x end").ExecuteNonQuery();

        IsNotNull(connection.CreateCommand("select object_id('dbo.tvf', 'IF')").ExecuteScalar());
        AreEqual(DBNull.Value, connection.CreateCommand("select object_id('dbo.tvf', 'FN')").ExecuteScalar());
        AreEqual(DBNull.Value, connection.CreateCommand("select object_id('dbo.scalar_f', 'IF')").ExecuteScalar());
        IsNotNull(connection.CreateCommand("select object_id('dbo.scalar_f', 'FN')").ExecuteScalar());
    }

    [TestMethod]
    public void Tvf_WithAlias_RebindsColumnQualifier()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create function dbo.tvf(@x int) returns table as return (select @x as a, @x * 2 as b)").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select c.a, c.b from dbo.tvf(3) as c").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(3, reader.GetInt32(0));
        AreEqual(6, reader.GetInt32(1));
    }
}
