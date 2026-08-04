using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>SELECT … INTO</c> writes the rows its projection computed straight into
/// the destination's columns. These pin what the copy preserves — every type
/// family, NULLs, <c>char</c> padding, a MAX string past the in-row limit, a
/// source that produces encoded rows rather than projected values, a schema
/// wider than the encoder's stack scratch — plus the statement-level
/// obligations around it (identity high-water mark, <c>@@ROWCOUNT</c>, and a
/// temp destination vanishing on <c>ROLLBACK</c>). Values probed against
/// SQL Server 2025.
/// </summary>
[TestClass]
public sealed class SelectIntoValueFidelityTests
{
    /// <summary>One populated row and one all-NULL row across every storage family.</summary>
    private const string Seed = """
        create table src (id int identity(5,1) primary key, c char(6) null, vc varchar(20) null,
                          nvc nvarchar(20) null, d decimal(9,3) null, m money null, dt datetime2(3) null,
                          bin varbinary(10) null, b bit null, big bigint null, f float null,
                          g uniqueidentifier null, big_txt varchar(max) null);
        insert src (c, vc, nvc, d, m, dt, bin, b, big, f, g, big_txt) values
            ('ab', 'hello', N'wörld', 1.5, 2.25, '2020-03-04T05:06:07.123', 0x0102, 1,
             9223372036854775807, 1.5e10, '22222222-2222-2222-2222-222222222222',
             replicate(cast('x' as varchar(max)), 9000)),
            (null, null, null, null, null, null, null, null, null, null, null, null);
        """;

    /// <summary>The copied row's cells joined by <c>|</c>.</summary>
    private static string CopiedRow(string projection, string where)
    {
        using var reader = new Simulation().ExecuteReader($"""
            {Seed}
            select * into dst from src;
            select {projection} from dst where {where}
            """);
        IsTrue(reader.Read());
        var cells = new string[reader.FieldCount];
        for (var i = 0; i < cells.Length; i++)
        {
            cells[i] = reader.IsDBNull(i)
                ? "NULL"
                : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!;
        }

        return string.Join('|', cells);
    }

    [TestMethod]
    public void EveryTypeFamily_CopiesItsValue()
        => AreEqual(
            "hello|wörld|1.500|2.2500|03/04/2020 05:06:07|True|9223372036854775807|15000000000|22222222-2222-2222-2222-222222222222",
            CopiedRow("vc, nvc, d, m, dt, b, big, f, g", "id = 5"));

    [TestMethod]
    public void EveryTypeFamily_CopiesItsNulls()
        => AreEqual(
            "NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL|NULL",
            CopiedRow("c, vc, nvc, d, m, dt, bin, b, big, f, g, big_txt", "id = 6"));

    /// <summary>
    /// <c>char(6)</c> keeps its blank padding through the copy — the
    /// destination column is <c>char(6)</c> and the encoder fills it.
    /// </summary>
    [TestMethod]
    public void FixedLengthString_KeepsItsPadding()
        => AreEqual("[ab    ]|6", CopiedRow("'[' + c + ']', datalength(c)", "id = 5"));

    [TestMethod]
    public void BinaryValue_CopiesItsBytes()
        => AreEqual("2|1|258", CopiedRow("datalength(bin), case when bin = 0x0102 then 1 else 0 end, cast(bin as int)", "id = 5"));

    /// <summary>
    /// A MAX string longer than a row can hold in line: the destination's
    /// encoder has the heap to push it off-row, and the whole value comes back.
    /// </summary>
    [TestMethod]
    public void MaxStringPastTheInRowLimit_CopiesWhole()
        => AreEqual("9000|x|x", CopiedRow("len(big_txt), substring(big_txt, 1, 1), substring(big_txt, 9000, 1)", "id = 5"));

    /// <summary>
    /// The identity high-water mark follows the copied values, so the next
    /// insert into the destination continues past the largest one copied.
    /// </summary>
    [TestMethod]
    public void IdentityHighWaterMark_FollowsTheCopiedValues()
        => AreEqual(7, new Simulation().ExecuteScalar<int>($"""
            {Seed}
            select id, vc into dst from src;
            insert dst (vc) values ('next');
            select id from dst where vc = 'next'
            """));

    [TestMethod]
    public void RowCount_ReportsTheRowsWritten()
        => AreEqual(2, new Simulation().ExecuteScalar<int>($"""
            {Seed}
            select id into dst from src;
            select @@rowcount
            """));

    /// <summary>
    /// A set operation produces encoded rows rather than projected values, so
    /// the copy decodes them — the other half of the row source.
    /// </summary>
    [TestMethod]
    public void SetOperationSource_CopiesEveryBranch()
        => AreEqual(6, new Simulation().ExecuteScalar<int>("""
            select v into dst from (select 1 as v union all select 2 union all select 3) x;
            select sum(v) from dst
            """));

    /// <summary>A view body is the other encoded-row producer.</summary>
    [TestMethod]
    public void ViewSource_CopiesItsRows()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table base (id int not null primary key, v varchar(10) not null)",
            "insert base values (1, 'a'), (2, 'b')",
            "create view vw as select id, v from base where id = 2");
        AreEqual("2|b", simulation.ExecuteScalar("""
            select id, v into dst from vw;
            select cast(id as varchar(3)) + '|' + v from dst
            """));
    }

    /// <summary>
    /// A temp destination created by <c>SELECT … INTO</c> inside a transaction
    /// is undone wholesale by <c>ROLLBACK</c> — rows and table together.
    /// </summary>
    [TestMethod]
    public void TempDestination_RolledBack_VanishesEntirely()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("create table base (id int not null primary key); insert base values (1), (2)").ExecuteNonQuery();
        _ = connection.CreateCommand("begin tran; select id into #t from base; rollback").ExecuteNonQuery();
        _ = connection.CreateCommand("select id into #t from base where id = 1").ExecuteNonQuery();
        AreEqual(1, connection.CreateCommand("select count(*) from #t").ExecuteScalar());
    }

    /// <summary>
    /// Seventy columns — past the width at which the row encoder takes its
    /// per-row scratch off the stack, so this is the heap-scratch fallback.
    /// </summary>
    [TestMethod]
    public void SchemaWiderThanTheEncodersStackScratch_CopiesEveryColumn()
    {
        var columns = string.Join(", ", Enumerable.Range(1, 70).Select(i => $"c{i} int not null"));
        var values = string.Join(", ", Enumerable.Range(1, 70).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var sum = string.Join(" + ", Enumerable.Range(1, 70).Select(i => $"c{i}"));
        AreEqual(2485, new Simulation().ExecuteScalar<int>($"""
            create table wide ({columns});
            insert wide values ({values});
            select * into dst from wide;
            select {sum} from dst
            """));
    }
}
