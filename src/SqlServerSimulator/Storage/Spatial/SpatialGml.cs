using System.Globalization;
using System.Text;
using System.Xml;

namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// The Geography Markup Language form of a spatial instance: <c>AsGml()</c>
/// writes it and <c>GeomFromGml(gml, srid)</c> reads it, in real's GML 3.1
/// subset (probed 2026-10-06 against SQL Server 2025).
/// </summary>
/// <remarks>
/// <para>Each kind has one element: <c>Point</c> (one <c>pos</c>),
/// <c>LineString</c> and <c>ArcString</c> (one <c>posList</c>),
/// <c>Polygon</c> (<c>LinearRing</c>s under <c>exterior</c> and
/// <c>interior</c>), <c>CompositeCurve</c> (a <c>curveMember</c> per element),
/// <c>PolygonPatch</c> (a <c>Ring</c> holding one <c>curveMember</c> per ring),
/// <c>MultiPoint</c> / <c>MultiCurve</c> / <c>MultiSurface</c> /
/// <c>MultiGeometry</c> (their members under one <c>pointMembers</c> /
/// <c>curveMembers</c> / <c>surfaceMembers</c> / <c>geometryMembers</c>), and
/// <c>geography</c>'s <c>FullGlobe</c> in Microsoft's own namespace. Only the
/// outermost element carries the GML namespace. Coordinates are X then Y —
/// latitude then longitude for <c>geography</c> — with Z and M dropped, in
/// the WKT writer's number form.</para>
/// <para>The reader takes that form back, plus a run of <c>pos</c> elements in
/// place of a <c>posList</c>, singular <c>pointMember</c>-style containers
/// ahead of the plural one, and an <c>Arc</c> of exactly three points. It
/// walks the elements in a fixed order the way an <see cref="XmlReader"/>
/// does, so a departure is the same <c>XmlException</c> real's reader
/// raises, and it hands the shape to the WKT reader, whose checks — ring
/// closure, point counts, a compound curve's continuity, latitude — are the
/// ones real's builder makes.</para>
/// </remarks>
internal static class SpatialGml
{
    private const string GmlNamespace = "http://www.opengis.net/gml";
    private const string GlobeNamespace = "http://schemas.microsoft.com/sqlserver/2011/geography";

    public static string Write(SpatialGeometry geometry, bool isGeography)
    {
        if (geometry.Root.Type == SpatialShapeType.FullGlobe)
            return $"<FullGlobe xmlns=\"{GlobeNamespace}\"/>";
        var builder = new StringBuilder();
        AppendShape(builder, geometry.Root, isGeography, top: true);
        return builder.ToString();
    }

    private static void AppendShape(StringBuilder builder, SpatialShape shape, bool isGeography, bool top)
    {
        var name = shape.Type switch
        {
            SpatialShapeType.Point => "Point",
            SpatialShapeType.LineString => "LineString",
            SpatialShapeType.Polygon => "Polygon",
            SpatialShapeType.MultiPoint => "MultiPoint",
            SpatialShapeType.MultiLineString => "MultiCurve",
            SpatialShapeType.MultiPolygon => "MultiSurface",
            SpatialShapeType.CircularString => "ArcString",
            SpatialShapeType.CompoundCurve => "CompositeCurve",
            SpatialShapeType.CurvePolygon => "PolygonPatch",
            _ => "MultiGeometry",
        };
        _ = builder.Append('<').Append(name);
        if (top)
            _ = builder.Append(" xmlns=\"").Append(GmlNamespace).Append('"');
        var empty = shape.Figures.Length == 0 && shape.Children.Length == 0;
        switch (shape.Type)
        {
            case SpatialShapeType.Point:
                _ = builder.Append('>');
                if (empty)
                    _ = builder.Append("<pos/>");
                else
                    _ = AppendPositions(builder.Append("<pos>"), shape.Figures[0], isGeography).Append("</pos>");
                break;
            case SpatialShapeType.LineString:
            case SpatialShapeType.CircularString:
                _ = builder.Append('>');
                AppendPosList(builder, empty ? [] : shape.Figures[0], isGeography);
                break;
            case SpatialShapeType.Polygon:
            case SpatialShapeType.CurvePolygon:
            case SpatialShapeType.CompoundCurve:
                if (empty)
                {
                    _ = builder.Append("/>");
                    return;
                }
                _ = builder.Append('>');
                if (shape.Type == SpatialShapeType.CompoundCurve)
                    AppendCurveMembers(builder, shape, 0, isGeography);
                else
                    AppendRings(builder, shape, isGeography);
                break;
            default:
                var members = shape.Type switch
                {
                    SpatialShapeType.MultiPoint => "pointMembers",
                    SpatialShapeType.MultiLineString => "curveMembers",
                    SpatialShapeType.MultiPolygon => "surfaceMembers",
                    _ => "geometryMembers",
                };
                _ = builder.Append('>');
                if (shape.Children.Length == 0)
                {
                    _ = builder.Append('<').Append(members).Append("/>");
                }
                else
                {
                    _ = builder.Append('<').Append(members).Append('>');
                    foreach (var child in shape.Children)
                        AppendShape(builder, child, isGeography, top: false);
                    _ = builder.Append("</").Append(members).Append('>');
                }
                break;
        }
        _ = builder.Append("</").Append(name).Append('>');
    }

