using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sp_describe_undeclared_parameters</c> — the parameter types ODBC's
/// <c>SQLDescribeParam</c> (pyodbc typing a <c>None</c>) and JDBC's parameter
/// metadata ask the server for. Every expectation probed 2026-09-26 against
/// SQL Server 2025.
/// </summary>
[TestClass]
public sealed class DescribeUndeclaredParametersTests
{
    private static Simulation WithTable()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table dq (id int identity primary key, b varbinary(max), vb varbinary(10), c varchar(20), nc nvarchar(30),
                d decimal(10,2), n numeric(20,10), dt datetime2(3), dd date, t time(5), f float, bi bigint, bt bit, x xml, ch char(4), m money)
            """);
        return sim;
    }

    // "@name type" per described parameter, in ordinal order.
    private static string Describe(Simulation sim, string tsql, string? parameters = null)
    {
        using var reader = sim.ExecuteReader(parameters is null
            ? $"exec sp_describe_undeclared_parameters N'{tsql.Replace("'", "''", StringComparison.Ordinal)}'"
            : $"exec sp_describe_undeclared_parameters N'{tsql.Replace("'", "''", StringComparison.Ordinal)}', N'{parameters}'");
        var rows = new List<string>();
        while (reader.Read())
            rows.Add($"{reader.GetString(reader.GetOrdinal("name"))} {reader.GetString(reader.GetOrdinal("suggested_system_type_name"))}");
        return string.Join(", ", rows);
    }

    [TestMethod]
    [DataRow("insert into dq (b, vb, c, nc, d, dt, dd, t, f, bi, bt, x, ch, m) values (@P1, @P2, @P3, @P4, @P5, @P6, @P7, @P8, @P9, @P10, @P11, @P12, @P13, @P14)",
        "@P1 varbinary(max), @P2 varbinary(10), @P3 varchar(20), @P4 nvarchar(30), @P5 decimal(10,2), @P6 datetime2(3), @P7 date, @P8 time(5), @P9 float, @P10 bigint, @P11 bit, @P12 xml, @P13 char(4), @P14 money")]
    [DataRow("insert into dq (c, bi) values (@P1, @P2), (@P3, @P4)", "@P1 varchar(20), @P2 bigint, @P3 varchar(20), @P4 bigint")]
    [DataRow("insert into dq (c) select @P1", "@P1 varchar(20)")]
    [DataRow("update dq set nc = @P1 where id = @P2", "@P1 nvarchar(30), @P2 int")]
    [DataRow("select 1 from dq where id = @P2 and c = @P1", "@P2 int, @P1 varchar(20)")]
    [DataRow("select 1 from dq where c = @P1 and d > @P2 and dt between @P3 and @P4", "@P1 varchar(20), @P2 decimal(38,19), @P3 datetime2(7), @P4 datetime2(7)")]
    [DataRow("select 1 from dq where c < @P1 and nc > @P2 and vb >= @P3 and t < @P4", "@P1 varchar(8000), @P2 nvarchar(4000), @P3 varbinary(8000), @P4 time(7)")]
    [DataRow("select 1 from dq where id in (@P1, @P2)", "@P1 int, @P2 int")]
    [DataRow("select 1 from dq where id in (select @P1)", "@P1 int")]
    [DataRow("select 1 from dq where c like @P1 and nc like @P2", "@P1 varchar(8000), @P2 nvarchar(4000)")]
    [DataRow("select top (@P1) id from dq", "@P1 bigint")]
    [DataRow("select id from dq order by id offset @P1 rows fetch next @P2 rows only", "@P1 bigint, @P2 bigint")]
    [DataRow("select @P1 + 1", "@P1 int")]
    [DataRow("select @P1 + 'x'", "@P1 varchar(8000)")]
    [DataRow("select @P1 + 1.5", "@P1 numeric(38,19)")]
    [DataRow("select c * @P1 from dq", "@P1 float")]
    [DataRow("select 1 where @P1 = 2.5", "@P1 numeric(2,1)")]
    [DataRow("select case when 1 = 1 then d else @P1 end from dq", "@P1 numeric(38,19)")]
    [DataRow("select coalesce(dt, @P1) from dq", "@P1 datetime2(7)")]
    [DataRow("select 1 union select @P1", "@P1 int")]
    [DataRow("select isnull(@P1, 1)", "@P1 int")]
    [DataRow("select 1 where @P1 is null", "@P1 int")]
    [DataRow("select cast(@P1 as int), convert(varchar(10), @P2, 120)", "@P1 int, @P2 varchar(10)")]
    [DataRow("declare @x int; set @x = @P1", "@P1 int")]
    public void DeducesTheTypeRealDoes(string tsql, string expected) =>
        AreEqual(expected, Describe(WithTable(), tsql));

    [TestMethod]
    public void DeclaredParameters_AreNotDescribed()
    {
        var sim = WithTable();
        AreEqual("", Describe(sim, "select 1 from dq where id = @P1", "@P1 int"));
        AreEqual("@b varchar(20)", Describe(sim, "select 1 from dq where id = @a and c = @b", "@a int"));
        AreEqual("", Describe(sim, "select 1"));
    }

    [TestMethod]
    public void DescribesEveryColumnAsRealDoes()
    {
        using var reader = WithTable().ExecuteReader("exec sp_describe_undeclared_parameters N'insert into dq (nc, d) values (@P1, @P2)'");
        IsTrue(reader.Read());
        AreEqual(24, reader.FieldCount);
        AreEqual(1, reader.GetInt32(0));
        AreEqual("@P1", reader.GetString(1));
        AreEqual(231, reader.GetInt32(2));
        AreEqual((short)60, reader.GetInt16(4));
        IsTrue(reader.GetBoolean(19));
        IsFalse(reader.GetBoolean(20));
        AreEqual(231, reader.GetInt32(22));
        AreEqual(60, reader.GetInt32(23));
        IsTrue(reader.Read());
        AreEqual(106, reader.GetInt32(2));
        AreEqual((short)9, reader.GetInt16(4));
        AreEqual((byte)10, reader.GetByte(5));
        AreEqual((byte)2, reader.GetByte(6));
        AreEqual(17, reader.GetInt32(23));
        IsFalse(reader.Read());
    }

    [TestMethod]
    [DataRow("select 1 from dq where c = @P1 or id = @P1", 11508)]
    [DataRow("select 1 from dq where @P1 = @P2", 11503)]
    [DataRow("select @P1", 11506)]
    [DataRow("select dd + @P1 from dq", 11507)]
    [DataRow("select 1 from dq where x = @P1", 11507)]
    public void AParameterNoTypeFits_IsRefused(string tsql, int number) =>
        WithTable().AssertSqlError($"exec sp_describe_undeclared_parameters N'{tsql}'", number);

    [TestMethod]
    public void ACompileError_IsFollowedByMsg11501()
    {
        var error = WithTable().AssertSqlError("exec sp_describe_undeclared_parameters N'select * from nosuch where i = @P1'", 208);
        AreEqual(11501, error.Errors[1].Number);
    }

    [TestMethod]
    public void AMissingOrNullStatement_IsRefused()
    {
        var sim = new Simulation();
        AreEqual(20, sim.AssertSqlError("exec sp_describe_undeclared_parameters", 201).State);
        AreEqual(21, sim.AssertSqlError("exec sp_describe_undeclared_parameters null", 214).State);
    }
}
