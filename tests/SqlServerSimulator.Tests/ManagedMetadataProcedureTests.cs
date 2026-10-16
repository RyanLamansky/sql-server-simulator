using System.Globalization;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The <c>_managed</c> metadata procedures SqlClient calls —
/// <c>sp_procedure_params_100_managed</c> / <c>sp_procedure_params_managed</c>,
/// <c>sp_columns_managed</c>, <c>sp_indexes_managed</c>,
/// <c>sp_indexcolumns_managed</c> — and the <c>ODBCSCALE</c> built-in its
/// <c>StructuredTypeMembers</c> query reads. Every asserted row was probed
/// against SQL Server 2025 (2026-10-16) with the same fixture.
/// </summary>
[TestClass]
public sealed class ManagedMetadataProcedureTests
{
    private static Simulation Fixture()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create schema s2",
            "create type dbo.phone from varchar(12) not null",
            "create type dbo.tt as table (k int primary key, v varchar(5))",
            "create xml schema collection dbo.xsc as N'<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"a\" type=\"xs:int\"/></xs:schema>'",
            """
            create table dbo.t (id int identity primary key, a nvarchar(20) not null default N'x', b decimal(9,2) null, c varchar(max), d datetime2(3), unique (a));
            create index ix_b on dbo.t (b desc) include (c);
            create table s2.sp (id int not null, x int sparse null, y varchar(10) sparse null, cs xml column_set for all_sparse_columns,
                h hierarchyid, ch char(3) collate Latin1_General_CS_AS, fl float(24), v vector(3), p dbo.phone);
            create table s2.k (a int not null, b varchar(10) not null, c int, constraint pk_k primary key nonclustered (b desc, a));
            create clustered index cx_k on s2.k (c, a);
            create table s2.h (a int, b int);
            create nonclustered columnstore index ncci on s2.h (a, b);
            """,
            "create view dbo.vw as select id, a from dbo.t",
            "create procedure dbo.p @x int, @y nvarchar(10) = N'q' output, @z decimal(5,2) out as select 1",
            """
            create procedure s2.many @a bit, @f date, @g datetime2(4), @j varchar(max), @n hierarchyid, @o geography, @r dbo.phone,
                @u sysname, @y vector(3), @tx xml(dbo.xsc), @js json, @tv dbo.tt readonly, @cur cursor varying output, @bi bigint as select 1
            """,
            "create procedure s2.many;2 @two int as select 2",
            "create function dbo.f (@a int) returns int as begin return @a end",
            "create function dbo.itvf (@p int) returns table as return select id from dbo.t where id = @p");
        return sim;
    }

    private static List<string> Rows(Simulation sim, string sql)
    {
        using var reader = sim.ExecuteReader(sql);
        var rows = new List<string>();
        while (reader.Read())
        {
            var cells = new string[reader.FieldCount];
            for (var i = 0; i < cells.Length; i++)
            {
                cells[i] = reader.IsDBNull(i) ? "NULL" : reader.GetValue(i) switch
                {
                    bool flag => flag ? "1" : "0",
                    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                    var value => value.ToString()!,
                };
            }
            rows.Add(string.Join("|", cells));
        }
        return rows;
    }

    [TestMethod]
    public void ProcedureParams100Managed_ReportsEachParametersSqlDbType()
    {
        var sim = Fixture();
        CollectionAssert.AreEqual(new[]
        {
            "@RETURN_VALUE|4|8|NULL|10|NULL|NULL|NULL|int|NULL|NULL|NULL|NULL",
            "@x|1|8|NULL|10|NULL|NULL|NULL|int|NULL|NULL|NULL|NULL",
            "@y|2|12|10|NULL|NULL|NULL|NULL|nvarchar|NULL|NULL|NULL|NULL",
            "@z|2|5|NULL|5|2|NULL|NULL|decimal|NULL|NULL|NULL|NULL",
        }, Rows(sim, "exec sys.sp_procedure_params_100_managed N'p'"));
        CollectionAssert.AreEqual(new[]
        {
            "@RETURN_VALUE|4|8|NULL|10|NULL|NULL|NULL|int|NULL|NULL|NULL|NULL",
            "@a|1|2|NULL|NULL|NULL|NULL|NULL|bit|NULL|NULL|NULL|NULL",
            "@f|1|31|NULL|NULL|NULL|NULL|NULL|date|NULL|NULL|NULL|0",
            "@g|1|33|NULL|NULL|NULL|NULL|NULL|datetime2|NULL|NULL|NULL|4",
            "@j|1|22|0|NULL|NULL|NULL|NULL|varchar|NULL|NULL|NULL|NULL",
            "@n|1|29|892|NULL|NULL|simulated|sys|hierarchyid|NULL|NULL|NULL|NULL",
            "@o|1|29|0|NULL|NULL|simulated|sys|geography|NULL|NULL|NULL|NULL",
            "@r|1|22|12|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL",
            "@u|1|12|128|NULL|NULL|NULL|NULL|nvarchar|NULL|NULL|NULL|NULL",
            "@y|1|21|20|NULL|NULL|NULL|NULL|varbinary|NULL|NULL|NULL|NULL",
            "@tx|1|25|NULL|NULL|NULL|simulated|s2|xml|simulated|dbo|xsc|NULL",
            "@tv|1|30|NULL|NULL|NULL|simulated|dbo|tt|NULL|NULL|NULL|NULL",
            "@cur|2|8|NULL|10|NULL|NULL|NULL|int|NULL|NULL|NULL|NULL",
            "@bi|1|0|NULL|19|NULL|NULL|NULL|bigint|NULL|NULL|NULL|NULL",
            "@RETURN_VALUE|4|8|NULL|10|NULL|NULL|NULL|int|NULL|NULL|NULL|NULL",
            "@two|1|8|NULL|10|NULL|NULL|NULL|int|NULL|NULL|NULL|NULL",
        }, Rows(sim, "exec sys.sp_procedure_params_100_managed 'many', null, 's2'"));
    }

    [TestMethod]
    public void ProcedureParams100Managed_FindsTheNameAsObjectIdDoes()
    {
        var sim = Fixture();
        IsEmpty(Rows(sim, "exec sys.sp_procedure_params_100_managed 'many'"));
        IsEmpty(Rows(sim, "exec sys.sp_procedure_params_100_managed 's2.many'"));
        CollectionAssert.AreEqual(new[] { "@two|1|8|NULL|10|NULL|NULL|NULL|int|NULL|NULL|NULL|NULL" },
            Rows(sim, "exec sys.sp_procedure_params_100_managed 'many', 2, 's2', '@TWO'"));
        CollectionAssert.AreEqual(new[]
        {
            "@RETURN_VALUE|4|8|NULL|10|NULL|NULL|NULL|int|NULL|NULL|NULL|NULL",
            "@loginame|1|12|128|NULL|NULL|NULL|NULL|nvarchar|NULL|NULL|NULL|NULL",
        }, Rows(sim, "exec sys.sp_procedure_params_100_managed 'sp_who'"));
        CollectionAssert.AreEqual(new[]
        {
            "@TABLE_RETURN_VALUE|4|23|NULL|NULL|NULL|NULL|NULL|table|NULL|NULL|NULL|NULL",
            "@p|1|8|NULL|10|NULL|NULL|NULL|int|NULL|NULL|NULL|NULL",
        }, Rows(sim, "exec sys.sp_procedure_params_100_managed 'itvf'"));
        _ = sim.AssertSqlError("exec sys.sp_procedure_params_100_managed", 201);
    }

    [TestMethod]
    public void ProcedureParamsManaged_ReportsTheOleDbColumns()
    {
        var sim = Fixture();
        var rows = Rows(sim, "exec sys.sp_procedure_params_managed 'many', null, 's2'");
        AreEqual("simulated|s2|many;1|@RETURN_VALUE|0|4|0|NULL|0|3|NULL|NULL|10|NULL|NULL|int|int|NULL|NULL|NULL|NULL|NULL|NULL", rows[0]);
        AreEqual("simulated|s2|many;1|@n|5|1|0|NULL|1|132|892|892|NULL|NULL|NULL|hierarchyid|hierarchyid|NULL|NULL|NULL|simulated|sys|hierarchyid", rows[5]);
        AreEqual("simulated|s2|many;1|@y|9|1|0|NULL|1|128|20|20|NULL|NULL|NULL|varbinary|varbinary|NULL|NULL|NULL|NULL|NULL|NULL", rows[9]);
        AreEqual("simulated|s2|many;1|@tx|10|1|0|NULL|1|141|NULL|NULL|NULL|NULL|NULL|xml|xml|simulated|dbo|xsc|NULL|NULL|NULL", rows[10]);
        AreEqual("simulated|s2|many;1|@tv|12|1|0|NULL|1|143|NULL|NULL|NULL|NULL|NULL|tt|tt|NULL|NULL|NULL|NULL|NULL|NULL", rows[11]);
        AreEqual("simulated|s2|many;2|@two|1|1|0|NULL|NULL|3|NULL|NULL|10|NULL|NULL|int|int|NULL|NULL|NULL|NULL|NULL|NULL", rows[^1]);
        CollectionAssert.AreEqual(new[]
        {
            "simulated|dbo|f;0|@RETURN_VALUE|0|4|0|NULL|1|3|NULL|NULL|10|NULL|NULL|int|int|NULL|NULL|NULL|NULL|NULL|NULL",
            "simulated|dbo|f;0|@a|1|1|0|NULL|1|3|NULL|NULL|10|NULL|NULL|int|int|NULL|NULL|NULL|NULL|NULL|NULL",
        }, Rows(sim, "exec sys.sp_procedure_params_managed 'f'"));
    }

    [TestMethod]
    public void ColumnsManaged_SchemaTypeChoosesTheSparseColumns()
    {
        var sim = Fixture();
        // Real lists a table's columns in its plan's order rather than by position.
        CollectionAssert.AreEquivalent(new[]
        {
            "simulated|s2|sp|id|1|NULL|NO|int|NULL|NULL|10|10|0|NULL|NULL|NULL|NULL|NULL|0|0|0",
            "simulated|s2|sp|cs|4|NULL|YES|xml|-1|-1|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|0|1|0",
            "simulated|s2|sp|h|5|NULL|YES|hierarchyid|892|892|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|0|0|0",
            "simulated|s2|sp|ch|6|NULL|YES|char|3|3|NULL|NULL|NULL|NULL|NULL|NULL|iso_1|NULL|0|0|0",
            "simulated|s2|sp|fl|7|NULL|YES|real|NULL|NULL|24|2|NULL|NULL|NULL|NULL|NULL|NULL|0|0|0",
            "simulated|s2|sp|v|8|NULL|YES|varbinary|20|20|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|0|0|0",
            "simulated|s2|sp|p|9|NULL|NO|varchar|12|12|NULL|NULL|NULL|NULL|NULL|NULL|iso_1|NULL|0|0|0",
        }, Rows(sim, "exec sys.sp_columns_managed null, null, 'sp', null, 0"));
        HasCount(9, Rows(sim, "exec sys.sp_columns_managed null, null, 'sp', null, 1"));
        CollectionAssert.AreEqual(new[]
        {
            "simulated|s2|sp|x|2|NULL|YES|int|NULL|NULL|10|10|0|NULL|NULL|NULL|NULL|NULL|1|0|0",
            "simulated|s2|sp|y|3|NULL|YES|varchar|10|10|NULL|NULL|NULL|NULL|NULL|NULL|iso_1|NULL|1|0|0",
        }, Rows(sim, "exec sys.sp_columns_managed null, null, 'sp', null, 2"));
        IsEmpty(Rows(sim, "exec sys.sp_columns_managed null, null, 'sp', null, null"));
        IsEmpty(Rows(sim, "exec sys.sp_columns_managed null, null, 'sp', null, 3"));
        _ = sim.AssertSqlError("exec sys.sp_columns_managed null, null, null, null, 'x'", 245);
    }

    [TestMethod]
    public void ColumnsManaged_ArgumentsAreLikePatterns()
    {
        var sim = Fixture();
        CollectionAssert.AreEquivalent(new[]
        {
            "simulated|dbo|t|id|1|NULL|NO|int|NULL|NULL|10|10|0|NULL|NULL|NULL|NULL|NULL|0|0|0",
            "simulated|dbo|t|a|2|(N'x')|NO|nvarchar|20|40|NULL|NULL|NULL|NULL|NULL|NULL|UNICODE|NULL|0|0|0",
            "simulated|dbo|vw|id|1|NULL|NO|int|NULL|NULL|10|10|0|NULL|NULL|NULL|NULL|NULL|0|0|0",
            "simulated|dbo|vw|a|2|NULL|NO|nvarchar|20|40|NULL|NULL|NULL|NULL|NULL|NULL|UNICODE|NULL|0|0|0",
            "simulated|s2|h|a|1|NULL|YES|int|NULL|NULL|10|10|0|NULL|NULL|NULL|NULL|NULL|0|0|0",
            "simulated|s2|k|a|1|NULL|NO|int|NULL|NULL|10|10|0|NULL|NULL|NULL|NULL|NULL|0|0|0",
            "simulated|s2|sp|id|1|NULL|NO|int|NULL|NULL|10|10|0|NULL|NULL|NULL|NULL|NULL|0|0|0",
        }, Rows(sim, "exec sys.sp_columns_managed 'SIM%', null, null, '[ai]%'"));
        CollectionAssert.AreEqual(new[]
        {
            "simulated|dbo|t|a|2|(N'x')|NO|nvarchar|20|40|NULL|NULL|NULL|NULL|NULL|NULL|UNICODE|NULL|0|0|0",
        }, Rows(sim, "exec sys.sp_columns_managed null, 'd%', 't', 'A'"));
        IsEmpty(Rows(sim, "exec sys.sp_columns_managed 'master', null, 't'"));
    }

    [TestMethod]
    public void IndexesManaged_ListsEveryIndexButTheHeapsByExactName()
    {
        var sim = Fixture();
        CollectionAssert.AreEqual(new[]
        {
            "simulated|s2|ncci|simulated|s2|h|ncci|NONCLUSTERED COLUMNSTORE",
            "simulated|s2|cx_k|simulated|s2|k|cx_k|CLUSTERED",
            "simulated|s2|pk_k|simulated|s2|k|pk_k|NONCLUSTERED",
        }, Rows(sim, "exec sys.sp_indexes_managed null, 's2'"));
        IsEmpty(Rows(sim, "exec sys.sp_indexes_managed null, 's%'"));
        CollectionAssert.AreEqual(new[] { "simulated|s2|pk_k|simulated|s2|k|pk_k|NONCLUSTERED" },
            Rows(sim, "exec sys.sp_indexes_managed 'SIMULATED', 'S2', 'K', 'PK_K'"));
    }

    [TestMethod]
    public void IndexColumnsManaged_ReportsKeyOrdinalsAndSystemTypes()
    {
        var sim = Fixture();
        CollectionAssert.AreEqual(new[]
        {
            "simulated|dbo|ix_b|simulated|dbo|t|b|1|106|ix_b",
            "simulated|dbo|ix_b|simulated|dbo|t|c|0|167|ix_b",
        }, Rows(sim, "exec sys.sp_indexcolumns_managed null, null, 't', 'ix_b'"));
        CollectionAssert.AreEqual(new[]
        {
            "simulated|s2|ncci|simulated|s2|h|a|0|56|ncci",
            "simulated|s2|ncci|simulated|s2|h|b|0|56|ncci",
            "simulated|s2|cx_k|simulated|s2|k|a|2|56|cx_k",
            "simulated|s2|cx_k|simulated|s2|k|c|1|56|cx_k",
            "simulated|s2|pk_k|simulated|s2|k|a|2|56|pk_k",
            "simulated|s2|pk_k|simulated|s2|k|b|1|167|pk_k",
        }, Rows(sim, "exec sys.sp_indexcolumns_managed null, 's2'"));
        _ = sim.AssertSqlError("exec sys.sp_indexes_managed @Bogus = 1", 8145);
    }

    [TestMethod]
    public void OdbcScale_ReturnsTheScaleOfATypeThatHasOne()
    {
        var sim = new Simulation();
        AreEqual("4|0|3|3|NULL|NULL|9|NULL|NULL|200|2|3|NULL",
            string.Join("|", Rows(sim, "select odbcscale(106, 4), odbcscale(58, 0), odbcscale(61, 3), odbcscale(42, 3), odbcscale(null, 1), odbcscale(56, null), odbcscale(56, 9), odbcscale(59, 2), odbcscale(62, 3), odbcscale(48, 200), odbcscale(106.7, 2.9), odbcscale('42', '3'), odbcscale(cast(1 as bigint), 1)")));
        _ = sim.AssertSqlError("select odbcscale(256, 1)", 220);
        _ = sim.AssertSqlError("select odbcscale(N'x', 1)", 245);
        _ = sim.AssertSqlError("select odbcscale(1)", 174);
    }
}
