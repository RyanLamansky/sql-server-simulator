using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sys.dm_exec_cursors(session_id)</c> lists the cursors a session has
/// declared, with the type and concurrency each resolved to, where its
/// declaration sits in its batch, and where its fetch position stands.
/// Probed 2026-09-30 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ExecCursorsDmvTests
{
    private const string Setup = "create table t (id int primary key, v int); insert t values (1, 1), (2, 2), (3, 3);\n";

    private const string Columns = "select string_agg(concat(name, ':', properties, ':', is_open, ':', fetch_status, ':', fetch_buffer_size, ':', fetch_buffer_start), ',') within group (order by name) from sys.dm_exec_cursors(@@spid)";

    [TestMethod]
    public void Declared_ListsTypeConcurrencyAndScope()
        => AreEqual(
            "c1:TSQL | Dynamic | Optimistic | Global (0):1:-9:0:0,c2:TSQL | Snapshot | Read Only | Local (0):0:-9:0:0,c3:TSQL | Keyset | Optimistic | Global (0):1:-9:0:0,ff:TSQL | Fast_Forward | Read Only | Global (0):0:-9:0:0,sl:TSQL | Dynamic | Scroll Locks | Global (0):0:-9:0:0",
            new Simulation().ExecuteScalar(Setup + """
                declare c1 cursor for select id from t order by id;
                declare c2 cursor local static for select id from t;
                declare c3 cursor scroll keyset for select id from t;
                declare ff cursor fast_forward for select id from t;
                declare sl cursor scroll_locks for select id from t;
                open c1; open c3;
                """ + Columns));

    [TestMethod]
    public void FetchPosition_FollowsTheCursor()
        => AreEqual(
            "c1:TSQL | Dynamic | Optimistic | Global (0):1:0:1:-1,c3:TSQL | Keyset | Optimistic | Global (0):1:0:1:3,st:TSQL | Snapshot | Read Only | Global (0):1:-1:0:-1",
            new Simulation().ExecuteScalar(Setup + """
                declare @i int;
                declare c1 cursor for select id from t order by id;
                declare c3 cursor scroll keyset for select id from t;
                declare st cursor scroll static for select id from t;
                open c1; open c3; open st;
                fetch c1 into @i; fetch last from c3 into @i; fetch last from st into @i; fetch next from st into @i;
                """ + Columns));

    [TestMethod]
    public void ClosedAndReopened_KeepTheLastFetchStatus()
        => AreEqual(
            "st:TSQL | Snapshot | Read Only | Global (0):1:-1:0:0",
            new Simulation().ExecuteScalar(Setup + """
                declare @i int;
                declare st cursor scroll static for select id from t;
                open st; fetch first from st into @i; fetch prior from st into @i; close st; open st;
                """ + Columns));

    [TestMethod]
    public void StatementOffsets_NameTheDeclaration()
        => AreEqual(
            "declare k cursor for select 1 a",
            new Simulation().ExecuteScalar("""
                declare @x int;
                declare k cursor for select 1 a;
                select substring(t.text, c.statement_start_offset / 2 + 1, (c.statement_end_offset - c.statement_start_offset) / 2 + 1)
                from sys.dm_exec_cursors(@@spid) c cross apply sys.dm_exec_sql_text(c.sql_handle) t
                """));

    [TestMethod]
    [DataRow("0", 1)]
    [DataRow("null", 1)]
    [DataRow("-5", 0)]
    public void SessionArgument_SelectsTheSessions(string session, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"declare k cursor for select 1 a; select count(*) from sys.dm_exec_cursors({session}) where session_id = @@spid or {session} = -5"));

    [TestMethod]
    public void CursorVariable_IsNamedByItsVariable()
        => AreEqual("@cv:TSQL | Dynamic | Optimistic | Global (0)", new Simulation().ExecuteScalar(Setup + "declare @cv cursor; set @cv = cursor for select id from t; open @cv; select concat(name, ':', properties) from sys.dm_exec_cursors(@@spid)"));

    [TestMethod]
    public void Deallocated_IsGone()
        => AreEqual(0, new Simulation().ExecuteScalar("declare k cursor for select 1 a; deallocate k; select count(*) from sys.dm_exec_cursors(@@spid)"));

    [TestMethod]
    public void QueryStoreOff_LeavesTheStatementHandleNull()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create database d; alter database d set query_store = off");
        AreEqual(0, simulation.ExecuteScalar("use d; declare k cursor for select 1 a; select count(statement_sql_handle) + count(statement_context_id) from sys.dm_exec_cursors(@@spid)"));
        AreEqual(2, simulation.ExecuteScalar("declare k2 cursor for select 1 a; select count(statement_sql_handle) + count(statement_context_id) from sys.dm_exec_cursors(@@spid) where name = 'k2'"));
    }

    [TestMethod]
    [DataRow("select * from sys.dm_exec_cursors()", 313)]
    [DataRow("select * from sys.dm_exec_cursors(1, 2)", 8144)]
    public void ArgumentCount_IsChecked(string sql, int number)
        => AreEqual(12, new Simulation().AssertSqlError(sql, number).LineNumber);
}
