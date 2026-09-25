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
}
