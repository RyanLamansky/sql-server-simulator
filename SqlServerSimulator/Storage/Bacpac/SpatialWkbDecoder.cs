using System.Buffers.Binary;
using System.Globalization;

namespace SqlServerSimulator.Storage.Bacpac;

/// <summary>
/// Decodes SQL Server's <c>geography</c> / <c>geometry</c> binary wire form
/// into the WKT (Well-Known Text) string the simulator stores internally.
/// Covers the simple-point case (the dominant shape in AdventureWorks
/// <c>Person.Address.SpatialLocation</c>); more complex shapes (LineString,
/// Polygon, MultiPolygon, GeometryCollection, FullGlobe) return
/// <see langword="null"/> so the row loader falls back to <c>SqlValue.Null</c>
/// rather than failing the whole BCP file.
/// </summary>
/// <remarks>
/// <para>Microsoft's spatial UDT serialization (a.k.a. the "MS spatial binary"
/// format) prefixes each value with a 4-byte SRID + 1-byte version + 1-byte
/// properties bitfield. The simple-point shortcut sets the
/// <c>IsSinglePoint</c> property bit; the payload is then exactly two IEEE 754
/// doubles (16 bytes), so the total wire length is 22 bytes.</para>
///
/// <para><b>Axis order divergence</b>: geography stores <c>(lat, long)</c> in
/// binary but WKT prints <c>POINT (long lat)</c> (longitude first, per OGC).
/// Geometry uses <c>(x, y)</c> throughout. The decoder honors that
/// inversion.</para>
///
/// <para>WKT formatting uses <see cref="CultureInfo.InvariantCulture"/> and
/// the <c>"R"</c> round-trip specifier so coordinate values reproduce
/// bit-for-bit on re-parse. NaN coordinates (legal in geometry; less common
/// in geography) emit as the OGC <c>NaN</c> sentinel.</para>
/// </remarks>
internal static class SpatialWkbDecoder
{
    /// <summary>
    /// Property bit indicating the value is a single point — payload is
    /// exactly two doubles (Lat/Y followed by Long/X).
    /// </summary>
    private const byte IsSinglePoint = 0x08;

    /// <summary>
    /// Property bit indicating Z coordinate is present. The simple-point
    /// shortcut path doesn't carry Z; if either Z or M is set the buffer
    /// length wouldn't match the 22-byte simple-point shape anyway. Kept
    /// here as documentation of the bitfield layout.
    /// </summary>
    private const byte HasZ = 0x01;
    private const byte HasM = 0x02;

    /// <summary>
    /// Decodes a simple-point payload to its WKT string, or returns
    /// <see langword="null"/> when the payload is anything else (more complex
    /// shape, future spatial version, malformed). Callers fall back to
    /// <c>SqlValue.Null</c> on null to keep the row loader resilient.
    /// </summary>
    public static string? TryDecodeSimplePoint(ReadOnlySpan<byte> wkb, bool isGeography)
    {
        // Simple-point wire length is fixed: 4 SRID + 1 version + 1 props + 16 coords.
        if (wkb.Length != 22)
            return null;
        var version = wkb[4];
        if (version is not (0x01 or 0x02))
            return null;
        var properties = wkb[5];
        if ((properties & IsSinglePoint) == 0)
            return null;
        if ((properties & (HasZ | HasM)) != 0)
            return null;

        var first = BinaryPrimitives.ReadDoubleLittleEndian(wkb[6..14]);
        var second = BinaryPrimitives.ReadDoubleLittleEndian(wkb[14..22]);

        // Geography: stored (lat, long); WKT prints (long, lat).
        // Geometry:  stored (x, y);     WKT prints (x, y).
        var (wktFirst, wktSecond) = isGeography ? (second, first) : (first, second);
        return $"POINT ({Format(wktFirst)} {Format(wktSecond)})";
    }

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
