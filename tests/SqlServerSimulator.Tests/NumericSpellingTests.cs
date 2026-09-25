using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>numeric</c> and <c>decimal</c> are one type with two names, and the
/// catalog keeps them apart: probed 2026-09-24 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class NumericSpellingTests
{
    private const string Table = "create table nm (d decimal(5, 2), n numeric(5, 2));";

    private static string ColumnTypes(Simulation sim, string table) =>
        (string)sim.ExecuteScalar($"select string_agg(concat(name, ':', type_name(system_type_id), ':', type_name(user_type_id)), ',') within group (order by column_id) from sys.columns where object_id = object_id('{table}')")!;

    [TestMethod]
    public void DeclaredColumns_KeepTheirNames()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Table);
        AreEqual("d:decimal:decimal,n:numeric:numeric", ColumnTypes(sim, "nm"));
        AreEqual("decimal,numeric", sim.ExecuteScalar("select string_agg(data_type, ',') within group (order by ordinal_position) from information_schema.columns where table_name = 'nm'"));
    }

    [TestMethod]
    public void AlterColumnAndAddColumn_TakeTheWrittenName()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"{Table} alter table nm alter column d numeric(6, 2); alter table nm add e numeric(3, 0)");
        AreEqual("d:numeric:numeric,n:numeric:numeric,e:numeric:numeric", ColumnTypes(sim, "nm"));
    }

    [TestMethod]
    public void View_CarriesItsSourcesNames()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(Table, "create view nmv as select n, d from nm");
        AreEqual("n:numeric:numeric,d:decimal:decimal", ColumnTypes(sim, "nmv"));
    }

    [TestMethod]
    public void SelectInto_CarriesTheName()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            {Table}
            declare @v numeric(4, 1);
            select nm.n, x.n as xn, 1.5 as lit, d, d + 1 as da, @v as v into nmi from nm cross join (select n from nm) x;
            with c as (select n from nm) select n into nmc from c
            """);
        AreEqual("n:numeric:numeric,xn:numeric:numeric,lit:numeric:numeric,d:decimal:decimal,da:decimal:decimal,v:numeric:numeric", ColumnTypes(sim, "nmi"));
        AreEqual("n:numeric:numeric", ColumnTypes(sim, "nmc"));
    }

    [TestMethod]
    [DataRow("select 2.0 as v into t union all select cast(1 as decimal(5, 1))", "numeric")]
    [DataRow("select cast(1 as decimal(5, 1)) as v into t union all select 2.0", "decimal")]
    [DataRow("select 1 as v into t union all select 2.0", "numeric")]
    [DataRow("select 1 as v into t union all select cast(1 as decimal(5, 1)) union all select 2.0", "decimal")]
    [DataRow("select v into t from (values (2.0), (cast(1 as decimal(5, 1)))) x(v)", "numeric")]
    [DataRow("select v into t from (values (cast(1 as decimal(5, 1))), (2.0)) x(v)", "decimal")]
    [DataRow("select avg(v) as v into t from (values (1.0), (2.0)) x(v)", "numeric")]
    public void FirstDecimalBranch_NamesTheColumn(string sql, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"{sql}; select type_name(system_type_id) from sys.columns where object_id = object_id('t')"));

    [TestMethod]
    [DataRow("n + d", "numeric")]
    [DataRow("d + d", "decimal")]
    [DataRow("n * 2", "numeric")]
    [DataRow("d * 1", "decimal")]
    [DataRow("coalesce(d, n)", "decimal")]
    [DataRow("coalesce(n, d)", "numeric")]
    [DataRow("isnull(n, 0)", "numeric")]
    [DataRow("sum(n) over ()", "numeric")]
    [DataRow("(select max(n) from nm)", "numeric")]
    public void ReferenceInsideAnExpression_CarriesTheName(string expression, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"{Table} select {expression} as v into t from nm; select type_name(system_type_id) from sys.columns where object_id = object_id('t')"));

    [TestMethod]
    public void ComputedColumn_OverANumericColumn_IsNumeric()
        => AreEqual("numeric", new Simulation().ExecuteScalar("create table cn (n numeric(5, 2), c as n * 2); select type_name(system_type_id) from sys.columns where name = 'c'"));

    [TestMethod]
    public void ParametersAndReturnTypes_KeepTheirNames()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create procedure pn @a numeric(5, 2), @b decimal(5, 2) as select @a as a, @b as b into pnt",
            "create function fn(@a numeric(5, 2)) returns numeric(6, 2) as begin return @a end",
            "exec pn 1, 2");
        AreEqual("pn@a:numeric,pn@b:decimal,fn:numeric,fn@a:numeric", sim.ExecuteScalar("""
            select string_agg(concat(object_name(object_id), name, ':', type_name(system_type_id)), ',') within group (order by object_id, parameter_id)
            from sys.parameters where object_id in (object_id('pn'), object_id('fn'))
            """));
        AreEqual("a:numeric:numeric,b:decimal:decimal", ColumnTypes(sim, "pnt"));
        AreEqual("numeric", sim.ExecuteScalar("select dbo.fn(1) as v into fnt; select type_name(system_type_id) from sys.columns where object_id = object_id('fnt')"));
    }

    [TestMethod]
    public void SqlVariantBaseType_ReportsTheArgumentsSpelling()
        => AreEqual("decimal|numeric|decimal|numeric|decimal|numeric", new Simulation().ExecuteScalar($"""
            {Table} insert nm values (1, 1);
            select concat(sql_variant_property(d, 'BaseType'), '|', sql_variant_property(n, 'BaseType'), '|',
                sql_variant_property(cast(1 as decimal(5, 1)), 'BaseType'), '|', sql_variant_property(1.5, 'BaseType'), '|',
                sql_variant_property(d + 1, 'BaseType'), '|', sql_variant_property(n + d, 'BaseType')) from nm
            """));
}
