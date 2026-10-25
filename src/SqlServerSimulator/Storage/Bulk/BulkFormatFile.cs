using System.Globalization;
using System.Text;
using System.Xml;

namespace SqlServerSimulator.Storage.Bulk;

/// <summary>
/// A bcp format file, non-XML or XML, read into the <see cref="BulkField"/>s
/// it describes plus the columns an <c>OPENROWSET(BULK …)</c> over it
/// exposes.
/// </summary>
internal sealed class BulkFormatFile(BulkField[] fields, string[] columnNames, SqlType[] columnTypes)
{
    /// <summary>The data file's fields, in file order.</summary>
    public readonly BulkField[] Fields = fields;

    /// <summary>The rowset's columns, in <see cref="BulkField.Target"/> order.</summary>
    public readonly string[] ColumnNames = columnNames;

    /// <summary>The rowset's column types, parallel to <see cref="ColumnNames"/>.</summary>
    public readonly SqlType[] ColumnTypes = columnTypes;

    /// <summary>
    /// Parses a format file's bytes, read from <paramref name="path"/>, as
    /// real reads one compiling the statement naming it: the non-XML form
    /// when its first token leads with a number above zero — <c>14.0x</c>
    /// does, and is an unknown version — and the XML form otherwise, a word,
    /// <c>0</c> or <c>-5</c> included, each refused where real refuses it
    /// (probed 2026-10-25 against SQL Server 2025). A <c>SQLDECIMAL</c> /
    /// <c>SQLNUMERIC</c> field in a non-XML file, or an XML column of either
    /// with a zero <c>PRECISION</c>, is flagged through
    /// <paramref name="hasDecimal"/>, which an <c>OPENROWSET(BULK …)</c>
    /// refuses.
    /// </summary>
    public static BulkFormatFile Parse(byte[] bytes, string path, Collation collation, out bool hasDecimal)
    {
        hasDecimal = false;
        if (bytes.Length == 0)
            throw SimulatedSqlException.BulkFormatFileIncomplete(path, empty: true);
        var text = DecodeText(bytes);
        var tokens = Tokenize(text);
        return tokens.Count != 0 && LeadsWithPositiveNumber(tokens[0]) ? ParseNonXml(tokens, path, collation, ref hasDecimal) : ParseXml(text, path, collation, ref hasDecimal);
    }

