using System.Text;
using System.Xml;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// The value of a sparse column set (<c>xml COLUMN_SET FOR
/// ALL_SPARSE_COLUMNS</c>), which stores nothing of its own: an element per
/// sparse column holding a value, in column order, each value written as
/// <c>FOR XML</c> writes it, and NULL when every sparse column is (probed
/// 2026-10-06 against SQL Server 2025). The column carries it as its
/// non-persisted computed expression, so every read computes it from the row.
/// </summary>
internal sealed class ColumnSetValue : Expression
{
    /// <summary>The table whose sparse columns the set covers, adopted when the table is built or the set added to it.</summary>
    public HeapTable? Table;

    public override SqlValue Run(RuntimeContext runtime)
    {
        if (this.Table is not { } table)
            return SqlValue.Null(SqlType.Xml);
        StringBuilder? content = null;
        foreach (var column in table.Columns)
        {
            if (!column.IsSparse)
                continue;
            var value = runtime.ResolveColumn(new MultiPartName(column.Name));
            if (value.IsNull)
                continue;
            var element = ForXmlName.Encode(column.Name);
            var text = Selection.ScalarForXmlText(value);
            content ??= new StringBuilder();
            if (text.Length == 0)
            {
                _ = content.Append('<').Append(element).Append(" />");
                continue;
            }
            _ = content.Append('<').Append(element).Append('>');
            Selection.AppendForXmlText(content, text, isAttribute: false);
            _ = content.Append("</").Append(element).Append('>');
        }
        return content is null ? SqlValue.Null(SqlType.Xml) : SqlValue.FromXml(content.ToString());
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Xml;

    internal override string DebugDisplay() => "COLUMN_SET FOR ALL_SPARSE_COLUMNS";

    internal override void Describe(NodeShape shape) { }

    /// <summary>
    /// Writes <paramref name="value"/>, as written to <paramref name="table"/>'s
    /// column set, into the sparse columns of <paramref name="row"/>: each
    /// element sets its column, and every sparse column it doesn't name becomes
    /// NULL. The content must be a sequence of attribute-free elements holding
    /// text — anything else is Msg 9524, an attribute Msg 9530 — naming each
    /// sparse column at most once (Msg 9525, else Msg 1911 state 201), with text
    /// converting to the column's type (Msg 9532). Element names match the
    /// columns' without regard to case (probed 2026-10-06 against SQL Server
    /// 2025).
    /// </summary>
    internal static void Write(HeapTable table, HeapColumn columnSet, SqlValue[] row, SqlValue value)
    {
        var columns = table.Columns;
        for (var i = 0; i < columns.Length; i++)
        {
            if (columns[i].IsSparse)
                row[i] = SqlValue.Null(columns[i].Type);
        }
        if (value.IsNull)
            return;

        var text = value.Type is XmlSqlType ? value.AsString : value.CoerceTo(SqlType.Xml).AsString;
        var seen = new HashSet<int>();
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment, DtdProcessing = DtdProcessing.Prohibit });
        _ = reader.Read();
        while (!reader.EOF)
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace or XmlNodeType.Comment or XmlNodeType.ProcessingInstruction:
                    _ = reader.Read();
                    continue;
                case XmlNodeType.Element:
                    break;
                default:
                    throw SimulatedSqlException.ColumnSetXmlMalformed();
            }

            var elementName = reader.LocalName;
            if (reader.MoveToFirstAttribute())
            {
                do
                {
                    if (reader.Prefix != "xmlns" && reader.LocalName != "xmlns")
                        throw SimulatedSqlException.ColumnSetXmlAttribute(columnSet.Name, reader.LocalName, elementName);
                }
                while (reader.MoveToNextAttribute());
                _ = reader.MoveToElement();
            }

            var ordinal = Array.FindIndex(columns, column => column.IsSparse && string.Equals(ForXmlName.Encode(column.Name), elementName, StringComparison.OrdinalIgnoreCase));
            if (ordinal < 0)
                throw SimulatedSqlException.ColumnSetXmlUnknownColumn(elementName);
            var column = columns[ordinal];
            if (!seen.Add(ordinal))
                throw SimulatedSqlException.ColumnSetXmlDuplicate(columnSet.Name, column.Name);

            var content = new StringBuilder();
            if (reader.IsEmptyElement)
            {
                _ = reader.Read();
            }
            else
            {
                _ = reader.Read();
                while (reader.NodeType != XmlNodeType.EndElement)
                {
                    switch (reader.NodeType)
                    {
                        case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                            _ = content.Append(reader.Value);
                            break;
                        case XmlNodeType.Comment or XmlNodeType.ProcessingInstruction:
                            break;
                        default:
                            throw SimulatedSqlException.ColumnSetXmlMalformed();
                    }
                    _ = reader.Read();
                }
                _ = reader.Read();
            }
            row[ordinal] = Convert(content.ToString(), column, columnSet);
        }
    }

    private static SqlValue Convert(string text, HeapColumn column, HeapColumn columnSet)
    {
        try
        {
            return column.Type is BinarySqlType or VarbinarySqlType
                ? SqlValue.FromVarbinary(System.Convert.FromBase64String(text)).CoerceTo(column.Type)
                : SqlValue.FromNVarchar(text).CoerceTo(column.Type);
        }
        catch (Exception failure) when (failure is SimulatedSqlException or FormatException or OverflowException or ArgumentException)
        {
            throw SimulatedSqlException.ColumnSetXmlConversion(columnSet.Name, column.TypeName, column.Name);
        }
    }
}
