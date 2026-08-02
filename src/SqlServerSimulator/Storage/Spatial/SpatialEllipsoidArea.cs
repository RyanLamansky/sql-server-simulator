namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// Ellipsoidal polygon area — <c>geography</c>'s <c>STArea()</c>, over a region
/// whose edges are the great elliptic arcs real joins consecutive vertices with.
/// </summary>
/// <remarks>
/// <para><b>The model.</b> The surface element in geodetic coordinates depends
/// on latitude alone, so its antiderivative
/// <see cref="SpatialEllipsoid.AreaBelow"/> turns the double integral into a
/// line integral by Green's theorem: the area enclosed by a ring is
/// <c>-∮ AreaBelow(φ) dλ</c> taken along the boundary. Each edge contributes
/// that integral over its own great elliptic track, and the tracks are what
/// make the answer real's rather than a quadrangle's — a "horizontal" edge
/// bulges poleward between its endpoints and the area follows the bulge.</para>
/// <para><b>How the model was identified</b> (2026-08-02): a 0.01° square at
/// the equator measures 1230907.2048772429 on SQL Server 2025 where the exact
/// parallel-bounded quadrangle is 1230907.2018475635, and the 3.03e-3 m² gap is
/// exactly the poleward bulge of a great elliptic top edge. Reproducing the
/// bulge closes it: across a probed matrix of squares, country-sized quads,
/// equator-crossing and southern-hemisphere polygons, holes, multipolygons and
/// slivers the model lands within <b>1e-10 relative</b>, and a 20°-wide
/// mid-latitude band matches to 2.5e-13.</para>
/// <para><b>Where real's own error shows.</b> Real degrades on edges that span
/// a large longitude range, and worse the nearer the pole they run: a 90°-wide
/// band at the equator differs by 2e-8, an equator-bounded hemisphere (whose
/// answer is real's own equator-to-pole zone) by 1.7e-8, an 85°-latitude band
/// by 5e-6, and a four-vertex cap around the pole by 1e-4 — while the same cap
/// written with 360 vertices comes back to 9e-10. Real's answers are not even
/// self-consistent there: its <c>FULLGLOBE</c> constant is within 2.6e-11 of the
/// exact surface area while the hemisphere it computes from a polygon is 1.7e-8
/// short of half of it. The simulator computes the model exactly and lets the
/// difference stand; see <c>docs/claude/spatial.md</c>.</para>
/// </remarks>
internal static class SpatialEllipsoidArea
{
    /// <summary>
    /// Area of one polygon in metres squared: the signed areas of its rings
    /// summed, so an interior ring wound against the exterior one subtracts.
    /// </summary>
    /// <remarks>
    /// Geography rings carry orientation — the interior lies to the <i>left</i>
    /// of the direction a ring is written — so a ring wound the other way names
    /// the complementary region and measures the rest of the globe, which is
    /// what the negative total folds into (probe-confirmed: a 0.01° square
    /// written clockwise measures the surface area less its own).
    /// </remarks>
    public static double Polygon(SpatialCoordinate[][] rings)
    {
        var total = 0.0;
        foreach (var ring in rings)
            total += Ring(ring);
        return total < 0 ? total + SpatialEllipsoid.SurfaceArea : total;
    }

