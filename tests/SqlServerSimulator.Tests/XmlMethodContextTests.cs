using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The contexts that refuse an <c>xml</c> method call while compiling (probed
/// 2026-09-28 against SQL Server 2025). See <c>docs/claude/xml.md</c>.
/// </summary>
[TestClass]
public sealed class XmlMethodContextTests
{
    [TestMethod]
    [DataRow("declare @x xml = '<a>1</a>'; print @x.value('(/a)[1]', 'int')")]
    [DataRow("declare @x xml = '<a>1</a>'; print 'v=' + @x.value('(/a)[1]', 'varchar(10)')")]
    [DataRow("declare @x xml = '<a>1</a>'; print case when @x.exist('/a') = 1 then 'y' else 'n' end")]
    [DataRow("if 1 = 0 begin declare @x xml = '<a/>'; print @x.exist('/a'); end")]
    public void PrintRefusesXmlMethod(string batch)
        => new Simulation().AssertSqlError(batch, 2722, "Xml data type methods are not allowed in expressions in this context.");

    [TestMethod]
    public void PrintRefusalStopsTheWholeBatch()
        => AreEqual(2722, Throws<SimulatedSqlException>(() => new Simulation().ExecuteReader("select 1; declare @x xml = '<a/>'; print @x.exist('/a')")).Number);

    [TestMethod]
    public void PrintRefusalAtCreateProcedure()
        => _ = new Simulation().AssertSqlError("create procedure p as print cast('<a/>' as xml).exist('/a')", 2722);

    [TestMethod]
    public void PrintAcceptsAWrappingUdf()
        => AreEqual("1", new Simulation().ExecuteBatchesScalar(
            "create function f (@x xml) returns int as begin return @x.value('(/a)[1]', 'int'); end",
            "create table t (s varchar(10)); declare @x xml = '<a>1</a>', @i int = @x.value('(/a)[1]', 'int'); if @x.exist('/a') = 1 insert t select dbo.f(@x); select s from t"));

    [TestMethod]
    [DataRow("create table t (x xml, check (x.exist('/a') = 1))", "t")]
    [DataRow("create table t (x xml check (len(x.value('(/a)[1]', 'varchar(10)')) > 0))", "t")]
    [DataRow("create table t (x xml); alter table t with nocheck add constraint ck check (x.exist('/a') = 1)", "t")]
    [DataRow("declare @t table (x xml, check (x.exist('/a') = 1))", "@t")]
    [DataRow("create type tt as table (x xml, check (x.exist('/a') = 1))", "tt")]
    public void CheckConstraintRefusesXmlMethod(string batch, string table)
    {
        var ex = new Simulation().AssertSqlError(batch, 423);
        AreEqual($"Xml data type methods are not supported in check constraints. Create a scalar user-defined function to wrap the method invocation. The error occurred at table \"{table}\".", ex.Errors[0].Message);
        AreEqual(1750, ex.Errors[1].Number);
    }

    [TestMethod]
    [DataRow("create table t (x xml, i as x.value('(/a)[1]', 'int') persisted)", 435, "t", "CREATE TABLE")]
    [DataRow("create table #t (x xml, i as x.value('(/a)[1]', 'int'))", 435, "#t", "CREATE TABLE")]
    [DataRow("create type tt as table (x xml, i as x.value('(/a)[1]', 'int'))", 435, "tt", "CREATE TABLE")]
    [DataRow("create table t (x xml); alter table t add i as x.value('(/a)[1]', 'int')", 435, "t", "ALTER TABLE")]
    [DataRow("declare @t table (x xml, i as x.value('(/a)[1]', 'int'))", 424, "@t", "CREATE TABLE")]
    public void ComputedColumnRefusesXmlMethod(string batch, int error, string table, string statement)
        => Contains($"The error occurred at column \"i\", table \"{table}\", in the {statement} statement.", new Simulation().AssertSqlError(batch, error).Errors[0].Message);

    [TestMethod]
    public void DuplicateColumnReportsFirst()
        => _ = new Simulation().AssertSqlError("create table t (x xml, i as x.value('(/a)[1]', 'int'), k int, k int)", 2705);

    [TestMethod]
    public void AJoinedUpdatesWhere_BindsSqlColumnToItsFromAlias()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table #dp (id nvarchar(10), g xml, v bit);
            insert #dp values (N'p1', '<d><v id="p1"/></d>', 0), (N'p2', '<d/>', 0);
            update dp set dp.v = 1 from #dp as dp where dp.g.exist('d/v[@id = sql:column("dp.id")]') = 1;
            select count(*) from #dp where v = 1
            """));

    [TestMethod]
    public void AnXmlMethodUnderQuotedIdentifierOff_EndsTheBatchAndItsTransaction()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("set quoted_identifier off", "create procedure p as declare @x xml = '<a/>'; select @x.exist('/a'); select 'after'", "set quoted_identifier on");
        _ = sim.AssertSqlError("create table t (a int); begin tran; insert t values (1); exec p; select 'reached'", 1934);
        AreEqual(0, sim.ExecuteScalar("select @@trancount"));
        AreEqual(1934, sim.ExecuteScalar("begin try exec p end try begin catch select error_number() end catch"));
    }
    [TestMethod]
    public void AModifyUnderQuotedIdentifierOff_EndsTheBatchToo()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "set quoted_identifier off",
            "create procedure p as declare @x xml = '<a/>'; set @x.modify('delete /a'); select 'after'",
            "set quoted_identifier on");
        var ex = sim.AssertSqlError("exec p; select 'reached'", 1934);
        Assert.StartsWith("SELECT failed because", ex.Errors[0].Message);
        AreEqual(1934, sim.ExecuteScalar("begin try exec p end try begin catch select error_number() end catch"));
    }
}
