using System.Buffers.Binary;
using SqlServerSimulator.Storage.Bacpac;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Decoder fidelity tests for <see cref="SpatialWkbDecoder"/>. Covers the
/// simple-point case (the dominant shape in AdventureWorks
/// <c>Person.Address.SpatialLocation</c>) plus the fall-through paths for
/// shapes the decoder doesn't model yet.
/// </summary>
[TestClass]
public sealed class SpatialWkbDecoderTests
{
    [TestMethod]
    public void SimplePoint_Geography_AxisInversion()
    {
        // Geography stores (lat, long) but WKT emits (long, lat). Build a
        // simple-point payload for lat=47.6, long=-122.3 and verify WKT
        // prints long first.
        var wkb = BuildSimplePoint(srid: 4326, firstCoord: 47.6, secondCoord: -122.3);
        var wkt = SpatialWkbDecoder.TryDecodeSimplePoint(wkb, isGeography: true);
        AreEqual("POINT (-122.3 47.6)", wkt);
    }

    [TestMethod]
    public void SimplePoint_Geometry_NoAxisInversion()
    {
        // Geometry stores and prints (x, y) in the same order.
        var wkb = BuildSimplePoint(srid: 0, firstCoord: 1.5, secondCoord: 2.5);
        var wkt = SpatialWkbDecoder.TryDecodeSimplePoint(wkb, isGeography: false);
        AreEqual("POINT (1.5 2.5)", wkt);
    }

    [TestMethod]
    public void SimplePoint_RoundTripsBitForBit()
    {
        // The "R" round-trip specifier preserves the full precision of an
        // IEEE 754 double through ToString. A re-parsed WKT must produce
        // bit-identical coordinates.
        var original = -122.13469409942627; // sample geography long from AW
        var wkb = BuildSimplePoint(srid: 4326, firstCoord: 47.642438, secondCoord: original);
        var wkt = SpatialWkbDecoder.TryDecodeSimplePoint(wkb, isGeography: true);
        IsNotNull(wkt);
        // Parse the first coordinate back from WKT and verify it round-trips.
        var openParen = wkt.IndexOf('(');
        var space = wkt.IndexOf(' ', openParen);
        var firstStr = wkt[(openParen + 1)..space];
        AreEqual(original, double.Parse(firstStr, System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void Truncated_Payload_ReturnsNull()
    {
        // Anything other than exactly 22 bytes can't be a simple-point —
        // decoder returns null so the row loader falls back to SqlValue.Null.
        var truncated = new byte[10];
        IsNull(SpatialWkbDecoder.TryDecodeSimplePoint(truncated, isGeography: true));
    }

    [TestMethod]
    public void NonSimplePoint_Properties_ReturnsNull()
    {
        // Properties byte without IsSinglePoint (0x08) bit means LineString /
        // Polygon / etc. — decoder returns null for the fall-back.
        var wkb = new byte[22];
        BinaryPrimitives.WriteInt32LittleEndian(wkb.AsSpan(0, 4), 4326);
        wkb[4] = 0x01; // version
        wkb[5] = 0x00; // properties — no IsSinglePoint
        IsNull(SpatialWkbDecoder.TryDecodeSimplePoint(wkb, isGeography: true));
    }

    [TestMethod]
    public void ZorM_Bits_ReturnsNull()
    {
        // Z or M coordinates would extend the payload past 22 bytes, but
        // even if the IsSinglePoint bit is set the decoder bails because
        // the simple-point shortcut doesn't support 3D / measured points.
        var wkb = BuildSimplePoint(srid: 4326, firstCoord: 0, secondCoord: 0);
        wkb[5] = 0x08 | 0x01; // IsSinglePoint + HasZ
        IsNull(SpatialWkbDecoder.TryDecodeSimplePoint(wkb, isGeography: true));
    }

    [TestMethod]
    public void UnknownVersion_ReturnsNull()
    {
        var wkb = BuildSimplePoint(srid: 4326, firstCoord: 0, secondCoord: 0);
        wkb[4] = 0xFF; // unknown version
        IsNull(SpatialWkbDecoder.TryDecodeSimplePoint(wkb, isGeography: true));
    }

    /// <summary>
    /// Builds a Microsoft-spatial-binary simple-point payload: 4-byte SRID,
    /// 1-byte version, 1-byte properties (IsSinglePoint), 8-byte first coord,
    /// 8-byte second coord. Geography callers pass (lat, long); geometry
    /// callers pass (x, y).
    /// </summary>
    private static byte[] BuildSimplePoint(int srid, double firstCoord, double secondCoord)
    {
        var buf = new byte[22];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), srid);
        buf[4] = 0x01; // version
        buf[5] = 0x08; // IsSinglePoint
        BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(6, 8), firstCoord);
        BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(14, 8), secondCoord);
        return buf;
    }
}
