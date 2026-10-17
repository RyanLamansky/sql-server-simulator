using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The SSMS-era <c>sp_MS*</c> helpers — <c>sp_MStablespace</c>,
/// <c>sp_MSindexspace</c>, <c>sp_MShelpindex</c>, <c>sp_MShelptype</c>,
/// <c>sp_MShelpcolumns</c> and <c>sp_MSdependencies</c> — over a fixture whose
/// answers were probed 2026-10-17 against SQL Server 2025 (the auto-named
/// constraints aside).
/// </summary>
[TestClass]
public sealed class MsHelperProcedureTests
{
    private static Simulation Fixture()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (id int constraint pk_t primary key, a int, b nvarchar(10)); create index ix_a on dbo.t (a); create unique index ux_b on dbo.t (b) include (a); create table dbo.h (x int)",
            "create view dbo.v as select id from dbo.t",
            "create type dbo.zzphone from varchar(12) not null",
            "create default dbo.zzdef as 'none'",
            "create rule dbo.zzrule as @v like '%'",
            """
            create table dbo.w (k int not null, ch char(5) not null, vb varbinary(max) null, nv nvarchar(max) null, ph dbo.zzphone, pc as (k + 1) persisted,
                sm smallint not null, bg bigint null, mo money null, bt bit null, nu numeric(18, 0) null, hi hierarchyid null, g uniqueidentifier rowguidcol null,
                i int identity(10, 5) not for replication, d nvarchar(3) not null constraint df_d default N'x',
                constraint uq_w unique nonclustered (sm desc, k) with (ignore_dup_key = on), constraint pk_w primary key clustered (k) with (fillfactor = 80))
            """,
            "exec sp_bindefault 'dbo.zzdef', 'dbo.w.ch'",
            "exec sp_bindrule 'dbo.zzrule', 'dbo.w.ch'",
            "create index ix_x on dbo.w (bg asc, mo desc, bt desc) include (sm) with (pad_index = on, fillfactor = 50, statistics_norecompute = on)",
            "create function dbo.zf () returns int as begin return (select count(*) from dbo.t) end",
            "create procedure dbo.zp as select dbo.zf(), * from dbo.v",
            "create type dbo.zzmoney from decimal(10, 2) null",
            "create type dbo.zzint from int not null");
        return sim;
    }

    private static List<string> Rows(Simulation simulation, string commandText)
    {
        using var reader = simulation.ExecuteReader(commandText);
        var rows = new List<string>();
        do
        {
            while (reader.Read())
                rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        }
        while (reader.NextResult());
        return rows;
    }

    [TestMethod]
    public void SpMsHelpColumns_DescribesEachStoredColumn()
    {
        var sim = Fixture();
        CollectionAssert.AreEqual(
            new[]
            {
                "k|1|int|4|NULL|NULL|int|NULL|NULL|False|False|4|NULL|NULL|NULL|NULL|0|NULL|False|False|NULL|NULL|NULL|NULL|NULL|NULL|NULL|1|0",
                "ch|2|char|5|NULL|NULL|char|dbo.zzdef|dbo.zzrule|False|False|1|NULL|NULL|NULL|NULL|0|NULL|False|False|True|dbo|zzdef|dbo|zzrule|SQL_Latin1_General_CP1_CI_AS|NULL|1|0",
                "vb|3|varbinary|-1|NULL|NULL|varbinary|NULL|NULL|True|False|1|NULL|NULL|NULL|NULL|0|NULL|False|False|True|NULL|NULL|NULL|NULL|NULL|NULL|0|0",
                "nv|4|nvarchar|0|NULL|NULL|nvarchar|NULL|NULL|True|False|1|NULL|NULL|NULL|NULL|0|NULL|False|False|NULL|NULL|NULL|NULL|NULL|SQL_Latin1_General_CP1_CI_AS|NULL|0|0",
                "ph|5|zzphone|12|NULL|NULL|varchar|NULL|NULL|False|False|0|NULL|NULL|NULL|NULL|0|NULL|False|False|True|NULL|NULL|NULL|NULL|SQL_Latin1_General_CP1_CI_AS|NULL|1|0",
                "pc|6|int|4|NULL|NULL|int|NULL|NULL|True|False|0|NULL|NULL|NULL|NULL|1|([k]+(1))|False|False|NULL|NULL|NULL|NULL|NULL|NULL|NULL|0|0",
                "sm|7|smallint|2|NULL|NULL|smallint|NULL|NULL|False|False|0|NULL|NULL|NULL|NULL|0|NULL|False|False|NULL|NULL|NULL|NULL|NULL|NULL|NULL|1|0",
                "bg|8|bigint|8|NULL|NULL|bigint|NULL|NULL|True|False|0|NULL|NULL|NULL|NULL|0|NULL|False|False|NULL|NULL|NULL|NULL|NULL|NULL|NULL|1|0",
                "mo|9|money|8|NULL|NULL|money|NULL|NULL|True|False|0|NULL|NULL|NULL|NULL|0|NULL|False|False|NULL|NULL|NULL|NULL|NULL|NULL|NULL|1|0",
                "bt|10|bit|1|NULL|NULL|bit|NULL|NULL|True|False|0|NULL|NULL|NULL|NULL|0|NULL|False|False|NULL|NULL|NULL|NULL|NULL|NULL|NULL|1|0",
                "nu|11|numeric|9|18|0|numeric|NULL|NULL|True|False|2|NULL|NULL|NULL|NULL|0|NULL|False|False|NULL|NULL|NULL|NULL|NULL|NULL|NULL|1|0",
                "g|13|uniqueidentifier|16|NULL|NULL|uniqueidentifier|NULL|NULL|True|False|8|NULL|NULL|NULL|NULL|0|NULL|False|False|NULL|NULL|NULL|NULL|NULL|NULL|NULL|1|0",
                "i|14|int|4|NULL|NULL|int|NULL|NULL|False|True|0|10|5|NULL|NULL|0|NULL|True|False|NULL|NULL|NULL|NULL|NULL|NULL|NULL|1|0",
                "d|15|nvarchar|3|NULL|NULL|nvarchar|NULL|NULL|False|False|1|NULL|NULL|df_d|(N'x')|0|NULL|False|False|NULL|NULL|NULL|NULL|NULL|SQL_Latin1_General_CP1_CI_AS|NULL|1|0",
            },
            Rows(sim, "exec sp_MShelpcolumns N'dbo.w'"));
        AreEqual("bg|bt|ch|d|g|i|k|mo|nu|nv|pc|ph|sm|vb", string.Join("|", Rows(sim, "exec sp_MShelpcolumns N'w', null, N'name'").Select(static row => row.Split('|')[0])));
        // @flags2 = 1 reports every numeric type's precision and scale.
        Assert.Contains("mo|9|money|8|19|4|money|", Rows(sim, "exec sp_MShelpcolumns N'dbo.w', null, null, 1")[8]);
        // A view's columns are never plain stored ones.
        AreEqual("id|1|int|4|NULL|NULL|int|NULL|NULL|False|False|0|NULL|NULL|NULL|NULL|0|NULL|False|False|NULL|NULL|NULL|NULL|NULL|NULL|NULL|0|0", Rows(sim, "exec sp_MShelpcolumns N'dbo.v'")[0]);
        var missing = sim.AssertSqlError("exec sp_MShelpcolumns N'nosuch'", 2786).Errors[0];
        AreEqual((76, "sp_MShelpcolumns"), (missing.LineNumber, missing.Procedure));
    }

    [TestMethod]
    public void SpMsHelpIndex_ReportsStatusBitsAndKeys()
    {
        var sim = Fixture();
        CollectionAssert.AreEqual(
            new[]
            {
                "pk_w|2066|1|80|k|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|PRIMARY|0|0|0|1",
                "uq_w|4099|2|0|sm|k|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|PRIMARY|0|1|0|1",
                "ix_x|16777472|3|50|bg|mo|bt|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|PRIMARY|0|6|0|1",
            },
            Rows(sim, "exec sp_MShelpindex N'dbo.w'"));
        HasCount(1, Rows(sim, "exec sp_MShelpindex N'dbo.w', N'IX_X'"));
        IsEmpty(Rows(sim, "exec sp_MShelpindex N'nosuch'"));
        IsEmpty(Rows(sim, "exec sp_MShelpindex N'dbo.h'"));
    }

    [TestMethod]
    public void SpMsHelpType_ListsSystemThenUserTypes()
    {
        var sim = Fixture();
        var sets = Rows(sim, "exec sp_MShelptype");
        HasCount(36 + 3, sets);
        CollectionAssert.Contains(sets, "nvarchar|8000|1|0|0|True|8000|4000|SQL_Latin1_General_CP1_CI_AS");
        CollectionAssert.AreEqual(
            new[]
            {
                "zzint|dbo|int|NULL|NULL|259|0|False|NULL|NULL|0|1|False|4|NULL|NULL|NULL",
                "zzmoney|dbo|decimal|NULL|NULL|258|0|True|10|2|2|0|False|9|NULL|NULL|NULL",
                "zzphone|dbo|varchar|NULL|NULL|257|12|False|NULL|NULL|1|0|True|12|NULL|NULL|SQL_Latin1_General_CP1_CI_AS",
            },
            Rows(sim, "exec sp_MShelptype null, N'uddt'"));
        CollectionAssert.AreEqual(new[] { "int|NULL|1|0|1|False|4|10|NULL" }, Rows(sim, "exec sp_MShelptype N'INT'"));
        HasCount(36, Rows(sim, "exec sp_MShelptype null, N'sdt'"));
        AreEqual(57, sim.AssertSqlError("exec sp_MShelptype N'dbo.zzphone'", 2786).Errors[0].LineNumber);
    }

    [TestMethod]
    public void SpMsDependencies_WalksOneObjectsDependencies()
    {
        var sim = Fixture();
        CollectionAssert.AreEqual(new[] { "8|t|dbo|1", "1|zf|dbo|2", "4|v|dbo|2" }, Rows(sim, "exec sp_MSdependencies N'dbo.zp'"));
        CollectionAssert.AreEqual(new[] { "8|t|dbo|1", "1|zf|dbo|2", "4|v|dbo|2", "16|zp|dbo|3" }, Rows(sim, "exec sp_MSdependencies N'dbo.zp', null, 0x801fd"));
        CollectionAssert.AreEqual(new[] { "1|zf|dbo|1", "4|v|dbo|1", "16|zp|dbo|2" }, Rows(sim, "exec sp_MSdependencies N'dbo.t', null, 0x401fd"));
        IsEmpty(Rows(sim, "exec sp_MSdependencies N'dbo.t', null, 0x40000"));
        AreEqual(152, sim.AssertSqlError("exec sp_MSdependencies N'nosuch'", 2786).Errors[0].LineNumber);
        _ = Throws<NotSupportedException>(() => sim.ExecuteNonQuery("exec sp_MSdependencies"));
    }

    [TestMethod]
    public void SpMsTableSpaceAndIndexSpace_ReportTheTablesSizes()
    {
        var sim = Fixture();
        _ = sim.ExecuteNonQuery("insert dbo.t (id, a, b) select value, value, cast(value as nvarchar(10)) from generate_series(1, 500)");
        var space = Rows(sim, "exec sp_MStablespace N'dbo.t'")[0].Split('|');
        AreEqual("500", space[0]);
        var indexes = Rows(sim, "exec sp_MSindexspace N'dbo.t'");
        CollectionAssert.AreEqual(new[] { "1|pk_t", "2|ix_a", "3|ux_b" }, indexes.ConvertAll(static row => string.Join("|", row.Split('|').Take(2))));
        AreEqual("Size excludes actual data.|(None)", indexes[0].Split('|')[3] + "|" + indexes[1].Split('|')[3]);
        // The clustered index named alone answers the table's whole index space.
        AreEqual(space[2], Rows(sim, "exec sp_MSindexspace N'dbo.t', N'pk_t'")[0]);
        AreEqual(space[0], Rows(sim, "declare @id int = object_id('dbo.t'); exec sp_MStablespace N'dbo.h', @id")[0].Split('|')[0]);
        CollectionAssert.AreEqual(new[] { "NULL|NULL|NULL" }, Rows(sim, "exec sp_MStablespace null, 0"));
        IsEmpty(Rows(sim, "exec sp_MSindexspace N'dbo.h'"));
        AreEqual(13, sim.AssertSqlError("exec sp_MStablespace N'dbo.v'", 15009).Errors[0].LineNumber);
        AreEqual(29, sim.AssertSqlError("exec sp_MSindexspace N'nosuch'", 2786).Errors[0].LineNumber);
        AreEqual(41, sim.AssertSqlError("exec sp_MSindexspace N'dbo.t', N'nosuch'", 2786).Errors[0].LineNumber);
    }
}
