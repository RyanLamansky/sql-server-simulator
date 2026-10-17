using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The inline schema <c>FOR XML RAW</c> / <c>AUTO</c> sends ahead of its rows
/// under <c>XMLSCHEMA</c> (an XSD) or <c>XMLDATA</c> (an XDR), with the
/// namespace the rows then carry. Its text is compiled from the result's
/// columns once, and numbered per execution from the session's counter for
/// that directive, which an <c>XMLSCHEMA('uri')</c> leaves alone. Every shape
/// here was probed 2026-10-17 against SQL Server 2025.
/// </summary>
internal sealed class ForXmlInlineSchema
{
    private const string SqlTypesNamespace = "http://schemas.microsoft.com/sqlserver/2004/sqltypes";

    /// <summary>True for <c>XMLDATA</c>'s XDR, false for <c>XMLSCHEMA</c>'s XSD.</summary>
    public readonly bool Xdr;

    /// <summary>The <c>XMLSCHEMA('uri')</c> target namespace, or null for the numbered one.</summary>
    public readonly string? Uri;

    private readonly ForXmlSchemaLevel[] levels;

    /// <summary>
    /// True for an XSD with a <c>sql_variant</c> element, whose rows declare
    /// the <c>xsi</c> and <c>sqltypes</c> prefixes and name each value's type
    /// with <c>xsi:type</c> (see <see cref="VariantTypeName"/>).
    /// </summary>
    public readonly bool TypesVariants;
    private readonly bool auto;
    private readonly bool elements;
    private readonly bool xsinil;

    private ForXmlInlineSchema(bool xdr, string? uri, ForXmlSchemaLevel[] levels, bool auto, bool elements, bool xsinil)
    {
        this.Xdr = xdr;
        this.Uri = uri;
        this.levels = levels;
        this.auto = auto;
        this.elements = elements;
        this.xsinil = xsinil;
        this.TypesVariants = !xdr && Array.Exists(levels, static level => Array.Exists(level.Columns, static column => column is { AsElement: true, Type: SqlVariantSqlType }));
    }

    /// <summary>The <c>sqltypes</c> name a <c>sql_variant</c> element's <c>xsi:type</c> gives the value it holds.</summary>
    public static string VariantTypeName(SqlValue value) =>
        value.Type is DecimalSqlType ? "numeric" : value.Type is SystemNameSqlType ? "nvarchar" : value.Type.SqlServerName;

    /// <summary>
    /// Compiles the schema for <paramref name="inner"/>'s columns, laid out
    /// as RAW's one element or AUTO's nesting <paramref name="levels"/>,
    /// refusing what real refuses as the statement compiles: under
    /// <c>XMLDATA</c> a type XDR can't name (Msg 6848, which still uses up a
    /// schema number), under attribute-centric <c>XMLSCHEMA</c> a
    /// <c>sql_variant</c> (Msg 6847).
    /// </summary>
    public static ForXmlInlineSchema Compile(bool xdr, string? uri, Selection inner, string rowElement, AutoLevel[]? levels, bool elements, bool xsinil, SimulatedDbConnection connection)
    {
        var nullability = inner.ColumnNullability;
        var reportsNumeric = inner.ColumnReportsNumeric;
        ForXmlSchemaColumn Column(int i)
        {
            var type = inner.Schema[i];
            var name = ForXmlName.Encode(inner.ColumnNames[i]);
            var asElement = elements || type is XmlSqlType;
            if (xdr && !MapsToXdr(type))
            {
                connection.XmlDataSchemaNumber++;
                throw SimulatedSqlException.ForXmlXmlDataUnmappedType(inner.ColumnNames[i]);
            }
            if (!xdr && !asElement && type is SqlVariantSqlType)
                throw SimulatedSqlException.ForXmlVariantAttributeWithSchema(inner.ColumnNames[i]);
            return new ForXmlSchemaColumn(name, type, nullability is null || nullability[i], asElement, reportsNumeric is not null && reportsNumeric[i]);
        }

        ForXmlSchemaLevel[] compiled;
        if (levels is null)
        {
            var columns = new ForXmlSchemaColumn[inner.Schema.Length];
            for (var i = 0; i < columns.Length; i++)
                columns[i] = Column(i);
            compiled = [new ForXmlSchemaLevel(rowElement, columns)];
        }
        else
        {
            compiled = new ForXmlSchemaLevel[levels.Length];
            for (var l = 0; l < levels.Length; l++)
                compiled[l] = new ForXmlSchemaLevel(ForXmlName.Encode(levels[l].Name), [.. levels[l].Columns.Select(Column)]);
        }
        return new ForXmlInlineSchema(xdr, uri, compiled, levels is not null, elements, xsinil);
    }