    private static void AppendRings(StringBuilder builder, SpatialShape shape, bool isGeography)
    {
        for (var i = 0; i < shape.Figures.Length; i++)
        {
            var side = i == 0 ? "exterior" : "interior";
            _ = builder.Append('<').Append(side).Append('>');
            if (shape.Type == SpatialShapeType.Polygon)
            {
                _ = builder.Append("<LinearRing>");
                AppendPosList(builder, shape.Figures[i], isGeography);
                _ = builder.Append("</LinearRing>");
            }
            else
            {
                _ = builder.Append("<Ring><curveMember>");
                switch (shape.FigureType(i))
                {
                    case SpatialFigureType.Composite:
                        _ = builder.Append("<CompositeCurve>");
                        AppendCurveMembers(builder, shape, i, isGeography);
                        _ = builder.Append("</CompositeCurve>");
                        break;
                    case SpatialFigureType.Arc:
                        AppendCurve(builder, isArc: true, shape.Figures[i], isGeography);
                        break;
                    default:
                        AppendCurve(builder, isArc: false, shape.Figures[i], isGeography);
                        break;
                }
                _ = builder.Append("</curveMember></Ring>");
            }
            _ = builder.Append("</").Append(side).Append('>');
        }
    }

    private static void AppendCurveMembers(StringBuilder builder, SpatialShape shape, int figure, bool isGeography)
    {
        foreach (var (isArc, points) in SpatialCurves.Elements(shape, figure))
        {
            _ = builder.Append("<curveMember>");
            AppendCurve(builder, isArc, points, isGeography);
            _ = builder.Append("</curveMember>");
        }
    }

    private static void AppendCurve(StringBuilder builder, bool isArc, SpatialCoordinate[] points, bool isGeography)
    {
        var name = isArc ? "ArcString" : "LineString";
        _ = builder.Append('<').Append(name).Append('>');
        AppendPosList(builder, points, isGeography);
        _ = builder.Append("</").Append(name).Append('>');
    }

    private static void AppendPosList(StringBuilder builder, SpatialCoordinate[] points, bool isGeography)
    {
        if (points.Length == 0)
            _ = builder.Append("<posList/>");
        else
            _ = AppendPositions(builder.Append("<posList>"), points, isGeography).Append("</posList>");
    }

    private static StringBuilder AppendPositions(StringBuilder builder, SpatialCoordinate[] points, bool isGeography)
    {
        for (var i = 0; i < points.Length; i++)
        {
            if (i > 0)
                _ = builder.Append(' ');
            var (first, second) = isGeography ? (points[i].Y, points[i].X) : (points[i].X, points[i].Y);
            _ = builder.Append(SpatialWktWriter.Format(first)).Append(' ').Append(SpatialWktWriter.Format(second));
        }
        return builder;
    }

    /// <summary>Reads GML into an instance with the given SRID, raising real's refusals.</summary>
    public static SpatialGeometry Read(string gml, int srid, bool isGeography) =>
        SpatialWktReader.Read(new Reader(gml, isGeography).ReadTop(), srid, isGeography);