    /// <summary>
    /// Signed area of a single closed ring, positive when the ring is written
    /// with its interior on the left.
    /// </summary>
    /// <remarks>
    /// A ring that encircles a pole never closes in longitude — its edges sweep
    /// a full turn — so the boundary is completed along the pole itself, whose
    /// contribution is the whole polar zone. That is the <c>|winding| &gt; π</c>
    /// branch; a ring that does close reads its integral directly.
    /// </remarks>
    private static double Ring(SpatialCoordinate[] ring)
    {
        var count = ring.Length - 1;
        if (count < 3)
            return 0;
        var first = -1;
        for (var i = 0; i < count && first < 0; i++)
        {
            if (Math.Abs(ring[i].Y) != 90)
                first = i;
        }
        if (first < 0)
            return 0;

        var integral = 0.0;
        var winding = 0.0;
        double? enteredPoleAt = null;
        var poleLatitude = 0.0;
        for (var step = 0; step < count; step++)
        {
            var from = ring[(first + step) % count];
            var to = ring[(first + step + 1) % count];
            if (Math.Abs(to.Y) == 90)
            {
                // The edge into a pole runs along a meridian and sweeps no
                // longitude; the meridian it arrived on is what the traverse
                // across the pole starts from.
                enteredPoleAt = Radians(from.X);
                poleLatitude = Math.Sign(to.Y) * Math.PI / 2;
                continue;
            }
            if (enteredPoleAt is { } entered)
            {
                var across = SpatialEllipsoid.ShortestLongitudeDelta(entered, Radians(to.X));
                integral += SpatialEllipsoid.AreaBelow(poleLatitude) * across;
                winding += across;
                enteredPoleAt = null;
                continue;
            }
            var (edge, delta) = Edge(from, to);
            integral += edge;
            winding += delta;
        }
        return Math.Abs(winding) > Math.PI ? (SpatialEllipsoid.SurfaceArea / 2) - integral : -integral;
    }

    /// <summary>
    /// One edge's <c>∫ AreaBelow(φ) dλ</c> along its great elliptic track, with
    /// the longitude it sweeps.
    /// </summary>
    private static (double Integral, double Delta) Edge(SpatialCoordinate from, SpatialCoordinate to)
    {
        var delta = SpatialEllipsoid.ShortestLongitudeDelta(Radians(from.X), Radians(to.X));
        var arc = GreatEllipticArc.Between(from, to);
        if (arc.Sweep == 0)
            return (0, delta);

        // A track whose plane holds the polar axis follows a meridian: longitude
        // stands still, except at a pole the track runs over, where it flips by
        // π at that single point and the jump is the edge's whole contribution.
        var normal = arc.PlaneNormal;
        if (Math.Abs(normal.Z) <= 1e-13 * normal.Length && Math.Abs(delta) > Math.PI / 2)
        {
            var pole = Math.Sign(arc.At(0.5).Z) * Math.PI / 2;
            return (SpatialEllipsoid.AreaBelow(pole) * delta, delta);
        }

        // Longitude is what the integrand varies with, so the panel count tracks
        // the longitude span — doubled at high latitude, where a track covers
        // its longitude far faster than its arc.
        var panels = GaussLegendre.PanelsFor(delta, Math.PI / 64, 32);
        if (Math.Max(Math.Abs(from.Y), Math.Abs(to.Y)) > 60)
            panels *= 2;
        var total = 0.0;
        var width = arc.Sweep / panels;
        for (var panel = 0; panel < panels; panel++)
        {
            var mid = arc.Start + (panel * width) + (width / 2);
            var half = width / 2;
            var sum = 0.0;
            for (var i = 0; i < GaussLegendre.Nodes.Length; i++)
            {
                sum += GaussLegendre.Weights[i]
                    * (Integrand(arc, mid + (half * GaussLegendre.Nodes[i])) + Integrand(arc, mid - (half * GaussLegendre.Nodes[i])));
            }
            total += sum * half;
        }
        return (total, delta);
    }

    /// <summary>The zone area below the track's latitude, times the rate longitude runs at.</summary>
    private static double Integrand(in GreatEllipticArc arc, double t)
    {
        var point = arc.PointAt(t);
        var tangent = arc.TangentAt(t);
        var axial = (point.X * point.X) + (point.Y * point.Y);
        var longitudeRate = ((point.X * tangent.Y) - (point.Y * tangent.X)) / axial;
        return SpatialEllipsoid.AreaBelow(SpatialEllipsoid.GeodeticLatitude(point)) * longitudeRate;
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180.0;
}
