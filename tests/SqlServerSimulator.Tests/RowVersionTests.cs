using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the <c>rowversion</c> / <c>timestamp</c> type:
/// auto-generation on INSERT, auto-bump on UPDATE, rejection of explicit
/// values (Msg 273 / Msg 272), one-per-table (Msg 2738), implicit
/// NOT NULL, comparison with <c>varbinary</c> for optimistic-concurrency
/// WHERE clauses, and <c>CAST</c> outbound to <c>bigint</c> /
/// <c>varbinary</c>. Sourced from probes against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class RowVersionTests
{
    private static byte[] ReadRowVersion(DbCommand command, int ordinal = 0)
    {
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        return (byte[])reader.GetValue(ordinal);
    }

    private static List<byte[]> ReadAllRowVersions(DbCommand command)
    {
        using var reader = command.ExecuteReader();
        var values = new List<byte[]>();
        while (reader.Read())
            values.Add((byte[])reader.GetValue(0));
        return values;
    }

    private static Simulation SeededOneRow(string columnType = "rowversion", string columnName = "rv")
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"""
            create table t (id int, {columnName} {columnType});
            insert t (id) values (1)
            """);
        return simulation;
    }

    // === Type declaration ===

    [TestMethod]
    public void RowVersion_KeywordAccepted()
    {
        var rvs = ReadAllRowVersions(SeededOneRow().CreateCommand("select rv from t"));
        HasCount(1, rvs);
        HasCount(8, rvs[0]);
    }

    [TestMethod]
    public void Timestamp_KeywordAccepted_LegacySynonym()
    {
        var rvs = ReadAllRowVersions(SeededOneRow("timestamp", "ts").CreateCommand("select ts from t"));
        HasCount(1, rvs);
        HasCount(8, rvs[0]);
    }

    [TestMethod]
    public void TwoRowVersionColumns_RaisesMsg2738()
    {
        var ex = Throws<SimulatedSqlException>(() =>
            _ = new Simulation().ExecuteNonQuery("create table t (rv1 rowversion, rv2 rowversion)"));
        AreEqual(2738, ex.Number);
    }

    // === Auto-generation on INSERT ===

    [TestMethod]
    public void Insert_AutoGeneratesRowVersionWhenColumnOmitted()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int, rv rowversion);
            insert t (id) values (1), (2), (3)
            """);

        var rvs = ReadAllRowVersions(simulation.CreateCommand("select rv from t"));
        HasCount(3, rvs);
        // Each is 8 bytes, monotonically increasing.
        var rv0 = BitConverter.ToUInt64(rvs[0].Reverse().ToArray());
        var rv1 = BitConverter.ToUInt64(rvs[1].Reverse().ToArray());
        var rv2 = BitConverter.ToUInt64(rvs[2].Reverse().ToArray());
        IsLessThan(rv1, rv0);
        IsLessThan(rv2, rv1);
    }

    [TestMethod]
    public void Insert_RowVersionListedExplicitly_RaisesMsg273()
    {
        var ex = Throws<SimulatedSqlException>(() => _ = new Simulation().ExecuteNonQuery("""
            create table t (id int, rv rowversion);
            insert t (id, rv) values (1, 0x00000000000000FF)
            """));
        AreEqual(273, ex.Number);
    }

    // Insert without column list: a rowversion column keeps its position in
    // the implicit list, so it has to be filled with the DEFAULT keyword —
    // probe-confirmed that real reports Msg 213 for the value list that simply
    // omits it, and Msg 273 for one supplying a real value.
    [TestMethod]
    public void Insert_NoColumnList_RowVersionOccupiesAPosition()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, rv rowversion)");

        _ = simulation.AssertSqlError("insert t values (1)", 213);
        _ = simulation.AssertSqlError("insert t values (1, 0x0000000000000000)", 273);

        _ = simulation.ExecuteNonQuery("insert t values (1, default)");
        HasCount(1, ReadAllRowVersions(simulation.CreateCommand("select rv from t")));
    }

    // Naming the rowversion column is legal as long as its cell is DEFAULT —
    // the escape hatch Msg 273's own text points at.
    [TestMethod]
    public void Insert_ColumnListNamingRowVersion_AcceptsDefault()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int, rv rowversion)");

        _ = simulation.ExecuteNonQuery("insert t (id, rv) values (1, default)");
        _ = simulation.AssertSqlError("insert t (id, rv) values (2, 0x0000000000000000)", 273);

        HasCount(1, ReadAllRowVersions(simulation.CreateCommand("select rv from t")));
    }

    // === Auto-bump on UPDATE ===

    [TestMethod]
    public void Update_AutoBumpsRowVersionEvenWhenSetIsUnrelated()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int, name varchar(20), rv rowversion);
            insert t (id, name) values (1, 'a')
            """);

        var initialRv = ReadRowVersion(simulation.CreateCommand("select rv from t"));
        _ = simulation.ExecuteNonQuery("update t set name = 'A' where id = 1");
        var afterRv = ReadRowVersion(simulation.CreateCommand("select rv from t"));

        IsFalse(initialRv.SequenceEqual(afterRv), "rowversion must change on UPDATE");
    }

    [TestMethod]
    public void Update_SetRowVersion_RaisesMsg272()
    {
        var ex = Throws<SimulatedSqlException>(() => _ = new Simulation().ExecuteNonQuery("""
            create table t (id int, rv rowversion);
            insert t (id) values (1);
            update t set rv = 0x00 where id = 1
            """));
        AreEqual(272, ex.Number);
    }

    // === CAST outbound ===

    [TestMethod]
    public void Cast_RowVersion_To_Varbinary8()
    {
        using var reader = SeededOneRow()
            .CreateCommand("select cast(rv as varbinary(8)) from t").ExecuteReader();
        IsTrue(reader.Read());
        var bytes = (byte[])reader.GetValue(0);
        HasCount(8, bytes);
    }

    [TestMethod]
    public void Cast_RowVersion_To_BigInt_BigEndian()
    {
        var simulation = SeededOneRow();
        var asBigInt = (long)simulation.ExecuteScalar("select cast(rv as bigint) from t")!;
        // The exact value depends on the simulation's counter, but it must match
        // the big-endian interpretation of the same bytes.
        var rvBytes = ReadRowVersion(simulation.CreateCommand("select rv from t"));
        var expected = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(rvBytes);
        AreEqual(expected, asBigInt);
    }

    // === Comparison with varbinary (optimistic-concurrency WHERE) ===

    [TestMethod]
    public void Where_RowVersion_Equals_VarbinaryParameter()
    {
        var simulation = SeededOneRow();
        var rvBytes = ReadRowVersion(simulation.CreateCommand("select rv from t"));

        using var connection = simulation.CreateOpenConnection();
        using var update = connection.CreateCommand(
            "update t set id = 99 output 1 where id = 1 and rv = @originalRv",
            ("@originalRv", rvBytes));

        using var reader = update.ExecuteReader();
        IsTrue(reader.Read(), "rowversion = varbinary parameter should match the row");
    }

    // Concurrency-violation case: a stale rowversion doesn't match.
    [TestMethod]
    public void Where_RowVersion_Equals_StaleVarbinary_NoMatch()
    {
        var staleRv = new byte[8]; // all zeros, never a real rowversion
        using var connection = SeededOneRow().CreateOpenConnection();
        using var update = connection.CreateCommand(
            "update t set id = 99 output 1 where rv = @stale", ("@stale", staleRv));

        using var reader = update.ExecuteReader();
        IsFalse(reader.Read());
    }
    /// <summary>
    /// A column written as just <c>timestamp</c> — bracketed or not, with or
    /// without a nullability — is a timestamp column named timestamp, in a
    /// table, a table variable and a table type (probed 2026-09-30 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("create table x (a int, timestamp); insert x (a) values (1); select type_name(system_type_id) + ':' + name from sys.columns where object_id = object_id('x') and column_id = 2")]
    [DataRow("create table x (a int, [timestamp] not null, b int); select type_name(system_type_id) + ':' + name from sys.columns where object_id = object_id('x') and column_id = 2")]
    [DataRow("declare @t table (timestamp, a int); insert @t (a) values (1); select 'timestamp:' + cast(datalength(timestamp) as varchar) from @t")]
    public void ATypelessTimestampColumn_IsNamedTimestamp(string sql)
        => StartsWith("timestamp:", (string)new Simulation().ExecuteScalar(sql)!);

    [TestMethod]
    public void ATypelessTimestampColumn_InATableType()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create type tt as table (a int, timestamp)");
        AreEqual("timestamp", simulation.ExecuteScalar("select c.name from sys.table_types t join sys.columns c on c.object_id = t.type_table_object_id where t.name = 'tt' and c.column_id = 2"));
    }

    /// <summary>
    /// A variable takes a timestamp from an integer, an exact number or a
    /// binary value; a string is Msg 257 and the rest Msg 206 (probed
    /// 2026-09-30 against SQL Server 2025, one member of each class).
    /// </summary>
    [TestMethod]
    [DataRow("'abc'", 257)]
    [DataRow("N'abc'", 257)]
    [DataRow("getdate()", 257)]
    [DataRow("cast(1 as float)", 206)]
    [DataRow("newid()", 206)]
    [DataRow("cast(getdate() as date)", 206)]
    [DataRow("cast(1 as sql_variant)", 206)]
    [DataRow("cast('a' as text)", 206)]
    public void AssigningToATimestampVariable_IsRefused(string value, int number)
        => _ = new Simulation().AssertSqlError($"declare @t timestamp = {value}", number);

    [TestMethod]
    public void AssigningToATimestampVariable_TakesIntegersExactNumbersAndBinary()
        => AreEqual(4, new Simulation().ExecuteScalar("declare @a timestamp = 1, @b timestamp = 1.5, @c timestamp = 0x01, @d timestamp = cast(1 as bit); select 4"));
}