    /// <summary>
    /// Draws this execution's schema number, where one is drawn, and returns
    /// the namespace the rows carry with the schema's text.
    /// </summary>
    public (string RowNamespace, string Text) Render(SimulatedDbConnection connection, bool typed)
    {
        if (this.Xdr)
        {
            var number = ++connection.XmlDataSchemaNumber;
            var name = "Schema" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return ("x-schema:#" + name, this.RenderXdr(name, typed));
        }
        var target = this.Uri;
        if (target is null)
        {
            var number = ++connection.XmlSchemaRowSetNumber;
            target = "urn:schemas-microsoft-com:sql:SqlRowSet" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return (target, this.RenderXsd(target, typed));
    }

    /// <summary>
    /// <paramref name="fragment"/> with <c>xmlns=""</c> on each of its
    /// top-level elements that has no prefix and declares no default
    /// namespace of its own, as real writes an embedded <c>xml</c> value
    /// under the rows' schema namespace (<c>&lt;x&gt;&lt;q xmlns=""/&gt;&lt;/x&gt;</c>).
    /// </summary>
    public static string WithoutDefaultNamespace(string fragment)
    {
        var sb = new StringBuilder(fragment.Length + 16);
        var depth = 0;
        var i = 0;
        while (i < fragment.Length)
        {
            if (fragment[i] != '<')
            {
                _ = sb.Append(fragment[i++]);
                continue;
            }
            // Comments, CDATA sections and processing instructions pass whole.
            var close = string.CompareOrdinal(fragment, i, "<!--", 0, 4) == 0 ? "-->"
                : string.CompareOrdinal(fragment, i, "<![CDATA[", 0, 9) == 0 ? "]]>"
                : string.CompareOrdinal(fragment, i, "<?", 0, 2) == 0 ? "?>"
                : null;
            if (close is not null)
            {
                var end = fragment.IndexOf(close, i, StringComparison.Ordinal);
                end = end < 0 ? fragment.Length : end + close.Length;
                _ = sb.Append(fragment, i, end - i);
                i = end;
                continue;
            }
            var tagEnd = i + 1;
            char? quote = null;
            while (tagEnd < fragment.Length && (quote is not null || fragment[tagEnd] != '>'))
            {
                if (fragment[tagEnd] is '"' or '\'')
                    quote = quote == fragment[tagEnd] ? null : quote ?? fragment[tagEnd];
                tagEnd++;
            }
            var tag = fragment.AsSpan(i, Math.Min(tagEnd + 1, fragment.Length) - i);
            if (tag.StartsWith("</"))
            {
                depth--;
                _ = sb.Append(tag);
            }
            else
            {
                var selfClosing = tag.EndsWith("/>");
                var nameEnd = 1;
                while (nameEnd < tag.Length && tag[nameEnd] is not (' ' or '/' or '>' or '\t' or '\n' or '\r'))
                    nameEnd++;
                if (depth == 0 && !tag[1..nameEnd].Contains(':') && !tag.Contains("xmlns=", StringComparison.Ordinal))
                    _ = sb.Append(tag[..nameEnd]).Append(" xmlns=\"\"").Append(tag[nameEnd..]);
                else
                    _ = sb.Append(tag);
                if (!selfClosing)
                    depth++;
            }
            i += tag.Length;
        }
        return sb.ToString();
    }

    private string RenderXsd(string target, bool typed)
    {
        var sb = new StringBuilder("<xsd:schema");
        // A typed result is a parsed instance, which lists the namespace
        // declarations ahead of the attributes.
        var declarations = (this.auto ? $" xmlns:schema=\"{target}\"" : "") + " xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:sqltypes=\"" + SqlTypesNamespace + "\"";
        _ = typed
            ? sb.Append(declarations).Append(" targetNamespace=\"").Append(target).Append('"')
            : sb.Append(" targetNamespace=\"").Append(target).Append('"').Append(declarations);
        _ = sb.Append(" elementFormDefault=\"qualified\"><xsd:import namespace=\"").Append(SqlTypesNamespace)
            .Append("\" schemaLocation=\"http://schemas.microsoft.com/sqlserver/2004/sqltypes/sqltypes.xsd\"/>");
        for (var l = 0; l < this.levels.Length; l++)
        {
            var level = this.levels[l];
            _ = sb.Append("<xsd:element name=\"").Append(level.Name).Append("\"><xsd:complexType>");
            var hasChild = l + 1 < this.levels.Length;
            if (hasChild || Array.Exists(level.Columns, static column => column.AsElement))
            {
                _ = sb.Append("<xsd:sequence>");
                foreach (var column in level.Columns)
                {
                    if (column.AsElement)
                        this.AppendXsdElement(sb, column);
                }
                if (hasChild)
                    _ = sb.Append("<xsd:element ref=\"schema:").Append(this.levels[l + 1].Name).Append("\" minOccurs=\"0\" maxOccurs=\"unbounded\"/>");
                _ = sb.Append("</xsd:sequence>");
            }
            foreach (var column in level.Columns)
            {
                if (!column.AsElement)
                    AppendXsdAttribute(sb, column);
            }
            _ = sb.Append("</xsd:complexType></xsd:element>");
        }
        return sb.Append("</xsd:schema>").ToString();
    }

    private void AppendXsdElement(StringBuilder sb, ForXmlSchemaColumn column)
    {
        var (simpleType, restriction) = XsdType(column);
        _ = sb.Append("<xsd:element name=\"").Append(column.Name).Append('"');
        if (simpleType is not null)
            _ = sb.Append(" type=\"").Append(simpleType).Append('"');
        if (this.xsinil && this.elements)
            _ = sb.Append(" nillable=\"1\"");
        else if (column.Nullable)
            _ = sb.Append(" minOccurs=\"0\"");
        if (column.Type is SqlVariantSqlType)
            _ = sb.Append(" sqltypes:sqlDbType=\"Variant\"");
        _ = restriction is null
            ? sb.Append("/>")
            : sb.Append("><xsd:simpleType>").Append(restriction).Append("</xsd:simpleType></xsd:element>");
    }

    private static void AppendXsdAttribute(StringBuilder sb, ForXmlSchemaColumn column)
    {
        var (simpleType, restriction) = XsdType(column);
        _ = sb.Append("<xsd:attribute name=\"").Append(column.Name).Append('"');
        if (simpleType is not null)
            _ = sb.Append(" type=\"").Append(simpleType).Append('"');
        if (!column.Nullable)
            _ = sb.Append(" use=\"required\"");
        _ = restriction is null
            ? sb.Append("/>")
            : sb.Append("><xsd:simpleType>").Append(restriction).Append("</xsd:simpleType></xsd:attribute>");
    }

    /// <summary>
    /// A column's XSD type: a <c>sqltypes</c> name for a type without facets,
    /// else the restriction carrying its length, digits, pattern or
    /// collation.
    /// </summary>
    private static (string? Name, string? Restriction) XsdType(ForXmlSchemaColumn column)
    {
        const string DatePart = "((000[1-9])|(00[1-9][0-9])|(0[1-9][0-9]{2})|([1-9][0-9]{3}))-((0[1-9])|(1[012]))-((0[1-9])|([12][0-9])|(3[01]))T";
        const string OffsetPart = "(([+-]((((0[0-9])|(1[0-3]))(:[0-5][0-9]))|14:00))|Z)";
        static string TimePart(int precision) => "(([01][0-9])|(2[0-3]))(:[0-5][0-9]){2}" + (precision > 0 ? $"(\\.[0-9]{{{precision.ToString(System.Globalization.CultureInfo.InvariantCulture)}}})?" : "?");
        static string Nines(int precision) => precision > 0 ? "." + new string('9', precision) : "";
        static string Patterned(string baseName, string pattern, string max) =>
            $"<xsd:restriction base=\"sqltypes:{baseName}\"><xsd:pattern value=\"{pattern}\"/><xsd:maxInclusive value=\"{max}\"/></xsd:restriction>";
        static string Sized(string baseName, int length) =>
            $"<xsd:restriction base=\"sqltypes:{baseName}\"><xsd:maxLength value=\"{length.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"/></xsd:restriction>";

        return column.Type switch
        {
            XmlSqlType => ("sqltypes:xml", null),
            SqlVariantSqlType => ("xsd:anyType", null),
            HierarchyIdSqlType => (null, "<xsd:restriction base=\"sqltypes:nvarchar\" sqltypes:localeId=\"1033\" sqltypes:sqlCompareOptions=\"BinarySort\"><xsd:maxLength value=\"4000\"/></xsd:restriction>"),
            Int32SqlType => ("sqltypes:int", null),
            BigIntSqlType => ("sqltypes:bigint", null),
            SmallIntSqlType => ("sqltypes:smallint", null),
            TinyIntSqlType => ("sqltypes:tinyint", null),
            BitSqlType => ("sqltypes:bit", null),
            DecimalSqlType d => (null, $"<xsd:restriction base=\"sqltypes:{(column.ReportsNumeric ? "numeric" : "decimal")}\"><xsd:totalDigits value=\"{d.precision.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"/><xsd:fractionDigits value=\"{d.scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"/></xsd:restriction>"),
            MoneySqlType => ("sqltypes:money", null),
            SmallMoneySqlType => ("sqltypes:smallmoney", null),
            FloatSqlType => ("sqltypes:float", null),
            RealSqlType => ("sqltypes:real", null),
            DateTimeSqlType => ("sqltypes:datetime", null),
            SmallDateTimeSqlType => ("sqltypes:smalldatetime", null),
            DateSqlType => ("sqltypes:date", null),
            TimeSqlType t => (null, Patterned("time", TimePart(t.precision), "12:59:59" + Nines(t.precision))),
            DateTime2SqlType t => (null, Patterned("datetime2", DatePart + TimePart(t.precision), "9999-12-31T12:59:59" + Nines(t.precision))),
            DateTimeOffsetSqlType t => (null, Patterned("datetimeoffset", DatePart + TimePart(t.precision) + OffsetPart, "9999-12-31T12:59:59" + Nines(t.precision) + "Z")),
            UniqueIdentifierSqlType => ("sqltypes:uniqueidentifier", null),
            RowVersionSqlType => ("sqltypes:timestamp", null),
            ImageSqlType => ("sqltypes:image", null),
            BinarySqlType b => (null, Sized("binary", b.length)),
            VarbinarySqlType { length: SqlType.MaxLengthSentinel } => ("sqltypes:varbinary", null),
            VarbinarySqlType v => (null, Sized("varbinary", v.length)),
            CharSqlType c => (null, StringRestriction("char", c, c.length)),
            VarcharSqlType v => (null, StringRestriction("varchar", v, v.length)),
            NCharSqlType c => (null, StringRestriction("nchar", c, c.length)),
            NVarcharSqlType v => (null, StringRestriction("nvarchar", v, v.length)),
            SystemNameSqlType s => (null, StringRestriction("nvarchar", s, 128)),
            TextSqlType t => (null, StringRestriction("text", t, SqlType.MaxLengthSentinel)),
            NTextSqlType t => (null, StringRestriction("ntext", t, SqlType.MaxLengthSentinel)),
            _ => throw new NotSupportedException($"FOR XML XMLSCHEMA over a {column.Type} column isn't modeled."),
        };
    }

    /// <summary>
    /// A string type's restriction: its collation's locale, comparison
    /// options, version, SQL sort order and supplementary-character flag, then
    /// its length unless it is a <c>max</c> or legacy LOB type.
    /// </summary>
    private static string StringRestriction(string baseName, SqlType type, int length)
    {
        var sb = new StringBuilder("<xsd:restriction base=\"sqltypes:").Append(baseName).Append('"');
        var collation = type.Collation ?? Collation.Baseline;
        if (Collation.TryGetMetrics(collation.Name, out var metrics))
        {
            var binary2 = collation.Name.Contains("_BIN2", StringComparison.OrdinalIgnoreCase);
            var binary = binary2 || collation.Name.EndsWith("_BIN", StringComparison.OrdinalIgnoreCase) || collation.Name.Contains("_BIN_", StringComparison.OrdinalIgnoreCase);
            string options;
            if (binary)
            {
                options = binary2 ? "BinarySort2" : "BinarySort";
            }
            else
            {
                List<string> words = [];
                if ((metrics.ComparisonStyle & 1) != 0)
                    words.Add("IgnoreCase");
                if ((metrics.ComparisonStyle & 2) != 0)
                    words.Add("IgnoreNonSpace");
                if ((metrics.ComparisonStyle & 0x10000) != 0)
                    words.Add("IgnoreKanaType");
                if ((metrics.ComparisonStyle & 0x20000) != 0)
                    words.Add("IgnoreWidth");
                options = words.Count == 0 ? "None" : string.Join(' ', words);
            }
            _ = sb.Append(" sqltypes:localeId=\"").Append(metrics.Lcid.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append("\" sqltypes:sqlCompareOptions=\"").Append(options).Append('"');
            if (metrics.Version > 0)
                _ = sb.Append(" sqltypes:sqlCollationVersion=\"").Append(metrics.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('"');
            if (Network.TdsCollationCodec.For(collation).SortId is > 0 and var sortId)
                _ = sb.Append(" sqltypes:sqlSortId=\"").Append(sortId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('"');
            if (!binary && (metrics.Version >= 3 || collation.Name.Contains("_SC", StringComparison.OrdinalIgnoreCase)))
                _ = sb.Append(" sqltypes:supplementaryCharacters=\"true\"");
        }
        return length is SqlType.MaxLengthSentinel or <= 0
            ? sb.Append("/>").ToString()
            : sb.Append("><xsd:maxLength value=\"").Append(length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("\"/></xsd:restriction>").ToString();
    }

    private string RenderXdr(string name, bool typed)
    {
        const string Declarations = " xmlns=\"urn:schemas-microsoft-com:xml-data\" xmlns:dt=\"urn:schemas-microsoft-com:datatypes\"";
        var sb = new StringBuilder("<Schema");
        _ = typed
            ? sb.Append(Declarations).Append(" name=\"").Append(name).Append('"')
            : sb.Append(" name=\"").Append(name).Append('"').Append(Declarations);
        _ = sb.Append('>');
        this.AppendXdrLevel(sb, 0);
        return sb.Append("</Schema>").ToString();
    }

    /// <summary>
    /// One level's <c>ElementType</c>: a child level's reference first, then
    /// its columns — declared inside as attributes, or as elements whose own
    /// <c>ElementType</c>s follow it — and then the child level's.
    /// </summary>
    private void AppendXdrLevel(StringBuilder sb, int index)
    {
        var level = this.levels[index];
        var child = index + 1 < this.levels.Length ? this.levels[index + 1] : null;
        var eltOnly = child is not null || this.elements;
        _ = sb.Append("<ElementType name=\"").Append(level.Name).Append(eltOnly ? "\" content=\"eltOnly\" model=\"closed\" order=\"many\">" : "\" content=\"empty\" model=\"closed\">");
        if (child is not null)
            _ = sb.Append("<element type=\"").Append(child.Name).Append("\" maxOccurs=\"*\"/>");
        if (this.elements)
        {
            foreach (var column in level.Columns)
                _ = sb.Append("<element type=\"").Append(column.Name).Append("\"/>");
            _ = sb.Append("</ElementType>");
            foreach (var column in level.Columns)
                _ = sb.Append("<ElementType name=\"").Append(column.Name).Append("\" content=\"textOnly\" model=\"closed\"").Append(XdrType(column.Type)).Append("/>");
        }
        else
        {
            foreach (var column in level.Columns)
                _ = sb.Append("<AttributeType name=\"").Append(column.Name).Append('"').Append(XdrType(column.Type)).Append("/>");
            foreach (var column in level.Columns)
                _ = sb.Append("<attribute type=\"").Append(column.Name).Append("\"/>");
            _ = sb.Append("</ElementType>");
        }
        if (child is not null)
            this.AppendXdrLevel(sb, index + 1);
    }

    /// <summary>Whether XDR has a datatype for <paramref name="type"/>; see <see cref="SimulatedSqlException.ForXmlXmlDataUnmappedType"/>.</summary>
    private static bool MapsToXdr(SqlType type) => type switch
    {
        DateSqlType or TimeSqlType or DateTime2SqlType or DateTimeOffsetSqlType or XmlSqlType or JsonSqlType or VectorSqlType => false,
        VarcharSqlType { length: SqlType.MaxLengthSentinel } or NVarcharSqlType { length: SqlType.MaxLengthSentinel } or VarbinarySqlType { length: SqlType.MaxLengthSentinel } => false,
        _ => true,
    };

    /// <summary>A column's <c>dt:type</c> attribute, empty for a <c>sql_variant</c>, which XDR declares untyped.</summary>
    private static string XdrType(SqlType type) => type switch
    {
        SqlVariantSqlType => "",
        Int32SqlType => " dt:type=\"i4\"",
        BigIntSqlType => " dt:type=\"i8\"",
        SmallIntSqlType => " dt:type=\"i2\"",
        TinyIntSqlType => " dt:type=\"ui1\"",
        BitSqlType => " dt:type=\"boolean\"",
        DecimalSqlType => " dt:type=\"number\"",
        MoneySqlType or SmallMoneySqlType => " dt:type=\"fixed.14.4\"",
        FloatSqlType => " dt:type=\"r8\"",
        RealSqlType => " dt:type=\"r4\"",
        DateTimeSqlType or SmallDateTimeSqlType => " dt:type=\"dateTime\"",
        UniqueIdentifierSqlType => " dt:type=\"uuid\"",
        BinarySqlType or VarbinarySqlType or ImageSqlType or RowVersionSqlType => " dt:type=\"bin.base64\"",
        _ => " dt:type=\"string\"",
    };
}

/// <summary>One element an inline schema declares: RAW's row, or one AUTO nesting level.</summary>
internal sealed class ForXmlSchemaLevel(string name, ForXmlSchemaColumn[] columns)
{
    public readonly string Name = name;
    public readonly ForXmlSchemaColumn[] Columns = columns;
}

/// <summary>One result column as an inline schema declares it.</summary>
internal readonly struct ForXmlSchemaColumn(string name, SqlType type, bool nullable, bool asElement, bool reportsNumeric)
{
    /// <summary>The column's name as the rows write it, escaped.</summary>
    public readonly string Name = name;
    public readonly SqlType Type = type;
    public readonly bool Nullable = nullable;

    /// <summary>Written as a child element — under ELEMENTS, and an <c>xml</c> value always.</summary>
    public readonly bool AsElement = asElement;

    /// <summary>A <c>numeric</c>-spelled decimal, which the schema names <c>numeric</c>.</summary>
    public readonly bool ReportsNumeric = reportsNumeric;
}
