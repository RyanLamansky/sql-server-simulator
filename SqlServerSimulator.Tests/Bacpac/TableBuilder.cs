using System.Xml.Linq;

namespace SqlServerSimulator.Bacpac;

/// <summary>
/// Per-table fluent surface returned by
/// <see cref="BacpacBuilder.Table(string, string, Action{TableBuilder})"/>.
/// Holds the column list + accumulated rows; emits the
/// <c>&lt;Element Type="SqlTable"&gt;</c> XML and the table's BCP wire
/// bytes on Build.
/// </summary>
public sealed class TableBuilder
{
    internal readonly string SchemaName;
    internal readonly string TableName;
    private readonly List<ColumnDef> _columns = [];
    private readonly List<object?[]> _rows = [];

    internal TableBuilder(string schemaName, string tableName)
    {
        SchemaName = schemaName;
        TableName = tableName;
    }

    internal IReadOnlyList<object?[]> Rows => _rows;
    internal int RowCount => _rows.Count;

    /// <summary>
    /// Adds a column. <paramref name="sqlType"/> is the raw SQL Server
    /// type name as it would appear in a CREATE TABLE column-type position
    /// (e.g. <c>"int"</c>, <c>"nvarchar(50)"</c>, <c>"decimal(18, 4)"</c>).
    /// Default nullability is NOT NULL — the bacpac convention is
    /// IsNullable=True default, but for test setup NOT NULL is more
    /// useful so explicit assertions on NULL-handling become opt-in.
    /// </summary>
    public TableBuilder Column(string name, string sqlType, bool nullable = false)
    {
        _columns.Add(new ColumnDef(name, sqlType, nullable));
        return this;
    }

    /// <summary>
    /// Adds one row to the table's BCP data. Value count must match
    /// the current column count; types must match the column types
    /// (e.g. <c>int</c> columns expect <c>int</c> values).
    /// </summary>
    public TableBuilder Row(params object?[] values)
    {
        if (values.Length != _columns.Count)
            throw new InvalidOperationException($"Row value count ({values.Length}) doesn't match column count ({_columns.Count}) on [{SchemaName}].[{TableName}].");
        _rows.Add(values);
        return this;
    }

    internal void WriteBcpRow(Stream stream, object?[] row)
    {
        for (var i = 0; i < _columns.Count; i++)
            BacpacBuilder.EncodeBcpValue(stream, _columns[i], row[i]);
    }

    internal XElement ToModelElement(XNamespace ns)
    {
        var columnsRelationship = new XElement(ns + "Relationship",
            new XAttribute("Name", "Columns"));
        foreach (var column in _columns)
        {
            columnsRelationship.Add(new XElement(ns + "Entry", ColumnElement(ns, column)));
        }

        return new XElement(ns + "Element",
            new XAttribute("Type", "SqlTable"),
            new XAttribute("Name", $"[{SchemaName}].[{TableName}]"),
            columnsRelationship);
    }

    private XElement ColumnElement(XNamespace ns, ColumnDef column)
    {
        var qualifiedName = $"[{SchemaName}].[{TableName}].[{column.Name}]";
        var element = new XElement(ns + "Element",
            new XAttribute("Type", "SqlSimpleColumn"),
            new XAttribute("Name", qualifiedName));

        if (!column.Nullable)
        {
            element.Add(new XElement(ns + "Property",
                new XAttribute("Name", "IsNullable"),
                new XAttribute("Value", "False")));
        }

        element.Add(new XElement(ns + "Relationship",
            new XAttribute("Name", "TypeSpecifier"),
            new XElement(ns + "Entry", TypeSpecifierElement(ns, column.SqlType))));
        return element;
    }

    private static XElement TypeSpecifierElement(XNamespace ns, string sqlType)
    {
        var (baseName, length, precision, scale, isMax) = ParseSqlType(sqlType);
        var element = new XElement(ns + "Element",
            new XAttribute("Type", "SqlTypeSpecifier"),
            new XElement(ns + "Relationship",
                new XAttribute("Name", "Type"),
                new XElement(ns + "Entry",
                    new XElement(ns + "References",
                        new XAttribute("Name", $"[{baseName}]"),
                        new XAttribute("ExternalSource", "BuiltIns")))));

        if (isMax)
            element.Add(PropertyElement(ns, "IsMax", "True"));
        if (length is not null)
            element.Add(PropertyElement(ns, "Length", length));
        if (precision is not null)
            element.Add(PropertyElement(ns, "Precision", precision));
        if (scale is not null)
            element.Add(PropertyElement(ns, "Scale", scale));
        return element;
    }

    private static XElement PropertyElement(XNamespace ns, string name, string value) =>
        new(ns + "Property",
            new XAttribute("Name", name),
            new XAttribute("Value", value));

    private static (string Base, string? Length, string? Precision, string? Scale, bool IsMax) ParseSqlType(string sqlType)
    {
        var trimmed = sqlType.Trim();
        var openParen = trimmed.IndexOf('(');
        if (openParen < 0)
            return (trimmed.ToLowerInvariant(), null, null, null, false);

        var baseName = trimmed[..openParen].Trim().ToLowerInvariant();
        var args = trimmed[(openParen + 1)..^1].Trim();
        if (string.Equals(args, "max", StringComparison.OrdinalIgnoreCase))
            return (baseName, null, null, null, true);

        var commaIndex = args.IndexOf(',');
        if (commaIndex < 0)
        {
            return baseName switch
            {
                "decimal" or "numeric" => (baseName, null, args.Trim(), null, false),
                "time" or "datetime2" or "datetimeoffset" => (baseName, null, null, args.Trim(), false),
                _ => (baseName, args.Trim(), null, null, false),
            };
        }
        var precision = args[..commaIndex].Trim();
        var scale = args[(commaIndex + 1)..].Trim();
        return (baseName, null, precision, scale, false);
    }
}

/// <summary>
/// Column metadata captured by <see cref="TableBuilder.Column(string, string, bool)"/>.
/// </summary>
internal readonly record struct ColumnDef(string Name, string SqlType, bool Nullable);
