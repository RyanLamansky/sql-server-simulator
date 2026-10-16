using System.Data.Common;
using System.Globalization;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// SQL Trace: <c>sp_trace_create</c>, <c>sp_trace_setevent</c>,
/// <c>sp_trace_setfilter</c>, <c>sp_trace_setstatus</c> and
/// <c>sp_trace_generateevent</c>, the <c>fn_trace_get*</c> functions, the
/// catalog views and the events a running trace writes, in SQL Server 2025's
/// shapes (probed 2026-10-16).
/// </summary>
[TestClass]
public sealed class SqlTraceTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Creates a stopped trace writing <paramref name="file"/> and answers its id.</summary>
    private static int Create(Simulation sim, string file = "/var/opt/mssql/log/t", int options = 0, string tail = "") =>
        (int)sim.ExecuteScalar($"""
            declare @id int, @one bigint = 1, @five bigint = 5, @seven bigint = 7, @stop datetime = '2099-01-01', @soon datetime = dateadd(second, 1, getdate());
            exec sp_trace_create @id output, {options}, N'{file}'{tail}; select @id
            """)!;

    /// <summary>A procedure's return code and the error it raised, if any, for one call outside any <c>TRY</c>.</summary>
    private static (int? Code, int? Error) Call(Simulation sim, string call, string declarations = "")
    {
        using var connection = sim.CreateOpenConnection();
        using (var create = connection.CreateCommand("create table #rc (rc int)"))
            _ = create.ExecuteNonQuery();
        int? error = null;
        try
        {
            using var command = connection.CreateCommand($"declare @rc int, @on bit = 1{declarations}; exec @rc = {call}; insert #rc values (@rc)");
            _ = command.ExecuteNonQuery();
        }
        catch (SimulatedSqlException ex)
        {
            error = ex.Number;
        }
        using var read = connection.CreateCommand("select rc from #rc");
        return (read.ExecuteScalar() is int code ? code : null, error);
    }

    /// <summary>Asserts one call's return code and error.</summary>
    private static void Expect(Simulation sim, int? code, int? error, string call, string declarations = "") =>
        AreEqual((code, error), Call(sim, call, declarations));

    /// <summary>Each row's columns as <c>name=value</c>, the NULL ones left out, for the rows <paramref name="query"/> reads.</summary>
    private static List<string> Rows(Simulation sim, string query)
    {
        using var reader = sim.ExecuteReader(query);
        var rows = new List<string>();
        while (reader.Read())
        {
            var fields = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (!reader.IsDBNull(i))
                    fields.Add(reader.GetName(i) + "=" + Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture));
            }
            rows.Add(string.Join(' ', fields));
        }
        return rows;
    }

    /// <summary>Each row's one value, for a query selecting one unnamed column.</summary>
    private static List<string> Values(Simulation sim, string query) =>
        Rows(sim, query).ConvertAll(static row => row[(row.IndexOf('=', StringComparison.Ordinal) + 1)..]);

    private static void Run(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand(sql);
        try
        {
            _ = command.ExecuteNonQuery();
        }
        catch (SimulatedSqlException)
        {
        }
    }

    [TestMethod]
    public void Create_AnswersTheLowestFreeIdAndListsTheStoppedTrace()
    {
        var sim = new Simulation();
        AreEqual(2, Create(sim, "/var/opt/mssql/log/a"));
        AreEqual(3, Create(sim, "/var/opt/mssql/log/b", options: 6));
        _ = sim.ExecuteNonQuery("exec sp_trace_setstatus 2, 2");
        AreEqual(2, Create(sim, "/var/opt/mssql/log/c.trc"));
        using var reader = sim.ExecuteReader("select id, status, path, max_size, max_files, is_rollover, is_shutdown, buffer_count, file_position, start_time from sys.traces where id > 1 order by id");
        IsTrue(reader.Read());
        AreEqual("/var/opt/mssql/log/c.trc.trc", reader.GetString(2));
        AreEqual(0, reader.GetInt32(1));
        AreEqual(5L, reader.GetInt64(3));
        AreEqual(1, reader.GetInt32(4));
        AreEqual(2, reader.GetInt32(7));
        AreEqual(1048576L, reader.GetInt64(8));
        IsTrue(reader.IsDBNull(9));
        IsTrue(reader.Read());
        AreEqual(0, reader.GetInt32(4));
        IsTrue(reader.GetBoolean(5));
        IsTrue(reader.GetBoolean(6));
    }

    [TestMethod]
    [DataRow("sp_trace_create @id output, 1, N'/var/opt/mssql/log/x'", 15, 19065)]
    [DataRow("sp_trace_create @id output, 16, N'/var/opt/mssql/log/x'", 10, 19060)]
    [DataRow("sp_trace_create @id output, 3, N'/var/opt/mssql/log/x'", 10, 19060)]
    [DataRow("sp_trace_create @id output, 10, NULL", 10, 19060)]
    [DataRow("sp_trace_create @id output, 8, N'/var/opt/mssql/log/x'", 15, 19065)]
    [DataRow("sp_trace_create @id output, 8, NULL", 1, 19051)]
    [DataRow("sp_trace_create @id output, 0, NULL", 15, 19065)]
    [DataRow("sp_trace_create @id output, 0, N''", 15, 19065)]
    [DataRow("sp_trace_create @id output, 0", 15, 19065)]
    [DataRow("sp_trace_create @id output, 0, N'/nosuch/x'", 12, 19062)]
    [DataRow("sp_trace_create @id output, 0, N'/var/opt/mssql/log/x', @zero", 15, 19065)]
    [DataRow("sp_trace_create @id output, 0, N'/var/opt/mssql/log/x', NULL, '2000-01-01'", 15, 214)]
    [DataRow("sp_trace_create @id output, 0, N'/var/opt/mssql/log/x', NULL, @past", 14, 19064)]
    [DataRow("sp_trace_create @id output, 0, N'/var/opt/mssql/log/x', NULL, NULL, 3", 15, 19065)]
    [DataRow("sp_trace_create @id output, 2, N'/var/opt/mssql/log/x', NULL, NULL, 1", 15, 19065)]
    [DataRow("sp_trace_create @id output, 2, N'/var/opt/mssql/log/x', NULL, NULL, -1", 15, 19065)]
    [DataRow("sp_trace_create @id output, 0, '/var/opt/mssql/log/x'", 15, 214)]
    [DataRow("sp_trace_create @id output, @small, N'/var/opt/mssql/log/x'", 15, 214)]
    [DataRow("sp_trace_create @id output, NULL, N'/var/opt/mssql/log/x'", 15, 214)]
    [DataRow("sp_trace_create @id output, 0, N'/var/opt/mssql/log/x', 5", 15, 214)]
    [DataRow("sp_trace_create @id output, 0, N'/var/opt/mssql/log/x', default", 15, 214)]
    [DataRow("sp_trace_create @big output, 0, N'/var/opt/mssql/log/x'", 15, 214)]
    [DataRow("sp_trace_create @id output, 2, N'/var/opt/mssql/log/x', NULL, NULL, 0", 0, null)]
    [DataRow("sp_trace_create @id output, 0, N'/var/opt/mssql/log/x', @five, @future", 0, null)]
    [DataRow("sp_trace_create @foo = @id output, @bar = 0, @baz = N'/var/opt/mssql/log/x'", 0, null)]
    [DataRow("sp_trace_create @id output, 0, N'relative'", 0, null)]
    [DataRow("sp_trace_create @id output", null, 201)]
    public void Create_RefusesByErrorAndReturnCode(string call, int? code, int? error) =>
        AreEqual((code, error), Call(new Simulation(), call,
            ", @id int, @big bigint, @small smallint = 0, @zero bigint = 0, @five bigint = 5, @past datetime = '2000-01-01', @future datetime2 = '2099-01-01'"));

    [TestMethod]
    public void Create_SendsTheOperatingSystemErrorAheadOfMsg19062ForAFileThatExists()
    {
        var sim = new Simulation();
        _ = Create(sim, "/var/opt/mssql/log/dup");
        using var connection = sim.CreateOpenConnection();
        var messages = new List<string>();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(static error => $"{error.Number}:{error.Message}"));
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("declare @id int; exec sp_trace_create @id output, 0, N'/var/opt/mssql/log/dup'").ExecuteNonQuery());
        AreEqual(19062, ex.Number);
        Contains("19097:Operating system error occurred while running SP_TRACE_CREATE. Error = 0x80070050(The file exists.).",
            string.Join('\n', messages.Concat(ex.Errors.Cast<SimulatedError>().Select(static error => $"{error.Number}:{error.Message}"))));
    }

    [TestMethod]
    public void Create_AStopTimePassedLeavesTheIdUnsetAndATryLeavesTheReturnCodeUnset() =>
        AreEqual("19064 NULL | 19060 -5 0", new Simulation().ExecuteScalar("""
            declare @id int, @rc int = -5, @past datetime = '2000-01-01', @a varchar(20), @b varchar(20);
            begin try exec @rc = sp_trace_create @id output, 0, N'/var/opt/mssql/log/x', NULL, @past; end try begin catch set @a = concat(error_number(), ' ', isnull(cast(@id as varchar), 'NULL')); end catch;
            begin try exec @rc = sp_trace_create @id output, 16, N'/var/opt/mssql/log/x'; end try begin catch set @b = concat(error_number(), ' ', @rc, ' ', @id); end catch;
            select concat(@a, ' | ', @b)
            """));

    [TestMethod]
    public void Arguments_ExtraOnesEndTheBatchAndAnInputTraceIdEndsIt()
    {
        var sim = new Simulation();
        AreEqual(4, sim.AssertSqlError("declare @id int; exec sp_trace_create @id output, 0, N'/var/opt/mssql/log/x', NULL, NULL, NULL, 7; select 1", 8144).State);
        AreEqual(57, sim.AssertSqlError("declare @id int; exec sp_trace_create @id, 0, N'/var/opt/mssql/log/x'; select 1", 591).State);
        AreEqual(15, sim.AssertSqlError("exec sp_trace_create", 201).State);
        AreEqual(17, sim.AssertSqlError("declare @id int; exec sp_trace_create @id output", 201).State);
        AreEqual(1, sim.AssertSqlError("exec sp_trace_generateevent 82, N'x', 0x01, 5", 8144).State);
    }

    [TestMethod]
    public void SetEvent_ListsEachEventWithSpidAndServerNameTraceWide()
    {
        var sim = new Simulation();
        var id = Create(sim);
        _ = sim.ExecuteNonQuery($"""
            declare @on bit = 1, @off bit = 0;
            exec sp_trace_setevent {id}, 12, 27, @on; exec sp_trace_setevent {id}, 12, 51, @on; exec sp_trace_setevent {id}, 13, 1, @on;
            exec sp_trace_setevent {id}, 13, 26, @on; exec sp_trace_setevent {id}, 14, 1, @on;
            exec sp_trace_setevent {id}, 15, 1, @on; exec sp_trace_setevent {id}, 15, 1, @off;
            exec sp_trace_setevent {id}, 16, NULL, @on; exec sp_trace_setevent {id}, 82, 2, @on
            """);
        CollectionAssert.AreEqual(
            new[] { "12:12", "12:26", "12:51", "13:1", "13:12", "13:26", "14:1", "14:12", "14:26", "16:12", "16:26", "82:2", "82:12", "82:26" },
            Values(sim, $"select concat(eventid, ':', columnid) from sys.fn_trace_geteventinfo({id})"));
        _ = sim.ExecuteNonQuery($"declare @off bit = 0; exec sp_trace_setevent {id}, 13, 26, @off; exec sp_trace_setevent {id}, 14, NULL, @off");
        AreEqual(0, sim.ExecuteScalar($"select count(*) from sys.fn_trace_geteventinfo({id}) where columnid = 26 or eventid = 14"));
    }

    [TestMethod]
    [DataRow("sp_trace_setevent @id, 1, 1, @on", 3, 19053)]
    [DataRow("sp_trace_setevent @id, 300, 1, @on", 3, 19053)]
    [DataRow("sp_trace_setevent @id, 12, 67, @on", 4, 19054)]
    [DataRow("sp_trace_setevent @id, 12, 0, @on", 4, 19054)]
    [DataRow("sp_trace_setevent @id, 12, 12, 0", 11, 19061)]
    [DataRow("sp_trace_setevent 99, 12, 1, @on", 9, 19059)]
    [DataRow("sp_trace_setevent 1, 12, 1, @on", 20, 19070)]
    [DataRow("sp_trace_setevent @id, 12, 1, NULL", 15, 214)]
    [DataRow("sp_trace_setevent @id, 12, 1, @nobit", 15, 214)]
    [DataRow("sp_trace_setevent @id, 12, 1, '1'", 15, 214)]
    [DataRow("sp_trace_setevent @id, @small, 1, @on", 15, 214)]
    [DataRow("sp_trace_setevent @id, 12, 1, 2", 0, null)]
    [DataRow("sp_trace_setevent @id, 12, 1, @big", 0, null)]
    [DataRow("sp_trace_setevent @id, 12, NULL, @on", 0, null)]
    [DataRow("sp_trace_setevent @id, 12, 1", null, 201)]
    public void SetEvent_RefusesByErrorAndReturnCode(string call, int? code, int? error)
    {
        var sim = new Simulation();
        var id = Create(sim);
        AreEqual((code, error), Call(sim, call, $", @id int = {id}, @nobit bit = NULL, @small smallint = 12, @big bigint = 1"));
    }

    [TestMethod]
    public void ARunningTrace_RefusesChangesAndClosingAndTheDefaultTraceRefusesEveryChange()
    {
        var sim = new Simulation();
        var id = Create(sim);
        _ = sim.ExecuteNonQuery($"declare @on bit = 1; exec sp_trace_setevent {id}, 82, 1, @on; exec sp_trace_setstatus {id}, 1");
        Expect(sim, 2, 19052, $"sp_trace_setevent {id}, 82, 2, @on");
        Expect(sim, 2, 19052, $"sp_trace_setfilter {id}, 1, 0, 6, N'x'");
        Expect(sim, 2, 19052, $"sp_trace_setstatus {id}, 2");
        Expect(sim, 0, null, $"sp_trace_setstatus {id}, 1");
        Expect(sim, 0, null, $"sp_trace_setstatus {id}, 3");
        Expect(sim, 8, 19058, $"sp_trace_setstatus {id}, -1");
        Expect(sim, 20, 19070, "sp_trace_setstatus 1, 0");
        Expect(sim, 0, null, $"sp_trace_setstatus {id}, 0");
        Expect(sim, 0, null, $"sp_trace_setstatus {id}, 2");
        Expect(sim, 9, 19059, $"sp_trace_setstatus {id}, 2");
    }

    [TestMethod]
    [DataRow("sp_trace_setfilter @id, 12, 0, 6, N'x'", 15, 214)]
    [DataRow("sp_trace_setfilter @id, 12, 0, 0, N'55'", 15, 214)]
    [DataRow("sp_trace_setfilter @id, 3, 0, 0, @bigv", 15, 214)]
    [DataRow("sp_trace_setfilter @id, 13, 0, 4, 1000", 15, 214)]
    [DataRow("sp_trace_setfilter @id, 13, 0, 4, @bigv", 0, null)]
    [DataRow("sp_trace_setfilter @id, 1, 0, 2, N'select'", 6, 19056)]
    [DataRow("sp_trace_setfilter @id, 1, 0, 6, N''", 6, 19056)]
    [DataRow("sp_trace_setfilter @id, 1, 0, 6, NULL", 6, 19056)]
    [DataRow("sp_trace_setfilter @id, 1, 0, 0, N''", 0, null)]
    [DataRow("sp_trace_setfilter @id, 11, 0, 6, 'sa'", 15, 214)]
    [DataRow("sp_trace_setfilter @id, 11, 0, 6, @variant", 15, 214)]
    [DataRow("sp_trace_setfilter @id, 12, 0, 8, 55", 6, 19056)]
    [DataRow("sp_trace_setfilter @id, 12, 2, 0, 55", 7, 19057)]
    [DataRow("sp_trace_setfilter @id, 27, 0, 0, 12", 4, 19054)]
    [DataRow("sp_trace_setfilter @id, 26, 0, 0, N'x'", 4, 19054)]
    [DataRow("sp_trace_setfilter @id, 99, 0, 0, 1", 4, 19054)]
    [DataRow("sp_trace_setfilter @id, 2, 0, 0, 0x01", 6, 19056)]
    [DataRow("sp_trace_setfilter @id, 14, 0, 2, N'2020-01-01'", 15, 214)]
    [DataRow("sp_trace_setfilter @id, 14, 0, 2, @when", 0, null)]
    [DataRow("sp_trace_setfilter @id, 54, 0, 6, @guid", 6, 19056)]
    [DataRow("sp_trace_setfilter @id, 3, 0, 6, NULL", 6, 19056)]
    [DataRow("sp_trace_setfilter @id, 3, 0, 1, NULL", 0, null)]
    [DataRow("sp_trace_setfilter 99, 12, 0, 0, 1", 9, 19059)]
    [DataRow("sp_trace_setfilter 1, 12, 0, 0, 1", 20, 19070)]
    [DataRow("sp_trace_setfilter @id, 12, 0, 0", null, 201)]
    public void SetFilter_RefusesByErrorAndReturnCode(string call, int? code, int? error)
    {
        var sim = new Simulation();
        var id = Create(sim);
        AreEqual((code, error), Call(sim, call, $", @id int = {id}, @bigv bigint = 5, @variant sql_variant = N'sa', @when datetime = '2020-01-01', @guid uniqueidentifier = newid()"));
    }

    [TestMethod]
    public void SetFilter_KeepsFiltersInOrderGroupedByColumnAndANullColumnClearsThem()
    {
        var sim = new Simulation();
        var id = Create(sim);
        Expect(sim, 0, null, $"sp_trace_setfilter {id}, 10, 0, 7, N'SQL Server Profiler%'");
        Expect(sim, 0, null, $"sp_trace_setfilter {id}, 12, 0, 0, 55");
        Expect(sim, 5, 19055, $"sp_trace_setfilter {id}, 10, 1, 6, N'x'");
        _ = sim.ExecuteNonQuery($"""
            declare @big bigint = 1000, @when datetime = '2020-01-01', @guid uniqueidentifier = newid();
            exec sp_trace_setfilter {id}, 12, 1, 2, 60; exec sp_trace_setfilter {id}, 13, 0, 4, @big;
            exec sp_trace_setfilter {id}, 14, 0, 2, @when; exec sp_trace_setfilter {id}, 54, 0, 0, @guid
            """);
        CollectionAssert.AreEqual(new[]
        {
            "10 0 7 SQL Server Profiler% nvarchar 40",
            "12 0 0 55 int 4",
            "12 1 2 60 int 4",
            "13 0 4 1000 bigint 8",
            "14 0 2 2020-01-01 datetime 8",
            "54 0 0   ",
        }, Values(sim, $"""
            select concat_ws(' ', columnid, logical_operator, comparison_operator, isnull(convert(nvarchar(30), value, 23), ''),
                isnull(cast(sql_variant_property(value, 'BaseType') as nvarchar), ''), isnull(cast(sql_variant_property(value, 'MaxLength') as nvarchar), ''))
            from sys.fn_trace_getfilterinfo({id})
            """));
        _ = sim.ExecuteNonQuery($"exec sp_trace_setfilter {id}, NULL, 0, 0, 5");
        AreEqual(0, sim.ExecuteScalar($"select count(*) from sys.fn_trace_getfilterinfo({id})"));
    }

    [TestMethod]
    public void GetInfo_ListsTheDefaultTraceThenEachUserTraceAsVariants()
    {
        var sim = new Simulation();
        _ = Create(sim, "/var/opt/mssql/log/fi", options: 2, tail: ", @seven, @stop");
        CollectionAssert.AreEqual(new[]
        {
            "1 1 2 int", "1 2 /var/opt/mssql/log/log_1.trc nchar", "1 3 20 bigint", "1 4  ", "1 5 1 int",
            "2 1 2 int", "2 2 /var/opt/mssql/log/fi.trc nchar", "2 3 7 bigint", "2 4 2099-01-01 datetime", "2 5 0 int",
        }, Values(sim, """
            select concat_ws(' ', traceid, property, isnull(convert(nvarchar(40), value, 23), ''), isnull(cast(sql_variant_property(value, 'BaseType') as nvarchar), ''))
            from sys.fn_trace_getinfo(default)
            """));
        AreEqual(5, sim.ExecuteScalar("select count(*) from ::fn_trace_getinfo(2)"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from fn_trace_getinfo(99)"));
        AreEqual(3, sim.AssertSqlError("select * from sys.fn_trace_getinfo()", 313).State);
        AreEqual(769, sim.ExecuteScalar("select count(*) from ::fn_trace_geteventinfo(1)"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.fn_trace_geteventinfo(0)"));
    }

    [TestMethod]
    public void CatalogViews_ListRealsRows()
    {
        var sim = new Simulation();
        AreEqual("21 66 180 6279 4316 139 22", sim.ExecuteScalar("""
            select concat_ws(' ', (select count(*) from sys.trace_categories), (select count(*) from sys.trace_columns),
                (select count(*) from sys.trace_events), (select count(*) from sys.trace_subclass_values),
                (select count(*) from sys.trace_event_bindings), (select count(*) from sys.trace_xe_event_map),
                (select count(*) from sys.trace_xe_action_map))
            """));
        AreEqual("RPC:Completed 11", sim.ExecuteScalar("select top 1 concat(name, ' ', category_id) from sys.trace_events"));
        AreEqual("TextData text 1073741824 1 0 0", sim.ExecuteScalar("""
            select concat_ws(' ', name, type_name, max_size, cast(is_filterable as int), cast(is_repeatable as int), cast(is_repeated_base as int))
            from sys.trace_columns where trace_column_id = 1
            """));
        AreEqual("sqlserver.user_event", sim.ExecuteScalar("select concat(package_name, '.', xe_event_name) from sys.trace_xe_event_map where trace_event_id = 82"));
        AreEqual(-486135952, sim.ExecuteScalar("select object_id('sys.trace_events')"));
        AreEqual("X ", sim.ExecuteScalar("select type from sys.all_objects where name = 'sp_trace_create'"));
        AreEqual(-905021835, sim.ExecuteScalar("select object_id('sys.fn_trace_getinfo')"));
    }

    [TestMethod]
    public void RunningTrace_WritesTheBatchStatementAndErrorEventsWithRealsColumns()
    {
        var sim = new Simulation();
        var id = Create(sim, "/var/opt/mssql/log/cap");
        _ = sim.ExecuteNonQuery($"""
            declare @on bit = 1, @e int, @c int;
            declare c cursor local for select trace_event_id, trace_column_id from sys.trace_event_bindings where trace_event_id in (12, 13, 40, 41, 162, 33);
            open c; fetch c into @e, @c;
            while @@fetch_status = 0 begin exec sp_trace_setevent {id}, @e, @c, @on; fetch c into @e, @c; end
            exec sp_trace_setfilter {id}, 10, 0, 6, N'app%';
            exec sp_trace_setstatus {id}, 1
            """);
        using (var client = sim.CreateDbConnection())
        {
            client.ConnectionString = "Application Name=app";
            client.Open();
            Run(client, "select 2 as b; select 3 as c");
            Run(client, "select 1/0");
        }
        _ = sim.ExecuteNonQuery($"exec sp_trace_setstatus {id}, 0");
        CollectionAssert.AreEqual(new[]
        {
            "162 Changed database context to 'simulated'. 5701 10 2",
            "162 Changed language setting to us_english. 5703 10 1",
            "13 select 2 as b; select 3 as c",
            "40 select 2 as b 1 0 24 0",
            "41 select 2 as b 1 0 24 1 1 0",
            "40 select 3 as c 1 30 54 0",
            "41 select 3 as c 1 30 54 1 1 0",
            "12 select 2 as b; select 3 as c 0 0",
            "13 select 1/0",
            "40 select 1/0 1 0 18 0",
            "33 Divide by zero error encountered. 8134 16 1",
            "162 Divide by zero error encountered. 8134 16 1",
            "41 select 1/0 1 0 18 0 0 0",
            "12 select 1/0 0 0",
        }, Values(sim, """
            select concat_ws(' ', EventClass, cast(TextData as nvarchar(100)), LineNumber, Offset, IntegerData2, IntegerData, RowCounts, Error, Severity, State, NestLevel)
            from sys.fn_trace_gettable(N'/var/opt/mssql/log/cap.trc', default) where EventClass < 65000 and ApplicationName = 'app'
            """));
        CollectionAssert.AreEqual(new[] { "65528", "65534", "65533" },
            Values(sim, "select EventClass from sys.fn_trace_gettable(N'/var/opt/mssql/log/cap.trc', default) where EventClass > 65000"));
        AreEqual(1, sim.ExecuteScalar("""
            select count(distinct SPID) from sys.fn_trace_gettable(N'/var/opt/mssql/log/cap.trc', default)
            where ApplicationName = 'app' and LoginName = 'sa' and DatabaseName = 'simulated' and ServerName = 'SIMULATED' and EventSequence > 0
            """));
    }

    [TestMethod]
    public void HeaderRow_CarriesRealsHeaderBlock()
    {
        var sim = new Simulation();
        var id = Create(sim, "/var/opt/mssql/log/h", options: 2, tail: ", @one");
        _ = sim.ExecuteNonQuery($"declare @on bit = 1; exec sp_trace_setevent {id}, 17, 1, @on; exec sp_trace_setevent {id}, 17, 10, @on; exec sp_trace_setevent {id}, 41, 1, @on; exec sp_trace_setstatus {id}, 1; exec sp_trace_setstatus {id}, 0");
        var header = (byte[])sim.ExecuteScalar("select BinaryData from sys.fn_trace_gettable(N'/var/opt/mssql/log/h.trc', default) where EventClass = 65528")!;
        AreEqual("FFFE900201004D006900630072006F0073006F0066007400", Convert.ToHexString(header, 0, 24));
        AreEqual("1100E10F03000000", Convert.ToHexString(header, 390, 8));
        AreEqual("0C00EDFB00FCFF08110001000A000C00FCFF06290001000C00FCFB0801000A000C001B00", Convert.ToHexString(header, 654, header.Length - 654));
    }

    [TestMethod]
    public void Start_ReportsEachOpenSessionAsAnExistingConnection()
    {
        var sim = new Simulation();
        var id = Create(sim, "/var/opt/mssql/log/ec");
        using var other = sim.CreateDbConnection();
        other.ConnectionString = "Application Name=other";
        other.Open();
        _ = sim.ExecuteNonQuery($"declare @on bit = 1; exec sp_trace_setevent {id}, 17, 10, @on; exec sp_trace_setevent {id}, 17, 25, @on; exec sp_trace_setevent {id}, 17, 1, @on; exec sp_trace_setstatus {id}, 1; exec sp_trace_setstatus {id}, 0");
        AreEqual("8000 -- network protocol: TCP/IP\r\n", sim.ExecuteScalar("""
            select concat(IntegerData, ' ', left(cast(TextData as nvarchar(max)), 29))
            from sys.fn_trace_gettable(N'/var/opt/mssql/log/ec.trc', default) where EventClass = 17 and ApplicationName = 'other'
            """));
    }

    [TestMethod]
    public void Filters_JoinAColumnsFiltersByTheirOwnOperatorAndTheColumnsByAnd()
    {
        var sim = new Simulation();
        void Trace(string file, string filters)
        {
            var id = Create(sim, "/var/opt/mssql/log/" + file).ToString(CultureInfo.InvariantCulture);
            _ = sim.ExecuteNonQuery($"declare @on bit = 1, @db int = db_id(); exec sp_trace_setevent {id}, 82, 1, @on; {filters.Replace("@t", id, StringComparison.Ordinal)}; exec sp_trace_setstatus {id}, 1");
        }
        Trace("f1", "exec sp_trace_setfilter @t, 1, 0, 6, N'a%'; exec sp_trace_setfilter @t, 1, 1, 6, N'b%'");
        Trace("f2", "exec sp_trace_setfilter @t, 1, 0, 6, N'a%'; exec sp_trace_setfilter @t, 3, 1, 0, 9999");
        Trace("f4", "exec sp_trace_setfilter @t, 1, 0, 7, N'a%'");
        Trace("f5", "exec sp_trace_setfilter @t, 1, 0, 0, N'ABC'");
        Trace("f6", "exec sp_trace_setfilter @t, 1, 0, 1, NULL");
        Trace("f7", "exec sp_trace_setfilter @t, 3, 0, 0, @db; exec sp_trace_setfilter @t, 3, 1, 0, 9999; exec sp_trace_setfilter @t, 1, 0, 6, N'_b[a-c]%'");
        _ = sim.ExecuteNonQuery("""
            exec sp_trace_generateevent 82, N'abc'; exec sp_trace_generateevent 82, N'Abc'; exec sp_trace_generateevent 82, N'bcd';
            exec sp_trace_generateevent 82, N'cde'; exec sp_trace_generateevent 82, NULL; exec sp_trace_generateevent 82, N'abc  ';
            exec sp_trace_generateevent 82, N'xbz'
            """);
        string Read(string file) => string.Join(',', Values(sim, $"select isnull(rtrim(cast(TextData as nvarchar(20))), '-') from sys.fn_trace_gettable(N'/var/opt/mssql/log/{file}.trc', default) where EventClass = 82"));
        AreEqual("abc,Abc,bcd,-,abc", Read("f1"));
        AreEqual("abc,Abc,-,abc", Read("f2"));
        AreEqual("bcd,cde,-,xbz", Read("f4"));
        AreEqual("abc,Abc,-,abc", Read("f5"));
        AreEqual("abc,Abc,bcd,cde,abc,xbz", Read("f6"));
        AreEqual("abc,Abc,-,abc", Read("f7"));
    }

    [TestMethod]
    public void GenerateEvent_WritesEachUserConfigurableClassAndRefusesOthers()
    {
        var sim = new Simulation();
        var id = Create(sim, "/var/opt/mssql/log/g");
        _ = sim.ExecuteNonQuery($"declare @on bit = 1; exec sp_trace_setevent {id}, 82, 1, @on; exec sp_trace_setevent {id}, 82, 2, @on; exec sp_trace_setevent {id}, 91, 1, @on; exec sp_trace_setstatus {id}, 1");
        Expect(sim, 0, null, "sp_trace_generateevent 82, N'hello', 0x0102");
        Expect(sim, 0, null, "sp_trace_generateevent 91, N'last'");
        Expect(sim, 0, null, "sp_trace_generateevent 83, N'not taken'");
        Expect(sim, 3, 19053, "sp_trace_generateevent 81, N'x'");
        Expect(sim, 3, 19053, "sp_trace_generateevent 92, N'x'");
        Expect(sim, 15, 214, "sp_trace_generateevent 82, 'varchar'");
        Expect(sim, 15, 214, "sp_trace_generateevent 82, @long", ", @long nvarchar(200) = replicate(N'a', 200)");
        CollectionAssert.AreEqual(new[] { "82 hello 0x0102", "91 last" }, Values(sim, """
            select concat_ws(' ', EventClass, cast(TextData as nvarchar(20)), convert(varchar(10), cast(BinaryData as varbinary(10)), 1))
            from sys.fn_trace_gettable(N'/var/opt/mssql/log/g.trc', default) where EventClass < 65000
            """));
    }

    [TestMethod]
    public void FullFile_RollsOverAndFnTraceGetTableReadsOnIntoTheNextFile()
    {
        var sim = new Simulation();
        var id = Create(sim, "/var/opt/mssql/log/r", options: 2, tail: ", @one, NULL, 2");
        _ = sim.ExecuteNonQuery($"declare @on bit = 1; exec sp_trace_setevent {id}, 82, 1, @on; exec sp_trace_setstatus {id}, 1");
        _ = sim.ExecuteNonQuery("declare @i int = 0, @text nvarchar(128) = replicate(N'x', 128); while @i < 4500 begin exec sp_trace_generateevent 82, @text; set @i += 1; end");
        AreEqual("/var/opt/mssql/log/r_1.trc", sim.ExecuteScalar($"select path from sys.traces where id = {id}"));
        var first = (int)sim.ExecuteScalar("select count(*) from sys.fn_trace_gettable(N'/var/opt/mssql/log/r.trc', 1) where EventClass = 82")!;
        var second = (int)sim.ExecuteScalar("select count(*) from sys.fn_trace_gettable(N'/var/opt/mssql/log/r_1.trc', default) where EventClass = 82")!;
        IsTrue(first > 0 && second > 0);
        AreEqual(4500, first + second);
        AreEqual(4500, sim.ExecuteScalar("select count(*) from sys.fn_trace_gettable(N'/var/opt/mssql/log/r.trc', default) where EventClass = 82"));
        AreEqual(65527, sim.ExecuteScalar("select top 1 EventClass from sys.fn_trace_gettable(N'/var/opt/mssql/log/r_1.trc', default) where EventClass = 65527"));
        AreEqual(4500L, sim.ExecuteScalar($"select event_count from sys.traces where id = {id}"));
    }

    [TestMethod]
    public void FullFile_WithoutRolloverStopsAndClosesTheTrace()
    {
        var sim = new Simulation();
        var id = Create(sim, "/var/opt/mssql/log/full", tail: ", @one");
        _ = sim.ExecuteNonQuery($"declare @on bit = 1; exec sp_trace_setevent {id}, 82, 1, @on; exec sp_trace_setstatus {id}, 1");
        _ = sim.ExecuteNonQuery("declare @i int = 0, @text nvarchar(128) = replicate(N'x', 128); while @i < 4500 begin exec sp_trace_generateevent 82, @text; set @i += 1; end");
        AreEqual(0, sim.ExecuteScalar($"select count(*) from sys.traces where id = {id}"));
        AreEqual(1, sim.ExecuteScalar($"""
            create table #l (d datetime, p nvarchar(12), t nvarchar(3999)); insert #l exec xp_readerrorlog 0, 1, N'was stopped because of an error';
            select count(*) from #l where replace(t, nchar(13) + nchar(10), N'') = N'Trace ID ''{id}'' was stopped because of an error. Cause: Stopping the trace because the current trace file is full and the rollover option is not specified.. Restart the trace after correcting the problem.'
            """));
    }

    [TestMethod]
    public void StartAndStop_WriteRealsErrorLogLines()
    {
        var sim = new Simulation();
        var id = Create(sim);
        _ = sim.ExecuteNonQuery($"declare @on bit = 1; exec sp_trace_setevent {id}, 82, 1, @on; exec sp_trace_setstatus {id}, 0; exec sp_trace_setstatus {id}, 1; exec sp_trace_setstatus {id}, 1; exec sp_trace_setstatus {id}, 0; exec sp_trace_setstatus {id}, 2");
        AreEqual($"SQL Trace ID {id} was started by login \"sa\".|SQL Trace stopped. Trace ID = '{id}'. Login Name = 'sa'.", sim.ExecuteScalar("""
            create table #l (d datetime, p nvarchar(12), t nvarchar(3999)); insert #l exec xp_readerrorlog 0, 1, N'SQL Trace';
            select string_agg(replace(t, nchar(13) + nchar(10), N''), '|') from #l
            """));
    }

    [TestMethod]
    public void StopTime_StopsAndClosesTheTraceAtItsNextEvent()
    {
        var sim = new Simulation();
        var id = Create(sim, "/var/opt/mssql/log/st", tail: ", @five, @soon");
        _ = sim.ExecuteNonQuery($"declare @on bit = 1; exec sp_trace_setevent {id}, 82, 1, @on; exec sp_trace_setstatus {id}, 1; exec sp_trace_generateevent 82, N'before'");
        _ = sim.ExecuteNonQuery("waitfor delay '00:00:01.200'; exec sp_trace_generateevent 82, N'after'");
        AreEqual(0, sim.ExecuteScalar($"select count(*) from sys.traces where id = {id}"));
        AreEqual("65528 65534 82:before 65533", string.Join(' ',
            Values(sim, "select concat(EventClass, ':' + cast(TextData as nvarchar(20))) from sys.fn_trace_gettable(N'/var/opt/mssql/log/st.trc', default)")));
    }

    [TestMethod]
    public void Permissions_RefuseALoginWithoutAlterTraceAtEachSurfacesState()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login lo with password = 'x', check_policy = off; create user lo for login lo; create login tr with password = 'x', check_policy = off; create user tr for login tr; use master; grant alter trace to tr");
        byte State(string sql) => sim.AssertSqlError("execute as login = 'lo'; " + sql, 8189).State;
        AreEqual(6, State("declare @id int; exec sp_trace_create @id output, 0, N'/var/opt/mssql/log/p'"));
        AreEqual(7, State("exec sp_trace_setstatus 99, 1"));
        AreEqual(8, State("declare @on bit = 1; exec sp_trace_setevent 99, 12, 1, @on"));
        AreEqual(9, State("exec sp_trace_setfilter 99, 12, 0, 0, 5"));
        AreEqual(10, State("exec sp_trace_generateevent 82, N'x'"));
        AreEqual(12, State("select * from sys.fn_trace_getinfo(0)"));
        AreEqual(13, State("select * from sys.fn_trace_geteventinfo(1)"));
        AreEqual(14, State("select * from sys.fn_trace_getfilterinfo(1)"));
        AreEqual(180, sim.ExecuteScalar("execute as login = 'lo'; select count(*) from sys.trace_events"));
        AreEqual(2, sim.ExecuteScalar("execute as login = 'tr'; declare @id int; exec sp_trace_create @id output, 0, N'/var/opt/mssql/log/p'; select @id"));
    }

    [TestMethod]
    public void RpcAndModuleEvents_ReportTheCallAsRealReconstructsIt()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p @a int as begin set nocount on; select @a + 1 as r; end");
        var id = Create(sim, "/var/opt/mssql/log/rpc");
        _ = sim.ExecuteNonQuery($"""
            declare @on bit = 1, @e int, @c int;
            declare c cursor local for select trace_event_id, trace_column_id from sys.trace_event_bindings where trace_event_id in (10, 11, 42, 43, 44, 45);
            open c; fetch c into @e, @c;
            while @@fetch_status = 0 begin exec sp_trace_setevent {id}, @e, @c, @on; fetch c into @e, @c; end
            exec sp_trace_setfilter {id}, 10, 0, 0, N'app';
            exec sp_trace_setstatus {id}, 1
            """);
        using (var client = sim.CreateDbConnection())
        {
            client.ConnectionString = "Application Name=app";
            client.Open();
            using var command = client.CreateCommand();
            command.CommandType = System.Data.CommandType.StoredProcedure;
            command.CommandText = "dbo.p";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@a";
            parameter.Value = 7;
            _ = command.Parameters.Add(parameter);
            _ = command.ExecuteScalar();
            Run(client, "exec dbo.p 8");
        }
        _ = sim.ExecuteNonQuery($"exec sp_trace_setstatus {id}, 0");
        CollectionAssert.AreEqual(new[]
        {
            "11 exec dbo.p @a=7 p",
            "42 exec dbo.p @a=7 p 8272 1",
            "44 set nocount on;  p 8272 1",
            "45 set nocount on;  p 8272 1",
            "44 select @a + 1 as r p 8272 1",
            "45 select @a + 1 as r p 8272 1",
            "43 exec dbo.p @a=7 p 8272 1 0",
            "10 exec dbo.p @a=7 p",
            "42 exec dbo.p 8 p 8272 1",
            "44 set nocount on;  p 8272 1",
            "45 set nocount on;  p 8272 1",
            "44 select @a + 1 as r p 8272 1",
            "45 select @a + 1 as r p 8272 1",
            "43 exec dbo.p 8 p 8272 1 0",
        }, Values(sim, """
            select concat_ws(' ', EventClass, cast(TextData as nvarchar(100)), ObjectName, ObjectType, NestLevel, case when EventClass = 43 then Duration end)
            from sys.fn_trace_gettable(N'/var/opt/mssql/log/rpc.trc', default) where EventClass < 65000
            """));
    }

    [TestMethod]
    public async Task Deadlock_WritesTheLockDeadlockChainAndGraphClasses()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table k (id int primary key, v int); insert k values (1, 0), (2, 0)");
        var id = Create(sim, "/var/opt/mssql/log/dl");
        _ = sim.ExecuteNonQuery($"""
            declare @on bit = 1, @e int, @c int;
            declare c cursor local for select trace_event_id, trace_column_id from sys.trace_event_bindings where trace_event_id in (25, 59, 148);
            open c; fetch c into @e, @c;
            while @@fetch_status = 0 begin exec sp_trace_setevent {id}, @e, @c, @on; fetch c into @e, @c; end
            exec sp_trace_setstatus {id}, 1
            """);
        using var first = sim.CreateOpenConnection();
        using var second = sim.CreateOpenConnection();
        Run(first, "begin tran; update k set v = 1 where id = 1");
        Run(second, "begin tran; update k set v = 2 where id = 2");
        var waiting = Task.Run(() => Run(first, "update k set v = 1 where id = 2"), this.TestContext.CancellationToken);
        _ = await Extensions.PollUntil(
            () => sim.ExecuteScalar("select count(*) from sys.dm_exec_requests where blocking_session_id <> 0"),
            count => count is int blocked && blocked == 1,
            this.TestContext.CancellationToken);
        Run(second, "update k set v = 2 where id = 1");
        await waiting.WaitAsync(TimeSpan.FromSeconds(10), this.TestContext.CancellationToken);
        Run(first, "commit");
        _ = sim.ExecuteNonQuery($"exec sp_trace_setstatus {id}, 0");
        CollectionAssert.AreEqual(new[] { "59 44 101 5 7 1 1", "59 44 101 5 7 1 1", "25 5 7 1", "148 2 1" }, Values(sim, """
            select concat_ws(' ', EventClass, case when EventClass <> 25 then SPID end, EventSubClass, Mode, Type, OwnerID, IsSystem)
            from sys.fn_trace_gettable(N'/var/opt/mssql/log/dl.trc', default) where EventClass < 65000
            """));
        StartsWith("<deadlock-list>\r\n <deadlock victim=\"process", (string)sim.ExecuteScalar("select cast(TextData as nvarchar(max)) from sys.fn_trace_gettable(N'/var/opt/mssql/log/dl.trc', default) where EventClass = 148")!);
    }

    [TestMethod]
    public void TraceFile_IsAFileOnTheServersDisk() =>
        AreEqual((byte)1, new Simulation().ExecuteScalar("""
            declare @id int; exec sp_trace_create @id output, 0, N'/var/opt/mssql/log/seen';
            create table #f (e tinyint, d tinyint, p tinyint); insert #f exec xp_fileexist N'/var/opt/mssql/log/seen.trc';
            select e from #f
            """));
}
