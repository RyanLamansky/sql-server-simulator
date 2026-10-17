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

    /// <summary>
    /// A fetch's columns read as their table's key when the cursor was asked
    /// for as KEYSET or DYNAMIC — named, through SCROLL or the forward-only
    /// default — even where its shape converted it to a snapshot, and never
    /// for STATIC, INSENSITIVE or FAST_FORWARD, a forward-only read-only one
    /// included; the key is a clustered table's clustering key over any
    /// nonclustered primary key, else that key (captured 2026-10-17 against
    /// SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("cursor dynamic for select id, v from pk", "id")]
    [DataRow("cursor keyset for select id, v from pk", "id")]
    [DataRow("cursor for select id, v from pk", "id")]
    [DataRow("scroll cursor for select v, id from pk", "id")]
    [DataRow("cursor dynamic for select distinct id, v from pk", "id")]
    [DataRow("cursor static for select id, v from pk", "")]
    [DataRow("insensitive cursor for select id, v from pk", "")]
    [DataRow("cursor fast_forward for select id, v from pk", "")]
    [DataRow("cursor local read_only for select id, v from pk", "")]
    [DataRow("cursor dynamic for select id, v from nc", "v")]
    [DataRow("cursor dynamic for select id, v from heap_pk", "id")]
    [DataRow("cursor dynamic for select id, v from heap", "")]
    public void Fetch_MarksTheKeyARequestedKeysetOrDynamicCursorIdentifiesRowsBy(string declaration, string keys)
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table pk (id int primary key, v int);
            create table nc (id int primary key nonclustered, v int);
            create clustered index cx on nc (v);
            create table heap_pk (id int primary key nonclustered, v int);
            create table heap (id int, v int);
            """).ExecuteNonQuery();
        using var reader = connection.CreateCommand($"declare c {declaration}; open c; fetch c; close c; deallocate c").ExecuteReader();
        var schema = reader.GetSchemaTable()!;
        AreEqual(keys, string.Join(",", schema.Rows.Cast<System.Data.DataRow>().Where(static row => (bool)row["IsKey"]).Select(static row => (string)row["ColumnName"])));
    }

    /// <summary>
    /// A fetch names the tables its select list reads, in the order it first
    /// reads them, reading a derived table, CTE or APPLY body through to the
    /// table or view behind each column — an expression, an aggregate or a set
    /// operation's column there an expression — while a view is named itself
    /// (captured 2026-10-17 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("cursor for select * from (select k1, v from ct) q", "k1:ct.k1:k v:ct.v:")]
    [DataRow("cursor static for select q.v, q.e from (select v, v + 1 as e from ct) q", "v:ct.v: e:.e:e")]
    [DataRow("cursor for with w as (select k1 as kk from ct) select * from w", "kk:ct.k1:k")]
    [DataRow("cursor for select * from cv", "k1:cv.k1:k e:cv.e:")]
    [DataRow("cursor for select q.e from (select * from cv) q", "e:cv.e:")]
    [DataRow("cursor for select ch.b, q.v from (select k1, v from ct) q join ch on ch.a = q.k1", "b:ch.b: v:ct.v:")]
    [DataRow("cursor for select x.v from ct x cross join ch", "v:ct.v:")]
    [DataRow("cursor for select q.n from (select count(*) as n from ct) q", "n:.n:e")]
    [DataRow("cursor for select q.k1 from (select k1 from ct union all select a from ch) q", "k1:.k1:e")]
    [DataRow("cursor for select y.v, x.k1 from ct x cross apply (select v from ct where ct.k1 = x.k1) y", "v:ct.v: k1:ct.k1:k")]
    public void Fetch_NamesTheTablesItsColumnsRead(string declaration, string columns)
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table ct (k1 int primary key, v int);
            create table ch (a int, b int);
            insert ct values (1, 10);
            insert ch values (1, 2);
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("create view cv as select k1, v + 1 as e from ct").ExecuteNonQuery();
        using var reader = connection.CreateCommand($"declare c {declaration}; open c; fetch c; close c; deallocate c").ExecuteReader();
        AreEqual(columns, string.Join(" ", reader.GetSchemaTable()!.Rows.Cast<System.Data.DataRow>().Where(static row => !(bool)row["IsHidden"]).Select(static row =>
            $"{row["ColumnName"]}:{row["BaseTableName"]}.{row["BaseColumnName"]}:{((bool)row["IsKey"] ? "k" : "")}{(row["IsExpression"] is true ? "e" : "")}")));
    }

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
