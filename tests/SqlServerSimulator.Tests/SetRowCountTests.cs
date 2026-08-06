using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>SET ROWCOUNT n</c>: the session cap on how many rows a statement returns
/// or changes, probed against SQL Server 2025 (2026-08-06). <c>0</c> lifts it.
/// See <c>docs/claude/dml.md</c> and <c>docs/claude/query.md</c>.
/// </summary>
[TestClass]
public sealed class SetRowCountTests
{
    private const string Seed = """
        CREATE TABLE t (id int PRIMARY KEY, v int NOT NULL);
        INSERT INTO t (id, v) SELECT n, n FROM (VALUES (1), (2), (3), (4), (5), (6), (7), (8), (9), (10)) AS s (n);
        """;

    private static DbConnection Seeded()
    {
        var connection = new Simulation().CreateOpenConnection();
        using var seed = connection.CreateCommand(Seed);
        _ = seed.ExecuteNonQuery();
        return connection;
    }

    private static object? Scalar(DbConnection connection, string commandText)
    {
        using var command = connection.CreateCommand(commandText);
        return command.ExecuteScalar();
    }

    /// <summary>Rows the statement actually handed to the client.</summary>
    private static int RowsReturned(DbConnection connection, string commandText)
    {
        using var command = connection.CreateCommand(commandText);
        using var reader = command.ExecuteReader();
        var rows = 0;
        while (reader.Read())
            rows++;
        return rows;
    }

    /// <summary>A capped SELECT returns the cap, and <c>@@ROWCOUNT</c> reports it.</summary>
    [TestMethod]
    public void SelectIsCappedAndReportedByRowCount()
    {
        using var connection = Seeded();
        AreEqual(3, RowsReturned(connection, "SET ROWCOUNT 3; SELECT id FROM t ORDER BY id"));
        AreEqual(3, Scalar(connection, "SET ROWCOUNT 3; SELECT id INTO #c FROM t; SELECT @@ROWCOUNT"));
    }

    /// <summary>
    /// TOP and ROWCOUNT compose as a minimum: the smaller of the two wins in
    /// both directions (probe-confirmed).
    /// </summary>
    [TestMethod]
    [DataRow(3, "TOP 5", 3)]
    [DataRow(5, "TOP 3", 3)]
    [DataRow(0, "TOP 4", 4)]
    public void TopAndRowCountComposeAsAMinimum(int rowCount, string top, int expected)
    {
        using var connection = Seeded();
        AreEqual(expected, RowsReturned(connection, $"SET ROWCOUNT {rowCount}; SELECT {top} id FROM t ORDER BY id"));
    }

    /// <summary>
    /// The cap is on the rows the statement emits, not the rows it reads: an
    /// aggregate over the whole table still answers 10 and reports 1 row.
    /// </summary>
    [TestMethod]
    public void AggregateStillReadsEveryRow()
    {
        using var connection = Seeded();
        AreEqual(10, Scalar(connection, "SET ROWCOUNT 3; SELECT COUNT(*) FROM t"));
        AreEqual(1, RowsReturned(connection, "SET ROWCOUNT 3; SELECT COUNT(*) FROM t"));
    }

    /// <summary>Every DML kind takes the cap.</summary>
    [TestMethod]
    public void UpdateIsCapped()
    {
        using var connection = Seeded();
        AreEqual(3, Scalar(connection, "SET ROWCOUNT 3; UPDATE t SET v = v + 100; SELECT @@ROWCOUNT"));
        AreEqual(3, Scalar(connection, "SET ROWCOUNT 0; SELECT COUNT(*) FROM t WHERE v > 100"));
    }

    [TestMethod]
    public void DeleteIsCapped()
    {
        using var connection = Seeded();
        AreEqual(2, Scalar(connection, "SET ROWCOUNT 2; DELETE FROM t; SELECT @@ROWCOUNT"));
        AreEqual(8, Scalar(connection, "SET ROWCOUNT 0; SELECT COUNT(*) FROM t"));
    }

    [TestMethod]
    public void InsertSelectIsCapped()
    {
        using var connection = Seeded();
        AreEqual(4, Scalar(connection, """
            SET ROWCOUNT 4;
            INSERT INTO t (id, v) SELECT n + 100, n FROM (VALUES (1), (2), (3), (4), (5), (6), (7), (8)) AS s (n);
            SELECT @@ROWCOUNT;
            """));
        AreEqual(14, Scalar(connection, "SET ROWCOUNT 0; SELECT COUNT(*) FROM t"));
    }

    [TestMethod]
    public void MergeIsCapped()
    {
        using var connection = Seeded();
        AreEqual(2, Scalar(connection, """
            SET ROWCOUNT 2;
            MERGE t AS tgt USING (VALUES (1), (2), (3), (4), (5), (6)) AS src (n) ON tgt.id = src.n
            WHEN MATCHED THEN UPDATE SET v = 999;
            SELECT @@ROWCOUNT;
            """));
        AreEqual(2, Scalar(connection, "SET ROWCOUNT 0; SELECT COUNT(*) FROM t WHERE v = 999"));
    }

