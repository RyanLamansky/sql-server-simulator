using SqlServerSimulator.Storage.Bacpac;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Byte-parity + round-trip tests for <see cref="HierarchyIdWireEncoder"/>, the
/// exact inverse of <see cref="HierarchyIdWireDecoder"/>. Expected byte strings
/// are hard-coded from a live SQL Server 2025 reference probe (2026-07-16,
/// <c>SELECT CAST(CAST('/N/' AS hierarchyid) AS varbinary(892))</c>), extended
/// with the AdventureWorks-derived multi-segment ground truth the decoder tests
/// captured (2026-05-15). Encoder output must equal those bytes.
/// </summary>
[TestClass]
public sealed class HierarchyIdWireEncoderTests
{
    [TestMethod]
    // Root path — zero segments encode to zero bytes.
    [DataRow("/", "")]
    // Tier 0..3 — `01 VV 1`.
    [DataRow("/0/", "48")]
    [DataRow("/1/", "58")]
    [DataRow("/2/", "68")]
    [DataRow("/3/", "78")]
    // Tier 4..7 — `100 VV 1`.
    [DataRow("/4/", "84")]
    [DataRow("/7/", "9C")]
    // Tier 8..15 — `101 VVV 1`.
    [DataRow("/8/", "A2")]
    [DataRow("/15/", "BE")]
    // Tier 16..79 — `110 VV 0 V 1 VVV 1`.
    [DataRow("/16/", "C110")]
    [DataRow("/79/", "DBF0")]
    // Multi-segment (single-label) — encodings concatenate then zero-pad.
    [DataRow("/0/0/", "4A40")]
    [DataRow("/1/2/", "5B40")]
    [DataRow("/1/2/3/", "5B5E")]
    [DataRow("/1/2/3/4/", "5B5F08")]
    [DataRow("/3/4/7/8/15/16/79/", "7C33D1BF823B7E")]
    // AW-derived ground truth (HR.Employee subset, 2026-05-15 probe).
    [DataRow("/1/1/4/", "5AE1")]
    [DataRow("/6/1/10/", "957540")]
    [DataRow("/6/3/3/", "95EF")]
    // Dotted sub-ordinals — non-final label encodes ordinal+1 with terminator 0.
    [DataRow("/0.1/", "52C0")]
    [DataRow("/0.2/", "5340")]
    [DataRow("/1.1/", "62C0")]
    [DataRow("/1.2/", "6340")]
    [DataRow("/2.10/", "7550")]
    [DataRow("/3.1/", "8160")]
    [DataRow("/1/2.3/", "5B9E")]
    [DataRow("/1/2.3/4/", "5B9F08")]
    public void Encode_MatchesLiveServerBytes(string path, string expectedHex) =>
        AreEqual(expectedHex, Convert.ToHexString(HierarchyIdWireEncoder.Encode(HierarchyIdSqlType.ParsePath(path))));

    [TestMethod]
    // Single-label-segment paths are the decoder's full domain, so these
    // survive encode → decode unchanged. (Dotted forms are byte-identical on
    // the wire but the decoder cannot read them back — a documented asymmetry.)
    [DataRow("/")]
    [DataRow("/0/")]
    [DataRow("/79/")]
    [DataRow("/1/2/3/4/5/")]
    [DataRow("/3/4/7/8/15/16/79/")]
    [DataRow("/6/1/10/")]
    public void Encode_ThenDecode_RoundTripsSingleLabelPaths(string path)
    {
        var original = HierarchyIdSqlType.ParsePath(path);
        var bytes = HierarchyIdWireEncoder.Encode(original);
        var decoded = HierarchyIdWireDecoder.Decode(bytes);
        AreEqual(path, HierarchyIdSqlType.PathToString(decoded));
    }

    [TestMethod]
    public void Encode_OrdinalAtOrAbove80_RaisesNotSupported() =>
        _ = ThrowsExactly<NotSupportedException>(() => HierarchyIdWireEncoder.Encode(HierarchyIdSqlType.ParsePath("/80/")));

    [TestMethod]
    public void Encode_NegativeOrdinal_RaisesNotSupported() =>
        _ = ThrowsExactly<NotSupportedException>(() => HierarchyIdWireEncoder.Encode(HierarchyIdSqlType.ParsePath("/-1/")));

    [TestMethod]
    // A non-final dotted label of ordinal 79 shifts to 80 and overflows the
    // modeled tier set, matching the ordinal-range ceiling.
    public void Encode_NonFinalDottedOrdinalOverflow_RaisesNotSupported() =>
        _ = ThrowsExactly<NotSupportedException>(() => HierarchyIdWireEncoder.Encode(HierarchyIdSqlType.ParsePath("/79.1/")));
}
