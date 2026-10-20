using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The API server cursor procedures — <c>sp_cursoropen</c>, <c>sp_cursorfetch</c>,
/// <c>sp_cursor</c>, <c>sp_cursoroption</c>, <c>sp_cursorclose</c> and the
/// prepared family — written as <c>EXEC</c> text, probed 2026-10-16 against
/// SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ApiCursorProcedureTests
{
    /// <summary>Every result set <paramref name="sql"/> returns, each as its column names then its rows, joined.</summary>
    private static List<string> ResultSets(DbConnection connection, string sql)
    {
        var sets = new List<string>();
        using var reader = connection.CreateCommand(sql).ExecuteReader();
        do
        {
            var rows = new List<string> { string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)) };
            while (reader.Read())
                rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i).ToInvariantString())));
            sets.Add(string.Join(';', rows));
        }
        while (reader.NextResult());
        return sets;
    }

    private static DbConnection Seeded()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (id int primary key, v int); insert t values (1, 10), (2, 20), (3, 30)").ExecuteNonQuery();
        return connection;
    }

    [TestMethod]
    public void Open_AnnouncesColumns_ThenFetchesByHandle()
    {
        using var connection = Seeded();
        CollectionAssert.AreEqual(new[]
        {
            "id|v|ROWSTAT",
            "r|c|s|cc|rc|n;0|3|1|1|3|0",
            "id|v|ROWSTAT;1|10|1;2|20|1",
            "r|n|fs;0|2|0",
            "id|v|ROWSTAT;3|30|1",
            "id|v|ROWSTAT",
            "r|n|fs;0|0|-1",
            "r|n;0|1",
        }, ResultSets(connection, """
            declare @c int, @s int = 1, @cc int = 1, @rc int, @r int;
            exec @r = sp_cursoropen @c output, N'select * from t', @s output, @cc output, @rc output;
            select @r r, @c - 180150000 c, @s s, @cc cc, @rc rc, @@rowcount n;
            exec @r = sp_cursorfetch @c, 2, 0, 2;
            select @r r, @@rowcount n, @@fetch_status fs;
            exec sp_cursorfetch @c;
            exec @r = sp_cursorfetch @c, 2, 0, 2;
            select @r r, @@rowcount n, @@fetch_status fs;
            exec @r = sp_cursorclose @c;
            select @r r, @@rowcount n;
            """));
    }

    [TestMethod]
    public void Fetch_InfoReportsTheBufferStart_AndRelativeCountsFromIt()
    {
        using var connection = Seeded();
        CollectionAssert.AreEqual(new[]
        {
            "id|v|ROWSTAT",
            "id|v|ROWSTAT;2|20|1;3|30|1",
            "id|v|ROWSTAT;1|10|1;2|20|1",
            "rn|nr;1|3",
        }, ResultSets(connection, """
            declare @c int, @rn int, @nr int;
            exec sp_cursoropen @c output, N'select * from t', 8, 1;
            exec sp_cursorfetch @c, 16, 2, 2;
            exec sp_cursorfetch @c, 32, -1, 2;
            exec sp_cursorfetch @c, 256, @rn output, @nr output;
            select @rn rn, @nr nr;
            """));
    }

    [TestMethod]
    public void FailedOpen_ReportsTheStatementsError_AndTheBatchGoesOn()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var error = Throws<SimulatedSqlException>(() => ResultSets(connection, """
            declare @c int = 5, @r int, @s int = 1, @cc int = 1, @rc int = 9;
            exec @r = sp_cursoropen @c output, N'select * from nosuch', @s output, @cc output, @rc output;
            select @r r, @c c, @s s, @cc cc, @rc rc;
            """));
        AreEqual((208, 16945, "sp_cursoropen"), (error.Errors[0].Number, error.Errors[1].Number, error.Errors[1].Procedure));
        CollectionAssert.AreEqual(new[] { "r|c|s|cc|rc;208|0|1|1|0" }, ResultSets(connection, """
            declare @c int = 5, @r int, @s int = 1, @cc int = 1, @rc int = 9;
            begin try
                exec @r = sp_cursoropen @c output, N'select * from nosuch', @s output, @cc output, @rc output;
            end try
            begin catch
            end catch
            select @r r, @c c, @s s, @cc cc, @rc rc;
            """));
    }

    [TestMethod]
    public void BadHandle_IsMsg16909_InHexadecimal_ReturningOne()
    {
        var error = new Simulation().AssertSqlError("declare @r int; exec @r = sp_cursorclose 12345", 16909).Errors[0];
        AreEqual(("sp_cursorclose: The cursor identifier value provided (3039) is not valid.", "sp_cursorclose", 1), (error.Message, error.Procedure, error.LineNumber));
    }

    [TestMethod]
    [DataRow("declare @c int; exec sp_cursoropen @c, N'select 1'", 16902, (byte)16)]
    [DataRow("declare @c int; exec sp_cursoropen @c output", 16903, (byte)29)]
    [DataRow("declare @c int; exec sp_cursoropen @c output, N''", 16902, (byte)18)]
    [DataRow("declare @c int; exec sp_cursoropen @c output, N'select 1', 0x10", 16902, (byte)20)]
    [DataRow("declare @c int; exec sp_cursoropen @c output, N'select 1', 3", 16902, (byte)24)]
    [DataRow("declare @c int; exec sp_cursoropen @c output, N'select 1', 1, 0", 16902, (byte)26)]
    [DataRow("declare @c int; exec sp_cursoropen @c output, N'select 1; select 2'", 16938, (byte)1)]
    [DataRow("exec sp_cursorfetch", 16903, (byte)1)]
    [DataRow("exec sp_cursorfetch null", 16902, (byte)1)]
    [DataRow("exec sp_cursorfetch 1, 3", 16902, (byte)4)]
    [DataRow("exec sp_cursorfetch 1, 128", 16902, (byte)45)]
    [DataRow("exec sp_cursorfetch 1, 2, 0, -1", 16902, (byte)7)]
    [DataRow("declare @n int; exec sp_cursorfetch 1, 256, 0, @n output", 16902, (byte)5)]
    [DataRow("exec sp_cursoroption 1, 1", 16903, (byte)4)]
    [DataRow("exec sp_cursor 1", 16903, (byte)6)]
    [DataRow("exec sp_cursor 1, 99, 1, N't'", 16902, (byte)33)]
    [DataRow("declare @p int; exec sp_cursorprepare @p output, null, N'select 1', 2", 214, (byte)3)]
    [DataRow("declare @p int, @c int; exec sp_cursorprepexec @p output, @c output, N'@x int', N'select @x', 1, 1, null, 5", 16902, (byte)50)]
    public void Arguments_AreCheckedAsRealChecksThem(string sql, int number, byte state)
    {
        var error = new Simulation().AssertSqlError(sql, number);
        AreEqual(state, error.Errors[0].State);
    }

    /// <summary>
    /// What an open cursor refuses: a SETPOSITION given a table or values
    /// (Msg 16914), a value written without its column's name (16936), and a
    /// TEXTPTR_ONLY or TEXTDATA column that isn't text, ntext or image (16926)
    /// (probed 2026-10-20).
    /// </summary>
    [TestMethod]
    [DataRow("exec sp_cursor @c, 32, 1, N't', 5", 16914, (byte)2)]
    [DataRow("exec sp_cursor @c, 4, 1, N't', 5", 16936, (byte)1)]
    [DataRow("exec sp_cursoroption @c, 1, 2", 16926, (byte)1)]
    [DataRow("exec sp_cursoroption @c, 3, 9", 16926, (byte)1)]
    public void OpenCursor_RefusesAsRealRefuses(string sql, int number, byte state)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int primary key, v int, tx text); insert t values (1, 10, 'a')");
        var error = simulation.AssertSqlError("declare @c int; exec sp_cursoropen @c output, N'select id, v, tx from t', 1, 4; exec sp_cursorfetch @c, 2, 1, 1; " + sql, number);
        AreEqual(state, error.Errors[0].State);
        _ = simulation.ExecuteNonQuery("declare @c int; exec sp_cursoropen @c output, N'select id, v, tx from t', 1, 4; exec sp_cursoroption @c, 1, 3; exec sp_cursoroption @c, 3, 0");
    }

    [TestMethod]
    public void Prepared_OpensByHandle_FromTheirOwnNumbering()
    {
        using var connection = Seeded();
        CollectionAssert.AreEqual(new[]
        {
            "id|v|ROWSTAT",
            "p|s|cc;1073741825|2|1",
            "id|v|ROWSTAT",
            "id|v|ROWSTAT;3|30|1",
            "id|v|ROWSTAT",
            "id|v|ROWSTAT;2|20|1;3|30|1",
            "r;0",
        }, ResultSets(connection, """
            declare @p int, @c int, @s int = 2, @cc int = 1, @r int;
            exec sp_cursorprepare @p output, N'@x int', N'select * from t where id >= @x', 1, @s output, @cc output;
            select @p p, @s s, @cc cc;
            exec sp_cursorexecute @p, @c output, 1, 1, null, @x = 3;
            exec sp_cursorfetch @c, 2, 0, 5;
            exec sp_cursorclose @c;
            exec sp_cursorprepexec @p output, @c output, N'@x int', N'select * from t where id >= @x', 4097, 1, null, 2;
            exec sp_cursorfetch @c, 2, 0, 5;
            exec @r = sp_unprepare @p;
            select @r r;
            """));
        var missing = Throws<SimulatedSqlException>(() => connection.CreateCommand("declare @c int; exec sp_cursorexecute 1073741826, @c output").ExecuteNonQuery());
        AreEqual(((byte)4, "sp_cursorexecute"), (missing.Errors[0].State, missing.Errors[0].Procedure));
    }

    [TestMethod]
    public void Positioned_UpdateDeleteInsert_ThroughTheFetchBuffer()
    {
        using var connection = Seeded();
        var empty = Throws<SimulatedSqlException>(() => ResultSets(connection, "declare @c int; exec sp_cursoropen @c output, N'select * from t', 1, 4; exec sp_cursor @c, 1, 1, N't', @v = 99"));
        AreEqual((16931, 3621), (empty.Errors[0].Number, empty.Errors[1].Number));
        _ = ResultSets(connection, """
            declare @c int;
            exec sp_cursoropen @c output, N'select * from t', 1, 4;
            exec sp_cursorfetch @c, 2, 0, 2;
            exec sp_cursor @c, 1, 1, N't', @v = 99;
            exec sp_cursor @c, 2, 2, N'';
            exec sp_cursor @c, 4, 0, N't', @id = 7, @v = 70;
            exec sp_cursorclose @c;
            """);
        CollectionAssert.AreEqual(new[] { "id|v;1|99;3|30;7|70" }, ResultSets(connection, "select * from t order by id"));
        var readOnly = Throws<SimulatedSqlException>(() => ResultSets(connection, "declare @c int; exec sp_cursoropen @c output, N'select * from t', 8, 1; exec sp_cursorfetch @c, 2, 0, 1; exec sp_cursor @c, 2, 1, N't'"));
        AreEqual((16929, (byte)2), (readOnly.Errors[0].Number, readOnly.Errors[0].State));
    }

    /// <summary>
    /// A positioned UPDATE reports the row it found, then what it wrote, and
    /// one that fails reports the found row ahead of its error, which ends the
    /// call as a write: Msg 3621 follows it, or a TRY catching it gets its
    /// count of 0 (probed 2026-10-20; the wire tests hold each DONE).
    /// </summary>
    [TestMethod]
    public void Positioned_WriteCounts_AndAFailedWrite()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("alter table t add check (v < 100)").ExecuteNonQuery();
        const string open = "declare @c int; exec sp_cursoropen @c output, N'select * from t', 1, 4; exec sp_cursorfetch @c, 2, 1, 1;";
        AreEqual(-1, connection.CreateCommand(open).ExecuteNonQuery());
        AreEqual(2, connection.CreateCommand(open + "exec sp_cursor @c, 1, 1, N't', @v = 50").ExecuteNonQuery());
        AreEqual(1, connection.CreateCommand(open + "begin try exec sp_cursor @c, 1, 1, N't', @v = 500 end try begin catch end catch").ExecuteNonQuery());
        var failed = Throws<SimulatedSqlException>(() => connection.CreateCommand(open + "exec sp_cursor @c, 1, 1, N't', @v = 500").ExecuteNonQuery());
        AreEqual((547, 3621), (failed.Errors[0].Number, failed.Errors[^1].Number));
        var duplicate = Throws<SimulatedSqlException>(() => connection.CreateCommand(open + "exec sp_cursor @c, 4, 0, N't', @id = 2, @v = 1").ExecuteNonQuery());
        AreEqual((2627, 3621), (duplicate.Errors[0].Number, duplicate.Errors[^1].Number));
    }

    [TestMethod]
    public void CursorName_ReachesTheCursorFromTSql()
    {
        using var connection = Seeded();
        CollectionAssert.AreEqual(new[] { "id|v|ROWSTAT", "cs;1", "id|v|ROWSTAT;1|10|1" }, ResultSets(connection, """
            declare @c int;
            exec sp_cursoropen @c output, N'select * from t', 1, 1;
            exec sp_cursoroption @c, 2, N'named';
            select cursor_status('global', 'named') cs;
            fetch next from named;
            """));
    }
}
