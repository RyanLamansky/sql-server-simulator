using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Byte-parity + round-trip tests for <see cref="HierarchyIdOrdPath"/>, the
/// canonical SQL Server <c>hierarchyid</c> OrdPath codec. Expected byte strings
/// are hard-coded from a live SQL Server 2025 reference probe (2026-07-17,
/// <c>SELECT CAST(CAST('/N/' AS hierarchyid) AS varbinary(892))</c>); the
/// reverse-CAST rejection cases mirror the server's strict canonicality check.
/// </summary>
[TestClass]
public sealed class HierarchyIdOrdPathTests
{
    [TestMethod]
    // Root — zero segments encode to zero bytes.
    [DataRow("/", "")]
    // Positive tier 01 (0..3), 100 (4..7), 101 (8..15).
    [DataRow("/0/", "48")]
    [DataRow("/1/", "58")]
    [DataRow("/3/", "78")]
    [DataRow("/4/", "84")]
    [DataRow("/7/", "9C")]
    [DataRow("/8/", "A2")]
    [DataRow("/15/", "BE")]
    // Tier 110 (16..79).
    [DataRow("/16/", "C110")]
    [DataRow("/79/", "DBF0")]
    // Tier 1110 (80..1103) — boundary-anchored.
    [DataRow("/80/", "E00440")]
    [DataRow("/81/", "E004C0")]
    [DataRow("/100/", "E02640")]
    [DataRow("/200/", "E0EC40")]
    [DataRow("/1103/", "EEEFC0")]
    // Tier 11110 (1104..5199) — boundary-anchored.
    [DataRow("/1104/", "F00088")]
    [DataRow("/2000/", "F1C088")]
    [DataRow("/5199/", "F7DDF8")]
    // Negative tier 0011 (-8..-1), 0010 (-72..-9).
    [DataRow("/-1/", "3F80")]
    [DataRow("/-8/", "3880")]
    [DataRow("/-9/", "2DF8")]
    [DataRow("/-72/", "2088")]
    // Negative deep tiers 00011011 (-1096..-73) and 00011010 (-2120..-1097).
    [DataRow("/-73/", "1BEEFC")]
    [DataRow("/-200/", "1BE044")]
    [DataRow("/-1096/", "1B0044")]
    [DataRow("/-1097/", "1AEEFC")]
    [DataRow("/-1200/", "1AE2C4")]
    // Multi-segment single-label — encodings concatenate then zero-pad.
    [DataRow("/1/2/", "5B40")]
    [DataRow("/1/2/3/4/", "5B5F08")]
    [DataRow("/3/4/7/8/15/16/79/", "7C33D1BF823B7E")]
    [DataRow("/6/1/10/", "957540")]
    [DataRow("/1/-1/", "59FC")]
    [DataRow("/-1/-1/", "3F9FC0")]
    // Dotted sub-ordinals — non-final label encodes ordinal+1 with terminator 0.
    [DataRow("/0.1/", "52C0")]
    [DataRow("/1.5/", "6460")]
    [DataRow("/2.5/", "7460")]
    [DataRow("/1/2.5/3/", "5BA378")]
    [DataRow("/1.79/", "66DF80")]
    [DataRow("/10.10/", "AD54")]
    [DataRow("/1.2.3/", "639E")]
    [DataRow("/-1.5/", "4460")]
    public void Encode_MatchesLiveServerBytes(string path, string expectedHex) =>
        AreEqual(expectedHex, Convert.ToHexString(HierarchyIdOrdPath.Encode(HierarchyIdSqlType.ParsePath(path))));

