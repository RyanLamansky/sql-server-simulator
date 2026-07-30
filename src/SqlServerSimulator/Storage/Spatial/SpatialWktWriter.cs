using System.Globalization;
using System.Text;

namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// Renders a <see cref="SpatialGeometry"/> as OGC Well-Known Text in the
/// canonical spelling real SQL Server produces: a space between the label and
/// the body (<c>POINT (1 2)</c>), <c>", "</c> between coordinates and between
/// collection members, and <c>EMPTY</c> for a shape with no coordinates.
/// </summary>
/// <remarks>
/// <para>Ordinates use .NET's round-trip <c>"R"</c> form under the invariant
/// culture, which is what real emits — <c>1.50</c> comes back as <c>1.5</c>,
/// <c>1e10</c> as <c>10000000000</c>, and <c>1e30</c> as <c>1E+30</c>.</para>
/// <para>The <c>includeZM</c> flag separates the two spellings real
/// exposes: <c>ToString()</c> and <c>AsTextZM()</c> carry the Z and M
/// ordinates, while <c>STAsText()</c> drops them. A point whose Z is absent
/// but whose M is present writes the Z slot as the literal <c>NULL</c>, the
/// same round-trippable spelling the reader accepts.</para>
/// </remarks>
internal static class SpatialWktWriter
{
    public static string Write(SpatialGeometry geometry, bool includeZM) => Write(geometry.Root, includeZM);

    public static string Write(SpatialShape shape, bool includeZM)
    {
        var builder = new StringBuilder();
        AppendShape(builder, shape, includeZM, bare: false);
        return builder.ToString();
    }

    private static string LabelOf(SpatialShapeType type) => type switch
    {
        SpatialShapeType.Point => "POINT",
        SpatialShapeType.LineString => "LINESTRING",
        SpatialShapeType.Polygon => "POLYGON",
        SpatialShapeType.MultiPoint => "MULTIPOINT",
        SpatialShapeType.MultiLineString => "MULTILINESTRING",
        SpatialShapeType.MultiPolygon => "MULTIPOLYGON",
        SpatialShapeType.GeometryCollection => "GEOMETRYCOLLECTION",
        SpatialShapeType.CircularString => "CIRCULARSTRING",
        SpatialShapeType.CompoundCurve => "COMPOUNDCURVE",
        SpatialShapeType.CurvePolygon => "CURVEPOLYGON",
        _ => "FULLGLOBE",
    };

    /// <summary>
    /// Writes one shape. <paramref name="bare"/> omits the label, which is how
    /// members of a Multi* collection appear — the parent already named the
    /// kind, so a MultiPolygon's members write as bare ring lists.
    /// </summary>
    private static void AppendShape(StringBuilder builder, SpatialShape shape, bool includeZM, bool bare)
    {
        if (!bare)
            _ = builder.Append(LabelOf(shape.Type));

        if (shape.Type == SpatialShapeType.FullGlobe)
            return;

        if (IsWrittenEmpty(shape))
        {
            _ = builder.Append(bare ? "EMPTY" : " EMPTY");
            return;
        }

        if (!bare)
            _ = builder.Append(' ');

        switch (shape.Type)
        {
            case SpatialShapeType.Point:
                _ = builder.Append('(');
                AppendCoordinate(builder, shape.Figures[0][0], includeZM);
                _ = builder.Append(')');
                return;
            case SpatialShapeType.LineString:
            case SpatialShapeType.CircularString:
                AppendFigure(builder, shape.Figures[0], includeZM);
                return;
            case SpatialShapeType.Polygon:
            case SpatialShapeType.CurvePolygon:
                _ = builder.Append('(');
                for (var i = 0; i < shape.Figures.Length; i++)
                {
                    if (i > 0)
                        _ = builder.Append(", ");
                    AppendFigure(builder, shape.Figures[i], includeZM);
                }
                _ = builder.Append(')');
                return;
            default:
                // Multi* members drop their label; a GeometryCollection's keep it.
                var bareChildren = shape.Type != SpatialShapeType.GeometryCollection;
                _ = builder.Append('(');
                for (var i = 0; i < shape.Children.Length; i++)
                {
                    if (i > 0)
                        _ = builder.Append(", ");
                    AppendShape(builder, shape.Children[i], includeZM, bareChildren);
                }
                _ = builder.Append(')');
                return;
        }
    }

    /// <summary>
    /// True when the shape prints as <c>EMPTY</c>. This is the shape's <i>own</i>
    /// emptiness, not <c>STIsEmpty()</c>'s recursive one — a collection holding
    /// only empty members still prints its members.
    /// </summary>
    private static bool IsWrittenEmpty(SpatialShape shape) => shape.Figures.Length == 0 && shape.Children.Length == 0;

    private static void AppendFigure(StringBuilder builder, SpatialCoordinate[] points, bool includeZM)
    {
        _ = builder.Append('(');
        for (var i = 0; i < points.Length; i++)
        {
            if (i > 0)
                _ = builder.Append(", ");
            AppendCoordinate(builder, points[i], includeZM);
        }
        _ = builder.Append(')');
    }

    private static void AppendCoordinate(StringBuilder builder, SpatialCoordinate point, bool includeZM)
    {
        _ = builder.Append(Format(point.X)).Append(' ').Append(Format(point.Y));
        if (!includeZM || (!point.Z.HasValue && !point.M.HasValue))
            return;
        _ = builder.Append(' ').Append(point.Z.HasValue ? Format(point.Z.Value) : "NULL");
        if (point.M.HasValue)
            _ = builder.Append(' ').Append(Format(point.M.Value));
    }

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
