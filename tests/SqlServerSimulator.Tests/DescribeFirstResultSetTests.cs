using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sp_describe_first_result_set</c>, probed 2026-09-24 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class DescribeFirstResultSetTests
{
    private const string Tables = """
        create table da (id int identity primary key, v varchar(10) not null, n numeric(5, 2), s nvarchar(max), c as id * 2);
        create table db (id int, w nvarchar(20));
        """;

    private static Simulation Seeded()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(Tables, "create view dv as select id, v from da");
        return sim;
    }

    // One line per column: name, nullable, type name, max_length, identity, updateable, computed, tds type, tds length.
    private static string Describe(Simulation sim, string tsql, string? parameters = null)
    {
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = parameters is null
            ? "exec sp_describe_first_result_set @tsql = @t"
            : "exec sp_describe_first_result_set @tsql = @t, @params = @p";
        var t = command.CreateParameter();
        t.ParameterName = "@t";
        t.Value = tsql;
        _ = command.Parameters.Add(t);
        if (parameters is not null)
        {
            var p = command.CreateParameter();
            p.ParameterName = "@p";
            p.Value = parameters;
            _ = command.Parameters.Add(p);
        }
        using var reader = command.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read())
        {
            lines.Add(string.Join(" ",
                reader.IsDBNull(2) ? "-" : reader.GetString(2), reader.GetBoolean(3) ? "null" : "notnull", reader.GetString(5), reader.GetInt16(6),
                reader.GetBoolean(27) ? "identity" : "-", reader.GetBoolean(29) ? "updateable" : "-", reader.GetBoolean(30) ? "computed" : "-",
                reader.GetInt32(35), reader.GetInt32(36)));
        }
        return string.Join(" | ", lines);
    }

    [TestMethod]
    public void BaseTableColumns()
        => AreEqual(
            "id notnull int 4 identity - - 56 4 | v notnull varchar(10) 10 - updateable - 167 10 | n null numeric(5,2) 5 - updateable - 108 17 | s null nvarchar(max) -1 - updateable - 231 65535 | c null int 4 - - computed 38 4",
            Describe(Seeded(), "select id, v, n, s, c from da"));

    [TestMethod]
    public void Expressions()
        => AreEqual(
            "a notnull int 4 - - computed 56 4 | b notnull varchar(1) 1 - - computed 167 1 | c null int 4 - - computed 38 4 | d notnull numeric(2,1) 5 - - computed 108 17 | - null bigint 8 - - computed 38 8",
            Describe(Seeded(), "select 1 as a, 'x' as b, null as c, 1.5 as d, cast(1 as bigint) + 1"));

    [TestMethod]
    public void AggregatesAndWindows_AreNotComputed()
        => AreEqual(
            "id notnull int 4 identity - - 56 4 | - null int 4 - - - 38 4",
            Describe(Seeded(), "select id, count(*) from da group by id"));

    [TestMethod]
    public void ViewColumns_TraceToTheirBaseColumns()
        => AreEqual(
            "id notnull int 4 identity - - 56 4 | v notnull varchar(10) 10 - updateable - 167 10",
            Describe(Seeded(), "select * from dv"));

    [TestMethod]
    public void Parameters_AreDeclared()
        => AreEqual("p null int 4 - - computed 38 4", Describe(Seeded(), "select @p as p", "@p int"));

    [TestMethod]
    public void FirstResultAfterOtherStatements_AndNothingRuns()
    {
        var sim = Seeded();
        AreEqual("w null nvarchar(20) 40 - updateable - 231 40", Describe(sim, "insert db values (1, N'a'); declare @x int = 1; select w from db"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from db"));
    }

    [TestMethod]
    public void NoResultSet_DescribesNothing()
        => AreEqual(string.Empty, Describe(Seeded(), "insert db values (1, N'a')"));

    [TestMethod]
    public void CompileError_IsFollowedByMsg11501()
    {
        var ex = Seeded().AssertSqlError("exec sp_describe_first_result_set N'select nosuch from da'", 207);
        AreEqual(11501, ex.Errors[1].Number);
    }

    [TestMethod]
    public void MissingObject_IsFollowedByMsg11529()
    {
        var ex = Seeded().AssertSqlError("exec sp_describe_first_result_set N'select * from nosuch'", 208);
        AreEqual(11529, ex.Errors[1].Number);
    }

    [TestMethod]
    [DataRow("select id, n from da union all select 1, 2", "notnull null")]
    [DataRow("select id, id from da union select n, 2 from da", "null notnull")]
    [DataRow("select id, n from da intersect select n, id from da", "notnull notnull")]
    [DataRow("select id, n from da except select n, id from da", "notnull null")]
    public void SetOperation_CombinesItsBranchesNullability(string tsql, string expected)
        => AreEqual(expected, string.Join(" ", Describe(Seeded(), tsql).Split(" | ").Select(column => column.Split(' ')[1])));

    [TestMethod]
    public void ConstantThatFailsToConvert_IsDescribedNotRun()
        => AreEqual("x null int 4 - - computed 38 4", Describe(Seeded(), "select cast('a' as int) as x"));

    [TestMethod]
    public void MalformedParameterList_IsFollowedByMsg11501()
    {
        var ex = Seeded().AssertSqlError("exec sp_describe_first_result_set N'select 1', N'@p'", 102);
        AreEqual(11501, ex.Errors[1].Number);
    }

    // ---- sys.dm_exec_describe_first_result_set ----

    [TestMethod]
    public void Dmv_DescribesAsTheProcedureDoes()
    {
        using var reader = Seeded().ExecuteReader(
            "select column_ordinal, name, is_nullable, system_type_name, max_length, is_identity_column, error_number from sys.dm_exec_describe_first_result_set(N'select id, v from da', null, 0)");
        AreEqual(7, reader.FieldCount);
        AreEqual(
            "1 id False int 4 True - | 2 v False varchar(10) 10 False -",
            string.Join(" | ", reader.EnumerateRecords().Select(r => $"{r.GetInt32(0)} {r.GetString(1)} {r.GetBoolean(2)} {r.GetString(3)} {r.GetInt16(4)} {r.GetBoolean(5)} {(r.IsDBNull(6) ? "-" : r.GetInt32(6))}")));
    }

    [TestMethod]
    [DataRow("select nosuch from da", "0 207 16 1 SYNTAX Invalid column name 'nosuch'. | 1 11501 16 1 SYNTAX The batch could not be analyzed because of compile errors.")]
    [DataRow("select * from nosuch", "0 208 16 1 SYNTAX Invalid object name 'nosuch'. | 1 11529 16 1 MISC The metadata could not be determined because every code path results in an error; see previous errors for some of these.")]
    [DataRow("select from", "0 156 15 1 SYNTAX Incorrect syntax near the keyword 'from'. | 1 11501 16 1 SYNTAX The batch could not be analyzed because of compile errors.")]
    public void Dmv_AnswersErrorsAsRows(string tsql, string expected)
    {
        using var connection = Seeded().CreateOpenConnection();
        using var command = connection.CreateCommand(
            "select column_ordinal, error_number, error_severity, error_state, error_type_desc, error_message, name from sys.dm_exec_describe_first_result_set(@t, null, 0)",
            ("@t", tsql));
        using var reader = command.ExecuteReader();
        AreEqual(expected, string.Join(" | ", reader.EnumerateRecords().Select(r =>
        {
            IsTrue(r.IsDBNull(6));
            return $"{r.GetInt32(0)} {r.GetInt32(1)} {r.GetInt32(2)} {r.GetInt32(3)} {r.GetString(4)} {r.GetString(5)}";
        })));
    }

    [TestMethod]
    public void Dmv_TakesParametersAndDescribesNothingForNullOrNoResult()
    {
        var sim = Seeded();
        AreEqual("int", sim.ExecuteScalar("select system_type_name from sys.dm_exec_describe_first_result_set(N'select @p + 1 as x', N'@p int', 0)"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.dm_exec_describe_first_result_set(null, null, 0)"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.dm_exec_describe_first_result_set(N'print 1', null, null)"));
    }

    [TestMethod]
    public void Dmv_RequiresExactlyThreeArguments()
    {
        var sim = Seeded();
        sim.AssertSqlError("select * from sys.dm_exec_describe_first_result_set(N'select 1', null)", 313,
            "An insufficient number of arguments were supplied for the procedure or function sys.dm_exec_describe_first_result_set.");
        sim.AssertSqlError("select * from sys.dm_exec_describe_first_result_set(N'select 1', null, 0, 1)", 8144,
            "Procedure or function sys.dm_exec_describe_first_result_set has too many arguments specified.");
    }
}
