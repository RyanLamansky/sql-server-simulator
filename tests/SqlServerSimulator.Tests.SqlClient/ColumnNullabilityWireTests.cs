using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Result-set nullability metadata (the COLMETADATA fNullable flag) as a real
/// SqlClient reader observes it via <c>GetColumnSchema()</c>. Single-source
/// SELECT projections carry per-column nullability inferred at parse — direct
/// refs preserve the base column's declaration, expressions claim nullable —
/// while joined shapes fall back to claiming every column nullable.
/// Load-bearing for DacFx bacpac export: its BCP data files drop the
/// per-value length prefix on fixed-width columns whose wire metadata says
/// NOT NULL, and the bacpac loader decodes per the model.xml declaration, so
/// the two must agree for an exported bacpac to re-import.
/// </summary>
[TestClass]
public sealed class ColumnNullabilityWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task SingleTableSelect_DirectRefsCarryDeclaredNullability()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table t (id int not null primary key, v nvarchar(40) null, d date not null);
            insert t values (1, N'a', '2026-01-01')
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select id, v, d, id + 1, isnull(v, N'x') from t", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        var columns = reader.GetColumnSchema();
        IsFalse(columns[0].AllowDBNull);
        IsTrue(columns[1].AllowDBNull);
        IsFalse(columns[2].AllowDBNull);
        // Integer arithmetic claims nullable (overflow), ISNULL with a
        // non-null fallback claims NOT NULL — SQL Server's documented
        // inference, shared with SELECT INTO's destination schema.
        IsTrue(columns[3].AllowDBNull);
        IsFalse(columns[4].AllowDBNull);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(1, reader.GetInt32(0));
    }

    [TestMethod]
    public async Task FromlessSelect_LiteralsNotNull_ExpressionsNullable()
    {
        // A FROM-less projection carries per-expression nullability like real:
        // a bare literal is NOT NULL (`select 1` → INT4 token, not INTN),
        // while a CAST, arithmetic, or the NULL literal claims nullable. The
        // FROM-less path bakes its row at parse and didn't set the metadata,
        // so it over-claimed every column nullable; pymssql/JDBC's coarse type
        // codes hid it, and tedious's token-name metadata surfaced it.
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select 1, 'x', 1.5, cast(1 as int), 1 + 2, null", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        var columns = reader.GetColumnSchema();
        IsFalse(columns[0].AllowDBNull);  // int literal
        IsFalse(columns[1].AllowDBNull);  // varchar literal
        IsFalse(columns[2].AllowDBNull);  // numeric literal
        IsTrue(columns[3].AllowDBNull);   // CAST claims nullable
        IsTrue(columns[4].AllowDBNull);   // arithmetic claims nullable
        IsTrue(columns[5].AllowDBNull);   // untyped NULL literal
    }

    [TestMethod]
    public async Task ConcatAndIif_ProjectProbeConfirmedNullability()
    {
        // CONCAT / CONCAT_WS never return NULL (NULL args skipped, all-NULL →
        // empty string) so they project NOT NULL regardless of operand
        // nullability; IIF inherits CASE's rule (NOT NULL iff both value arms
        // are non-null). Probe-confirmed against SQL Server 2025 and surfaced by
        // go-mssqldb / tedious COLMETADATA fNullable (the sim previously
        // over-claimed all four nullable).
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("""
            select
                concat('a', 'b'),
                concat('a', null),
                concat_ws(',', 'a', null),
                iif(1 > 2, 'x', 'y'),
                iif(1 > 2, 'x', null)
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        var columns = reader.GetColumnSchema();
        IsFalse(columns[0].AllowDBNull);  // CONCAT of non-null literals
        IsFalse(columns[1].AllowDBNull);  // CONCAT with a NULL arg is still NOT NULL
        IsFalse(columns[2].AllowDBNull);  // CONCAT_WS is NOT NULL
        IsFalse(columns[3].AllowDBNull);  // IIF, both value arms non-null
        IsTrue(columns[4].AllowDBNull);   // IIF, a NULL value arm → nullable
    }

    [TestMethod]
    public async Task CaseFamilyArmConversion_ReachesTheWireFlag()
    {
        // A CASE-family arm (or GREATEST / LEAST argument) reads nullable when
        // the arm unification's conversion could alter the value: the int
        // literal beside a decimal(9, 2) column doesn't fit the column's seven
        // integral digits, and neither does a scaled decimal fit float's binary
        // mantissa. Widening the same arms converts nothing. Probe-confirmed
        // against SQL Server 2025; the exhaustive matrix lives in the main
        // suite's ResultNullabilityTests.
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table t (m decimal(9, 2) not null, i int not null, f float not null);
            insert t values (1.5, 1, 1)
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(
            "select coalesce(m, 0), coalesce(m, 0.0), greatest(m, 1), coalesce(m, f), coalesce(i, f) from t", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        var columns = reader.GetColumnSchema();
        IsTrue(columns[0].AllowDBNull);   // int literal → decimal(9, 2)
        IsFalse(columns[1].AllowDBNull);  // numeric(2, 1) literal fits
        IsTrue(columns[2].AllowDBNull);   // GREATEST carries the same rule
        IsTrue(columns[3].AllowDBNull);   // decimal → float loses the exact value
        IsFalse(columns[4].AllowDBNull);  // int → float does not

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(1.5m, reader.GetDecimal(0));
    }

    [TestMethod]
    public async Task ValuesConstructorColumn_NullableIsOrOverRows()
    {
        // A VALUES row-constructor column projects NOT NULL iff no row supplies
        // a nullable cell there, and nullable as soon as one row does — the
        // single derived source flows through the per-column inference. Probe-
        // confirmed against SQL Server 2025.
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var allNonNull = new SqlCommand(
            "select n from (values('a'), ('b')) v(n)", connection))
        await using (var reader = await allNonNull.ExecuteReaderAsync(TestContext.CancellationToken))
            IsFalse(reader.GetColumnSchema()[0].AllowDBNull);

        await using (var oneNull = new SqlCommand(
            "select c from (values(1), (cast(null as int))) v(c)", connection))
        await using (var reader = await oneNull.ExecuteReaderAsync(TestContext.CancellationToken))
            IsTrue(reader.GetColumnSchema()[0].AllowDBNull);
    }

    [TestMethod]
    public async Task NotNullFixedWidthColumns_ReadOverWire_WithFixedLenTokens()
    {
        // A NOT NULL fixed-width column now carries the FIXEDLENTYPE token
        // (INT4 / INT8 / BIT / MONEY / DATETIME) and a raw ROW value, matching
        // real. SqlClient must still read every value and see NOT NULL metadata.
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table t (
                i int not null, b bigint not null, f bit not null,
                m money not null, d datetime not null);
            insert t values (5, 9000000000, 1, 12.34, '2020-01-02 03:04:05')
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select i, b, f, m, d from t", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        var columns = reader.GetColumnSchema();
        for (var i = 0; i < 5; i++)
            IsFalse(columns[i].AllowDBNull, columns[i].ColumnName);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(5, reader.GetInt32(0));
        AreEqual(9000000000L, reader.GetInt64(1));
        IsTrue(reader.GetBoolean(2));
        AreEqual(12.34m, reader.GetDecimal(3));
        AreEqual(new DateTime(2020, 1, 2, 3, 4, 5), reader.GetDateTime(4));
        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task PerBuiltInDispositions_ReachTheWireFlag()
    {
        // The per-built-in nullability table has no rule behind it — CEILING /
        // FLOOR / ROUND / SIGN / RADIANS and the …FROMPARTS family propagate
        // their arguments', PI and the GETDATE family are unconditionally NOT
        // NULL, and their near neighbours (ABS, POWER, RAND, NEWID) stay
        // nullable. The exhaustive matrix lives in the main suite's
        // ResultNullabilityTests; this pins that it is the flag SqlClient reads.
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table t (a int not null, b int null);
            insert t values (1, null)
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(
            "select ceiling(a), ceiling(b), sign(a), abs(a), pi(), getdate(), rand() from t", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        var columns = reader.GetColumnSchema();
        IsFalse(columns[0].AllowDBNull);  // CEILING propagates a NOT NULL argument
        IsTrue(columns[1].AllowDBNull);   // ... and a nullable one
        IsFalse(columns[2].AllowDBNull);  // SIGN propagates
        IsTrue(columns[3].AllowDBNull);   // ABS never does
        IsFalse(columns[4].AllowDBNull);  // PI is unconditionally NOT NULL
        IsFalse(columns[5].AllowDBNull);  // so is GETDATE
        IsTrue(columns[6].AllowDBNull);   // RAND is not
    }

    [TestMethod]
    public async Task AtAtConstantsAndConcatOperators_ReachTheWireFlag()
    {
        // Every @@-constant but @@IDENTITY is NOT NULL, and `+` splits on which
        // operator it is: concatenation propagates its operands' nullability
        // where arithmetic claims nullable however non-null the operands are.
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table t (a int not null, s varchar(10) not null, sn varchar(10) null);
            insert t values (1, 'x', null)
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(
            "select @@rowcount, @@spid, @@identity, s + s, s + sn, a + a, a & a, -a from t", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        var columns = reader.GetColumnSchema();
        IsFalse(columns[0].AllowDBNull);  // @@ROWCOUNT
        IsFalse(columns[1].AllowDBNull);  // @@SPID
        IsTrue(columns[2].AllowDBNull);   // @@IDENTITY is the exception
        IsFalse(columns[3].AllowDBNull);  // string concatenation propagates
        IsTrue(columns[4].AllowDBNull);   // ... including a nullable operand
        IsTrue(columns[5].AllowDBNull);   // arithmetic + is always nullable
        IsFalse(columns[6].AllowDBNull);  // bitwise & propagates
        IsTrue(columns[7].AllowDBNull);   // unary minus is arithmetic
    }

    [TestMethod]
    public async Task CaseFamilyConstantFolds_ReachTheWireFlag()
    {
        // The CASE family drops an arm whose condition folds FALSE and takes
        // only the arm whose condition folds TRUE, which is what separates
        // NULLIF(1, 2) / COALESCE(NULL, 5) / CASE WHEN 1 = 1 THEN 5 END from
        // their unfoldable spellings.
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("""
            select
                nullif(1, 2),
                nullif(1, 1),
                coalesce(null, 5),
                case when 1 = 1 then 5 end,
                case when 1 = 2 then 5 end
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        var columns = reader.GetColumnSchema();
        IsFalse(columns[0].AllowDBNull);  // arms differ, so the constant survives
        IsTrue(columns[1].AllowDBNull);   // arms match, so it folds to NULL
        IsFalse(columns[2].AllowDBNull);  // the constant-NULL argument drops out
        IsFalse(columns[3].AllowDBNull);  // constant-true beats the implicit ELSE NULL
        IsTrue(columns[4].AllowDBNull);   // constant-false leaves only that ELSE
    }

    /// <summary>
    /// A join reports each side against its own NULL-fill exposure: an INNER
    /// join preserves both operands' base nullability, while an outer join's
    /// NULL-filled side reads nullable however its base column is declared.
    /// Probe-confirmed against SQL Server 2025 through
    /// <c>sys.dm_exec_describe_first_result_set</c>.
    /// </summary>
    [TestMethod]
    [DataRow("join", false, false)]
    [DataRow("left join", false, true)]
    [DataRow("right join", true, false)]
    [DataRow("full join", true, true)]
    public async Task JoinedSelect_ReportsPerSideNullability(string joinKeyword, bool leftNullable, bool rightNullable)
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table a (id int not null primary key);
            create table b (id int not null primary key, aid int not null);
            insert a values (1); insert b values (10, 1)
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(
            $"select a.id, b.id from a {joinKeyword} b on b.aid = a.id", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        var columns = reader.GetColumnSchema();
        AreEqual(leftNullable, columns[0].AllowDBNull);
        AreEqual(rightNullable, columns[1].AllowDBNull);
    }
}
