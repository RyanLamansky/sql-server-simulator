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
        var ex = Throws<DbException>(() =>
            _ = new Simulation().ExecuteNonQuery("create table t (rv1 rowversion, rv2 rowversion)"));
        AreEqual("2738", ex.Data["HelpLink.EvtID"]);
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
        var ex = Throws<DbException>(() => _ = new Simulation().ExecuteNonQuery("""
            create table t (id int, rv rowversion);
            insert t (id, rv) values (1, 0x00000000000000FF)
            """));
        AreEqual("273", ex.Data["HelpLink.EvtID"]);
    }

    // Insert without column list: the simulator must skip rowversion when
    // synthesizing the destination list (rowversion is auto-only).
    [TestMethod]
    public void Insert_NoColumnList_AutoGeneratesAndDoesntFail()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int, rv rowversion);
            insert t values (1)
            """);

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
        var ex = Throws<DbException>(() => _ = new Simulation().ExecuteNonQuery("""
            create table t (id int, rv rowversion);
            insert t (id) values (1);
            update t set rv = 0x00 where id = 1
            """));
        AreEqual("272", ex.Data["HelpLink.EvtID"]);
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
}
