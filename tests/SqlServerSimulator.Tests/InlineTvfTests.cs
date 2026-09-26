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

    private static SimulatedSqlException AssertSqlError(DbConnection connection, string sql, int errorNumber)
    {
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand(sql).ExecuteScalar());
        AreEqual(errorNumber, ex.Number, $"expected Msg {errorNumber}");
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

    /// <summary>
    /// An inline function's body is inlined into the query that reads it the
    /// way a view's is, so a constant <c>TOP 100 PERCENT</c> drops its
    /// <c>ORDER BY</c> and the rows come back in scan order (probed 2026-09-23).
    /// </summary>
    [TestMethod]
    public void TopHundredPercent_DropsTheOrderBy()
    {
        using var connection = Open();
        _ = connection.CreateCommand("create table t (a int); insert t values (3), (1), (2)").ExecuteNonQuery();
        _ = connection.CreateCommand("create function dbo.tf() returns table as return (select top 100 percent a from t order by a)").ExecuteNonQuery();
        using var reader = connection.CreateCommand("select a from dbo.tf()").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 3, 1, 2 }, values);
    }

    /// <summary>
    /// A row limit read from a parameter creates — the body binds without the
    /// parameter's value, so the count is settled from its declared type — and
    /// applies the argument (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select top (@n) v from (values (1), (2), (3)) t (v) order by v", 2, "1,2")]
    [DataRow("select top (@n) percent v from (values (1), (2), (3), (4)) t (v) order by v", 50, "1,2")]
    [DataRow("select v from (values (1), (2), (3)) t (v) order by v offset @n rows", 1, "2,3")]
    public void RowLimitFromAParameter_Creates(string body, int argument, string expected)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches($"create function f (@n int) returns table as return ({body})");
        using var reader = simulation.ExecuteReader($"select v from dbo.f({argument})");
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        AreEqual(expected, string.Join(",", values));
    }

    /// <summary>
    /// A one-part name reaches a table-valued function in FROM and APPLY
    /// through the default schema (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("create function f() returns table as return select 1 a", "select a from f()")]
    [DataRow("create function f() returns @r table (a int) as begin insert @r values (1); return end", "select a from f()")]
    [DataRow("create function f(@x int) returns table as return select @x a", "select x.a from (values (1)) v(n) cross apply f(v.n) x")]
    public void AOnePartName_ReachesATableValuedFunction(string function, string query)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(function);
        AreEqual(1, sim.ExecuteScalar(query));
    }

    /// <summary>
    /// An inline function whose body no longer binds reports the body's error,
    /// attributed to the function, then Msg 4413 naming the reference as
    /// written — at line 12 when it is schema-qualified (probed 2026-09-26
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select * from f()", "f", 1)]
    [DataRow("select * from dbo.f()", "dbo.f", 12)]
    public void AnInlineFunctionThatNoLongerBinds_IsFollowedByMsg4413(string query, string written, int trailerLine)
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table t (a int)", "create function f() returns table as return select a from t", "drop table t");
        var errors = sim.AssertSqlError(query, 208).Errors;
        AreEqual("f", errors[0].Procedure);
        AreEqual($"Could not use view or function '{written}' because of binding errors.", errors[1].Message);
        AreEqual(trailerLine, errors[1].LineNumber);
    }

    /// <summary>
    /// A bare <c>RETURN</c> body may be a set operation, its later branches
    /// not read as new statements (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void ABareReturnBody_TakesASetOperation()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create function f (@x int) returns table as return select @x * 2 d union all select @x * 3 except select 0");
        AreEqual(10, sim.ExecuteScalar("select sum(d) from dbo.f(2)"));
    }
}
