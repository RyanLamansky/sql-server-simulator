using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A text pointer names its row, not the value the cell holds: two rows
/// holding one value get distinct pointers, an ordinary <c>UPDATE</c> leaves a
/// pointer read before it valid, and a cell a write set NULL keeps its pointer
/// where one never given a value has none. Probed 2026-09-30 against SQL
/// Server 2025.
/// </summary>
[TestClass]
public sealed class TextPointerRowIdentityTests
{
    private const string Setup = "create table t (id int primary key, c text); insert t values (1, 'same'), (2, 'same'), (3, null);";

    private const string Rows = "select string_agg(concat(id, ':', isnull(cast(c as varchar(20)), 'NULL')), ',') within group (order by id) from t";

    [TestMethod]
    public void RowsHoldingOneValue_GetDistinctPointers()
        => AreEqual("1:same,2:two,3:NULL", new Simulation().ExecuteScalar(Setup + """
            declare @p1 varbinary(16), @p2 varbinary(16);
            select @p1 = textptr(c) from t where id = 1;
            select @p2 = textptr(c) from t where id = 2;
            if @p1 = @p2 throw 50000, 'shared', 1;
            writetext t.c @p2 'two';
            """ + Rows));

    [TestMethod]
    [DataRow("update t set c = 'updated' where id = 1")]
    [DataRow("update t set id = 10 where id = 1")]
    [DataRow("update t set c = replicate(cast('x' as varchar(max)), 9000) where id = 1")]
    public void OrdinaryUpdate_LeavesThePointerValid(string update)
        => AreEqual(1, new Simulation().ExecuteScalar(Setup + $"""
            declare @p varbinary(16);
            select @p = textptr(c) from t where id = 1;
            {update};
            updatetext t.c @p null 0 '+';
            select textvalid('t.c', @p) + count(*) - 1 from t where cast(c as varchar(max)) like '%+'
            """));

    [TestMethod]
    public void CellSetNull_KeepsItsPointer()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Setup + "declare @p varbinary(16); select @p = textptr(c) from t where id = 1; writetext t.c @p null; update t set c = null where id = 2");
        AreEqual("1:1,2:1,3:0", simulation.ExecuteScalar("select string_agg(concat(id, ':', textvalid('t.c', textptr(c))), ',') within group (order by id) from t"));
        AreEqual(DBNull.Value, simulation.ExecuteScalar("declare @p varbinary(16); select @p = textptr(c) from t where id = 1; readtext t.c @p 0 0"));
        AreEqual("1:back,2:NULL,3:NULL", simulation.ExecuteScalar("declare @p varbinary(16); select @p = textptr(c) from t where id = 1; writetext t.c @p 'back'; " + Rows));
        _ = simulation.ExecuteNonQuery("update t set c = 'v' where id = 3");
        AreEqual(1, simulation.ExecuteScalar("select textvalid('t.c', textptr(c)) from t where id = 3"));
    }

    [TestMethod]
    public void DeletedRow_InvalidatesItsPointer()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Setup);
        AreEqual(0, simulation.ExecuteScalar("declare @p varbinary(16); select @p = textptr(c) from t where id = 2; delete t where id = 2; select textvalid('t.c', @p)"));
        _ = simulation.AssertSqlError("declare @p varbinary(16); select @p = textptr(c) from t where id = 1; delete t where id = 1; readtext t.c @p 0 0", 7123);
    }

    [TestMethod]
    public void TruncatedTable_StartsWithoutPointers()
        => AreEqual(0, new Simulation().ExecuteScalar(Setup + "update t set c = null; truncate table t; insert t values (1, null); select count(textptr(c)) from t"));

    [TestMethod]
    public void UpdateSet_ReadsTheSamePointer()
        => AreEqual(3, new Simulation().ExecuteScalar(
            "create table u (id int primary key, c ntext, p varbinary(16)); insert u values (1, N'a', null), (2, N'a', null), (3, N'b', null); update u set p = textptr(c); select count(distinct p) from u where p = textptr(c)"));

    [TestMethod]
    [DataRow("select textvalid('nosuch.c', textptr(c)) from t where id = 1")]
    [DataRow("select textvalid('t.nosuch', textptr(c)) from t where id = 1")]
    [DataRow("select textvalid('t.id', textptr(c)) from t where id = 1")]
    [DataRow("create table u (id int, c text); insert u values (1, 'same'); select textvalid('u.c', textptr(c)) from t where id = 1")]
    public void TextValid_NamesAnotherColumn_IsZero(string sql)
        => AreEqual(0, new Simulation().ExecuteScalar(Setup + sql));

    [TestMethod]
    public void TextValid_QualifiedName_Resolves()
        => AreEqual(1, new Simulation().ExecuteScalar(Setup + "select textvalid('dbo.t.c', textptr(c)) from t where id = 1"));

    [TestMethod]
    [DataRow("select textptr(c) from (select id, c from t) d")]
    public void TextPtr_ThroughADerivedSource_IsRefused(string sql)
        => _ = new Simulation().AssertSqlError(Setup + sql, 280);

    [TestMethod]
    public void TextPtr_ThroughAView_IsRefused()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(Setup, "create view v as select id, c from t");
        _ = simulation.AssertSqlError("select textptr(c) from v", 280);
    }

    /// <summary>
    /// Every read path carries the row's address to the pointer: a heap of
    /// identical rows, a NOLOCK scan, a join, a correlated subquery and a
    /// cursor each hand out a pointer that writes the row it was read from.
    /// </summary>
    [TestMethod]
    public void EveryReadPath_NamesTheRowItRead()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table h (id int, c text); insert h values (1, 'x'), (1, 'x')");
        AreEqual("x,y", simulation.ExecuteScalar("declare @p varbinary(16); select top 1 @p = textptr(c) from h with (nolock); writetext h.c @p 'y'; select string_agg(cast(c as varchar(9)), ',') within group (order by cast(c as varchar(9))) from h"));
        _ = simulation.ExecuteNonQuery(Setup);
        AreEqual(2, simulation.ExecuteScalar("select count(*) from t a join t b on b.id = a.id where textptr(a.c) = textptr(b.c)"));
        AreEqual(2, simulation.ExecuteScalar("select count(*) from t where (select textptr(t.c)) is not null"));
        AreEqual("1:same!,2:same!,3:NULL", simulation.ExecuteScalar("""
            declare @id int, @p varbinary(16);
            declare k cursor for select id, textptr(c) from t order by id;
            open k; fetch k into @id, @p;
            while @@fetch_status = 0 begin; if @p is not null updatetext t.c @p null 0 '!'; fetch k into @id, @p; end;
            close k; deallocate k;
            """ + Rows));
    }
}
