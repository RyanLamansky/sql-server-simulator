using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// An index's <c>FILLFACTOR</c> / <c>PAD_INDEX</c>, recorded from every
/// declaring form and reported through <c>sys.indexes</c> and
/// <c>INDEXPROPERTY</c>. Every expectation probed 2026-09-26 against SQL
/// Server 2025.
/// </summary>
[TestClass]
public sealed class IndexFillFactorTests
{
    private static string Report(string ddl)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int not null, a int, b int)");
        _ = simulation.ExecuteNonQuery(ddl);
        return (string)simulation.ExecuteScalar("""
            select string_agg(concat(name, ':', fill_factor, is_padded, indexproperty(object_id, name, 'IndexFillFactor'), indexproperty(object_id, name, 'IsPadIndex')), ',') within group (order by name)
            from sys.indexes where object_id = object_id('t') and index_id > 0
            """)!;
    }

    [TestMethod]
    [DataRow("create index ix on t (a)", "ix:0000")]
    [DataRow("create index ix on t (a) with (fillfactor = 80)", "ix:800800")]
    [DataRow("create index ix on t (a) with (fillfactor = 80, pad_index = on)", "ix:801801")]
    [DataRow("create index ix on t (a) with (pad_index = on)", "ix:0101")]
    [DataRow("create index ix on t (a) with fillfactor = 70", "ix:700700")]
    [DataRow("alter table t add constraint pk primary key (id) with (fillfactor = 90, pad_index = on)", "pk:901901")]
    [DataRow("alter table t add constraint uq unique (a) with fillfactor = 60", "uq:600600")]
    [DataRow("create index ix on t (a); alter index ix on t rebuild with (fillfactor = 50)", "ix:500500")]
    [DataRow("create index ix on t (a) with (fillfactor = 80); alter index ix on t rebuild", "ix:800800")]
    [DataRow("create index ix on t (a) with (fillfactor = 80); alter index ix on t rebuild with (pad_index = on)", "ix:801801")]
    [DataRow("create index ix on t (a) with (fillfactor = 80); alter index all on t rebuild with (fillfactor = 30)", "ix:300300")]
    public void FillFactor_IsRecorded(string ddl, string expected)
        => AreEqual(expected, Report(ddl));

    [TestMethod]
    public void CreateTable_RecordsInlineFillFactors()
        => AreEqual("ix:651,pk:750", new Simulation().ExecuteScalar("""
            create table t (id int not null constraint pk primary key with (fillfactor = 75), a int, index ix (a) with (fillfactor = 65, pad_index = on));
            select string_agg(concat(name, ':', fill_factor, is_padded), ',') within group (order by name) from sys.indexes where object_id = object_id('t')
            """));

    [TestMethod]
    public void Rollback_RestoresFillFactor()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int); create index ix on t (a) with (fillfactor = 80)").ExecuteNonQuery();
        _ = connection.CreateCommand("begin tran; alter index ix on t rebuild with (fillfactor = 20); rollback").ExecuteNonQuery();
        AreEqual((byte)80, connection.CreateCommand("select fill_factor from sys.indexes where name = 'ix'").ExecuteScalar());
    }

    [TestMethod]
    [DataRow("create index ix on t (a) with (fillfactor = 0)", 129, "Fillfactor 0 is not a valid percentage; fillfactor must be between 1 and 100.")]
    [DataRow("create index ix on t (a) with (fillfactor = 101)", 129, "Fillfactor 101 is not a valid percentage; fillfactor must be between 1 and 100.")]
    [DataRow("create index ix on t (a) with (fillfactor = -1)", 129, "Fillfactor -1 is not a valid percentage; fillfactor must be between 1 and 100.")]
    [DataRow("create index ix on t (a); alter index ix on t set (fillfactor = 30)", 155, "'fillfactor' is not a recognized ALTER INDEX SET option.")]
    [DataRow("create index ix on t (a); alter index ix on t set (online = on)", 155, "'online' is not a recognized ALTER INDEX SET option.")]
    [DataRow("create index ix on t (a); alter index ix on t set (nope = on)", 155, "'nope' is not a recognized ALTER INDEX option.")]
    public void Refusals_MatchReal(string sql, int number, string message)
        => new Simulation().AssertSqlError($"create table t (id int not null, a int); {sql}", number, message);
}
