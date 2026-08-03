namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// <c>geography</c>'s bounding-cap pair — <c>EnvelopeCenter()</c> and
/// <c>EnvelopeAngle()</c>.
/// </summary>
/// <remarks>
/// <para>Both are computed on the <b>unit sphere</b>, reading each coordinate's
/// latitude as a spherical angle rather than a geodetic one: the centre is the
/// normalized <b>sum of the instance's points as unit vectors</b>, and the
/// angle is the greatest angle from that centre to any of them, in degrees.
/// Probing SQL Server 2025 reproduces both to the last few bits — a 1° square
/// at the equator centres on latitude <c>0.50001903822621641</c> and reports
/// <c>0.70711575561904183</c>, which is what the vector mean gives and what
/// neither the coordinate midpoint nor the minimal enclosing cap does.</para>
/// <para>Two rules ride on top, both probe-derived. A closed figure's repeated
/// last point takes no part in the sum, while an ordinary repeated vertex does
/// (<c>LINESTRING(0 0, 0 0, 10 0)</c> centres a third of the way along).
/// And an instance whose greatest angle reaches 90° reports the angle as
/// <b>180</b>, real's way of saying no cap below a hemisphere holds it.</para>
/// </remarks>
internal static class SpatialEnvelope
{
    /// <summary>
    /// Magnitude below which the summed direction carries no usable bearing and
    /// real falls back to the north pole. Bracketed by probe: two points 1.75e-8
    /// apart in summed magnitude still answer with their own bearing, and
    /// 1.75e-9 apart answer <c>POINT (0 90)</c>.
    /// </summary>
    private const double DegenerateSum = 1e-8;

    /// <summary>The angle at which real stops reporting a cap and answers 180 instead.</summary>
    private const double HemisphereDegrees = 90;

    /// <summary><c>EnvelopeCenter()</c>, or null for an empty instance.</summary>
    public static SpatialCoordinate? Center(SpatialShape shape)
    {
        if (Sum(shape) is not { } sum)
            return null;
        var (x, y, z) = sum;
        var magnitude = Math.Sqrt((x * x) + (y * y) + (z * z));
        return magnitude < DegenerateSum
            ? new SpatialCoordinate(0, 90)
            : new SpatialCoordinate(
                double.RadiansToDegrees(Math.Atan2(y / magnitude, x / magnitude)),
                double.RadiansToDegrees(Math.Asin(Math.Clamp(z / magnitude, -1, 1))));
    }

    /// <summary><c>EnvelopeAngle()</c>, or null for an empty instance.</summary>
    public static double? Angle(SpatialShape shape)
    {
        if (Center(shape) is not { } center)
            return null;
        var (cx, cy, cz) = UnitVector(center);
        var widest = 0.0;
        foreach (var point in Points(shape))
        {
            var (x, y, z) = UnitVector(point);
            var cosine = Math.Clamp((cx * x) + (cy * y) + (cz * z), -1, 1);
            widest = Math.Max(widest, double.RadiansToDegrees(Math.Acos(cosine)));
        }
        return widest >= HemisphereDegrees ? 180 : widest;
    }

    private static (double X, double Y, double Z)? Sum(SpatialShape shape)
    {
        var sumX = 0.0;
        var sumY = 0.0;
        var sumZ = 0.0;
        var any = false;
        foreach (var point in Points(shape))
        {
            var (x, y, z) = UnitVector(point);
            sumX += x;
            sumY += y;
            sumZ += z;
            any = true;
        }
        return any ? (sumX, sumY, sumZ) : null;
    }

    /// <summary>
    /// Every point the sum reads: each figure's vertices, less the repeat that
    /// closes a closed figure. Two coordinates written the same way anywhere
    /// else both count.
    /// </summary>
    private static IEnumerable<SpatialCoordinate> Points(SpatialShape shape)
    {
        foreach (var figure in shape.Figures)
        {
            var count = figure.Length;
            if (count > 1 && figure[0].X == figure[^1].X && figure[0].Y == figure[^1].Y)
                count--;
            for (var i = 0; i < count; i++)
                yield return figure[i];
        }
        foreach (var child in shape.Children)
        {
            foreach (var point in Points(child))
                yield return point;
        }
    }

    /// <summary>The unit vector of a (longitude, latitude) pair read as spherical angles.</summary>
    private static (double X, double Y, double Z) UnitVector(SpatialCoordinate point)
    {
        var longitude = double.DegreesToRadians(point.X);
        var latitude = double.DegreesToRadians(point.Y);
        var horizontal = Math.Cos(latitude);
        return (horizontal * Math.Cos(longitude), horizontal * Math.Sin(longitude), Math.Sin(latitude));
    }
}
