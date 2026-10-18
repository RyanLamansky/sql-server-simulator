using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The tables a browse statement's TABNAME names, as SQL Server 2025 sends
/// them (captured 2026-10-18 with a raw TDS client): those its columns read,
/// hidden key columns included, in the order the columns first read them — a
/// keyless table or one behind a grouped body or statement goes unnamed — with
/// a multi-statement function named as the table it returns and an inline
/// function flattened into the tables its body reads.
/// </summary>
[TestClass]
public sealed class BrowseTableNameTests
{
    private static TdsSessionFixture Seeded()
    {
        var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("""
            create table bt (k int primary key, v varchar(10));
            create table bt2 (k2 int primary key, w int);
            create table h (a int);
            insert bt values (1, 'a');
            insert bt2 values (1, 2);
            """);
        _ = fixture.RunBatch("create function dbo.mf() returns @r table (a int primary key, b int) as begin insert @r values (1, 2); return end");
        _ = fixture.RunBatch("create function dbo.inf() returns table as return select k, v from dbo.bt");
        _ = fixture.RunBatch("set no_browsetable on");
        return fixture;
    }

    [TestMethod]
    [DataRow("select b.w, a.v from dbo.bt a cross join dbo.bt2 b", "dbo.bt2 dbo.bt", "w:0009 v:0009 k:0008 k2:0008")]
    [DataRow("select b.w from dbo.h x cross join dbo.bt2 b", "dbo.bt2", "w:0009 k2:0008")]
    [DataRow("select count(*) as n from dbo.bt", "", "n:0001")]
    [DataRow("select b.w, a.v from dbo.bt a cross join dbo.bt2 b group by a.v, b.w", "dbo.bt2 dbo.bt", "w:0009 v:0009")]
    [DataRow("select b.w from (select v from dbo.bt group by v) d cross join dbo.bt2 b", "dbo.bt2", "w:0009 k2:0008")]
    [DataRow("select b from dbo.mf()", "dbo.mf", "b:0009 a:0008")]
    [DataRow("select v from dbo.inf()", "dbo.bt", "v:0009 k:0008")]
    [DataRow("select v from dbo.inf() f join dbo.mf() m on f.k = m.a", "dbo.bt dbo.mf", "v:0009 k:0008 a:0008")]
    public void Tabname_NamesTheTablesTheColumnsRead(string sql, string tables, string columns)
    {
        using var fixture = Seeded();
        var response = fixture.RunBatch(sql);
        AreEqual(tables, string.Join(" ", TdsSessionFixture.BrowseTableNames(response)));
        AreEqual(columns, string.Join(" ", TdsSessionFixture.ColumnFlags(response)));
    }
}
