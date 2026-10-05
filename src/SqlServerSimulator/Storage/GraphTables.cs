using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlServerSimulator.Parser.Expressions;

namespace SqlServerSimulator.Storage;

/// <summary>What <c>CREATE TABLE … AS NODE | AS EDGE</c> made a table: <c>sys.tables.is_node</c> / <c>is_edge</c>.</summary>
internal enum GraphTableKind : byte
{
    None,
    Node,
    Edge,
}

/// <summary>
/// The internal column a node or edge table carries, numbered as
/// <c>sys.columns.graph_type</c> reports it (probed 2026-09-27 against SQL
/// Server 2025). The three <c>…Computed</c> kinds plus
/// <see cref="GraphIdComputed"/> are the <c>$node_id</c> / <c>$edge_id</c> /
/// <c>$from_id</c> / <c>$to_id</c> pseudo-columns; the rest are hidden and
/// store what the pseudo-columns render.
/// </summary>
internal enum GraphColumnKind : byte
{
    None = 0,
    GraphId = 1,
    GraphIdComputed = 2,
    FromId = 3,
    FromObjId = 4,
    FromIdComputed = 5,
    ToId = 6,
    ToObjId = 7,
    ToIdComputed = 8,
}

/// <summary>
/// The node / edge table column set and the JSON identifiers its pseudo-columns
/// read and write. See <c>docs/claude/graph.md</c>.
/// </summary>
internal static class GraphColumns
{
    public const string NodeId = "$node_id";
    public const string EdgeId = "$edge_id";
    public const string FromId = "$from_id";
    public const string ToId = "$to_id";

    /// <summary>The 32 hex digits real appends to every internal column's name.</summary>
    private const int SuffixLength = 32;

    /// <summary>
    /// <c>sys.columns.graph_type_desc</c> for <paramref name="kind"/>, null for an
    /// ordinary column.
    /// </summary>
    public static string? Describe(GraphColumnKind kind) => kind switch
    {
        GraphColumnKind.GraphId => "GRAPH_ID",
        GraphColumnKind.GraphIdComputed => "GRAPH_ID_COMPUTED",
        GraphColumnKind.FromId => "GRAPH_FROM_ID",
        GraphColumnKind.FromObjId => "GRAPH_FROM_OBJ_ID",
        GraphColumnKind.FromIdComputed => "GRAPH_FROM_ID_COMPUTED",
        GraphColumnKind.ToId => "GRAPH_TO_ID",
        GraphColumnKind.ToObjId => "GRAPH_TO_OBJ_ID",
        GraphColumnKind.ToIdComputed => "GRAPH_TO_ID_COMPUTED",
        _ => null,
    };

    /// <summary>
    /// Whether <paramref name="leaf"/> is a written pseudo-column
    /// (<c>$node_id</c>, …) that names the internal column
    /// <paramref name="columnName"/> — the pseudo name, an underscore and the
    /// 32-digit suffix. A derived table or view that projects the column keeps
    /// its name, so the pseudo-column reaches through it too.
    /// </summary>
    public static bool IsPseudoColumnFor(string columnName, string leaf) =>
        leaf.Length > 0
        && leaf[0] == '$'
        && columnName.Length == leaf.Length + 1 + SuffixLength
        && columnName[leaf.Length] == '_'
        && columnName.StartsWith(leaf, StringComparison.OrdinalIgnoreCase)
        && IsPseudoName(leaf);

