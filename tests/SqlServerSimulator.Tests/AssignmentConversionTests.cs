using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The one-way implicit-conversion rule a value meets on its way into a typed
/// target, probed 2026-09-24 against SQL Server 2025 over every ordered pair of
/// thirty-one types (a variable of each initialized from one of every other),
/// plus binary comparison and a new database's rowversion counter.
/// </summary>
[TestClass]
public sealed class AssignmentConversionTests
{
    [TestMethod]
    [DataRow("datetime", "decimal(10, 2)", 257, "Implicit conversion from data type datetime to decimal is not allowed. Use the CONVERT function to run this query.")]
    [DataRow("int", "xml", 206, "Operand type clash: int is incompatible with xml")]
    [DataRow("date", "int", 206, "Operand type clash: date is incompatible with int")]
    [DataRow("varchar(10)", "varbinary(10)", 257, "Implicit conversion from data type varchar to varbinary is not allowed. Use the CONVERT function to run this query.")]
    [DataRow("sql_variant", "int", 257, "Implicit conversion from data type sql_variant to int is not allowed. Use the CONVERT function to run this query.")]
    [DataRow("varchar(max)", "sql_variant", 206, "Operand type clash: varchar(max) is incompatible with sql_variant")]
    [DataRow("uniqueidentifier", "int", 206, "Operand type clash: uniqueidentifier is incompatible with int")]
    [DataRow("varbinary(10)", "float", 206, "Operand type clash: varbinary is incompatible with float")]
    public void Variable_RefusesAnImplicitConversionRealWont(string source, string target, int number, string message)
        => new Simulation().AssertSqlError($"declare @s {source}; declare @t {target} = @s", number, message);

    [TestMethod]
    [DataRow("int", "varchar(10)")]
    [DataRow("varchar(10)", "date")]
    [DataRow("date", "datetime")]
    [DataRow("datetime", "time")]
    [DataRow("bit", "sql_variant")]
    [DataRow("varchar(10)", "xml")]
    [DataRow("nvarchar(10)", "hierarchyid")]
    [DataRow("varbinary(10)", "int")]
    [DataRow("timestamp", "varbinary(10)")]
    public void Variable_AcceptsAnImplicitConversion(string source, string target)
        => IsNull(new Simulation().ExecuteScalar($"declare @s {(source == "timestamp" ? "rowversion" : source)}; declare @t {target} = @s; select 1 where 1 = 0"));

    [TestMethod]
    public void BareNull_IsAssignableToAnything()
        => AreEqual(1, new Simulation().ExecuteScalar("declare @x xml = null; declare @u uniqueidentifier; set @u = null; select 1"));

    [TestMethod]
    [DataRow("declare @d decimal(10, 2); set @d = getdate()")]
    [DataRow("declare @d decimal(10, 2); select @d = getdate()")]
    [DataRow("create table t (d decimal(10, 2)); insert t values (getdate())")]
    [DataRow("create table t (d decimal(10, 2)); update t set d = getdate()")]
    [DataRow("create table t (d decimal(10, 2) default getdate())")]
    [DataRow("select isnull(cast(1 as decimal(10, 2)), getdate())")]
    public void EveryAssignmentSite_RaisesMsg257(string sql)
        => new Simulation().AssertSqlError(sql, 257);

    [TestMethod]
    public void EmptyUpdate_StillRaises()
        => new Simulation().AssertSqlError("create table t (x xml); update t set x = 1 where 1 = 0", 206);

    [TestMethod]
    public void FunctionReturn_RaisesAtCreate()
        => new Simulation().AssertSqlError("create function f() returns decimal(10, 2) as begin return getdate() end", 257);

    [TestMethod]
    public void FunctionArgument_RaisesAtTheCall()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create function f(@d decimal(10, 2)) returns int as begin return 1 end");
        _ = sim.AssertSqlError("declare @t datetime = getdate(); select dbo.f(@t)", 257);
    }

    [TestMethod]
    public void SideEffectInTheReturn_ReportsAlone()
        => new Simulation().AssertSqlError("create function f() returns int as begin return newid() end", 443);

    [TestMethod]
    public void Coalesce_StillUnifies()
        => IsNotNull(new Simulation().ExecuteScalar("select coalesce(cast(1 as decimal(10, 2)), getdate())"));

    // ---- binary comparison zero-pads the shorter side ----

    [TestMethod]
    [DataRow("0x0102 = 0x010200", 1)]
    [DataRow("0x0102 = 0x01020000", 1)]
    [DataRow("0x = 0x00", 1)]
    [DataRow("0x0102 < 0x010200", 0)]
    [DataRow("0x0100 > 0x01", 0)]
    [DataRow("cast(0x0102 as varbinary(5)) = cast(0x010200 as binary(3))", 1)]
    public void BinaryComparison_IgnoresTrailingZeros(string condition, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select case when {condition} then 1 else 0 end"));

    [TestMethod]
    public void BinaryDistinct_CollapsesTrailingZeros()
        => AreEqual("1|2", new Simulation().ExecuteScalar("""
            create table b (v varbinary(5));
            insert b values (0x0102), (0x010200);
            select concat((select count(distinct v) from b), '|', (select count(*) from b where v = 0x0102))
            """));

    // ---- rowversion ----

    [TestMethod]
    public void NewDatabase_CountsRowVersionsFrom2000()
        => AreEqual("0x00000000000007D0|0x00000000000007D1|0x00000000000007D0|2001", new Simulation().ExecuteScalar("""
            create table r (a int, v rowversion);
            declare @before varchar(20) = convert(varchar(20), @@dbts, 1);
            declare @min varchar(20) = convert(varchar(20), min_active_rowversion(), 1);
            declare @still varchar(20) = convert(varchar(20), @@dbts, 1);
            insert r (a) values (1);
            select concat(@before, '|', @min, '|', @still, '|', (select cast(v as bigint) from r))
            """));
}
