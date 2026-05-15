using SqlServerSimulator.Storage.Bacpac;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Decoder fidelity tests for <see cref="HierarchyIdWireDecoder"/>. Ground
/// truth via a synthetic + AdventureWorks2025-derived probe sweep against
/// SQL Server 2025 on 2026-05-15 (Kardax7 reference).
/// </summary>
[TestClass]
public sealed class HierarchyIdWireDecoderTests
{
    [TestMethod]
    // Range [0..3] — `01` prefix, 2 value bits, 5-bit encoding.
    [DataRow("48", "/0/")]
    [DataRow("58", "/1/")]
    [DataRow("68", "/2/")]
    [DataRow("78", "/3/")]
    // Range [4..7] — `100` prefix, 2 value bits, 6-bit encoding.
    [DataRow("84", "/4/")]
    [DataRow("8C", "/5/")]
    [DataRow("94", "/6/")]
    [DataRow("9C", "/7/")]
    // Range [8..15] — `101` prefix, 3 value bits, 7-bit encoding.
    [DataRow("A2", "/8/")]
    [DataRow("A6", "/9/")]
    [DataRow("AA", "/10/")]
    [DataRow("AE", "/11/")]
    [DataRow("B2", "/12/")]
    [DataRow("B6", "/13/")]
    [DataRow("BA", "/14/")]
    [DataRow("BE", "/15/")]
    // Range [16..79] — `110` prefix, 6 value bits with structural-bit
    // insertion, 12-bit encoding.
    [DataRow("C110", "/16/")]
    [DataRow("C130", "/17/")]
    [DataRow("C170", "/19/")]
    [DataRow("C1F0", "/23/")]
    [DataRow("C310", "/24/")]
    [DataRow("C9B0", "/37/")]
    [DataRow("CBF0", "/47/")]
    [DataRow("D110", "/48/")]
    [DataRow("D9F0", "/71/")]
    [DataRow("DBF0", "/79/")]
    // Multi-level paths — encodings concatenate, then pad to byte boundary.
    [DataRow("5AC0", "/1/1/")]
    [DataRow("5B40", "/1/2/")]
    [DataRow("6AC0", "/2/1/")]
    [DataRow("5AD6", "/1/1/1/")]
    [DataRow("5ADA", "/1/1/2/")]
    [DataRow("5B5E", "/1/2/3/")]
    [DataRow("5B5F08", "/1/2/3/4/")]
    [DataRow("5B5F0C60", "/1/2/3/4/5/")]
    // AW-derived ground truth (HR.Employee subset).
    [DataRow("5AE1", "/1/1/4/")]
    [DataRow("5AE158", "/1/1/4/1/")]
    [DataRow("957540", "/6/1/10/")]
    [DataRow("9574C0", "/6/1/9/")]
    [DataRow("957440", "/6/1/8/")]
    [DataRow("7AD6", "/3/1/1/")]
    [DataRow("7AD6B0", "/3/1/1/1/")]
    [DataRow("95EF", "/6/3/3/")]
    public void Decode_KnownEncoding_RoundTripsToCanonicalString(string hex, string expectedPath)
    {
        var bytes = HexToBytes(hex);
        var path = HierarchyIdWireDecoder.Decode(bytes);
        var actual = HierarchyIdSqlType.PathToString(path);
        AreEqual(expectedPath, actual);
    }

    [TestMethod]
    public void Decode_EmptyBytes_ReturnsRoot()
    {
        var path = HierarchyIdWireDecoder.Decode([]);
        AreEqual("/", HierarchyIdSqlType.PathToString(path));
    }

    [TestMethod]
    public void Decode_LargeOrdinal_RaisesNotSupported()
    {
        // /80/ uses the `1110` prefix (range [80..207]) which the decoder
        // doesn't yet handle. Surfaces via NotSupportedException so the BCP
        // loader's per-file try/catch can route it to Skipped.
        var bytes = HexToBytes("E00440");
        _ = ThrowsExactly<NotSupportedException>(() => HierarchyIdWireDecoder.Decode(bytes));
    }

    [TestMethod]
    public void Decode_NegativeOrdinal_RaisesNotSupported()
    {
        // /-1/ uses the `00` prefix range — also deferred.
        var bytes = HexToBytes("3F80");
        _ = ThrowsExactly<NotSupportedException>(() => HierarchyIdWireDecoder.Decode(bytes));
    }

    private static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}