    private static string DecodeText(byte[] bytes) =>
        bytes is [0xFF, 0xFE, ..] ? Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)
        : bytes is [0xEF, 0xBB, 0xBF, ..] ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
        : Encoding.UTF8.GetString(bytes);

    private static bool LeadsWithPositiveNumber(string token)
    {
        var sawPoint = false;
        for (var at = token.StartsWith('+') ? 1 : 0; at < token.Length; at++)
        {
            if (token[at] == '.' && !sawPoint)
                sawPoint = true;
            else if (!char.IsAsciiDigit(token[at]))
                return false;
            else if (token[at] != '0')
                return true;
        }
        return false;
    }

    /// <summary>
    /// The non-XML form, read token by token as real reads it: a version —
    /// <c>6.0</c>, <c>7.0</c> and <c>17.0</c> lay a field out in seven
    /// tokens, <c>8.0</c> to <c>16.0</c> in eight, the eighth its collation —
    /// a field count, then per field its order, host type, prefix length, data
    /// length, quoted terminator, server column order (0 to skip) and server
    /// column name. Each token is refused as it's read and a file ending among
    /// them is Msg 4862; once every field has read, two fields loading one
    /// column are refused, then a collation that isn't one (probed 2026-10-25
    /// against SQL Server 2025).
    /// </summary>
    private static BulkFormatFile ParseNonXml(List<string> tokens, string path, Collation collation, ref bool hasDecimal)
    {
        var tokensPerField = tokens[0] switch
        {
            "10.0" or "11.0" or "12.0" or "13.0" or "14.0" or "15.0" or "16.0" or "8.0" or "9.0" => 8,
            "17.0" or "6.0" or "7.0" => 7,
            _ => throw SimulatedSqlException.BulkFormatFileUnknownVersion(path),
        };
        if (tokens.Count < 2 || !int.TryParse(tokens[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var count) || count <= 0)
            throw SimulatedSqlException.BulkFormatFileColumnCount(path);

        var at = 2;
        var specs = new List<(string HostName, int Prefix, int Length, string Terminator, int ServerOrder, string Name, string CollationName)>();
        for (var field = 1; field <= count; field++)
        {
            if (!int.TryParse(Next(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var order) || order != field)
                throw SimulatedSqlException.BulkFormatFileColumnNumber(path);
            var hostName = Next();
            if (!IsHostTypeName(hostName))
                throw SimulatedSqlException.BulkFormatFileDataType(field, path);
            if (!int.TryParse(Next(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var prefix) || prefix is not (0 or 1 or 2 or 4 or 8))
                throw SimulatedSqlException.BulkFormatFilePrefix(field, path);
            if (!int.TryParse(Next(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var length) || length < 0)
                throw SimulatedSqlException.BulkFormatFileColumnLength(field, path);
            var terminator = Next();
            if (!int.TryParse(Next(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var serverOrder) || serverOrder < 0)
                throw SimulatedSqlException.BulkFormatFileDestinationColumn(field, path);
            specs.Add((hostName, prefix, length, terminator, serverOrder, Next(), tokensPerField == 8 ? Next() : ""));
        }
        // Of two fields loading one column, real names the first.
        var loadedBy = new Dictionary<int, int>();
        for (var i = 0; i < specs.Count; i++)
        {
            if (specs[i].ServerOrder > 0 && !loadedBy.TryAdd(specs[i].ServerOrder, i + 1))
                throw SimulatedSqlException.BulkFormatFileDestinationColumn(loadedBy[specs[i].ServerOrder], path);
        }
        for (var i = 0; i < specs.Count; i++)
        {
            if (!IsFormatFileCollation(specs[i].CollationName))
                throw SimulatedSqlException.BulkFormatFileCollation(i + 1, path);
        }

        var fields = new BulkField[specs.Count];
        var columns = new SortedDictionary<int, (string Name, SqlType Type)>();
        for (var i = 0; i < specs.Count; i++)
        {
            var (hostName, prefix, length, terminatorText, serverOrder, name, _) = specs[i];
            hasDecimal |= hostName is "SQLDECIMAL" or "SQLNUMERIC";
            var terminator = UnescapeTerminator(terminatorText, wide: hostName is "SQLNCHAR" or "SQLNVARCHAR" or "SQLNTEXT");
            var (host, nativeType, rowsetType) = HostFor(hostName, length, prefix, collation);
            fields[i] = new BulkField(host, nativeType, prefix, length, BulkField.EncodeTerminator(host, terminator), serverOrder - 1, name);
            if (serverOrder > 0)
                columns[serverOrder] = (name, rowsetType);
        }
        return new BulkFormatFile(fields, [.. columns.Values.Select(c => c.Name)], [.. columns.Values.Select(c => c.Type)]);

        string Next() => at < tokens.Count ? tokens[at++] : throw SimulatedSqlException.BulkFormatFileIncomplete(path, empty: false);
    }

    /// <summary>
    /// Whether <paramref name="name"/> is a bcp host type real knows, in its
    /// case — the non-XML form's host types and the XML form's column types
    /// are one list (probed 2026-10-25 against SQL Server 2025).
    /// </summary>
    private static bool IsHostTypeName(string name) => name is "SQLBIGINT" or "SQLBINARY" or "SQLBIT" or "SQLCHAR" or "SQLDATE"
        or "SQLDATETIM4" or "SQLDATETIM8" or "SQLDATETIME" or "SQLDATETIME2" or "SQLDATETIMEOFFSET" or "SQLDECIMAL" or "SQLFLT4"
        or "SQLFLT8" or "SQLIMAGE" or "SQLINT" or "SQLMONEY" or "SQLMONEY4" or "SQLNCHAR" or "SQLNTEXT" or "SQLNUMERIC"
        or "SQLNVARCHAR" or "SQLSMALLINT" or "SQLTEXT" or "SQLTIME" or "SQLTINYINT" or "SQLUDT" or "SQLUNIQUEID"
        or "SQLVARIANT" or "SQLVARYBIN" or "SQLVARYCHAR" or "SQLVECTOR";

    /// <summary>
    /// Whether a field's collation is one real takes: none, <c>RAW</c> in that
    /// case, or a collation's name in any (probed 2026-10-25 against SQL
    /// Server 2025).
    /// </summary>
    private static bool IsFormatFileCollation(string name) => name.Length == 0 || name == "RAW" || Collation.IsRecognized(name);

    /// <summary>Whitespace-separated tokens, a double-quoted one kept whole with its quotes stripped.</summary>
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }
            if (text[i] == '"')
            {
                var builder = new StringBuilder();
                for (i++; i < text.Length && text[i] != '"'; i++)
                {
                    // A backslash escapes the next character, a quote included.
                    if (text[i] == '\\' && i + 1 < text.Length)
                        _ = builder.Append('\\').Append(text[++i]);
                    else
                        _ = builder.Append(text[i]);
                }
                i++;
                tokens.Add(builder.ToString());
                continue;
            }
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
                i++;
            tokens.Add(text[start..i]);
        }
        return tokens;
    }

    /// <summary>
    /// A terminator as a format file or a <c>WITH</c> option writes it: the
    /// escapes <c>\t</c>, <c>\n</c>, <c>\r</c>, <c>\0</c>, <c>\\</c> and
    /// <c>\"</c>, or a <c>0x</c> hex string of bytes — UTF-16 code units
    /// when <paramref name="wide"/>.
    /// </summary>
    public static string UnescapeTerminator(string written, bool wide = false)
    {
        if (written.Length > 2 && written[0] == '0' && written[1] is 'x' or 'X' && written.Length % 2 == 0)
        {
            var hex = written.AsSpan(2);
            var allHex = true;
            foreach (var c in hex)
                allHex &= char.IsAsciiHexDigit(c);
            if (allHex)
            {
                var bytes = Convert.FromHexString(hex);
                return wide && bytes.Length % 2 == 0 ? Encoding.Unicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
            }
        }
        if (!written.Contains('\\', StringComparison.Ordinal))
            return written;
        var builder = new StringBuilder(written.Length);
        for (var i = 0; i < written.Length; i++)
        {
            if (written[i] != '\\' || i + 1 == written.Length)
            {
                _ = builder.Append(written[i]);
                continue;
            }
            var escaped = written[++i];
            _ = escaped switch
            {
                '"' or '\\' => builder.Append(escaped),
                '0' => builder.Append('\0'),
                'n' => builder.Append('\n'),
                'r' => builder.Append('\r'),
                't' => builder.Append('\t'),
                _ => builder.Append('\\').Append(escaped),
            };
        }
        return builder.ToString();
    }

    /// <summary>
    /// A non-XML host type's decoding and the rowset column it exposes:
    /// <c>SQLCHAR</c> as <c>varchar(n)</c>, <c>SQLNCHAR</c> as
    /// <c>nvarchar(n / 2)</c>, a binary as <c>varbinary(n)</c> — each
    /// <c>max</c> for a zero length — and a native type as itself.
    /// </summary>
    private static (BulkHostType Host, SqlType? Native, SqlType Rowset) HostFor(string hostName, int length, int prefix, Collation collation)
    {
        var max = length == 0 || prefix == 8;
        return hostName switch
        {
            "SQLCHAR" or "SQLVARYCHAR" or "SQLTEXT" => (BulkHostType.Char, null, VarcharSqlType.Get(max ? SqlType.MaxLengthSentinel : Math.Min(length, 8000), collation.ForVarcharStorage(), Coercibility.Implicit)),
            "SQLNCHAR" or "SQLNVARCHAR" or "SQLNTEXT" => (BulkHostType.NChar, null, NVarcharSqlType.Get(max ? SqlType.MaxLengthSentinel : Math.Clamp(length / 2, 1, 4000), collation, Coercibility.Implicit)),
            "SQLBINARY" or "SQLVARYBIN" or "SQLIMAGE" or "SQLUDT" => (BulkHostType.Binary, null, VarbinarySqlType.Get(max ? SqlType.MaxLengthSentinel : Math.Min(length, 8000))),
            _ => NativeHost(NativeTypeFor(hostName, 18, 0)),
        };

        static (BulkHostType, SqlType?, SqlType) NativeHost(SqlType type) => (BulkHostType.Native, type, type);
    }

    /// <summary>The value type of a native host type name, <c>SQLINT</c> to <c>SQLDATETIMEOFFSET</c>.</summary>
    private static SqlType NativeTypeFor(string hostName, int precision, int scale) => hostName switch
    {
        "SQLBIGINT" => SqlType.BigInt,
        "SQLBIT" => SqlType.Bit,
        "SQLDATE" => SqlType.Date,
        "SQLDATETIM4" => SqlType.SmallDateTime,
        "SQLDATETIME" => SqlType.DateTime,
        "SQLDATETIME2" => SqlType.GetDateTime2(7),
        "SQLDATETIMEOFFSET" => SqlType.GetDateTimeOffset(7),
        "SQLDECIMAL" or "SQLNUMERIC" => SqlType.GetDecimal(precision, scale),
        "SQLFLT4" => SqlType.Real,
        "SQLFLT8" => SqlType.Float,
        "SQLINT" => SqlType.Int32,
        "SQLMONEY" => SqlType.Money,
        "SQLMONEY4" => SqlType.SmallMoney,
        "SQLSMALLINT" => SqlType.SmallInt,
        "SQLTIME" => SqlType.GetTime(7),
        "SQLTINYINT" => SqlType.TinyInt,
        "SQLUNIQUEID" => SqlType.UniqueIdentifier,
        _ => throw new NotSupportedException($"The bcp host data type '{hostName}' isn't modeled."),
    };

    private const string FormatNamespace = "http://schemas.microsoft.com/sqlserver/2004/bulkload/format";

    private const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>An XML <c>FIELD</c>, as its attributes read.</summary>
    private sealed class XmlField(string id, string kind, int? length, int? maxLength, int? prefix, string? terminator, string? collationName)
    {
        public readonly string Id = id;
        public readonly string Kind = kind;
        public readonly int? Length = length;
        public readonly int? MaxLength = maxLength;
        public readonly int? Prefix = prefix;
        public readonly string? Terminator = terminator;
        public readonly string? CollationName = collationName;
    }

    /// <summary>An XML <c>COLUMN</c>, as its attributes read.</summary>
    private sealed class XmlColumn(string source, string name, string? kind, int? length, int? precision, int? scale)
    {
        public readonly string Source = source;
        public readonly string Name = name;
        public readonly string? Kind = kind;
        public readonly int? Length = length;
        public readonly int? Precision = precision;
        public readonly int? Scale = scale;
    }

    /// <summary>
    /// The XML form: a <c>RECORD</c> of <c>FIELD</c>s (<c>CharTerm</c>,
    /// <c>CharFixed</c>, <c>CharPrefix</c>, their <c>NChar</c> siblings,
    /// <c>NativeFixed</c>, <c>NativePrefix</c>) and a <c>ROW</c> of
    /// <c>COLUMN</c>s, each naming its field by <c>SOURCE</c> and typing the
    /// column it becomes. Real reads it as a stream, refusing what it meets
    /// in document order, and stops at the root element's end, so nothing
    /// after it is read; once the walk ends, a collation that isn't one is
    /// refused, then a file with no field or no column (probed 2026-10-25
    /// against SQL Server 2025).
    /// </summary>
    private static BulkFormatFile ParseXml(string text, string path, Collation collation, ref bool hasDecimal)
    {
        ReadXmlProlog(text);
        var fields = new List<XmlField>();
        var columns = new List<XmlColumn>();
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true,
            });
            ReadXmlRoot(reader, path, fields, columns);
        }
        catch (XmlException)
        {
            throw new NotSupportedException($"The XML parsing error real raises for the format file '{path}' isn't modeled.");
        }
        for (var i = 0; i < fields.Count; i++)
        {
            if (fields[i].CollationName is { } collationName && !IsFormatFileCollation(collationName))
                throw SimulatedSqlException.BulkFormatFileCollation(i + 1, path);
        }
        if (fields.Count == 0 || columns.Count == 0)
            throw SimulatedSqlException.BulkFormatFileColumnCount(path);
        hasDecimal = columns.Exists(column => column.Kind is "SQLDECIMAL" or "SQLNUMERIC" && column.Precision == 0);

        var targets = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new string[columns.Count];
        var types = new SqlType[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            targets[column.Source] = i;
            names[i] = column.Name;
            types[i] = XmlColumnType(column, fields.Find(field => field.Id == column.Source)!, collation);
        }

        var built = new BulkField[fields.Count];
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var terminator = UnescapeTerminator(field.Terminator ?? "");
            var length = field.Length ?? field.MaxLength ?? 0;
            var prefix = field.Prefix ?? 0;
            var target = targets.TryGetValue(field.Id, out var mapped) ? mapped : -1;
            var name = target >= 0 ? names[target] : "";
            var nativeType = target >= 0 ? types[target] : SqlType.VarbinaryMax;
            built[i] = field.Kind switch
            {
                "CharFixed" => new BulkField(BulkHostType.Char, null, 0, length, [], target, name),
                "CharPrefix" => new BulkField(BulkHostType.Char, null, prefix, length, [], target, name),
                "CharTerm" => new BulkField(BulkHostType.Char, null, 0, length, BulkField.EncodeTerminator(BulkHostType.Char, terminator), target, name),
                "NCharFixed" => new BulkField(BulkHostType.NChar, null, 0, length, [], target, name),
                "NCharPrefix" => new BulkField(BulkHostType.NChar, null, prefix, length, [], target, name),
                "NCharTerm" => new BulkField(BulkHostType.NChar, null, 0, length, BulkField.EncodeTerminator(BulkHostType.NChar, terminator), target, name),
                "NativeFixed" => NativeField(nativeType, 0, length, target, name),
                _ => NativeField(nativeType, prefix, length, target, name),
            };
        }
        return new BulkFormatFile(built, names, types);

        static BulkField NativeField(SqlType type, int prefix, int length, int target, string name) => type switch
        {
            VarbinarySqlType or BinarySqlType or ImageSqlType => new(BulkHostType.Binary, null, prefix, length, [], target, name),
            CharSqlType or VarcharSqlType or TextSqlType => new(BulkHostType.Char, null, prefix, length, [], target, name),
            NCharSqlType or NVarcharSqlType or NTextSqlType => new(BulkHostType.NChar, null, prefix, length, [], target, name),
            _ => new(BulkHostType.Native, type, prefix, length, [], target, name),
        };
    }

    /// <summary>
    /// Reads what may precede the root element — whitespace, the declaration,
    /// comments, processing instructions — refusing a document with no
    /// element (Msg 9435) and text ahead of one (Msg 9422), at the end of the
    /// text or the <c>&lt;</c> that ends it (probed 2026-10-25 against SQL
    /// Server 2025).
    /// </summary>
    private static void ReadXmlProlog(string text)
    {
        var at = 0;
        while (true)
        {
            while (at < text.Length && text[at] is ' ' or '\t' or '\r' or '\n')
                at++;
            string close;
            if (text.AsSpan(at).StartsWith("<?"))
                close = "?>";
            else if (text.AsSpan(at).StartsWith("<!--"))
                close = "-->";
            else
                break;
            var end = text.IndexOf(close, at + 2, StringComparison.Ordinal);
            if (end < 0)
                break;
            at = end + close.Length;
        }
        if (at == text.Length)
            throw XmlParsingError(text, text.Length, rootMissing: true);
        if (text[at] != '<')
        {
            var markup = text.IndexOf('<', at);
            throw XmlParsingError(text, markup < 0 ? text.Length : markup + 1, rootMissing: false);
        }
    }

    /// <summary>
    /// Msg 9422 or 9435 at the line and character real's reader stands on
    /// once it has read the first <paramref name="end"/> characters of
    /// <paramref name="text"/>: a line break starts the next line at
    /// character 0.
    /// </summary>
    private static SimulatedSqlException XmlParsingError(string text, int end, bool rootMissing)
    {
        var line = 1;
        var character = 0;
        for (var i = 0; i < end; i++)
        {
            if (text[i] == '\n' || (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')))
            {
                line++;
                character = 0;
            }
            else if (text[i] != '\r' && !(char.IsLowSurrogate(text[i]) && i > 0 && char.IsHighSurrogate(text[i - 1])))
            {
                character++;
            }
        }
        return SimulatedSqlException.BulkFormatFileXmlParsing(rootMissing, line, character);
    }

    /// <summary>
    /// Walks the root element: a <c>BCPFORMAT</c> holding a <c>RECORD</c>
    /// and then a <c>ROW</c>, all in the format namespace. A root in no
    /// namespace is an unexpected info item (Msg 4856), any other wrong one
    /// an unexpected element (Msg 4855); among the root's children, a
    /// misplaced <c>RECORD</c> or <c>ROW</c> is an unexpected element and
    /// anything else out of place — another element, the root's end where a
    /// child belongs — an unexpected info item (probed 2026-10-25 against SQL
    /// Server 2025).
    /// </summary>
    private static void ReadXmlRoot(XmlReader reader, string path, List<XmlField> fields, List<XmlColumn> columns)
    {
        var lines = (IXmlLineInfo)reader;
        _ = reader.MoveToContent();
        if (reader.NamespaceURI.Length == 0)
            throw SimulatedSqlException.BulkFormatFileUnexpectedInfoItem(lines.LineNumber, path);
        if (reader.NamespaceURI != FormatNamespace || reader.LocalName != "BCPFORMAT")
            throw SimulatedSqlException.BulkFormatFileUnexpectedElement(lines.LineNumber, path, reader.Name);
        if (reader.IsEmptyElement)
            throw SimulatedSqlException.BulkFormatFileUnexpectedInfoItem(lines.LineNumber, path);

        // The child read next: RECORD, ROW, then none.
        var expected = "RECORD";
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (expected is not null)
                    throw SimulatedSqlException.BulkFormatFileUnexpectedInfoItem(lines.LineNumber, path);
                return;
            }
            if (reader.NodeType != XmlNodeType.Element)
                continue;
            var inFormat = reader.NamespaceURI == FormatNamespace;
            if (inFormat && reader.LocalName == expected)
            {
                if (expected == "RECORD")
                {
                    ReadXmlItems(reader, path, "FIELD", fields, columns);
                    expected = "ROW";
                }
                else
                {
                    ReadXmlItems(reader, path, "COLUMN", fields, columns);
                    expected = null;
                }
                continue;
            }
            throw expected is not null && inFormat && reader.LocalName is "RECORD" or "ROW"
                ? SimulatedSqlException.BulkFormatFileUnexpectedElement(lines.LineNumber, path, reader.Name)
                : SimulatedSqlException.BulkFormatFileUnexpectedInfoItem(lines.LineNumber, path);
        }
    }

    /// <summary>
    /// Reads a <c>RECORD</c>'s <c>FIELD</c>s or a <c>ROW</c>'s
    /// <c>COLUMN</c>s, refusing any other element in it, or in an item, as
    /// unexpected (Msg 4855).
    /// </summary>
    private static void ReadXmlItems(XmlReader reader, string path, string item, List<XmlField> fields, List<XmlColumn> columns)
    {
        if (reader.IsEmptyElement)
            return;
        var lines = (IXmlLineInfo)reader;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read() && reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.NodeType != XmlNodeType.Element)
                continue;
            if (reader.NamespaceURI != FormatNamespace || reader.LocalName != item)
                throw SimulatedSqlException.BulkFormatFileUnexpectedElement(lines.LineNumber, path, reader.Name);
            if (item == "FIELD")
                fields.Add(ReadXmlField(reader, path, ids));
            else
                columns.Add(ReadXmlColumn(reader, path, fields, referenced));
            if (reader.IsEmptyElement)
                continue;
            while (reader.Read() && reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType == XmlNodeType.Element)
                    throw SimulatedSqlException.BulkFormatFileUnexpectedElement(lines.LineNumber, path, reader.Name);
            }
        }
    }

    /// <summary>
    /// A <c>FIELD</c>'s attributes, read as real reads them (probed
    /// 2026-10-25 against SQL Server 2025): in written order, a name no field
    /// takes refused (Msg 4857), a value that doesn't read refused (Msg 4858),
    /// and an <c>ID</c> an earlier field holds refused (Msg 4872); then the
    /// <c>xsi:type</c>, the <c>ID</c> and the attribute the type requires
    /// must be there (Msg 4859), and of the attributes the type doesn't take
    /// the first refused (Msg 4857) is <c>MAX_LENGTH</c>, then
    /// <c>TERMINATOR</c>, <c>PREFIX_LENGTH</c> and <c>COLLATION</c>, whatever
    /// order they're written in.
    /// </summary>
    private static XmlField ReadXmlField(XmlReader reader, string path, HashSet<string> ids)
    {
        var line = ((IXmlLineInfo)reader).LineNumber;
        string? id = null, kind = null, terminator = null, collationName = null;
        int? length = null, maxLength = null, prefix = null;
        for (var more = reader.MoveToFirstAttribute(); more; more = reader.MoveToNextAttribute())
        {
            var value = reader.Value;
            switch (XmlAttributeName(reader))
            {
                case "COLLATION":
                    collationName = value;
                    break;
                case "ID":
                    if (value.Length != 0 && !ids.Add(value))
                        throw SimulatedSqlException.BulkFormatFileDuplicateId(line, path, value);
                    id = value;
                    break;
                case "LENGTH":
                    length = XmlIntAttribute(reader, path, line, 1, int.MaxValue);
                    break;
                case "MAX_LENGTH":
                    maxLength = XmlIntAttribute(reader, path, line, 1, int.MaxValue);
                    break;
                case "PREFIX_LENGTH":
                    prefix = XmlIntAttribute(reader, path, line, 1, 8);
                    if (prefix is not (1 or 2 or 4 or 8))
                        throw SimulatedSqlException.BulkFormatFileBadAttribute(line, path, value, "PREFIX_LENGTH");
                    break;
                case "TERMINATOR":
                    terminator = value.Length != 0 ? value : throw SimulatedSqlException.BulkFormatFileBadAttribute(line, path, value, "TERMINATOR");
                    break;
                case "xsi:type":
                    kind = value is "CharFixed" or "CharPrefix" or "CharTerm" or "NCharFixed" or "NCharPrefix" or "NCharTerm" or "NativeFixed" or "NativePrefix"
                        ? value
                        : throw SimulatedSqlException.BulkFormatFileBadAttribute(line, path, value, "xsi:type");
                    break;
                case var other:
                    throw SimulatedSqlException.BulkFormatFileAttributeNotAllowed(line, path, other);
            }
        }
        _ = reader.MoveToElement();
        if (kind is null)
            throw SimulatedSqlException.BulkFormatFileMissingAttribute(line, path, "xsi:type");
        if (string.IsNullOrEmpty(id))
            throw SimulatedSqlException.BulkFormatFileMissingAttribute(line, path, "ID");
        var fixedWidth = kind is "CharFixed" or "NCharFixed" or "NativeFixed";
        var prefixed = kind is "CharPrefix" or "NCharPrefix" or "NativePrefix";
        var terminated = !fixedWidth && !prefixed;
        var missing = fixedWidth && length is null ? "LENGTH"
            : prefixed && prefix is null ? "PREFIX_LENGTH"
            : terminated && terminator is null ? "TERMINATOR"
            : null;
        if (missing is not null)
            throw SimulatedSqlException.BulkFormatFileMissingAttribute(line, path, missing);
        var refused = fixedWidth && maxLength is not null ? "MAX_LENGTH"
            : !terminated && terminator is not null ? "TERMINATOR"
            : !prefixed && prefix is not null ? "PREFIX_LENGTH"
            : kind is "NativeFixed" or "NativePrefix" && collationName is not null ? "COLLATION"
            : null;
        if (refused is not null)
            throw SimulatedSqlException.BulkFormatFileAttributeNotAllowed(line, path, refused);
        return new XmlField(id, kind, length, maxLength, prefix, terminator, collationName);
    }

    /// <summary>
    /// A <c>COLUMN</c>'s attributes, read as a <c>FIELD</c>'s are (probed
    /// 2026-10-25 against SQL Server 2025): a <c>SOURCE</c> naming no field
    /// is Msg 4873 and one an earlier column named Msg 4874; then the
    /// <c>SOURCE</c> must be there; <c>SCALE</c> belongs to the decimal pair
    /// and the three time types with fractional seconds, <c>PRECISION</c> to
    /// the decimal pair and <c>LENGTH</c> to the string and binary types —
    /// refused in that order, whatever order they're written in; and a time
    /// type's <c>SCALE</c> runs to 7, while a decimal one without a
    /// <c>PRECISION</c> at least as large is a bad value real reports as
    /// empty.
    /// </summary>
    private static XmlColumn ReadXmlColumn(XmlReader reader, string path, List<XmlField> fields, HashSet<string> referenced)
    {
        var line = ((IXmlLineInfo)reader).LineNumber;
        string? source = null, kind = null, scaleText = null;
        var name = "";
        int? length = null, precision = null, scale = null;
        for (var more = reader.MoveToFirstAttribute(); more; more = reader.MoveToNextAttribute())
        {
            var value = reader.Value;
            switch (XmlAttributeName(reader))
            {
                case "LENGTH":
                    length = XmlIntAttribute(reader, path, line, 1, int.MaxValue);
                    break;
                case "NAME":
                    name = value;
                    break;
                case "NULLABLE":
                    if (value is not ("NO" or "YES"))
                        throw SimulatedSqlException.BulkFormatFileBadAttribute(line, path, value, "NULLABLE");
                    break;
                case "PRECISION":
                    precision = XmlIntAttribute(reader, path, line, 0, 38);
                    break;
                case "SCALE":
                    scale = XmlIntAttribute(reader, path, line, 0, 38);
                    scaleText = value;
                    break;
                case "SOURCE":
                    if (!fields.Exists(field => field.Id == value))
                        throw SimulatedSqlException.BulkFormatFileUnknownIdReference(line, path, value);
                    if (!referenced.Add(value))
                        throw SimulatedSqlException.BulkFormatFileDuplicateIdReference(line, path, value);
                    source = value;
                    break;
                case "xsi:type":
                    kind = IsHostTypeName(value) ? value : throw SimulatedSqlException.BulkFormatFileBadAttribute(line, path, value, "xsi:type");
                    break;
                case var other:
                    throw SimulatedSqlException.BulkFormatFileAttributeNotAllowed(line, path, other);
            }
        }
        _ = reader.MoveToElement();
        if (source is null)
            throw SimulatedSqlException.BulkFormatFileMissingAttribute(line, path, "SOURCE");
        var decimalPair = kind is "SQLDECIMAL" or "SQLNUMERIC";
        var fractionalSeconds = kind is "SQLDATETIME2" or "SQLDATETIMEOFFSET" or "SQLTIME";
        var refused = !decimalPair && !fractionalSeconds && scale is not null ? "SCALE"
            : !decimalPair && precision is not null ? "PRECISION"
            : kind is not ("SQLBINARY" or "SQLCHAR" or "SQLNCHAR" or "SQLNVARCHAR" or "SQLVARYBIN" or "SQLVARYCHAR") && length is not null ? "LENGTH"
            : null;
        if (refused is not null)
            throw SimulatedSqlException.BulkFormatFileAttributeNotAllowed(line, path, refused);
        if (fractionalSeconds && scale > 7)
            throw SimulatedSqlException.BulkFormatFileBadAttribute(line, path, scaleText!, "SCALE");
        if (decimalPair && scale is not null && !(scale <= precision))
            throw SimulatedSqlException.BulkFormatFileBadAttribute(line, path, "", "SCALE");
        return new XmlColumn(source, name, kind, length, precision, scale);
    }

    /// <summary>
    /// An attribute's name as the checks read it: an unqualified one as
    /// written, <c>xsi:type</c> whatever its prefix, a namespace declaration
    /// as the empty string — real names one so in Msg 4857 — and any other
    /// qualified one as written.
    /// </summary>
    private static string XmlAttributeName(XmlReader reader) => reader.NamespaceURI switch
    {
        "" => reader.LocalName,
        "http://www.w3.org/2000/xmlns/" => "",
        XsiNamespace when reader.LocalName == "type" => "xsi:type",
        _ => reader.Name,
    };

    /// <summary>The current attribute's <c>int</c>, refused as a bad value (Msg 4858) outside <paramref name="minimum"/> to <paramref name="maximum"/>.</summary>
    private static int XmlIntAttribute(XmlReader reader, string path, int line, int minimum, int maximum) =>
        int.TryParse(reader.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) && value >= minimum && value <= maximum
            ? value
            : throw SimulatedSqlException.BulkFormatFileBadAttribute(line, path, reader.Value, XmlAttributeName(reader));

    /// <summary>
    /// An XML <c>COLUMN</c>'s <c>xsi:type</c> (with <c>LENGTH</c>,
    /// <c>PRECISION</c>, <c>SCALE</c>) as a column type; a string or binary
    /// one without a <c>LENGTH</c> takes its field's maximum length, in
    /// characters (probed 2026-09-29 against SQL Server 2025). One without an
    /// <c>xsi:type</c> takes its field's: <c>varchar</c>, <c>nvarchar</c> of
    /// half its bytes, or <c>varbinary</c> for a native field, 8,000 bytes
    /// long when the field gives no length (probed 2026-10-25).
    /// </summary>
    private static SqlType XmlColumnType(XmlColumn column, XmlField field, Collation collation)
    {
        var fieldLength = field.MaxLength ?? field.Length;
        if (column.Kind is null)
        {
            var bytes = fieldLength is { } n ? (n <= 8000 ? n : SqlType.MaxLengthSentinel) : 8000;
            return field.Kind switch
            {
                "CharFixed" or "CharPrefix" or "CharTerm" => VarcharSqlType.Get(bytes, collation.ForVarcharStorage(), Coercibility.Implicit),
                "NCharFixed" or "NCharPrefix" or "NCharTerm" => NVarcharSqlType.Get(bytes == SqlType.MaxLengthSentinel ? bytes : Math.Max(bytes / 2, 1), collation, Coercibility.Implicit),
                _ => VarbinarySqlType.Get(bytes),
            };
        }
        var length = column.Length ?? fieldLength;
        return column.Kind switch
        {
            "SQLCHAR" or "SQLVARYCHAR" => VarcharSqlType.Get(length is { } n and <= 8000 ? n : SqlType.MaxLengthSentinel, collation.ForVarcharStorage(), Coercibility.Implicit),
            "SQLTEXT" => SqlType.Text,
            "SQLNCHAR" or "SQLNVARCHAR" => NVarcharSqlType.Get(length is { } n and <= 4000 ? n : SqlType.MaxLengthSentinel, collation, Coercibility.Implicit),
            "SQLNTEXT" => SqlType.NText,
            "SQLBINARY" or "SQLVARYBIN" => VarbinarySqlType.Get(length is { } n and <= 8000 ? n : SqlType.MaxLengthSentinel),
            "SQLIMAGE" => SqlType.Image,
            "SQLVARIANT" => SqlType.SqlVariant,
            "SQLDATETIME2" => SqlType.GetDateTime2(column.Scale ?? 7),
            "SQLDATETIMEOFFSET" => SqlType.GetDateTimeOffset(column.Scale ?? 7),
            "SQLTIME" => SqlType.GetTime(column.Scale ?? 7),
            // A zero PRECISION reads as none, as real loads one.
            _ => NativeTypeFor(column.Kind ?? "", column.Precision is > 0 and var precision ? precision : 18, column.Scale ?? 0),
        };
    }
}