    /// <summary>
    /// The position in <paramref name="columnNames"/> of the internal column the
    /// pseudo-column <paramref name="leaf"/> names, or -1.
    /// </summary>
    public static int FindPseudoColumn(string[] columnNames, string leaf)
    {
        for (var i = 0; i < columnNames.Length; i++)
        {
            if (IsPseudoColumnFor(columnNames[i], leaf))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// The ordinal of the pseudo-column a single-table write's
    /// <paramref name="name"/> spells — a bare <c>$node_id</c>, never a
    /// delimited one — or -1.
    /// </summary>
    public static int FindPseudoColumn(HeapTable table, Parser.MultiPartName name)
    {
        if (name.LeafDelimited || !name.Leaf.StartsWith('$'))
            return -1;
        for (var i = 0; i < table.Columns.Length; i++)
        {
            if (IsPseudoColumnFor(table.Columns[i].Name, name.Leaf))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Rewrites every pseudo-column reference under <paramref name="root"/>
    /// (<c>inserted.$node_id</c>) to name the internal column it reads, for the
    /// resolvers that match a single table's columns by name — an OUTPUT
    /// clause's.
    /// </summary>
    public static void BindPseudoReferences(Parser.ExpressionNode root, HeapColumn[] columns) =>
        root.Walk((node, _) =>
        {
            if (node is Reference { ReferencedName: var name } reference && !name.LeafDelimited && name.Leaf.StartsWith('$')
                && Array.Find(columns, column => IsPseudoColumnFor(column.Name, name.Leaf)) is { } column)
            {
                reference.ReferencedName = name.ImmediateQualifier is { } qualifier
                    ? new Parser.MultiPartName(qualifier).WithAddedPart(column.Name)
                    : new Parser.MultiPartName(column.Name);
            }
            return true;
        });

    /// <summary>
    /// The ordinal of the hidden <c>graph_id</c> a written <c>$node_id</c> /
    /// <c>$edge_id</c> keys an index or constraint by, or -1 when
    /// <paramref name="columnName"/> is neither or the columns carry no graph id.
    /// </summary>
    public static int IdentifierKeyOrdinal(IReadOnlyList<HeapColumn?> columns, string columnName)
    {
        if (!columnName.Equals(NodeId, StringComparison.OrdinalIgnoreCase) && !columnName.Equals(EdgeId, StringComparison.OrdinalIgnoreCase))
            return -1;
        var identifier = -1;
        var graphId = -1;
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i] is not { } column)
                continue;
            if (column.GraphKind == GraphColumnKind.GraphId)
                graphId = i;
            else if (IsPseudoColumnFor(column.Name, columnName))
                identifier = i;
        }
        return identifier >= 0 ? graphId : -1;
    }

    /// <summary>Whether <paramref name="leaf"/> spells one of the four pseudo-columns.</summary>
    public static bool IsPseudoName(string leaf) =>
        leaf.Equals(NodeId, StringComparison.OrdinalIgnoreCase)
        || leaf.Equals(EdgeId, StringComparison.OrdinalIgnoreCase)
        || leaf.Equals(FromId, StringComparison.OrdinalIgnoreCase)
        || leaf.Equals(ToId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the internal columns <c>AS NODE</c> / <c>AS EDGE</c> puts ahead of
    /// the declared ones, in real's order: <c>graph_id</c> and <c>$node_id</c>
    /// (or <c>$edge_id</c>), then for an edge <c>from_obj_id</c>,
    /// <c>from_id</c>, <c>$from_id</c>, <c>to_obj_id</c>, <c>to_id</c>,
    /// <c>$to_id</c>. Real suffixes each name with a fresh GUID; the suffix here
    /// is a hash of the table name and the column's kind, so a script's names
    /// are stable from run to run.
    /// </summary>
    public static HeapColumn[] Create(GraphTableKind kind, string tableName, Collation collation)
    {
        var identifierType = NVarcharSqlType.Get(1000, collation, Coercibility.Implicit);
        var graphId = Internal("graph_id", GraphColumnKind.GraphId, SqlType.BigInt, tableName);
        var self = Pseudo(
            kind == GraphTableKind.Node ? NodeId : EdgeId,
            GraphColumnKind.GraphIdComputed,
            identifierType,
            tableName,
            new GraphIdentifier(kind == GraphTableKind.Node ? GraphIdentifierKind.Node : GraphIdentifierKind.Edge, graphId.Name, null, identifierType),
            nullable: false);
        if (kind == GraphTableKind.Node)
            return [graphId, self];

        var fromObj = Internal("from_obj_id", GraphColumnKind.FromObjId, SqlType.Int32, tableName);
        var fromId = Internal("from_id", GraphColumnKind.FromId, SqlType.BigInt, tableName);
        var from = Pseudo(FromId, GraphColumnKind.FromIdComputed, identifierType, tableName, new GraphIdentifier(GraphIdentifierKind.From, fromId.Name, fromObj.Name, identifierType), nullable: true);
        var toObj = Internal("to_obj_id", GraphColumnKind.ToObjId, SqlType.Int32, tableName);
        var toId = Internal("to_id", GraphColumnKind.ToId, SqlType.BigInt, tableName);
        var to = Pseudo(ToId, GraphColumnKind.ToIdComputed, identifierType, tableName, new GraphIdentifier(GraphIdentifierKind.To, toId.Name, toObj.Name, identifierType), nullable: true);
        return [graphId, self, fromObj, fromId, from, toObj, toId, to];
    }

    private static HeapColumn Internal(string prefix, GraphColumnKind kind, SqlType type, string tableName) =>
        new(Suffixed(prefix, kind, tableName), type, maxLength: null, nullable: false, isHidden: true) { GraphKind = kind };

    private static HeapColumn Pseudo(string prefix, GraphColumnKind kind, NVarcharSqlType type, string tableName, GraphIdentifier expression, bool nullable) =>
        new(Suffixed(prefix, kind, tableName), type, maxLength: 1000, nullable, computedExpression: expression, computedDefinition: null) { GraphKind = kind };

    private static string Suffixed(string prefix, GraphColumnKind kind, string tableName)
    {
        var high = Fnv64(tableName, (byte)kind, 14695981039346656037UL);
        var low = Fnv64(tableName, (byte)(kind + 0x40), 0x9E3779B97F4A7C15UL);
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}_{high:X16}{low:X16}");
    }

    private static ulong Fnv64(string text, byte salt, ulong offset)
    {
        var hash = (offset ^ salt) * 1099511628211UL;
        foreach (var c in text)
            hash = (hash ^ char.ToUpperInvariant(c)) * 1099511628211UL;
        return hash;
    }

    /// <summary>Whether <paramref name="kind"/> is one of the hidden columns only the engine may read or write.</summary>
    public static bool IsInternal(GraphColumnKind kind) =>
        kind is GraphColumnKind.GraphId or GraphColumnKind.FromId or GraphColumnKind.FromObjId or GraphColumnKind.ToId or GraphColumnKind.ToObjId;

    /// <summary>The graph column of <paramref name="kind"/> in <paramref name="table"/>'s column list, or -1.</summary>
    public static int OrdinalOf(HeapTable table, GraphColumnKind kind)
    {
        var columns = table.Columns;
        for (var i = 0; i < columns.Length; i++)
        {
            if (columns[i].GraphKind == kind)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// The node or edge identifier JSON real renders — keys in fixed order, no
    /// whitespace, the schema and table names JSON-escaped.
    /// </summary>
    public static string Render(bool isEdge, string schemaName, string tableName, long id)
    {
        var builder = new StringBuilder(64)
            .Append(isEdge ? "{\"type\":\"edge\",\"schema\":\"" : "{\"type\":\"node\",\"schema\":\"");
        AppendEscaped(builder, schemaName);
        _ = builder.Append("\",\"table\":\"");
        AppendEscaped(builder, tableName);
        return builder.Append("\",\"id\":").Append(id.ToString(CultureInfo.InvariantCulture)).Append('}').ToString();
    }

    private static void AppendEscaped(StringBuilder builder, string text)
    {
        foreach (var c in text)
        {
            _ = c switch
            {
                '"' => builder.Append("\\\""),
                '\\' => builder.Append("\\\\"),
                '\b' => builder.Append("\\b"),
                '\f' => builder.Append("\\f"),
                '\n' => builder.Append("\\n"),
                '\r' => builder.Append("\\r"),
                '\t' => builder.Append("\\t"),
                < ' ' => builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)),
                _ => builder.Append(c),
            };
        }
    }

    /// <summary>The schema holding <paramref name="table"/>, or null when it isn't in <paramref name="database"/>.</summary>
    public static Schema? SchemaOf(Database database, HeapTable table)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            if (schema.SchemaId == table.SchemaId)
                return schema;
        }
        return null;
    }

    /// <summary>The node or edge table <paramref name="objectId"/> names in <paramref name="database"/>, or null.</summary>
    public static HeapTable? FindByObjectId(Database database, int objectId)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, table) in schema.HeapTables)
            {
                if (table.ObjectId == objectId)
                    return table.GraphKind == GraphTableKind.None ? null : table;
            }
        }
        return null;
    }

    /// <summary>
    /// The identifier <paramref name="table"/>'s row <paramref name="id"/> has,
    /// or null when the table isn't a node or edge table in
    /// <paramref name="database"/> — what <c>NODE_ID_FROM_PARTS</c> and the
    /// pseudo-columns render.
    /// </summary>
    public static string? RenderFor(Database database, HeapTable table, long id) =>
        table.GraphKind == GraphTableKind.None || SchemaOf(database, table) is not { } schema
            ? null
            : Render(table.GraphKind == GraphTableKind.Edge, schema.Name, table.Name, id);

    /// <summary>
    /// Reads an identifier the way the <c>*_FROM_NODE_ID</c> / <c>*_FROM_EDGE_ID</c>
    /// functions do: exactly the rendered shape — the four keys in order, no
    /// whitespace anywhere, an integer id — though key names and the
    /// <c>type</c> value match without regard to case and string escapes are
    /// decoded (probed 2026-09-27 against SQL Server 2025). The table must be a
    /// node table (<paramref name="edge"/> false) or an edge table in
    /// <paramref name="database"/>.
    /// </summary>
    public static bool TryParseStrict(string text, Database database, bool edge, out HeapTable table, out long id)
    {
        table = null!;
        id = 0;
        var reader = new StrictReader(text);
        if (!reader.Expect('{')
            || !reader.Key("type") || reader.String() is not { } type
            || !reader.Expect(',') || !reader.Key("schema") || reader.String() is not { } schema
            || !reader.Expect(',') || !reader.Key("table") || reader.String() is not { } name
            || !reader.Expect(',') || !reader.Key("id") || !reader.Integer(out id)
            || !reader.Expect('}') || !reader.AtEnd)
        {
            return false;
        }
        if (!type.Equals(edge ? "edge" : "node", StringComparison.OrdinalIgnoreCase)
            || Resolve(database, schema, name, edge ? GraphTableKind.Edge : GraphTableKind.Node) is not { } found)
        {
            return false;
        }
        table = found;
        return true;
    }

    /// <summary>
    /// Reads an identifier written into a pseudo-column by INSERT: any JSON
    /// object holding exactly the <c>type</c>, <c>schema</c>, <c>table</c> and
    /// <c>id</c> keys, in any order and with any whitespace, the id an integer
    /// literal (probed 2026-09-27 against SQL Server 2025).
    /// </summary>
    public static bool TryParseWritten(string text, Database database, bool edge, out HeapTable table, out long id)
    {
        table = null!;
        id = 0;
        string? type = null, schema = null, name = null;
        long? parsedId = null;
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = reader.GetString()!;
                if (!reader.Read())
                    return false;
                if (key.Equals("id", StringComparison.OrdinalIgnoreCase))
                {
                    if (reader.TokenType != JsonTokenType.Number || !IsIntegerLiteral(reader.ValueSpan) || !reader.TryGetInt64(out var value))
                        return false;
                    parsedId = value;
                    continue;
                }
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    return false;
                var stringValue = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                if (key.Equals("type", StringComparison.OrdinalIgnoreCase))
                    type = stringValue;
                else if (key.Equals("schema", StringComparison.OrdinalIgnoreCase))
                    schema = stringValue;
                else if (key.Equals("table", StringComparison.OrdinalIgnoreCase))
                    name = stringValue;
                else
                    return false;
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
                return false;
        }
        catch (JsonException)
        {
            return false;
        }
        if (type is null || schema is null || name is null || parsedId is not { } resolvedId
            || !type.Equals(edge ? "edge" : "node", StringComparison.OrdinalIgnoreCase)
            || Resolve(database, schema, name, edge ? GraphTableKind.Edge : GraphTableKind.Node) is not { } found)
        {
            return false;
        }
        table = found;
        id = resolvedId;
        return true;
    }

    private static bool IsIntegerLiteral(ReadOnlySpan<byte> number)
    {
        foreach (var b in number)
        {
            if (b is (byte)'.' or (byte)'e' or (byte)'E')
                return false;
        }
        return true;
    }

    private static HeapTable? Resolve(Database database, string schemaName, string tableName, GraphTableKind kind) =>
        // Names compare as SQL Server compares them, trailing spaces aside.
        database.Schemas.TryGetValue(schemaName.TrimEnd(' '), out var schema)
        && schema.HeapTables.TryGetValue(tableName.TrimEnd(' '), out var table)
        && table.GraphKind == kind
            ? table
            : null;

    /// <summary>A forward-only reader over the strict identifier shape.</summary>
    private ref struct StrictReader(string text)
    {
        private readonly string text = text;
        private int position;

        public readonly bool AtEnd => this.position == this.text.Length;

        public bool Expect(char c)
        {
            if (this.position >= this.text.Length || this.text[this.position] != c)
                return false;
            this.position++;
            return true;
        }

        public bool Key(string name) =>
            this.String() is { } key && key.Equals(name, StringComparison.OrdinalIgnoreCase) && this.Expect(':');

        public string? String()
        {
            if (!this.Expect('"'))
                return null;
            var builder = new StringBuilder();
            while (this.position < this.text.Length)
            {
                var c = this.text[this.position++];
                if (c == '"')
                    return builder.ToString();
                if (c != '\\')
                {
                    _ = builder.Append(c);
                    continue;
                }
                if (this.position >= this.text.Length)
                    return null;
                var escaped = this.text[this.position++];
                switch (escaped)
                {
                    case '"' or '\\' or '/':
                        _ = builder.Append(escaped);
                        break;
                    case 'b':
                        _ = builder.Append('\b');
                        break;
                    case 'f':
                        _ = builder.Append('\f');
                        break;
                    case 'n':
                        _ = builder.Append('\n');
                        break;
                    case 'r':
                        _ = builder.Append('\r');
                        break;
                    case 't':
                        _ = builder.Append('\t');
                        break;
                    case 'u' when this.position + 4 <= this.text.Length
                        && int.TryParse(this.text.AsSpan(this.position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code):
                        _ = builder.Append((char)code);
                        this.position += 4;
                        break;
                    default:
                        return null;
                }
            }
            return null;
        }

        public bool Integer(out long value)
        {
            var start = this.position;
            if (this.position < this.text.Length && this.text[this.position] == '-')
                this.position++;
            var digitsStart = this.position;
            while (this.position < this.text.Length && char.IsAsciiDigit(this.text[this.position]))
                this.position++;
            var digits = this.position - digitsStart;
            if (digits == 0 || (digits > 1 && this.text[digitsStart] == '0'))
            {
                value = 0;
                return false;
            }
            return long.TryParse(this.text.AsSpan(start, this.position - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }
    }
}
