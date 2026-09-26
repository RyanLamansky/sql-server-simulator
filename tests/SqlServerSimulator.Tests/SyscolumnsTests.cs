using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The legacy <c>syscolumns</c> view: <c>sys.columns</c> and
/// <c>sys.parameters</c> in SQL Server 2000's shape, with its storage
/// encodings. Every expectation probed 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class SyscolumnsTests
{
    private static string Row(Simulation simulation, string objectName, string columnName) =>
        (string)simulation.ExecuteScalar($"""
            select concat_ws('|', xtype, typestat, xusertype, length, xprec, xscale, colstat, offset, status, type, usertype,
                isnull(str(prec), '-'), isnull(str(scale), '-'), collationid, convert(varchar(12), tdscollation, 1), isnullable)
            from syscolumns where id = object_id('{objectName}') and name = '{columnName}'
            """)!;

    [TestMethod]
    [DataRow("n0", "48|1|48|1|3|0|0|2|0|48|5|         3|         0|0|0x0000000000|0")]
    [DataRow("u0", "48|0|48|1|3|0|0|-1|8|38|5|         3|         0|0|0x0000000000|1")]
    [DataRow("n1", "127|1|127|8|19|0|0|3|0|63|0|        19|         0|0|0x0000000000|0")]
    [DataRow("u1", "127|0|127|8|19|0|0|-2|8|108|0|        19|         0|0|0x0000000000|1")]
    [DataRow("n2", "104|1|104|1|1|0|0|11|0|50|16|         1|-|0|0x0000000000|0")]
    [DataRow("u2", "104|0|104|1|1|0|0|11|8|50|16|         1|-|0|0x0000000000|1")]
    [DataRow("n3", "106|1|106|5|9|2|0|12|0|55|24|         9|         2|0|0x0000000000|0")]
    [DataRow("u4", "62|0|62|8|53|0|0|-3|8|109|8|        53|-|0|0x0000000000|1")]
    [DataRow("n5", "175|3|175|10|0|0|0|17|16|47|1|        10|-|872468488|0x0904D00034|0")]
    [DataRow("u5", "175|2|175|10|0|0|0|-4|56|39|1|        10|-|872468488|0x0904D00034|1")]
    [DataRow("n6", "231|3|231|-1|0|0|0|-5|16|39|0|        -1|-|872468488|0x0904D00034|0")]
    [DataRow("u7", "35|0|35|16|0|0|0|-6|8|35|19|-|-|872468488|0x0904D00034|1")]
    [DataRow("n8", "239|3|239|20|0|0|0|27|16|47|0|        10|-|872468488|0x0904D00034|0")]
    [DataRow("u8", "239|2|239|20|0|0|0|-7|24|39|0|        10|-|872468488|0x0904D00034|1")]
    [DataRow("u9", "173|2|173|10|0|0|0|-8|56|37|3|        10|-|0|0x0000000000|1")]
    [DataRow("n10", "231|3|256|256|0|0|0|-9|16|39|18|       128|-|872468488|0x0904D00034|0")]
    [DataRow("u11", "42|0|42|8|27|7|0|-10|8|0|0|        27|         7|0|0x0000000000|1")]
    public void TableColumns_CarryTheLegacyEncodings(string column, string expected)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (n0 tinyint not null, u0 tinyint, n1 bigint not null, u1 bigint, n2 bit not null, u2 bit, n3 decimal(9, 2) not null,
                u4 float, n5 char(10) not null, u5 char(10), n6 nvarchar(max) not null, u7 text, n8 nchar(10) not null, u8 nchar(10),
                u9 binary(10), n10 sysname not null, u11 datetime2)
            """);
        AreEqual(expected, Row(simulation, "t", column));
    }

    [TestMethod]
    public void Identity_Computed_AndADefault()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int identity primary key, a varchar(5) not null default 'x', g as id + 1)");
        AreEqual("1|128|2", simulation.ExecuteScalar("select concat_ws('|', colstat, status, offset) from syscolumns where name = 'id'"));
        AreEqual("4|72|0|1", simulation.ExecuteScalar("select concat_ws('|', colstat, status, offset, iscomputed) from syscolumns where name = 'g'"));
        AreEqual(1, simulation.ExecuteScalar("select count(*) from syscolumns c join sys.default_constraints d on d.object_id = c.cdefault where c.name = 'a'"));
    }

    [TestMethod]
    public void DroppedColumns_LeaveTheLayoutRepacked()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int not null, b int not null, c varchar(3), d int not null); alter table t drop column b");
        AreEqual("a:1:2,c:3:-1,d:4:6", simulation.ExecuteScalar(
            "select string_agg(concat(name, ':', colid, ':', offset), ',') within group (order by colid) from syscolumns where id = object_id('t')"));
    }

    [TestMethod]
    public void Parameters_AndAScalarFunctionsReturnValue()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create procedure p @x int, @y varchar(10) output as select 1",
            "create function f (@p int) returns decimal(5, 2) as begin return 1 end",
            "create function tf (@p int) returns table as return select @p q");
        AreEqual("@x:1:1:8:38:0,@y:2:1:72:39:1", simulation.ExecuteScalar(
            "select string_agg(concat(name, ':', colid, ':', number, ':', status, ':', type, ':', isoutparam), ',') within group (order by colid) from syscolumns where id = object_id('p')"));
        AreEqual(":0:0:72:106:1,@p:1:0:8:38:0", simulation.ExecuteScalar(
            "select string_agg(concat(name, ':', colid, ':', number, ':', status, ':', type, ':', isoutparam), ',') within group (order by colid) from syscolumns where id = object_id('f')"));
        AreEqual("q:0,@p:1", simulation.ExecuteScalar(
            "select string_agg(concat(name, ':', number), ',') within group (order by number) from syscolumns where id = object_id('tf')"));
    }

    [TestMethod]
    public void AViewsColumns_HaveNoLayout()
        => AreEqual("0:1:56,0:26:39", new Simulation().ExecuteBatchesScalar(
            "create table t (a int not null, b varchar(5))",
            "create view v as select a, b from t",
            "select string_agg(concat(offset, ':', status + typestat, ':', type), ',') within group (order by colid) from syscolumns where id = object_id('v')"));
}
