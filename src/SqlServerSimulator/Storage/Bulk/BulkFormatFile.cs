using System.Globalization;
using System.Text;
using System.Xml.Linq;

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
    /// Parses a format file's bytes; null when they are neither form. A
    /// <c>SQLDECIMAL</c> / <c>SQLNUMERIC</c> field in a non-XML file is
    /// flagged through <paramref name="hasDecimal"/>, which an
    /// <c>OPENROWSET(BULK …)</c> refuses.
    /// </summary>
    public static BulkFormatFile? Parse(byte[] bytes, Collation collation, out bool hasDecimal)
    {
        hasDecimal = false;
        var text = DecodeText(bytes);
        return text.TrimStart().StartsWith('<') ? ParseXml(text, collation) : ParseNonXml(text, collation, ref hasDecimal);
    }

    private static string DecodeText(byte[] bytes) =>
        bytes is [0xFF, 0xFE, ..] ? Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)
        : bytes is [0xEF, 0xBB, 0xBF, ..] ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
        : Encoding.UTF8.GetString(bytes);

    /// <summary>
    /// The non-XML form: a version line, a field count, then per field its
    /// order, host type, prefix length, data length, quoted terminator,
    /// server column order (0 to skip), server column name and collation.
    /// </summary>
    private static BulkFormatFile? ParseNonXml(string text, Collation collation, ref bool hasDecimal)
    {
        var tokens = Tokenize(text);
        if (tokens.Count < 2 || !int.TryParse(tokens[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || tokens.Count < 2 + (count * 8))
            return null;
        var fields = new BulkField[count];
        var columns = new SortedDictionary<int, (string Name, SqlType Type)>();
        for (var i = 0; i < count; i++)
        {
            var at = 2 + (i * 8);
            var hostName = tokens[at + 1].ToUpperInvariant();
            if (!int.TryParse(tokens[at + 2], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix)
                || !int.TryParse(tokens[at + 3], NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                || !int.TryParse(tokens[at + 5], NumberStyles.None, CultureInfo.InvariantCulture, out var serverOrder))
            {
                return null;
            }
            var name = tokens[at + 6];
            hasDecimal |= hostName is "SQLDECIMAL" or "SQLNUMERIC";
            var terminator = UnescapeTerminator(tokens[at + 4], wide: hostName is "SQLNCHAR" or "SQLNVARCHAR" or "SQLNTEXT");
            var (host, nativeType, rowsetType) = HostFor(hostName, length, prefix, collation);
            fields[i] = new BulkField(host, nativeType, prefix, length, BulkField.EncodeTerminator(host, terminator), serverOrder - 1, name);
            if (serverOrder > 0)
                columns[serverOrder] = (name, rowsetType);
        }
        return new BulkFormatFile(fields, [.. columns.Values.Select(c => c.Name)], [.. columns.Values.Select(c => c.Type)]);
    }

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

    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>
    /// The XML form: a <c>RECORD</c> of <c>FIELD</c>s (<c>CharTerm</c>,
    /// <c>CharFixed</c>, <c>CharPrefix</c>, their <c>NChar</c> siblings,
    /// <c>NativeFixed</c>, <c>NativePrefix</c>) and a <c>ROW</c> of
    /// <c>COLUMN</c>s, each naming its field by <c>SOURCE</c> and typing the
    /// column it becomes.
    /// </summary>
    private static BulkFormatFile? ParseXml(string text, Collation collation)
    {
        XElement root;
        try
        {
            root = XElement.Parse(text);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
        var record = root.Elements().FirstOrDefault(e => e.Name.LocalName == "RECORD");
        var row = root.Elements().FirstOrDefault(e => e.Name.LocalName == "ROW");
        if (record is null || row is null)
            return null;

        var fieldLengths = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var field in record.Elements().Where(e => e.Name.LocalName == "FIELD"))
        {
            if (((int?)field.Attribute("MAX_LENGTH") ?? (int?)field.Attribute("LENGTH")) is { } fieldLength)
                fieldLengths[(string?)field.Attribute("ID") ?? ""] = fieldLength;
        }
        var columnsBySource = new Dictionary<string, (int Ordinal, XElement Column)>(StringComparer.Ordinal);
        var ordinal = 0;
        var names = new List<string>();
        var types = new List<SqlType>();
        foreach (var column in row.Elements().Where(e => e.Name.LocalName == "COLUMN"))
        {
            var source = (string?)column.Attribute("SOURCE") ?? "";
            var sqlType = XmlColumnType(column, fieldLengths.TryGetValue(source, out var sourceLength) ? sourceLength : null, collation);
            columnsBySource[source] = (ordinal++, column);
            names.Add((string?)column.Attribute("NAME") ?? "");
            types.Add(sqlType);
        }

        var fields = new List<BulkField>();
        foreach (var field in record.Elements().Where(e => e.Name.LocalName == "FIELD"))
        {
            var id = (string?)field.Attribute("ID") ?? "";
            var kind = (string?)field.Attribute(Xsi + "type") ?? "";
            var terminator = UnescapeTerminator((string?)field.Attribute("TERMINATOR") ?? "");
            var length = (int?)field.Attribute("LENGTH") ?? (int?)field.Attribute("MAX_LENGTH") ?? 0;
            var prefix = (int?)field.Attribute("PREFIX_LENGTH") ?? 0;
            var target = columnsBySource.TryGetValue(id, out var mapped) ? mapped.Ordinal : -1;
            var name = target >= 0 ? names[target] : "";
            var nativeType = target >= 0 ? types[target] : SqlType.VarbinaryMax;
            var built = kind switch
            {
                "CharFixed" => new BulkField(BulkHostType.Char, null, 0, length, [], target, name),
                "CharPrefix" => new BulkField(BulkHostType.Char, null, prefix, length, [], target, name),
                "CharTerm" => new BulkField(BulkHostType.Char, null, 0, length, BulkField.EncodeTerminator(BulkHostType.Char, terminator), target, name),
                "NCharFixed" => new BulkField(BulkHostType.NChar, null, 0, length, [], target, name),
                "NCharPrefix" => new BulkField(BulkHostType.NChar, null, prefix, length, [], target, name),
                "NCharTerm" => new BulkField(BulkHostType.NChar, null, 0, length, BulkField.EncodeTerminator(BulkHostType.NChar, terminator), target, name),
                "NativeFixed" => NativeField(nativeType, 0, length, target, name),
                "NativePrefix" => NativeField(nativeType, prefix, length, target, name),
                _ => throw new NotSupportedException($"The XML format file field type '{kind}' isn't modeled."),
            };
            fields.Add(built);
        }
        return new BulkFormatFile([.. fields], [.. names], [.. types]);

        static BulkField NativeField(SqlType type, int prefix, int length, int target, string name) => type switch
        {
            VarbinarySqlType or BinarySqlType or ImageSqlType => new(BulkHostType.Binary, null, prefix, length, [], target, name),
            CharSqlType or VarcharSqlType or TextSqlType => new(BulkHostType.Char, null, prefix, length, [], target, name),
            NCharSqlType or NVarcharSqlType or NTextSqlType => new(BulkHostType.NChar, null, prefix, length, [], target, name),
            _ => new(BulkHostType.Native, type, prefix, length, [], target, name),
        };
    }

    /// <summary>
    /// An XML <c>COLUMN</c>'s <c>xsi:type</c> (with <c>LENGTH</c>,
    /// <c>PRECISION</c>, <c>SCALE</c>) as a column type; a string or binary
    /// one without a <c>LENGTH</c> takes its field's maximum length, in
    /// characters (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    private static SqlType XmlColumnType(XElement column, int? fieldLength, Collation collation)
    {
        var kind = ((string?)column.Attribute(Xsi + "type") ?? "").ToUpperInvariant();
        var length = (int?)column.Attribute("LENGTH") ?? fieldLength;
        return kind switch
        {
            "SQLCHAR" or "SQLVARYCHAR" => VarcharSqlType.Get(length is { } n and <= 8000 ? n : SqlType.MaxLengthSentinel, collation.ForVarcharStorage(), Coercibility.Implicit),
            "SQLTEXT" => SqlType.Text,
            "SQLNCHAR" or "SQLNVARCHAR" => NVarcharSqlType.Get(length is { } n and <= 4000 ? n : SqlType.MaxLengthSentinel, collation, Coercibility.Implicit),
            "SQLNTEXT" => SqlType.NText,
            "SQLBINARY" or "SQLVARYBIN" => VarbinarySqlType.Get(length is { } n and <= 8000 ? n : SqlType.MaxLengthSentinel),
            "SQLIMAGE" => SqlType.Image,
            "SQLVARIANT" => SqlType.SqlVariant,
            _ => NativeTypeFor(kind, (int?)column.Attribute("PRECISION") ?? 18, (int?)column.Attribute("SCALE") ?? 0),
        };
    }
}