    [TestMethod]
    // Every modeled shape survives encode → decode, including the dotted and
    // negative forms the previous decoder could not read back.
    [DataRow("/")]
    [DataRow("/0/")]
    [DataRow("/79/")]
    [DataRow("/100/")]
    [DataRow("/5199/")]
    [DataRow("/-1/")]
    [DataRow("/-72/")]
    [DataRow("/-200/")]
    [DataRow("/-1200/")]
    [DataRow("/1/2/3/4/5/")]
    [DataRow("/3/4/7/8/15/16/79/")]
    [DataRow("/1.5/")]
    [DataRow("/1/2.5/3/")]
    [DataRow("/1.2.3/")]
    [DataRow("/-1.5/")]
    [DataRow("/6/1/10/")]
    public void Encode_ThenDecode_RoundTrips(string path)
    {
        var bytes = HierarchyIdOrdPath.Encode(HierarchyIdSqlType.ParsePath(path));
        AreEqual(path, HierarchyIdSqlType.PathToString(HierarchyIdOrdPath.Decode(bytes)));
    }

    [TestMethod]
    // The four wide tiers, which carry the domain past int in both directions.
    [DataRow("/5200/")]
    [DataRow("/100000/")]
    [DataRow("/4294972495/")]
    [DataRow("/4294972496/")]
    [DataRow("/281479271683151/")]
    [DataRow("/-4169/")]
    [DataRow("/-4294971464/")]
    [DataRow("/-4294971465/")]
    [DataRow("/-281479271682120/")]
    [DataRow("/281479271683150.1/")]
    [DataRow("/1.281479271683151/")]
    public void Encode_ThenDecode_RoundTripsTheWideTiers(string path)
    {
        var bytes = HierarchyIdOrdPath.Encode(HierarchyIdSqlType.ParsePath(path));
        AreEqual(path, HierarchyIdSqlType.PathToString(HierarchyIdOrdPath.Decode(bytes)));
    }

    [TestMethod]
    // Outside the domain, the parse is what refuses (Msg 6522) — including a
    // non-final dotted label at the very top, which encodes as ordinal + 1 and
    // so has nowhere to go.
    [DataRow("/281479271683152/")]
    [DataRow("/-281479271682121/")]
    [DataRow("/281479271683151.1/")]
    public void ParsePath_OutsideTheDomain_RaisesMsg6522(string path) =>
        AreEqual(6522, ThrowsExactly<SimulatedSqlException>(() => HierarchyIdSqlType.ParsePath(path)).Number);

    [TestMethod]
    // A *computed* ordinal past the top reaches the encoder instead, which is
    // real's other 6522 form (state 2, naming WriteOrd).
    public void Encode_ComputedOrdinalPastTheDomain_RaisesMsg6522State2()
    {
        var ex = ThrowsExactly<SimulatedSqlException>(
            () => HierarchyIdOrdPath.Encode([[HierarchyIdOrdPath.DomainMax + 1]]));
        AreEqual(6522, ex.Number);
        AreEqual(2, ex.State);
    }

    [TestMethod]
    [DataRow("58", "/1/")]
    [DataRow("", "/")]
    [DataRow("5B40", "/1/2/")]
    [DataRow("E00440", "/80/")]
    [DataRow("3F80", "/-1/")]
    [DataRow("6460", "/1.5/")]
    public void DecodeCanonical_AcceptsCanonicalBytes(string hex, string expectedPath) =>
        AreEqual(expectedPath, HierarchyIdSqlType.PathToString(HierarchyIdOrdPath.DecodeCanonical(Convert.FromHexString(hex))));

    [TestMethod]
    // SQL Server rejects any non-canonical byte string on CAST(0x… AS
    // hierarchyid): wrong pad bits (0x59 vs canonical 0x58), all-zero non-empty,
    // garbage prefixes, trailing bytes. Probe-confirmed 2026-07-17.
    [DataRow("59")]
    [DataRow("5A")]
    [DataRow("FF")]
    [DataRow("FFFF")]
    [DataRow("00")]
    [DataRow("01")]
    [DataRow("48FF")]
    [DataRow("5842")]
    public void DecodeCanonical_RejectsNonCanonicalBytes(string hex) =>
        _ = ThrowsExactly<SimulatedSqlException>(() => HierarchyIdOrdPath.DecodeCanonical(Convert.FromHexString(hex)));
}
