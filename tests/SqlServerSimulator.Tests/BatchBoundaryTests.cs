using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Verifies <c>CREATE/ALTER PROCEDURE / FUNCTION / VIEW / TRIGGER / SCHEMA</c>
/// raises Msg 111 ("'X' must be the first statement in a query batch.") when
/// it isn't the first statement. Probe-confirmed wording per kind: PROCEDURE
/// merges CREATE / ALTER into one label; the others use their separate
/// CREATE / ALTER labels. Inner CommandText-equivalent contexts (procedure,
/// function, trigger, dynamic-SQL bodies) get a fresh BatchContext so the
/// check naturally resets — proven by the IFBlock + WHILE body cases below
/// (which raise Msg 111 because BlockDepth &gt; 0).
/// </summary>
[TestClass]
public class BatchBoundaryTests
{
    [TestMethod]
    [DataRow("create schema audit", "create schema staging", "CREATE SCHEMA")]
    [DataRow("create table t (id int)", "create view v as select 1 as x", "CREATE VIEW")]
    [DataRow("create table t (id int)", "create function fn() returns int as begin return 1 end", "CREATE FUNCTION")]
    [DataRow("create table t (id int)", "create proc p as select 1", "CREATE/ALTER PROCEDURE")]
    [DataRow("create table t (id int)", "create trigger tr on t after insert as select 1", "CREATE TRIGGER")]
    public void CreateMustBeFirstStatement_RaisesMsg111(string first, string secondCreate, string expectedLabel)
        => new Simulation().AssertSqlError(
            $"{first}; {secondCreate}",
            111,
            $"'{expectedLabel}' must be the first statement in a query batch.");