    [TestMethod]
    public void SelectIntoIsCapped()
    {
        using var connection = Seeded();
        AreEqual(2, Scalar(connection, "SET ROWCOUNT 2; SELECT id INTO #c FROM t; SET ROWCOUNT 0; SELECT COUNT(*) FROM #c"));
    }

    /// <summary>
    /// A <c>SELECT @v = …</c> assignment is capped too, so the variable keeps
    /// the value from the last row inside the cap (probe-confirmed: 5 of a
    /// descending 10..1 scan under ROWCOUNT 2 is the second row's value).
    /// </summary>
    [TestMethod]
    public void AssignmentSelectIsCapped()
    {
        using var connection = Seeded();
        AreEqual(9, Scalar(connection, "SET ROWCOUNT 2; DECLARE @x int; SELECT @x = id FROM t ORDER BY id DESC; SELECT @x"));
    }

    /// <summary>An <c>OFFSET … FETCH</c> statement is capped the same way.</summary>
    [TestMethod]
    public void OffsetFetchIsCapped()
    {
        using var connection = Seeded();
        AreEqual(2, RowsReturned(connection, "SET ROWCOUNT 2; SELECT id FROM t ORDER BY id OFFSET 1 ROWS FETCH NEXT 5 ROWS ONLY"));
    }

    /// <summary>
    /// The cap persists into a called procedure, while the procedure's own
    /// <c>SET ROWCOUNT</c> reverts when it returns — and dynamic SQL scopes the
    /// same way.
    /// </summary>
    [TestMethod]
    public void ModuleScoping()
    {
        using var connection = Seeded();
        using (var create = connection.CreateCommand("CREATE PROC p_read AS SELECT id FROM t ORDER BY id;"))
        {
            _ = create.ExecuteNonQuery();
        }

        using (var create = connection.CreateCommand("CREATE PROC p_cap AS BEGIN SET ROWCOUNT 1; SELECT id FROM t ORDER BY id; END"))
        {
            _ = create.ExecuteNonQuery();
        }

        AreEqual(2, RowsReturned(connection, "SET ROWCOUNT 2; EXEC p_read;"));
        _ = Scalar(connection, "SET ROWCOUNT 0; SELECT 1");
        AreEqual(1, RowsReturned(connection, "SET ROWCOUNT 0; EXEC p_cap;"));
        AreEqual(10, Scalar(connection, "SELECT COUNT(*) FROM t"));
        AreEqual(2, RowsReturned(connection, "EXEC ('SET ROWCOUNT 2; SELECT id FROM t ORDER BY id;');"));
        AreEqual(10, Scalar(connection, "SELECT COUNT(*) FROM t"));
    }

    /// <summary>The variable form, including a <c>bigint</c> real accepts there.</summary>
    [TestMethod]
    public void VariableForm()
    {
        using var connection = Seeded();
        AreEqual(2, RowsReturned(connection, "DECLARE @n int = 2; SET ROWCOUNT @n; SELECT id FROM t ORDER BY id"));
        AreEqual(10, Scalar(connection, "DECLARE @b bigint = 5000000000; SET ROWCOUNT @b; SELECT COUNT(*) FROM t"));
    }

    /// <summary>
    /// Argument errors, as probed: a negative literal never reaches the option's
    /// own validation because the grammar has no sign slot (Msg 102), a
    /// non-integral literal is Msg 1080, and a NULL or negative variable is
    /// Msg 507 state 2.
    /// </summary>
    [TestMethod]
    public void ArgumentErrors()
    {
        var simulation = new Simulation();
        simulation.ValidateSyntaxError("SET ROWCOUNT -1", "-");
        simulation.AssertSqlError("SET ROWCOUNT 2.5", 1080, "The integer value 2.5 is out of range.");
        simulation.AssertSqlError(
            "DECLARE @z int = NULL; SET ROWCOUNT @z;",
            507,
            "Invalid argument for SET ROWCOUNT. Must be a non-null non-negative integer.");
        var negative = simulation.AssertSqlError("DECLARE @z int = -3; SET ROWCOUNT @z;", 507);
        AreEqual((byte)2, negative.State);
    }

    /// <summary>
    /// Real refuses <c>NEXT VALUE FOR</c> under an active ROWCOUNT with the same
    /// Msg 11739 a <c>TOP</c> earns — the message names all three sources.
    /// </summary>
    [TestMethod]
    public void NextValueForIsRefusedUnderAnActiveRowCount()
    {
        using var connection = Seeded();
        using (var create = connection.CreateCommand("CREATE SEQUENCE sq AS int START WITH 1;"))
        {
            _ = create.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand("SET ROWCOUNT 2; SELECT NEXT VALUE FOR sq FROM t;");
        var ex = Throws<SimulatedSqlException>(command.ExecuteScalar);
        AreEqual(11739, ex.Number);
    }
}
