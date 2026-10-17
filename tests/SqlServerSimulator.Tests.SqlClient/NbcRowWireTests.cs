using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Rows whose NULLs outweigh a null bitmap travel as NBCROW, as real sends
/// them, and SqlClient reads them as it reads the in-process rows: every
/// column family's NULL beside its values, a row per token kind in one result,
/// and a cursor fetch.
/// </summary>
[TestClass]
public sealed class NbcRowWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("""
        select * from (values
            (1, cast(null as int), cast(null as varchar(5)), cast(null as nvarchar(max)), cast(null as varbinary(max)), cast(null as decimal(10, 2)),
                cast(null as uniqueidentifier), cast(null as datetime2), cast(null as sql_variant), cast(null as xml), cast(null as text), cast(null as date)),
            (2, 5, 'abc', N'long', 0x0102, 1.25, newid(), sysdatetime(), cast(7 as sql_variant), cast('<a/>' as xml), cast('t' as text), cast('2020-01-02' as date)),
            (3, null, 'x', null, null, null, null, null, null, null, null, null)) v(a, b, c, d, e, f, g, h, i, j, k, l)
        order by a
        """)]
    [DataRow("select * from n order by id")]
    [DataRow("declare c cursor local keyset for select id, a, b from n order by id; open c; fetch next from c; fetch next from c; close c; deallocate c")]
    public async Task NullHeavyRows_ReadAsInProcess(string sql)
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table n (id int primary key, a int null, b nvarchar(20) null, c varbinary(max) null, d datetimeoffset null, e bit null, f float null, g money null);
            insert n values (1, null, null, null, null, null, null, null), (2, 1, N'x', 0x01, sysdatetimeoffset(), 1, 1.5, 2.25), (3, null, N'y', null, null, 0, null, null);
            """);
        var expected = Wire.ReadAllInProc(simulation, sql);
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        var actual = Wire.Drain(reader);
        HasCount(expected.Count, actual);
        for (var r = 0; r < expected.Count; r++)
        {
            // A drawn value differs between the two reads; its nullness doesn't.
            for (var c = 0; c < expected[r].Length; c++)
            {
                if (expected[r][c] is null or Guid or DateTime or DateTimeOffset)
                    AreEqual(expected[r][c] is null, actual[r][c] is null);
                else
                    Wire.AssertValueEqual(expected[r][c]!, actual[r][c]!);
            }
        }
    }
}
