using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the built-in <c>STRING_SPLIT(input, sep [, enable_ordinal])</c>
/// rowset function. Validates the schema-shape decision at parse time
/// (presence of <c>ordinal</c> column), the single-char separator rule
/// (Msg 214), the invalid-third-arg rule (Msg 4199), and the segmentation
/// behavior (consecutive / trailing separators, empty inputs).
/// </summary>
[TestClass]
public sealed class StringSplitTests
{
    private static List<(string Value, long? Ordinal)> ReadAll(DbCommand command)
    {
        using var reader = command.ExecuteReader();
        var rows = new List<(string, long?)>();
        while (reader.Read())
        {
            var value = reader.IsDBNull(0) ? null! : reader.GetString(0);
            long? ordinal = reader.FieldCount > 1 ? reader.GetInt64(1) : null;
            rows.Add((value, ordinal));
        }
        return rows;
    }

    [TestMethod]
    public void Basic_SplitsCommaSeparated()
    {
        using var conn = new Simulation().CreateOpenConnection();
        var rows = ReadAll(conn.CreateCommand("select value from STRING_SPLIT('a,b,c', ',')"));
        HasCount(3, rows);
        AreEqual("a", rows[0].Value);
        AreEqual("b", rows[1].Value);
        AreEqual("c", rows[2].Value);
    }

    [TestMethod]
    public void ConsecutiveSeparators_ProducesEmptyValueRow()
    {
        using var conn = new Simulation().CreateOpenConnection();
        var rows = ReadAll(conn.CreateCommand("select value from STRING_SPLIT('a,,b', ',')"));
        HasCount(3, rows);
        AreEqual("a", rows[0].Value);
        AreEqual(string.Empty, rows[1].Value);
        AreEqual("b", rows[2].Value);
    }

    [TestMethod]
    public void TrailingSeparator_ProducesEmptyTailRow()
    {
        using var conn = new Simulation().CreateOpenConnection();
        var rows = ReadAll(conn.CreateCommand("select value from STRING_SPLIT('a,b,', ',')"));
        HasCount(3, rows);
        AreEqual(string.Empty, rows[2].Value);
    }

    [TestMethod]
    public void EmptyInputString_ProducesSingleEmptyRow()
    {
        using var conn = new Simulation().CreateOpenConnection();
        var rows = ReadAll(conn.CreateCommand("select value from STRING_SPLIT('', ',')"));
        HasCount(1, rows);
        AreEqual(string.Empty, rows[0].Value);
    }

    [TestMethod]
    public void NullInputString_ProducesZeroRows()
    {
        using var conn = new Simulation().CreateOpenConnection();
        var rows = ReadAll(conn.CreateCommand("select value from STRING_SPLIT(cast(NULL as varchar(20)), ',')"));
        IsEmpty(rows);
    }

    [TestMethod]
    public void NoSeparatorInInput_OneRowPassthrough()
    {
        using var conn = new Simulation().CreateOpenConnection();
        var rows = ReadAll(conn.CreateCommand("select value from STRING_SPLIT('abc', ',')"));
        HasCount(1, rows);
        AreEqual("abc", rows[0].Value);
    }

    /// <summary>
    /// Probe-confirmed: NULL separator surfaces Msg 214 even when the
    /// input is also NULL — separator validation runs first.
    /// </summary>
    [TestMethod]
    public void NullSeparator_RaisesMsg214()
    {
        using var conn = new Simulation().CreateOpenConnection();
        var ex = Throws<DbException>(() => ReadAll(conn.CreateCommand("select value from STRING_SPLIT('a,b', cast(NULL as varchar(1)))")));
        AreEqual("214", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void MultiCharSeparator_RaisesMsg214()
    {
        using var conn = new Simulation().CreateOpenConnection();
        var ex = Throws<DbException>(() => ReadAll(conn.CreateCommand("select value from STRING_SPLIT('a,,b,,c', ',,')")));
        AreEqual("214", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void EmptySeparator_RaisesMsg214()
    {
        using var conn = new Simulation().CreateOpenConnection();
        var ex = Throws<DbException>(() => ReadAll(conn.CreateCommand("select value from STRING_SPLIT('a,b', '')")));
        AreEqual("214", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void EnableOrdinal_OneArg_AddsOrdinalColumn()
    {
        using var conn = new Simulation().CreateOpenConnection();
        var rows = ReadAll(conn.CreateCommand("select value, ordinal from STRING_SPLIT('a,b,c', ',', 1)"));
        HasCount(3, rows);
        AreEqual(1L, rows[0].Ordinal);
        AreEqual(2L, rows[1].Ordinal);
        AreEqual(3L, rows[2].Ordinal);
    }

    [TestMethod]
    public void EnableOrdinal_Zero_OmitsOrdinalColumn()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand("select * from STRING_SPLIT('a,b', ',', 0)").ExecuteReader();
        AreEqual(1, reader.FieldCount);
    }

    [TestMethod]
    public void EnableOrdinal_Null_TreatedAsZero()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand("select * from STRING_SPLIT('a,b', ',', cast(NULL as int))").ExecuteReader();
        AreEqual(1, reader.FieldCount);
    }

    [TestMethod]
    public void EnableOrdinal_TwoOrHigher_RaisesMsg4199()
    {
        var ex = Throws<DbException>(() => new Simulation().ExecuteScalar("select value from STRING_SPLIT('a,b', ',', 2)"));
        AreEqual("4199", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void NVarcharInput_ProducesNvarcharValue()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var reader = conn.CreateCommand("select * from STRING_SPLIT(N'aαbαc', N'α')").ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        HasCount(3, rows);
        AreEqual("a", rows[0]);
        AreEqual("b", rows[1]);
        AreEqual("c", rows[2]);
    }

    [TestMethod]
    public void EmptyInputWithEnableOrdinal_ProducesSingleOrdinalOneRow()
    {
        using var conn = new Simulation().CreateOpenConnection();
        var rows = ReadAll(conn.CreateCommand("select value, ordinal from STRING_SPLIT('', ',', 1)"));
        HasCount(1, rows);
        AreEqual(string.Empty, rows[0].Value);
        AreEqual(1L, rows[0].Ordinal);
    }

    [TestMethod]
    public void CrossApplyWithStringSplit_FlattensPerRowSplits()
    {
        using var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("""
            create table tagged (id int, tags varchar(100));
            insert tagged values (1, 'a,b,c'), (2, 'd,e')
            """).ExecuteNonQuery();
        var rows = ReadAll(conn.CreateCommand("""
            select v.value, v.ordinal
            from tagged t
            cross apply STRING_SPLIT(t.tags, ',', 1) as v
            """));
        HasCount(5, rows);
        AreEqual(("a", 1), rows[0]);
        AreEqual(("b", 2), rows[1]);
        AreEqual(("c", 3), rows[2]);
        AreEqual(("d", 1), rows[3]);
        AreEqual(("e", 2), rows[4]);
    }

    [TestMethod]
    public void NonIntThirdArg_RaisesMsg8116()
    {
        var ex = Throws<DbException>(() => new Simulation().ExecuteScalar("select value from STRING_SPLIT('a,b', ',', '1')"));
        AreEqual("8116", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void EnableOrdinal_NonConstantVariable_RaisesMsg8748()
        => new Simulation().AssertSqlError(
            "declare @e int = 1; select * from STRING_SPLIT('a,b', ',', @e)",
            8748,
            "The enable_ordinal argument for string_split only supports constant values (not variables or columns).");
}
