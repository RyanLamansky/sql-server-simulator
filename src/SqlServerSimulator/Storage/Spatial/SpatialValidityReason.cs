namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// The 24400-series reason <c>IsValidDetailed()</c> reports for an instance its
/// type's validator refuses, for the shapes whose reason the probes pin: a
/// single LineString and a single-ring Polygon, on either type.
/// </summary>
/// <remarks>
/// <para>Probed 2026-10-06 against SQL Server 2025. A figure whose points are
/// all one is <b>24406</b> (<i>curve degenerates to a point</i>), as is a
/// <c>geometry</c> figure ending on a repeat the validator refuses, numbered
/// by where that point first occurs in the figure. Two edges sharing a
/// stretch are <b>24413</b> (<i>two overlapping edges</i>), which is all a
/// line can break. A ring that meets itself otherwise reads per type:
/// <c>geometry</c> reports any crossing, a stretch two non-adjacent edges
/// share in full both ways, or a touch as <b>24404</b> (<i>intersects itself
/// or some other ring</i>) and a remaining overlap as 24413, while
/// <c>geography</c> reports any overlap first as 24413, then a crossing as
/// <b>24409</b> (<i>lies in the interior of a polygon</i>) and a touch as
/// 24404.</para>
/// <para>Collections, Multi* instances, rings with holes and curves number
/// their figures and entries by rules the probes didn't settle, so they get
/// no reason here.</para>
/// </remarks>
internal static class SpatialValidityReason
{
    /// <summary>The reason text, or null for a shape outside the modeled set or with no reason found.</summary>
    public static string? For(SpatialShape shape, bool geography)
    {
        if (shape.Type is not (SpatialShapeType.LineString or SpatialShapeType.Polygon) || shape.Figures.Length != 1 || shape.Children.Length != 0)
            return null;
        if (!geography)
            shape = SpatialValidator.Snapped(shape);
        var figure = shape.Figures[0];
        if (figure.Length == 0)
            return null;
        var first = PlanarPoint.From(figure[0]);
        if (Array.TrueForAll(figure, point => PlanarPoint.From(point) == first))
            return Degenerate(1);
        if (!geography && SpatialValidator.EndsOnRefusedRepeat(figure))
        {
            var last = PlanarPoint.From(figure[^1]);
            return Degenerate(Array.FindIndex(figure, point => PlanarPoint.From(point) == last) + 1);
        }

        var segments = new List<PlanarSegment>();
        for (var i = 1; i < figure.Length; i++)
        {
            var segment = new PlanarSegment(PlanarPoint.From(figure[i - 1]), PlanarPoint.From(figure[i]));
            if (!segment.IsDegenerate)
                segments.Add(segment);
        }

        var ring = shape.Type == SpatialShapeType.Polygon;
        bool overlap = false, bridge = false, cross = false, touch = false;
        for (var i = 0; i < segments.Count; i++)
        {
            for (var j = i + 1; j < segments.Count; j++)
            {
                var adjacent = j == i + 1 || (ring && i == 0 && j == segments.Count - 1);
                var (one, other) = (segments[i], segments[j]);
                if (SpatialTopology.OverlapsIn1D(one, other))
                {
                    overlap = true;
                    bridge |= !adjacent && one.A == other.B && one.B == other.A && EnclosesArea(segments, i, j);
                }
                else if (!adjacent && SpatialTopology.ProperlyCross(one, other))
                {
                    cross = true;
                }
                else if (!adjacent && SpatialTopology.Intersects(one, other))
                {
                    touch = true;
                }
            }
        }

        if (!ring)
            return overlap ? Overlapping : null;
        return geography
            ? overlap ? Overlapping : cross || (touch && LobesTurnApart(segments)) ? InteriorPortion : touch ? SelfIntersecting : null
            : cross || bridge ? SelfIntersecting : overlap ? Overlapping : touch ? SelfIntersecting : null;
    }

    /// <summary>
    /// Whether the run between a stretch a ring walks out along and back
    /// (segments <paramref name="outward"/> and <paramref name="back"/>)
    /// encloses area — a bridge to a loop, which <c>geometry</c> reads as a
    /// self-intersection, rather than a nested spike, which it reads as an
    /// overlap.
    /// </summary>
    private static bool EnclosesArea(List<PlanarSegment> segments, int outward, int back)
    {
        var loop = new PlanarPoint[back - outward];
        for (var k = outward + 1; k < back; k++)
            loop[k - outward - 1] = segments[k].A;
        loop[^1] = segments[back].A;
        return SpatialTopology.SignedRingArea(loop) != 0;
    }

    /// <summary>
    /// Whether a <c>geography</c> ring that touches itself turns its two lobes
    /// opposite ways — the lobe split at the first point the ring meets itself
    /// again, a vertex it revisits or one lying on another edge. On the round
    /// earth a ring's direction decides which side is its interior, so a lobe
    /// turned the other way puts part of the ring inside the polygon (24409),
    /// where lobes turned alike merely touch (24404).
    /// </summary>
    private static bool LobesTurnApart(List<PlanarSegment> segments)
    {
        var vertices = new List<PlanarPoint>(segments.Count + 1);
        foreach (var segment in segments)
            vertices.Add(segment.A);
        // A vertex lying inside another edge becomes a vertex of that edge too.
        for (var k = 0; k < vertices.Count; k++)
        {
            for (var m = 0; m < segments.Count; m++)
            {
                var edge = segments[m];
                if (vertices[k] != edge.A && vertices[k] != edge.B && SpatialTopology.OnSegment(vertices[k], edge))
                {
                    var at = vertices.IndexOf(edge.A) + 1;
                    vertices.Insert(at, vertices[k]);
                    return Opposed(vertices, at < k ? at : k, at < k ? k + 1 : at);
                }
            }
        }
        for (var a = 0; a < vertices.Count; a++)
        {
            var b = vertices.IndexOf(vertices[a], a + 1);
            if (b > 0)
                return Opposed(vertices, a, b);
        }
        return false;
    }

    private static bool Opposed(List<PlanarPoint> vertices, int a, int b)
    {
        var inner = new PlanarPoint[b - a + 1];
        for (var k = a; k <= b; k++)
            inner[k - a] = vertices[k];
        var outer = new List<PlanarPoint>(vertices.Count - (b - a) + 1);
        for (var k = b; k < vertices.Count; k++)
            outer.Add(vertices[k]);
        for (var k = 0; k <= a; k++)
            outer.Add(vertices[k]);
        return Math.Sign(SpatialTopology.SignedRingArea(inner)) * Math.Sign(SpatialTopology.SignedRingArea([.. outer])) < 0;
    }

    private const string InteriorPortion = "24409: Not valid because some portion of polygon ring (1) lies in the interior of a polygon.";

    private const string Overlapping = "24413: Not valid because of two overlapping edges in curve (1).";

    private const string SelfIntersecting = "24404: Not valid because polygon ring (1) intersects itself or some other ring.";

    private static string Degenerate(int curve) =>
        $"24406: Not valid because curve ({curve.ToString(System.Globalization.CultureInfo.InvariantCulture)}) degenerates to a point.";
}
