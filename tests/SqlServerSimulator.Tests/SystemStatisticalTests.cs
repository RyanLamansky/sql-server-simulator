using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// System statistical DBA-introspection surface: the <c>@@</c> counters
/// (<c>@@CONNECTIONS</c> / <c>@@CPU_BUSY</c> / <c>@@IDLE</c> / <c>@@IO_BUSY</c>
/// / <c>@@PACK_RECEIVED</c> / <c>@@PACK_SENT</c> / <c>@@PACKET_ERRORS</c> /
/// <c>@@TIMETICKS</c> / <c>@@TOTAL_ERRORS</c> / <c>@@TOTAL_READ</c> /
/// <c>@@TOTAL_WRITE</c>) and the <c>sys.fn_virtualfilestats</c> system TVF.
/// Types + values probe-confirmed against SQL Server 2025 (2026-07-19): every
/// scalar is <c>int</c>, <c>@@TIMETICKS</c> is the hardware-invariant 31250.
/// The elapsed-activity counters report 0 because the in-process simulator
/// performs no physical IO / CPU-time / TDS-packet accounting; <c>@@CONNECTIONS</c>
/// reflects the live session-allocation count.
/// </summary>
[TestClass]
public sealed class SystemStatisticalTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void AtAt_TimeTicks_Returns31250()
        => AreEqual(31250, new Simulation().ExecuteScalar("select @@timeticks"));

    [TestMethod]
    public void AtAt_CpuBusy_ReturnsZero()
        => AreEqual(0, new Simulation().ExecuteScalar("select @@cpu_busy"));

    [TestMethod]
    public void AtAt_Idle_ReturnsZero()
        => AreEqual(0, new Simulation().ExecuteScalar("select @@idle"));

    [TestMethod]
    public void AtAt_IoBusy_ReturnsZero()
        => AreEqual(0, new Simulation().ExecuteScalar("select @@io_busy"));

    [TestMethod]
    public void AtAt_PackReceived_ReturnsZero()
        => AreEqual(0, new Simulation().ExecuteScalar("select @@pack_received"));

    [TestMethod]
    public void AtAt_PackSent_ReturnsZero()
        => AreEqual(0, new Simulation().ExecuteScalar("select @@pack_sent"));

    [TestMethod]
    public void AtAt_PacketErrors_ReturnsZero()
        => AreEqual(0, new Simulation().ExecuteScalar("select @@packet_errors"));

    [TestMethod]
    public void AtAt_TotalErrors_ReturnsZero()
        => AreEqual(0, new Simulation().ExecuteScalar("select @@total_errors"));

    [TestMethod]
    public void AtAt_TotalRead_ReturnsZero()
        => AreEqual(0, new Simulation().ExecuteScalar("select @@total_read"));

    [TestMethod]
    public void AtAt_TotalWrite_ReturnsZero()
        => AreEqual(0, new Simulation().ExecuteScalar("select @@total_write"));

    [TestMethod]
    public void AtAt_Statistical_BaseTypeIsInt()
        => AreEqual("int", new Simulation().ExecuteScalar(
            "select sql_variant_property(cast(@@timeticks as sql_variant), 'BaseType')"));

    [TestMethod]
    public void AtAt_CpuBusy_CaseInsensitive()
        => AreEqual(0, new Simulation().ExecuteScalar("select @@CPU_BUSY"));

    [TestMethod]
    public void AtAt_Connections_FirstConnectionReportsOne()
        => AreEqual(1, new Simulation().ExecuteScalar("select @@connections"));

    [TestMethod]
    public void AtAt_Connections_AdvancesPerSession()
    {
        var sim = new Simulation();
        AreEqual(1, sim.ExecuteScalar("select @@connections"));
        AreEqual(2, sim.ExecuteScalar("select @@connections"));
    }

    [TestMethod]
    public void AtAt_Connections_BaseTypeIsInt()
        => AreEqual("int", new Simulation().ExecuteScalar(
            "select sql_variant_property(cast(@@connections as sql_variant), 'BaseType')"));

    [TestMethod]
    public void VirtualFileStats_ColumnShape()
    {
        using var reader = new Simulation().ExecuteReader("select * from sys.fn_virtualfilestats(1, 1)");
        AreEqual(12, reader.FieldCount);
        AreEqual("DbId", reader.GetName(0));
        AreEqual("FileId", reader.GetName(1));
        AreEqual("TimeStamp", reader.GetName(2));
        AreEqual("NumberReads", reader.GetName(3));
        AreEqual("BytesRead", reader.GetName(4));
        AreEqual("IoStallReadMS", reader.GetName(5));
        AreEqual("NumberWrites", reader.GetName(6));
        AreEqual("BytesWritten", reader.GetName(7));
        AreEqual("IoStallWriteMS", reader.GetName(8));
        AreEqual("IoStallMS", reader.GetName(9));
        AreEqual("BytesOnDisk", reader.GetName(10));
        AreEqual("FileHandle", reader.GetName(11));
        AreEqual(typeof(short), reader.GetFieldType(0));
        AreEqual(typeof(short), reader.GetFieldType(1));
        AreEqual(typeof(long), reader.GetFieldType(2));
        AreEqual(typeof(long), reader.GetFieldType(10));
        AreEqual(typeof(byte[]), reader.GetFieldType(11));
    }

    [TestMethod]
    public void VirtualFileStats_SpecificDbFile_ReturnsOneRow()
    {
        using var reader = new Simulation().ExecuteReader("select * from sys.fn_virtualfilestats(1, 1)");
        IsTrue(reader.Read());
        AreEqual((short)1, reader.GetValue(0));
        AreEqual((short)1, reader.GetValue(1));
        AreEqual(0L, reader.GetValue(2));
        IsTrue(reader.GetValue(11) is byte[] { Length: 8 });
        IsFalse(reader.Read());
    }

    /// <summary>
    /// master / tempdb / model / msdb + the lazily-seeded "simulated".
    /// </summary>
    [TestMethod]
    public void VirtualFileStats_NullDb_WildcardsAllDatabases()
        => AreEqual(5, new Simulation().ExecuteScalar(
            "select count(*) from sys.fn_virtualfilestats(null, null)"));

    [TestMethod]
    public void VirtualFileStats_NullFile_ReturnsModeledFileOnly()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "select count(*) from sys.fn_virtualfilestats(2, null)"));

    [TestMethod]
    public void VirtualFileStats_UnknownDatabase_ReturnsNoRows()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "select count(*) from sys.fn_virtualfilestats(999, 1)"));

    [TestMethod]
    public void VirtualFileStats_NegativeArguments_ReturnNoRows()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "select count(*) from sys.fn_virtualfilestats(-1, -1)"));

    [TestMethod]
    public void VirtualFileStats_NonExistentFile_ReturnsNoRows()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "select count(*) from sys.fn_virtualfilestats(1, 2)"));

    [TestMethod]
    public void VirtualFileStats_BareForm_Resolves()
        => AreEqual(5, new Simulation().ExecuteScalar(
            "select count(*) from fn_virtualfilestats(null, null)"));

    [TestMethod]
    public void VirtualFileStats_TooFewArguments_Msg313()
    {
        var ex = new Simulation().AssertSqlError(
            "select * from sys.fn_virtualfilestats(1)", 313);
        Assert.Contains("insufficient number of arguments", ex.Message);
    }

    [TestMethod]
    public void VirtualFileStats_TooManyArguments_Msg8144()
    {
        var ex = new Simulation().AssertSqlError(
            "select * from sys.fn_virtualfilestats(1, 1, 1)", 8144);
        Assert.Contains("too many arguments", ex.Message);
    }
}