    [TestMethod]
    public void AlterProcedureMustBeFirstStatement_RaisesMsg111()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create proc p as select 1");
        simulation.AssertSqlError(
            "declare @x int = 1; alter proc p as select 2",
            111,
            "'CREATE/ALTER PROCEDURE' must be the first statement in a query batch.");
    }

    [TestMethod]
    [DataRow("create view v as select 1 as x", "alter view v as select 2 as x", "ALTER VIEW")]
    [DataRow("create function fn() returns int as begin return 1 end",
        "alter function fn() returns int as begin return 2 end", "ALTER FUNCTION")]
    public void AlterModuleMustBeFirstStatement_RaisesMsg111(string create, string alter, string expectedLabel)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(create);
        simulation.AssertSqlError(
            $"declare @x int = 1; {alter}",
            111,
            $"'{expectedLabel}' must be the first statement in a query batch.");
    }

    [TestMethod]
    public void AlterTriggerMustBeFirstStatement_RaisesMsg111()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (id int)",
            "create trigger tr on t after insert as select 1");
        simulation.AssertSqlError(
            "declare @x int = 1; alter trigger tr on t after insert as select 2",
            111,
            "'ALTER TRIGGER' must be the first statement in a query batch.");
    }

    /// <summary>
    /// The state byte identifies the offending statement kind. Probe-confirmed
    /// 2026-07-31 — real also carries 12 for CREATE RULE and 13 for CREATE
    /// DEFAULT, neither of which the simulator parses.
    /// </summary>
    [TestMethod]
    [DataRow("create table t (id int); create proc p as select 1", (byte)1)]
    [DataRow("create table t (id int); create function fn() returns int as begin return 1 end", (byte)4)]
    [DataRow("create table t (id int); create trigger tr on t after insert as select 1", (byte)6)]
    [DataRow("create table t (id int); create view v as select 1 as x", (byte)9)]
    [DataRow("create schema audit; create schema staging", (byte)14)]
    public void Msg111_CarriesPerKindState(string sql, byte expectedState)
        => AreEqual(expectedState, new Simulation().AssertSqlError(sql, 111).State);

    /// <inheritdoc cref="Msg111_CarriesPerKindState"/>
    [TestMethod]
    public void Msg111_AlterTrigger_CarriesState7()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (id int)",
            "create trigger tr on t after insert as select 1");
        AreEqual((byte)7, simulation.AssertSqlError(
            "declare @x int = 1; alter trigger tr on t after insert as select 2", 111).State);
    }

    /// <inheritdoc cref="Msg111_CarriesPerKindState"/>
    [TestMethod]
    [DataRow("create function fn() returns int as begin return 1 end",
        "alter function fn() returns int as begin return 2 end", (byte)5)]
    [DataRow("create view v as select 1 as x", "alter view v as select 2 as x", (byte)10)]
    public void Msg111_AlterModule_CarriesPerKindState(string create, string alter, byte expectedState)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(create);
        AreEqual(expectedState, simulation.AssertSqlError($"declare @x int = 1; {alter}", 111).State);
    }

    /// <summary>
    /// <c>CREATE OR ALTER</c> reports under the plain <c>CREATE</c> label and
    /// state, never the ALTER one — real names the statement by the verb it
    /// started with (probe-confirmed).
    /// </summary>
    [TestMethod]
    [DataRow("create or alter procedure p as select 2", "CREATE/ALTER PROCEDURE", (byte)1)]
    [DataRow("create or alter function fn() returns int as begin return 2 end", "CREATE FUNCTION", (byte)4)]
    [DataRow("create or alter view v as select 2 as x", "CREATE VIEW", (byte)9)]
    public void CreateOrAlter_UsesCreateLabelAndState(string statement, string expectedLabel, byte expectedState)
    {
        var exception = new Simulation().AssertSqlError($"declare @x int = 1; {statement}", 111);
        AreEqual($"'{expectedLabel}' must be the first statement in a query batch.", exception.Message);
        AreEqual(expectedState, exception.State);
    }

    [TestMethod]
    public void LeadingSemicolons_DontCountAsFirstStatement()
        => new Simulation().ExecuteNonQuery("; create proc p as select 1");

    [TestMethod]
    public void CreateProcInsideIfBlock_RaisesMsg111()
        => new Simulation().AssertSqlError(
            "if 1 = 1 begin create proc p as select 1 end",
            111,
            "'CREATE/ALTER PROCEDURE' must be the first statement in a query batch.");

    [TestMethod]
    public void CreateProcInsideWhileBody_RaisesMsg111()
        => new Simulation().AssertSqlError(
            "while 1 = 0 begin create proc p as select 1 end",
            111,
            "'CREATE/ALTER PROCEDURE' must be the first statement in a query batch.");

    [TestMethod]
    public void CreateProcAsFirstStatement_Works()
        => new Simulation().ExecuteNonQuery("create proc p as select 1");

    [TestMethod]
    public void ProcedureBodyIsItsOwnBatch_AllowsCreateProc()
    {
        // Inside a proc body, the dispatched statements form a new batch with
        // its own BatchContext, so CREATE PROCEDURE may legitimately appear
        // as the first statement of the body. (Real SQL Server actually
        // raises Msg 156 syntax error in this position; the simulator's
        // divergence here is documented and benign — no app emits nested
        // CREATE PROCEDUREs inside a proc body.)
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create proc outer_p as create proc inner_p as select 1",
            "exec outer_p"); // Invoke the outer proc — its body creates inner_p.
        Assert.AreEqual(1, simulation.ExecuteScalar("select count(*) from sys.procedures where name = 'inner_p'"));
    }

    /// <summary>
    /// The other half of the batch-position rule, probe-confirmed against SQL
    /// Server 2025 (2026-08-04): a VIEW or FUNCTION body runs to the end of its
    /// batch, so the module must be the batch's <em>only</em> statement, not
    /// merely its first. Real reports the leftover token as an ordinary syntax
    /// error — Msg 156 when it is a keyword — attributed to the module being
    /// defined, and leaves the module uncreated.
    /// </summary>
    [TestMethod]
    [DataRow("create view v as select 1 as x", "v")]
    [DataRow("create view v(a) as select 1 as x", "v")]
    [DataRow("create function fn() returns int as begin return 1 end", "fn")]
    [DataRow("create function tvf() returns table as return (select 1 as a)", "tvf")]
    [DataRow("create function tvf() returns table as return select 1 as a", "tvf")]
    [DataRow("create function mtvf() returns @r table (a int) as begin insert @r values (1) return end", "mtvf")]
    public void StatementAfterModuleBody_RaisesMsg156(string create, string moduleName)
    {
        var simulation = new Simulation();
        var ex = simulation.AssertSqlError($"{create}{Environment.NewLine}select 99", 156);
        AreEqual("Incorrect syntax near the keyword 'select'.", ex.Message);
        AreEqual(moduleName, ex.Procedure);
        AreEqual(2, ex.Errors[0].LineNumber);

        // Real parses the batch before executing any of it, so nothing was created.
        AreEqual(DBNull.Value, simulation.ExecuteScalar($"select object_id('dbo.{moduleName}')"));
    }

    /// <summary>
    /// A leftover token that isn't a keyword is Msg 102 (probe-confirmed for
    /// <c>)</c>, <c>@v</c>, a number, a string literal and a binary literal).
    /// It never reaches the trailing-statement check: the body's own parse is
    /// greedy and rejects the token first, which is why real's <c>Procedure</c>
    /// attribution is missing here — a view's body errors carry none, the
    /// standing divergence recorded in <c>programmable.md</c>.
    /// </summary>
    [TestMethod]
    [DataRow(")", ")")]
    [DataRow("@zz", "@zz")]
    [DataRow("123", "123")]
    public void NonKeywordAfterModuleBody_RaisesMsg102(string trailing, string echoed)
        => new Simulation().AssertSqlError(
            $"create view v as select 1 as x\n{trailing}", 102, $"Incorrect syntax near '{echoed}'.");

    /// <summary>The keyword is echoed as the source spelled it, not canonicalized.</summary>
    [TestMethod]
    [DataRow("SELECT 99", "SELECT")]
    [DataRow("PRINT 'x'", "PRINT")]
    [DataRow("declare @x int", "declare")]
    [DataRow("EXECUTE dbo.nothing", "EXECUTE")]
    public void StatementAfterModuleBody_EchoesTheKeywordAsWritten(string trailing, string echoed)
        => new Simulation().AssertSqlError(
            $"create view v as select 1 as x\n{trailing}", 156, $"Incorrect syntax near the keyword '{echoed}'.");

    /// <summary>Trailing separators and comments are not statements.</summary>
    [TestMethod]
    [DataRow("create view v as select 1 as x;")]
    [DataRow("create view v as select 1 as x;;;")]
    [DataRow("create view v as select 1 as x\n-- trailing comment")]
    [DataRow("create function v() returns int as begin return 1 end;")]
    public void TrailingSeparatorsAndComments_AreAccepted(string batch)
        => new Simulation().ExecuteNonQuery(batch);

    /// <summary>
    /// Procedure and trigger bodies are multi-statement and read to
    /// end-of-batch, so a trailing statement is swallowed into the body rather
    /// than rejected — probe-confirmed on real by executing the module and by
    /// reading <c>OBJECT_DEFINITION</c>.
    /// </summary>
    [TestMethod]
    public void StatementAfterProcedureBody_JoinsTheBody()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create proc p as select 1 as a\nselect 99 as b");
        Assert.Contains("select 99 as b", (string)simulation.ExecuteScalar("select object_definition(object_id('dbo.p'))")!);
    }

    [TestMethod]
    public void StatementAfterTriggerBody_JoinsTheBody()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (id int)",
            "create trigger tr on t after insert as select 1 as a\nselect 99 as b");
        Assert.Contains("select 99 as b", (string)simulation.ExecuteScalar("select object_definition(object_id('dbo.tr'))")!);
    }
}
