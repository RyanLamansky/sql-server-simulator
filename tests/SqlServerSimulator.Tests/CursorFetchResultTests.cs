using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The result set a T-SQL <c>FETCH</c> without <c>INTO</c> answers: the
/// cursor's columns plus a trailing hidden <c>ROWSTAT</c>, sent even when the
/// fetch lands on no row, and the zero-and-NULL row a deleted keyset member
/// reads as. Probed 2026-09-25 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class CursorFetchResultTests
{
    private static List<(int FieldCount, int VisibleFieldCount, List<object[]> Rows)> ResultSets(string commandText)
    {
        using var connection = new Simulation().CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        using var reader = command.ExecuteReader();
        var results = new List<(int, int, List<object[]>)>();
        do
        {
            var rows = new List<object[]>();
            while (reader.Read())
            {
                var row = new object[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                    row[i] = reader.GetValue(i);
                rows.Add(row);
            }
            results.Add((reader.FieldCount, reader.VisibleFieldCount, rows));
        } while (reader.NextResult());
        return results;
    }

    [TestMethod]
    public void Fetch_EndsInAHiddenRowStat_EvenPastTheEnd()
    {
        var results = ResultSets("""
            declare c cursor local fast_forward for select 1 a, 'x' b;
            open c; fetch next from c; fetch next from c; close c; deallocate c
            """);

        HasCount(2, results);
        AreEqual(3, results[0].FieldCount);
        AreEqual(2, results[0].VisibleFieldCount);
        CollectionAssert.AreEqual(new object[] { 1, "x", 1 }, results[0].Rows.Single());
        AreEqual(3, results[1].FieldCount);
        IsEmpty(results[1].Rows);
    }

    [TestMethod]
    public void GetValues_FillsOnlyTheVisibleColumns()
    {
        using var connection = new Simulation().CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "declare c cursor local static for select 7 a; open c; fetch next from c";
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        var values = new object[] { "untouched", "untouched" };
        AreEqual(1, reader.GetValues(values));
        CollectionAssert.AreEqual(new object[] { 7, "untouched" }, values);
        AreEqual("ROWSTAT", reader.GetName(1));
    }

    [TestMethod]
    public void DeletedKeysetMember_ReadsAsZeroesAndNulls()
    {
        var results = ResultSets("""
            create table k (id int primary key, a varchar(5) not null, b datetime not null, c decimal(5,2) not null,
                d bit not null, f varbinary(4) not null, n nchar(3) not null, m varchar(max) not null,
                dd date not null, t time not null, g nvarchar(3) null);
            insert k values (1, 'x', getdate(), 1.5, 1, 0x01, 'a', 'm', getdate(), '01:00', 'q'), (2, 'y', getdate(), 2, 0, 0x02, 'b', 'n', getdate(), '02:00', 'r');
            declare c cursor local keyset for select id, a, b, c, d, f, n, m, dd, t, g, id + 1 w from k;
            open c; delete k where id = 1; fetch first from c; fetch next from c; close c; deallocate c
            """);

        CollectionAssert.AreEqual(
            new object[] { 0, "     ", new DateTime(1900, 1, 1), 0.00m, false, new byte[4], "   ", "", DateTime.MinValue, TimeSpan.Zero, DBNull.Value, DBNull.Value, 2 },
            results[0].Rows.Single(),
            new ValueComparer());
        AreEqual(1, results[1].Rows.Single()[^1]);
    }

    [TestMethod]
    public void DeletedKeysetMember_WritesZeroesAndNullsIntoVariables()
        => AreEqual("-2|0|[   ]|NULL", new Simulation().ExecuteScalar("""
            create table k (id int primary key, a char(3) not null, g int null);
            insert k values (1, 'x', 5), (2, 'y', 6);
            declare c cursor local keyset for select id, a, g from k;
            open c; delete k where id = 1;
            declare @id int = 9, @a char(3) = 'zzz', @g int = 9;
            fetch first from c into @id, @a, @g;
            select concat(@@fetch_status, '|', @id, '|[', @a, ']|', isnull(str(@g), 'NULL'))
            """));

    private sealed class ValueComparer : System.Collections.IComparer
    {
        public int Compare(object? x, object? y) => (x, y) switch
        {
            (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b) ? 0 : 1,
            (DateOnly a, DateTime b) => a == DateOnly.FromDateTime(b) ? 0 : 1,
            _ => Equals(x, y) ? 0 : 1,
        };
    }
}
