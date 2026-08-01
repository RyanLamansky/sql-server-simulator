using System.Globalization;
using System.Text;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// FOR JSON serialization options; non-null only on the wrapper
    /// <see cref="Selection"/> produced by <see cref="ParseOptionalForJson"/>.
    /// Its presence marks a query whose result is a single JSON string, which
    /// an enclosing FOR JSON serializer embeds raw (not re-escaped) — the same
    /// role <see cref="JsonQuery"/> plays for the JSON_* builders.
    /// </summary>
    internal ForJsonOptions? ForJson;

    /// <summary>
    /// The projection expressions (one per output column), captured so the
    /// FOR JSON serializer can detect JSON-producing columns (nested FOR JSON /
    /// <c>JSON_QUERY</c> / <c>JSON_OBJECT</c> / <c>JSON_ARRAY</c>) and embed
    /// them as raw JSON. Null for shapes that don't route through the standard
    /// projection builders (set-op chains); such columns are all treated as
    /// non-raw (quoted), a documented degradation for the exotic-shape case.
    /// </summary>
    internal Expression[]? ProjectionExpressions;

    /// <summary>
    /// The fixed single-column name SQL Server assigns a top-level FOR JSON
    /// result set (a GUID-shaped sentinel; consumers concatenate the chunks).
    /// </summary>
    private const string ForJsonColumnName = "JSON_F52E2B61-18A1-11d1-B105-00805F49916B";

    /// <summary>
    /// Parses the trailing <c>FOR JSON { PATH | AUTO } [, ROOT[('name')]]
    /// [, INCLUDE_NULL_VALUES] [, WITHOUT_ARRAY_WRAPPER]</c> clause when the
    /// cursor sits on <c>FOR</c>, wrapping <paramref name="inner"/> in a
    /// serializer that projects the single JSON-string column. When the
    /// <c>FOR</c> is anything else (<c>FOR XML</c> / <c>FOR BROWSE</c>), the
    /// cursor is restored and <paramref name="inner"/> is returned unchanged so
    /// the downstream dispatch raises its own Msg 102. Leaves the cursor on the
    /// first token past the clause.
    /// </summary>
    internal static Selection ParseOptionalForJson(ParserContext context, Selection inner)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.For })
            return inner;

        var checkpoint = context.SaveCheckpoint();
        context.MoveNextRequired();
        if (context.Token is not Name jsonKeyword || !Collation.Baseline.Equals(jsonKeyword.Value, "JSON"))
        {
            // FOR XML / FOR BROWSE / FOR SYSTEM_TIME leftovers — not ours.
            context.RestoreCheckpoint(checkpoint);
            return inner;
        }

        var mode = context.GetNextRequired() is Name modeName && Collation.Baseline.Equals(modeName.Value, "AUTO")
            ? ForJsonMode.Auto
            : context.Token is Name pathName && Collation.Baseline.Equals(pathName.Value, "PATH")
                ? ForJsonMode.Path
                : throw SimulatedSqlException.SyntaxErrorNear(context);

        var includeNulls = false;
        var withoutArrayWrapper = false;
        var rootSpecified = false;
        string? rootName = null;

        context.MoveNextOptional();
        while (context.Token is Operator { Character: ',' })
        {
            if (context.GetNextRequired() is not Name optionName)
                throw SimulatedSqlException.SyntaxErrorNear(context);

            if (Collation.Baseline.Equals(optionName.Value, "ROOT"))
            {
                rootSpecified = true;
                rootName = "root";
                context.MoveNextOptional();
                if (context.Token is Operator { Character: '(' })
                {
                    if (context.GetNextRequired() is not Literal { Value.Type.Category: SqlTypeCategory.String } rootLiteral)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    rootName = rootLiteral.Value.AsString;
                    if (context.GetNextRequired() is not Operator { Character: ')' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextOptional();
                }
            }
            else if (Collation.Baseline.Equals(optionName.Value, "INCLUDE_NULL_VALUES"))
            {
                includeNulls = true;
                context.MoveNextOptional();
            }
            else if (Collation.Baseline.Equals(optionName.Value, "WITHOUT_ARRAY_WRAPPER"))
            {
                withoutArrayWrapper = true;
                context.MoveNextOptional();
            }
            else
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
        }

        return rootSpecified && withoutArrayWrapper
            ? throw SimulatedSqlException.ForJsonRootWithoutWrapperConflict()
            : WrapForJson(inner, new ForJsonOptions(mode, includeNulls, withoutArrayWrapper, rootSpecified ? rootName : null));
    }

    private static Selection WrapForJson(Selection inner, ForJsonOptions options)
    {
        // Every column needs a name (Msg 13605), checked once at parse.
        for (var i = 0; i < inner.ColumnNames.Length; i++)
        {
            if (inner.ColumnNames[i].Length == 0)
                throw SimulatedSqlException.ForJsonColumnWithoutName();
        }

        // Compile-time raw-embed detection per column (nested FOR JSON /
        // JSON_QUERY / JSON_OBJECT / JSON_ARRAY embed as raw JSON).
        var rawColumns = new bool[inner.ColumnNames.Length];
        if (inner.ProjectionExpressions is { } projection)
        {
            for (var i = 0; i < projection.Length && i < rawColumns.Length; i++)
                rawColumns[i] = ColumnProducesRawJson(projection[i]);
        }

        var schema = new SqlType[] { SqlType.NVarcharMax };
        var columnNames = new[] { ForJsonColumnName };
        var innerSchema = inner.Schema;

        if (options.Mode == ForJsonMode.Auto)
        {
            // AUTO nests each FROM source one level deeper as an array-valued
            // property keyed by the source's name; each level's own columns
            // are flat keys (dots are not split).
            var levels = BuildAutoLevels(inner, forJson: true);
            var levelNodes = new List<ForJsonNode>[levels.Length];
            for (var i = 0; i < levels.Length; i++)
            {
                levelNodes[i] = [];
                foreach (var column in levels[i].Columns)
                    levelNodes[i].Add(new ForJsonNode(inner.ColumnNames[column], column, null));
            }

            return new Selection(schema, columnNames,
                hasOrderBy: false,
                hasTopOrOffsetOrFetch: false,
                (batch, outerResolver) => SerializeForJsonAuto(inner, innerSchema, levels, levelNodes, rawColumns, options, batch, outerResolver))
            {
                ForJson = options,
            };
        }

        // PATH splits dotted aliases into a contiguity-checked nesting tree.
        var root = new List<ForJsonNode>();
        for (var i = 0; i < inner.ColumnNames.Length; i++)
        {
            var name = inner.ColumnNames[i];
            InsertForJsonPath(root, name.Split('.'), 0, i, name);
        }

        return new Selection(schema, columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => SerializeForJson(inner, innerSchema, root, rawColumns, options, batch, outerResolver))
        {
            ForJson = options,
        };
    }

    private static IEnumerable<byte[]> SerializeForJson(
        Selection inner, SqlType[] innerSchema, List<ForJsonNode> root, bool[] rawColumns,
        ForJsonOptions options, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var body = new StringBuilder();
        var any = false;
        foreach (var rowBytes in inner.Execute(batch, outerResolver).RowBytes)
        {
            if (any)
                _ = body.Append(',');
            any = true;
            _ = RenderForJsonObject(body, root, rowBytes, innerSchema, rawColumns, options.IncludeNulls);
        }

        // Empty input rowset → no output row at all (a scalar subquery then
        // yields SQL NULL, matching real SQL Server).
        if (!any)
            yield break;

        yield return ForJsonRow(body, options);
    }

    /// <summary>
    /// Wraps the comma-separated per-row objects in the array wrapper (unless
    /// <c>WITHOUT_ARRAY_WRAPPER</c>) and the <c>ROOT</c> object, then encodes
    /// the whole document as the single result row.
    /// </summary>
    private static byte[] ForJsonRow(StringBuilder body, ForJsonOptions options)
    {
        var document = new StringBuilder();
        if (options.WithoutArrayWrapper)
            _ = document.Append(body);
        else
            _ = document.Append('[').Append(body).Append(']');

        if (options.RootName is { } rootName)
        {
            var wrapped = new StringBuilder();
            _ = wrapped.Append('{');
            AppendForJsonString(wrapped, rootName);
            _ = wrapped.Append(':').Append(document).Append('}');
            document = wrapped;
        }

        return RowEncoder.EncodeRow(
            [SqlType.NVarcharMax],
            [SqlValue.FromNVarchar(SqlType.NVarcharMax, document.ToString())]);
    }

    /// <summary>
    /// Renders one object <c>{ … }</c> from <paramref name="nodes"/> for a
    /// single row, appending onto <paramref name="sb"/>. Returns whether any
    /// property was written — a nested object with no surviving properties (all
    /// leaves NULL under omit-NULL semantics) is dropped by its caller, while
    /// the top-level per-row object is always emitted even when empty.
    /// </summary>
    private static bool RenderForJsonObject(
        StringBuilder sb, List<ForJsonNode> nodes, byte[] rowBytes, SqlType[] innerSchema, bool[] rawColumns, bool includeNulls)
    {
        _ = sb.Append('{');
        var any = AppendForJsonProperties(sb, nodes, rowBytes, innerSchema, rawColumns, includeNulls);
        _ = sb.Append('}');
        return any;
    }

    /// <summary>
    /// Appends one row's properties from <paramref name="nodes"/> — the body
    /// of <see cref="RenderForJsonObject"/>, split out so the AUTO nesting
    /// serializer can write a level's own properties into an object it keeps
    /// open for the nested levels. Returns whether any property was written.
    /// </summary>
    private static bool AppendForJsonProperties(
        StringBuilder sb, List<ForJsonNode> nodes, byte[] rowBytes, SqlType[] innerSchema, bool[] rawColumns, bool includeNulls)
    {
        var first = true;
        foreach (var node in nodes)
        {
            if (node.Children is { } children)
            {
                var child = new StringBuilder();
                if (!RenderForJsonObject(child, children, rowBytes, innerSchema, rawColumns, includeNulls))
                    continue;
                if (!first)
                    _ = sb.Append(',');
                first = false;
                AppendForJsonString(sb, node.Key);
                _ = sb.Append(':').Append(child);
                continue;
            }

            var value = RowDecoder.DecodeColumn(innerSchema, rowBytes, node.LeafColumn);
            if (value.IsNull && !includeNulls)
                continue;
            if (!first)
                _ = sb.Append(',');
            first = false;
            AppendForJsonString(sb, node.Key);
            _ = sb.Append(':');
            if (value.IsNull)
                _ = sb.Append("null");
            else
                AppendForJsonValue(sb, value, rawColumns[node.LeafColumn]);
        }
        return !first;
    }

    /// <summary>
    /// Serializes a <c>FOR JSON AUTO</c> projection whose sources nest: each
    /// level past the first is an array-valued property on its parent object,
    /// keyed by the source's name. Consecutive rows sharing an outer level's
    /// values extend that level's open object (their inner objects accumulate
    /// in one array) while the innermost level emits one object per row —
    /// the same grouping FOR XML AUTO applies to its elements.
    /// </summary>
    private static IEnumerable<byte[]> SerializeForJsonAuto(
        Selection inner, SqlType[] innerSchema, AutoLevel[] levels, List<ForJsonNode>[] levelNodes, bool[] rawColumns,
        ForJsonOptions options, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var body = new StringBuilder();
        // Whether the object currently open at each level wrote a property of
        // its own — decides the comma before a nested level's array key.
        var hasProperties = new bool[levels.Length];
        var openDepth = 0;
        byte[]? previous = null;

        foreach (var rowBytes in inner.Execute(batch, outerResolver).RowBytes)
        {
            var depth = previous is null ? 0 : AutoRestartDepth(levels, innerSchema, previous, rowBytes);
            for (var i = openDepth - 1; i >= depth; i--)
            {
                _ = body.Append('}');
                // A level deeper than the restart point loses its whole array;
                // at the restart point itself the array stays open for the
                // sibling object about to be written.
                if (i > depth)
                    _ = body.Append(']');
            }

            var sibling = openDepth > depth;
            for (var i = depth; i < levels.Length; i++)
            {
                if (i == depth && sibling)
                {
                    _ = body.Append(',');
                }
                else if (i > 0)
                {
                    if (hasProperties[i - 1])
                        _ = body.Append(',');
                    AppendForJsonString(body, levels[i].Name);
                    _ = body.Append(":[");
                }
                _ = body.Append('{');
                hasProperties[i] = AppendForJsonProperties(body, levelNodes[i], rowBytes, innerSchema, rawColumns, options.IncludeNulls);
                openDepth = i + 1;
            }
            previous = rowBytes;
        }

        // Empty input rowset → no output row at all (a scalar subquery then
        // yields SQL NULL, matching real SQL Server).
        if (previous is null)
            yield break;

        for (var i = openDepth - 1; i >= 0; i--)
        {
            _ = body.Append('}');
            if (i > 0)
                _ = body.Append(']');
        }

        yield return ForJsonRow(body, options);
    }

    /// <summary>
    /// Inserts one column's dotted path into the ordered nesting tree,
    /// enforcing SQL Server's contiguity rule: an object's properties must be
    /// consecutive in the select list. A path may only extend the last sibling
    /// at each level; a segment matching an earlier sibling (a reopened
    /// object), a leaf name reused as an object prefix, or a duplicate leaf all
    /// raise Msg 13601 naming the offending column alias.
    /// </summary>
    private static void InsertForJsonPath(List<ForJsonNode> level, string[] segments, int index, int column, string alias)
    {
        var key = segments[index];
        var isLeaf = index == segments.Length - 1;

        if (level.Count > 0 && level[^1].Key == key)
        {
            var last = level[^1];
            if (isLeaf || last.Children is null)
                throw SimulatedSqlException.ForJsonPropertyConflict(alias);
            InsertForJsonPath(last.Children, segments, index + 1, column, alias);
            return;
        }

        // A key matching a non-last sibling means the object was already closed.
        for (var i = 0; i < level.Count - 1; i++)
        {
            if (level[i].Key == key)
                throw SimulatedSqlException.ForJsonPropertyConflict(alias);
        }

        if (isLeaf)
        {
            level.Add(new ForJsonNode(key, column, null));
            return;
        }
        var node = new ForJsonNode(key, -1, []);
        level.Add(node);
        InsertForJsonPath(node.Children!, segments, index + 1, column, alias);
    }

    /// <summary>
    /// True when the column expression produces a JSON document whose string
    /// value the serializer embeds verbatim: a nested FOR JSON subquery, or any
    /// of the JSON-producing builders recognized by
    /// <see cref="JsonValueRender.ProducesJson"/>. Unwraps the alias and
    /// parenthesis wrappers.
    /// </summary>
    private static bool ColumnProducesRawJson(Expression expression) => expression switch
    {
        NamedExpression named => ColumnProducesRawJson(named.Inner),
        Parenthesized parenthesized => ColumnProducesRawJson(parenthesized.Wrapped),
        ScalarSubqueryExpression subquery => subquery.Inner.ForJson is not null,
        _ => JsonValueRender.ProducesJson(expression),
    };

    /// <summary>
    /// Appends a non-NULL <see cref="SqlValue"/> as a FOR JSON fragment. FOR
    /// JSON's formatting diverges from the JSON_* builders in three probed
    /// ways: <c>float</c> / <c>real</c> use SQL Server's scientific notation
    /// (15 / 7 fraction digits, signed 3-digit exponent), the date/time types
    /// drop an all-zero fractional second, and the string escaper additionally
    /// escapes <c>/</c> as <c>\/</c>.
    /// </summary>
    private static void AppendForJsonValue(StringBuilder sb, SqlValue value, bool raw)
    {
        if (raw)
        {
            _ = sb.Append(value.CoerceTo(SqlType.NVarchar).AsString);
            return;
        }

        var type = value.Type;
        switch (type)
        {
            case var _ when type == SqlType.Bit:
                _ = sb.Append(value.AsBoolean ? "true" : "false");
                return;
            case SqlVariantSqlType:
                AppendForJsonValue(sb, value.AsVariantInner, false);
                return;
            case var _ when type == SqlType.TinyInt:
                _ = sb.Append(value.AsByte.ToString(CultureInfo.InvariantCulture));
                return;
            case var _ when type == SqlType.SmallInt:
                _ = sb.Append(value.AsInt16.ToString(CultureInfo.InvariantCulture));
                return;
            case var _ when type == SqlType.Int32:
                _ = sb.Append(value.AsInt32.ToString(CultureInfo.InvariantCulture));
                return;
            case var _ when type == SqlType.BigInt:
                _ = sb.Append(value.AsInt64.ToString(CultureInfo.InvariantCulture));
                return;
            case DecimalSqlType:
                _ = sb.Append(value.AsDecimal.ToString(CultureInfo.InvariantCulture));
                return;
            case var _ when type == SqlType.Money || type == SqlType.SmallMoney:
                _ = sb.Append(value.AsMoney.ToString("0.0000", CultureInfo.InvariantCulture));
                return;
            case var _ when type == SqlType.Float:
                _ = sb.Append(value.AsDouble.ToString("0.000000000000000e+000", CultureInfo.InvariantCulture));
                return;
            case var _ when type == SqlType.Real:
                _ = sb.Append(value.AsSingle.ToString("0.0000000e+000", CultureInfo.InvariantCulture));
                return;
            case BinarySqlType or VarbinarySqlType:
                _ = sb.Append('"').Append(Convert.ToBase64String(value.AsBytes)).Append('"');
                return;
            case var _ when type == SqlType.Date:
                _ = sb.Append('"').Append(value.CoerceTo(SqlType.NVarchar).AsString).Append('"');
                return;
            case DateTime2SqlType dt2:
                AppendForJsonDateTime(sb, value.AsDateTime2, dt2.precision);
                return;
            case var _ when type == SqlType.DateTime:
                AppendForJsonDateTime(sb, value.AsDateTime, 3);
                return;
            case var _ when type == SqlType.SmallDateTime:
                AppendForJsonDateTime(sb, value.AsSmallDateTime, 0);
                return;
            case TimeSqlType time:
                _ = sb.Append('"');
                AppendForJsonTime(sb, value.AsTime, time.precision);
                _ = sb.Append('"');
                return;
            case DateTimeOffsetSqlType dto:
                AppendForJsonDateTimeOffset(sb, value.AsDateTimeOffset, dto.precision);
                return;
            default:
                // char / nchar / varchar / nvarchar / text / uniqueidentifier /
                // hierarchyid / spatial / everything else: SQL Server's default
                // string form, JSON-escaped.
                AppendForJsonString(sb, value.CoerceTo(SqlType.NVarchar).AsString);
                return;
        }
    }

    private static void AppendForJsonDateTime(StringBuilder sb, DateTime value, int precision)
    {
        _ = sb.Append('"').Append(value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        AppendForJsonFraction(sb, value.Ticks % TimeSpan.TicksPerSecond, precision);
        _ = sb.Append('"');
    }

    private static void AppendForJsonTime(StringBuilder sb, TimeSpan value, int precision)
    {
        _ = sb.Append(value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
        AppendForJsonFraction(sb, value.Ticks % TimeSpan.TicksPerSecond, precision);
    }

    private static void AppendForJsonDateTimeOffset(StringBuilder sb, DateTimeOffset value, int precision)
    {
        _ = sb.Append('"').Append(value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        AppendForJsonFraction(sb, value.Ticks % TimeSpan.TicksPerSecond, precision);
        _ = sb.Append(value.ToString("zzz", CultureInfo.InvariantCulture)).Append('"');
    }

    /// <summary>
    /// Appends <c>.fff…</c> at the declared precision, but only when the
    /// fractional second is non-zero — SQL Server drops an all-zero fraction
    /// entirely (probe-confirmed) while keeping interior/trailing zeros of a
    /// non-zero fraction (<c>.100</c>, not <c>.1</c>).
    /// </summary>
    private static void AppendForJsonFraction(StringBuilder sb, long ticksInSecond, int precision)
    {
        if (precision == 0 || ticksInSecond == 0)
            return;
        // TimeSpan.TicksPerSecond is 10^7, so a p-digit fraction divides by 10^(7-p).
        var divisor = 1L;
        for (var p = precision; p < 7; p++)
            divisor *= 10;
        var scaled = ticksInSecond / divisor;
        if (scaled == 0)
            return;
        _ = sb.Append('.').Append(scaled.ToString("D" + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
    }

    private static void AppendForJsonString(StringBuilder sb, string value)
    {
        _ = sb.Append('"');
        foreach (var c in value)
        {
            _ = c switch
            {
                '"' => sb.Append("\\\""),
                '\\' => sb.Append("\\\\"),
                '/' => sb.Append("\\/"),
                '\b' => sb.Append("\\b"),
                '\f' => sb.Append("\\f"),
                '\n' => sb.Append("\\n"),
                '\r' => sb.Append("\\r"),
                '\t' => sb.Append("\\t"),
                _ when c < 0x20 => sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)),
                _ => sb.Append(c),
            };
        }
        _ = sb.Append('"');
    }
}

/// <summary>The two FOR JSON serialization modes.</summary>
internal enum ForJsonMode
{
    /// <summary>Column aliases drive nesting via dotted paths (the workhorse).</summary>
    Path,
    /// <summary>Column names become flat keys; each FROM source nests one level deeper as a sub-array.</summary>
    Auto,
}

/// <summary>
/// Parsed FOR JSON clause options. Immutable, so it rides the cached plan.
/// </summary>
internal sealed class ForJsonOptions(ForJsonMode mode, bool includeNulls, bool withoutArrayWrapper, string? rootName)
{
    public readonly ForJsonMode Mode = mode;

    /// <summary>Emit <c>"key":null</c> for NULL columns instead of omitting them.</summary>
    public readonly bool IncludeNulls = includeNulls;

    /// <summary>Emit comma-separated objects with no surrounding <c>[ ]</c>.</summary>
    public readonly bool WithoutArrayWrapper = withoutArrayWrapper;

    /// <summary>The ROOT wrapper name, or null when no ROOT option was given (empty string is a valid name).</summary>
    public readonly string? RootName = rootName;
}

/// <summary>
/// One node of a FOR JSON PATH nesting tree: either a leaf bound to a result
/// column, or an object holding ordered child nodes.
/// </summary>
internal sealed class ForJsonNode(string key, int leafColumn, List<ForJsonNode>? children)
{
    public readonly string Key = key;

    /// <summary>Result-column index for a leaf; -1 for an object node.</summary>
    public readonly int LeafColumn = leafColumn;

    /// <summary>Ordered children for an object node; null for a leaf.</summary>
    public readonly List<ForJsonNode>? Children = children;
}