    /// <summary>
    /// A pull reader over the GML that translates it to WKT element by element,
    /// asserting each element the grammar expects next the way
    /// <c>XmlReader.ReadStartElement</c> / <c>ReadEndElement</c> do.
    /// </summary>
    private sealed class Reader
    {
        private readonly XmlReader xml;
        private readonly bool isGeography;
        private readonly StringBuilder wkt = new();

        public Reader(string gml, bool isGeography)
        {
            this.isGeography = isGeography;
            this.xml = XmlReader.Create(new StringReader(gml), new XmlReaderSettings
            {
                ConformanceLevel = ConformanceLevel.Fragment,
                DtdProcessing = DtdProcessing.Prohibit,
            });
        }

        public string ReadTop()
        {
            try
            {
                _ = this.xml.MoveToContent();
                if (this.xml.NodeType != XmlNodeType.Element)
                    throw SimulatedSqlException.SpatialGmlNoElement(this.isGeography);
                if (this.xml.NamespaceURI == GlobeNamespace && this.xml.LocalName == "FullGlobe")
                {
                    RefuseAttributes();
                    _ = this.wkt.Append("FULLGLOBE");
                    SkipElement();
                }
                else if (this.xml.NamespaceURI != GmlNamespace || !ReadShape(this.xml.LocalName))
                {
                    throw SimulatedSqlException.SpatialGmlTopLevelTag(this.isGeography, this.xml.LocalName);
                }
                _ = this.xml.MoveToContent();
                if (!this.xml.EOF)
                    throw SimulatedSqlException.SpatialGmlMultipleTopLevel(this.isGeography);
                return this.wkt.ToString();
            }
            catch (XmlException malformed)
            {
                throw SimulatedSqlException.SpatialGmlXml(this.isGeography, malformed.Message);
            }
        }

