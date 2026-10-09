using SqlServerSimulator.Storage;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <see cref="RealPageLayout"/> fills pages as real fills a table loaded in
/// key order: each shape's page counts are what <c>%%physloc%%</c> reported
/// for the same table on SQL Server 2025 (probed 2026-10-09), the fill of every
/// page in order, a repeated count written once with its repetitions.
/// </summary>
[TestClass]
public sealed class RealPageLayoutTests
{
    [TestMethod]
    [DataRow("id int not null primary key, pad char(2000) not null", "value, 'x'", 400, "", "4x100")]
    [DataRow("id int not null primary key, pad char(100) not null", "value, 'x'", 400, "", "71x5,45")]
    [DataRow("id int not null primary key, pad char(10) not null", "value, 'x'", 2000, "", "352x5,240")]
    [DataRow("id int not null primary key", "value", 3000, "", "622x4,512")]
    [DataRow("id int not null primary key, b bigint not null", "value, value", 3000, "", "385x7,305")]
    [DataRow("id int not null primary key, b int null", "value, value", 3000, "", "476x6,144")]
    [DataRow("id int not null primary key, s varchar(100) not null", "value, replicate('x', 100)", 1000, "", "69x14,34")]
    [DataRow("id int not null primary key, s varchar(100) null", "value, null", 3000, "", "622x4,512")]
    [DataRow("id int not null primary key, s varchar(100) not null, t varchar(100) not null", "value, replicate('x', 50), replicate('y', 30)", 1000, "", "81x12,28")]
    [DataRow("id int not null primary key, s nchar(100) not null", "value, N'x'", 1000, "", "38x26,12")]
    [DataRow("id int not null primary key, a bit not null, b bit not null, c bit not null, d bit not null, e bit not null, f bit not null, g bit not null, h bit not null, i bit not null", "value, 1, 1, 1, 1, 1, 1, 1, 1, 1", 3000, "", "506x5,470")]
    [DataRow("id int not null primary key, d datetime2 not null, m decimal(18, 2) not null, g uniqueidentifier not null, dt date not null", "value, sysdatetime(), 1.5, newid(), getdate()", 3000, "", "165x18,30")]
    [DataRow("id bigint not null primary key, pad char(500) not null", "value, 'x'", 500, "", "15x33,5")]
    [DataRow("id int not null primary key, pad char(2000) not null", "value, 'x'", 400, "alter database current set allow_snapshot_isolation on", "3x133,1")]
    [DataRow("id int not null, pad char(2000) not null", "value, 'x'", 400, "", "4x100")]
    [DataRow("a int not null, id int not null, pad char(1000) not null, primary key (a, id)", "1, value, 'x'", 400, "", "7x57,1")]
    [DataRow("id int not null primary key, s varchar(max) not null", "value, replicate('x', 300)", 500, "", "25x20")]
    [DataRow("id int not null primary key, s varchar(1000) not null", "value, replicate('x', value % 500)", 1000, "", "110,52,40,34,30,27,25,23,21,20,19,18x2,17,16x2,53,82,49,39,33,29,26,24,23,21,20,19,18x2,17,16x2,11")]
    public void PagesFillAsReals(string columns, string values, int rows, string database, string fills)
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        if (database.Length != 0)
            Exec(connection, database);
        Exec(connection, $"create table t ({columns}); insert t select {values} from generate_series(1, {rows})");
        var table = connection.CurrentDatabase.Schemas["dbo"].HeapTables["t"];
        var layout = RealPageLayout.For(table);
        var counts = new int[layout.PageCount];
        foreach (var (pageIndex, slotIndex, _) in table.Heap.EnumerateRowsWithAddress())
            counts[layout.PageOf((pageIndex, slotIndex))]++;
        AreEqual(fills, Runs(counts));
    }

    private static string Runs(int[] counts)
    {
        List<string> runs = [];
        for (var i = 0; i < counts.Length;)
        {
            var run = 1;
            while (i + run < counts.Length && counts[i + run] == counts[i])
                run++;
            runs.Add(run == 1 ? counts[i].ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{counts[i]}x{run}"));
            i += run;
        }
        return string.Join(",", runs);
    }

    private static void Exec(SimulatedDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }
}
