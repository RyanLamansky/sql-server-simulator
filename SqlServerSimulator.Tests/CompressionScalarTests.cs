using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the <c>COMPRESS</c> / <c>DECOMPRESS</c> gzip scalar
/// functions. Real SQL Server's pair: COMPRESS wraps any string/binary in a
/// gzip stream as varbinary(MAX); DECOMPRESS inflates back to bytes. Tests
/// assert at the byte level — the <c>CAST(varbinary AS nvarchar)</c>
/// shape WWI's <c>Website.VehicleTemperatures</c> view uses isn't yet
/// modeled (the bacpac loader captures the view's body as text and only
/// fails on query, not on CREATE).
/// </summary>
[TestClass]
public sealed class CompressionScalarTests
{
    /// <summary>Round-trip a UTF-16 string through COMPRESS / DECOMPRESS: the inflated bytes equal the original encoding.</summary>
    [TestMethod]
    public void CompressDecompress_RoundTripsBytes()
    {
        var simulation = new Simulation();
        var result = simulation.ExecuteScalar("select decompress(compress(N'Hello, World!'))");
        var bytes = IsInstanceOfType<byte[]>(result);
        AreEqual("Hello, World!", System.Text.Encoding.Unicode.GetString(bytes));
    }

    /// <summary>Empty string round-trips correctly through a valid gzip stream.</summary>
    [TestMethod]
    public void CompressDecompress_EmptyString_RoundTrips()
    {
        var result = new Simulation().ExecuteScalar("select decompress(compress(N''))");
        var bytes = IsInstanceOfType<byte[]>(result);
        IsEmpty(bytes);
    }

    /// <summary>COMPRESS(NULL) yields NULL.</summary>
    [TestMethod]
    public void Compress_NullInput_ReturnsNull()
        => _ = IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar("select compress(cast(null as nvarchar(10)))"));

    /// <summary>DECOMPRESS(NULL) yields NULL.</summary>
    [TestMethod]
    public void Decompress_NullInput_ReturnsNull()
        => _ = IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar("select decompress(cast(null as varbinary(10)))"));

    /// <summary>DECOMPRESS on invalid gzip → NULL (lax disposition; real SQL Server raises Msg 9803).</summary>
    [TestMethod]
    public void Decompress_InvalidGzip_ReturnsNull()
        => _ = IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar("select decompress(cast(0xDEADBEEF as varbinary(100)))"));

    /// <summary>COMPRESS output starts with gzip magic bytes 1f 8b regardless of input size.</summary>
    [TestMethod]
    public void Compress_HasGzipMagic()
    {
        var result = new Simulation().ExecuteScalar("select compress(N'payload')");
        var bytes = IsInstanceOfType<byte[]>(result);
        IsGreaterThanOrEqualTo(2, bytes.Length, "gzip stream is at least 2 bytes");
        AreEqual((byte)0x1f, bytes[0], "gzip magic byte 1");
        AreEqual((byte)0x8b, bytes[1], "gzip magic byte 2");
    }

    /// <summary>COMPRESS of a CP1252 varchar then DECOMPRESS gives back the original bytes (round-trip).</summary>
    [TestMethod]
    public void CompressDecompress_VarcharRoundTripBytes()
    {
        var simulation = new Simulation();
        var result = simulation.ExecuteScalar("select decompress(compress(cast('ABC123' as varchar(100))))");
        var bytes = IsInstanceOfType<byte[]>(result);
        AreEqual("ABC123", System.Text.Encoding.GetEncoding(1252).GetString(bytes));
    }

    /// <summary>Round-trip from a stored varbinary column — the WWI pre-cast pipeline.</summary>
    [TestMethod]
    public void Decompress_FromStoredColumn_RoundTripsBytes()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table sensors (id int primary key, payload varbinary(max));
            insert into sensors values (1, compress(N'sensor-42-reading-normal'));
            """);
        var result = simulation.ExecuteScalar("select decompress(payload) from sensors where id = 1");
        var bytes = IsInstanceOfType<byte[]>(result);
        AreEqual("sensor-42-reading-normal", System.Text.Encoding.Unicode.GetString(bytes));
    }
}