        /// <summary>
        /// Translates the shape element <paramref name="name"/> the reader sits
        /// on, or answers false for a name that isn't one.
        /// </summary>
        private bool ReadShape(string name)
        {
            switch (name)
            {
                case "Arc":
                case "ArcString":
                    _ = this.wkt.Append("CIRCULARSTRING");
                    ReadArc(name);
                    return true;
                case "CompositeCurve":
                    _ = this.wkt.Append("COMPOUNDCURVE");
                    if (OpenWithContent("CompositeCurve"))
                    {
                        _ = this.wkt.Append(" (");
                        ReadCurveMembers(composite: true);
                        _ = this.wkt.Append(')');
                        Close();
                    }
                    else
                    {
                        _ = this.wkt.Append(" EMPTY");
                    }
                    return true;
                case "LineString":
                    _ = this.wkt.Append("LINESTRING");
                    ReadPositions("LineString", ring: false);
                    return true;
                case "MultiCurve":
                    ReadCollection("MULTILINESTRING", "MultiCurve", "curveMember", "LineString");
                    return true;
                case "MultiGeometry":
                    ReadCollection("GEOMETRYCOLLECTION", "MultiGeometry", "geometryMember", null);
                    return true;
                case "MultiPoint":
                    ReadCollection("MULTIPOINT", "MultiPoint", "pointMember", "Point");
                    return true;
                case "MultiSurface":
                    ReadCollection("MULTIPOLYGON", "MultiSurface", "surfaceMember", "Polygon");
                    return true;
                case "Point":
                    _ = this.wkt.Append("POINT");
                    if (OpenWithContent("Point"))
                    {
                        var coordinates = ReadNumbers("pos");
                        if (coordinates.Count == 0)
                            _ = this.wkt.Append(" EMPTY");
                        else if (coordinates.Count != 2)
                            throw SimulatedSqlException.SpatialGmlPosCount(this.isGeography, coordinates.Count);
                        else
                            AppendPoints(coordinates, parenthesized: true);
                        Close();
                    }
                    else
                    {
                        _ = this.wkt.Append(" EMPTY");
                    }
                    return true;
                case "Polygon":
                    _ = this.wkt.Append("POLYGON");
                    ReadRings("Polygon", "LinearRing");
                    return true;
                case "PolygonPatch":
                    _ = this.wkt.Append("CURVEPOLYGON");
                    ReadRings("PolygonPatch", "Ring");
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>A collection's members: any number of singular member elements, then optionally one plural container.</summary>
        private void ReadCollection(string label, string name, string member, string? memberKind)
        {
            _ = this.wkt.Append(label);
            if (!OpenWithContent(name))
            {
                _ = this.wkt.Append(" EMPTY");
                return;
            }
            var count = 0;
            var start = this.wkt.Length;
            _ = this.wkt.Append(" (");
            while (IsStart(member))
            {
                if (OpenWithContent(member))
                {
                    ReadMember(memberKind, ref count);
                    Close();
                }
            }
            if (IsStart(member + "s"))
            {
                if (OpenWithContent(member + "s"))
                {
                    while (!AtEnd())
                        ReadMember(memberKind, ref count);
                    Close();
                }
            }
            Close();
            if (count == 0)
            {
                this.wkt.Length = start;
                _ = this.wkt.Append(" EMPTY");
            }
            else
            {
                _ = this.wkt.Append(')');
            }
        }

        private void ReadMember(string? memberKind, ref int count)
        {
            if (count++ > 0)
                _ = this.wkt.Append(", ");
            var at = this.wkt.Length;
            if (memberKind is not null)
            {
                Expect(memberKind);
                _ = ReadShape(memberKind);
                // A Multi* member is written without its label.
                var written = this.wkt.ToString(at, this.wkt.Length - at);
                var labelEnd = written.IndexOf(' ', StringComparison.Ordinal);
                this.wkt.Length = at;
                _ = this.wkt.Append(written.AsSpan(labelEnd + 1));
                return;
            }
            _ = this.xml.MoveToContent();
            if (this.xml.NodeType == XmlNodeType.Element && this.xml.NamespaceURI == GlobeNamespace && this.xml.LocalName == "FullGlobe")
            {
                RefuseAttributes();
                _ = this.wkt.Append("FULLGLOBE");
                SkipElement();
                return;
            }
            if (this.xml.NodeType != XmlNodeType.Element || this.xml.NamespaceURI != GmlNamespace || !ReadShape(this.xml.LocalName))
                Expect("Point");
        }

        private void ReadPositions(string name, bool ring)
        {
            if (!OpenWithContent(name))
            {
                if (ring)
                    throw SimulatedSqlException.SpatialGmlPosListEmpty(this.isGeography);
                _ = this.wkt.Append(" EMPTY");
                return;
            }
            var coordinates = IsStart("pos") ? ReadPosRun() : ReadPosList();
            if (coordinates.Count == 0)
            {
                if (ring)
                    throw SimulatedSqlException.SpatialGmlPosListEmpty(this.isGeography);
                _ = this.wkt.Append(" EMPTY");
            }
            else
            {
                AppendPoints(coordinates, parenthesized: true);
            }
            Close();
        }

        private void ReadArc(string name)
        {
            if (!OpenWithContent(name))
            {
                if (name == "Arc")
                    throw SimulatedSqlException.SpatialGmlArcPointCount(this.isGeography);
                _ = this.wkt.Append(" EMPTY");
                return;
            }
            var coordinates = IsStart("pos") ? ReadPosRun() : ReadPosList();
            Close();
            var points = coordinates.Count / 2;
            if (name == "Arc" && points != 3)
                throw SimulatedSqlException.SpatialGmlArcPointCount(this.isGeography);
            if (points == 0)
            {
                _ = this.wkt.Append(" EMPTY");
                return;
            }
            if (points < 3)
                throw SimulatedSqlException.SpatialCircularStringTooFewPoints(this.isGeography);
            // A trailing point that completes no arc is dropped.
            if (points % 2 == 0)
                coordinates.RemoveRange(coordinates.Count - 2, 2);
            AppendPoints(coordinates, parenthesized: true);
        }

        private void ReadRings(string name, string ringName)
        {
            if (!OpenWithContent(name))
            {
                _ = this.wkt.Append(" EMPTY");
                return;
            }
            _ = this.wkt.Append(" (");
            var side = "exterior";
            var first = true;
            while (first || IsStart("interior"))
            {
                if (!first)
                    _ = this.wkt.Append(", ");
                Expect(side);
                if (Open(side))
                {
                    Expect(ringName);
                    if (ringName == "LinearRing")
                    {
                        var at = this.wkt.Length;
                        ReadPositions("LinearRing", ring: true);
                        // A ring is written without a label.
                        _ = this.wkt.Remove(at, 1);
                    }
                    else if (OpenWithContent("Ring"))
                    {
                        Expect("curveMember");
                        _ = Open("curveMember");
                        ReadRingCurve();
                        Close();
                        Close();
                    }
                    else
                    {
                        _ = this.wkt.Append("EMPTY");
                    }
                    Close();
                }
                first = false;
                side = "interior";
            }
            _ = this.wkt.Append(')');
            Close();
        }

        /// <summary>A PolygonPatch ring's one curve: a line, an arc string or a composite, written as a CURVEPOLYGON ring.</summary>
        private void ReadRingCurve()
        {
            _ = this.xml.MoveToContent();
            var name = this.xml.NodeType == XmlNodeType.Element && this.xml.NamespaceURI == GmlNamespace ? this.xml.LocalName : null;
            var at = this.wkt.Length;
            switch (name)
            {
                case "Arc":
                case "ArcString":
                    _ = ReadShape(name);
                    if (this.wkt.ToString(at, this.wkt.Length - at).EndsWith(" EMPTY", StringComparison.Ordinal))
                        throw SimulatedSqlException.SpatialCircularStringRingEmpty(this.isGeography);
                    break;
                case "CompositeCurve":
                    _ = ReadShape(name);
                    if (this.wkt.ToString(at, this.wkt.Length - at).EndsWith(" EMPTY", StringComparison.Ordinal))
                        throw SimulatedSqlException.SpatialCompoundCurveRingEmpty(this.isGeography);
                    break;
                default:
                    Expect("LineString");
                    _ = ReadShape("LineString");
                    var written = this.wkt.ToString(at, this.wkt.Length - at);
                    if (written.EndsWith(" EMPTY", StringComparison.Ordinal))
                        throw SimulatedSqlException.SpatialCircularStringRingEmpty(this.isGeography);
                    this.wkt.Length = at;
                    _ = this.wkt.Append(written.AsSpan("LINESTRING ".Length));
                    break;
            }
        }

        private void ReadCurveMembers(bool composite)
        {
            var count = 0;
            do
            {
                if (count++ > 0)
                    _ = this.wkt.Append(", ");
                Expect("curveMember");
                _ = Open("curveMember");
                _ = this.xml.MoveToContent();
                var name = this.xml.NodeType == XmlNodeType.Element && this.xml.NamespaceURI == GmlNamespace ? this.xml.LocalName : null;
                var at = this.wkt.Length;
                if (name is "ArcString" or "Arc")
                {
                    _ = ReadShape(name);
                }
                else
                {
                    Expect("LineString");
                    _ = ReadShape("LineString");
                    // A composite's line elements are bare.
                    var written = this.wkt.ToString(at, this.wkt.Length - at);
                    this.wkt.Length = at;
                    _ = this.wkt.Append(composite ? written.AsSpan("LINESTRING ".Length) : written);
                }
                Close();
            }
            while (!AtEnd());
        }

        private List<double> ReadPosRun()
        {
            var coordinates = new List<double>();
            while (IsStart("pos"))
            {
                var pos = ReadNumbers("pos");
                if (pos.Count != 2)
                    throw SimulatedSqlException.SpatialGmlPosCount(this.isGeography, pos.Count);
                coordinates.AddRange(pos);
            }
            return coordinates;
        }

        private List<double> ReadPosList()
        {
            var coordinates = ReadNumbers("posList");
            return coordinates.Count % 2 != 0
                ? throw SimulatedSqlException.SpatialGmlPosListOdd(this.isGeography, coordinates.Count)
                : coordinates;
        }

        /// <summary>Reads element <paramref name="name"/>'s whitespace-separated numbers, as <c>XmlConvert</c> spells them.</summary>
        private List<double> ReadNumbers(string name)
        {
            Expect(name);
            var empty = this.xml.IsEmptyElement;
            RefuseAttributes();
            var text = empty ? string.Empty : this.xml.ReadElementContentAsString();
            if (empty)
                _ = this.xml.Read();
            var coordinates = new List<double>();
            foreach (var token in text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                double value;
                try
                {
                    value = XmlConvert.ToDouble(token);
                }
                catch (FormatException)
                {
                    throw SimulatedSqlException.SpatialGmlXml(this.isGeography,
                        "Content cannot be converted to the type System.Double[]. ---> System.FormatException: Input string was not in a correct format.",
                        "System.FormatException");
                }
                if (!double.IsFinite(value))
                    throw SimulatedSqlException.SpatialCoordinateNotFinite(this.isGeography);
                coordinates.Add(value);
            }
            return coordinates;
        }

        private void AppendPoints(List<double> coordinates, bool parenthesized)
        {
            if (parenthesized)
                _ = this.wkt.Append(" (");
            for (var i = 0; i < coordinates.Count; i += 2)
            {
                if (i > 0)
                    _ = this.wkt.Append(", ");
                var (x, y) = this.isGeography ? (coordinates[i + 1], coordinates[i]) : (coordinates[i], coordinates[i + 1]);
                _ = this.wkt.Append(x.ToString("R", CultureInfo.InvariantCulture)).Append(' ').Append(y.ToString("R", CultureInfo.InvariantCulture));
            }
            if (parenthesized)
                _ = this.wkt.Append(')');
        }

        /// <summary>
        /// <c>ReadStartElement</c>'s check without the read: the reader must sit
        /// on element <paramref name="name"/> in the GML namespace.
        /// </summary>
        private void Expect(string name)
        {
            _ = this.xml.MoveToContent();
            if (this.xml.NodeType == XmlNodeType.Element)
            {
                if (this.xml.LocalName != name || this.xml.NamespaceURI != GmlNamespace)
                    throw new XmlException($"Element '{name}' with namespace name '{GmlNamespace}' was not found.");
                return;
            }
            throw new XmlException($"'{this.xml.NodeType}' is an invalid XmlNodeType.");
        }

        /// <summary>Steps into element <paramref name="name"/>, answering false for an empty element, which has no content to read or close.</summary>
        private bool Open(string name)
        {
            Expect(name);
            RefuseAttributes();
            var empty = this.xml.IsEmptyElement;
            _ = this.xml.Read();
            return !empty;
        }

        /// <summary>
        /// Steps into element <paramref name="name"/>, answering false — with
        /// the element already left — when it holds nothing.
        /// </summary>
        private bool OpenWithContent(string name)
        {
            if (!Open(name))
                return false;
            if (!AtEnd())
                return true;
            Close();
            return false;
        }

        /// <summary><c>ReadEndElement</c>: the reader must sit on an end tag.</summary>
        private void Close()
        {
            _ = this.xml.MoveToContent();
            if (this.xml.NodeType != XmlNodeType.EndElement)
                throw new XmlException($"'{this.xml.NodeType}' is an invalid XmlNodeType.");
            _ = this.xml.Read();
        }

        private bool AtEnd()
        {
            _ = this.xml.MoveToContent();
            return this.xml.NodeType == XmlNodeType.EndElement;
        }

        private bool IsStart(string name)
        {
            _ = this.xml.MoveToContent();
            return this.xml.NodeType == XmlNodeType.Element && this.xml.LocalName == name && this.xml.NamespaceURI == GmlNamespace;
        }

        private void SkipElement()
        {
            if (this.xml.IsEmptyElement)
                _ = this.xml.Read();
            else
                this.xml.Skip();
        }

        /// <summary>Any attribute but a namespace declaration is 24130.</summary>
        private void RefuseAttributes()
        {
            if (!this.xml.HasAttributes)
                return;
            for (var i = 0; i < this.xml.AttributeCount; i++)
            {
                this.xml.MoveToAttribute(i);
                if (this.xml.Prefix != "xmlns" && this.xml.LocalName != "xmlns")
                {
                    _ = this.xml.MoveToElement();
                    throw SimulatedSqlException.SpatialGmlAttribute(this.isGeography);
                }
            }
            _ = this.xml.MoveToElement();
        }
    }
}
