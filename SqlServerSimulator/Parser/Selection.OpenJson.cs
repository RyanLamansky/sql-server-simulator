using System.Text.Json;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// <c>OPENJSON(json [, doc_path]) [WITH (col TYPE [path] [AS JSON], ...)]</c>
/// rowset-returning function support. Implemented as a <see cref="Selection"/>
/// factory so the rest of the FROM-source machinery (alias / qualifier /
/// lateral re-execution per outer row) reuses the existing derived-table
/// codepath.
/// </summary>
/// <remarks>
/// EF Core 10 emits OPENJSON for primitive collections (<c>List&lt;int&gt;</c>
/// / <c>List&lt;string&gt;</c> properties) and for owned-many-as-JSON
/// projections. Two shapes:
/// <list type="bullet">
/// <item><c>OPENJSON([c].[Tags]) WITH ([value] nvarchar(max) '$')</c> — primitive collection where '$' self-references each array element.</item>
/// <item><c>OPENJSON([c].[Scores]) AS [s]</c> (no WITH) — default schema <c>(key nvarchar(4000), value nvarchar(max), type int)</c>; used for <c>Count()</c>.</item>
/// </list>
/// </remarks>
internal sealed partial class Selection
{
    /// <summary>
    /// Default schema columns when OPENJSON is invoked without a WITH
    /// clause: <c>key nvarchar(4000)</c>, <c>value nvarchar(max)</c>,
    /// <c>type int</c>. Type code mapping matches SQL Server's
    /// documentation: 0=null, 1=string, 2=number, 3=true/false, 4=array,
    /// 5=object.
    /// </summary>
    private static readonly SqlType[] OpenJsonDefaultSchema = [SqlType.NVarchar, SqlType.NVarchar, SqlType.Int32];
    private static readonly string[] OpenJsonDefaultColumnNames = ["key", "value", "type"];

    /// <summary>
    /// Builds a Selection that, when executed, evaluates <paramref name="jsonInput"/>
    /// (and optionally <paramref name="docPath"/>) using the supplied outer
    /// resolver and yields one row per JSON array element / object property.
    /// </summary>
    /// <param name="jsonInput">The first argument — the JSON text expression. May correlate to outer columns.</param>
    /// <param name="docPath">The optional second argument — a path expression locating a sub-document; null when omitted.</param>
    /// <param name="withColumns">When non-null, the parsed WITH-clause columns; null selects the default <c>(key, value, type)</c> schema.</param>
    public static Selection FromOpenJson(Expression jsonInput, Expression? docPath, OpenJsonColumn[]? withColumns)
    {
        SqlType[] schema;
        string[] columnNames;
        if (withColumns is null)
        {
            schema = OpenJsonDefaultSchema;
            columnNames = OpenJsonDefaultColumnNames;
        }
        else
        {
            schema = new SqlType[withColumns.Length];
            columnNames = new string[withColumns.Length];
            for (var i = 0; i < withColumns.Length; i++)
            {
                schema[i] = withColumns[i].Type;
                columnNames[i] = withColumns[i].Name;
            }
        }

        return new Selection(schema, columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            outerResolver => EnumerateOpenJsonRows(jsonInput, docPath, withColumns, schema, outerResolver));
    }

    private static IEnumerable<byte[]> EnumerateOpenJsonRows(
        Expression jsonInput,
        Expression? docPath,
        OpenJsonColumn[]? withColumns,
        SqlType[] schema,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        var resolver = outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n));
        var jsonValue = jsonInput.Run(resolver);
        if (jsonValue.IsNull)
            yield break;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonValue.AsString);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (docPath is not null)
            {
                var pathValue = docPath.Run(resolver);
                if (pathValue.IsNull)
                    yield break;
                var path = JsonPath.Parse(pathValue.AsString);
                var match = path.Walk(root);
                if (match is null)
                    yield break;
                root = match.Value;
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var element in root.EnumerateArray())
                {
                    yield return BuildOpenJsonRow(element, withColumns, schema, key: index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    index++;
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                    yield return BuildOpenJsonRow(property.Value, withColumns, schema, key: property.Name);
            }
        }
    }

    private static byte[] BuildOpenJsonRow(JsonElement element, OpenJsonColumn[]? withColumns, SqlType[] schema, string key)
    {
        if (withColumns is null)
        {
            // Default schema: (key, value, type).
            var typeCode = element.ValueKind switch
            {
                JsonValueKind.Null => 0,
                JsonValueKind.String => 1,
                JsonValueKind.Number => 2,
                JsonValueKind.True or JsonValueKind.False => 3,
                JsonValueKind.Array => 4,
                JsonValueKind.Object => 5,
                _ => 0,
            };
            var keyValue = SqlValue.FromNVarchar(key);
            var valueText = element.ValueKind switch
            {
                JsonValueKind.Null => SqlValue.Null(SqlType.NVarchar),
                JsonValueKind.String => SqlValue.FromNVarchar(element.GetString()!),
                JsonValueKind.True => SqlValue.FromNVarchar("true"),
                JsonValueKind.False => SqlValue.FromNVarchar("false"),
                JsonValueKind.Number => SqlValue.FromNVarchar(element.GetRawText()),
                _ => SqlValue.FromNVarchar(element.GetRawText()),
            };
            return RowEncoder.EncodeRow(schema, [keyValue, valueText, SqlValue.FromInt32(typeCode)]);
        }

        var values = new SqlValue[withColumns.Length];
        for (var i = 0; i < withColumns.Length; i++)
        {
            var column = withColumns[i];
            var matched = column.Path.Walk(element);
            values[i] = matched is null
                ? SqlValue.Null(column.Type)
                : ExtractColumnValue(matched.Value, column);
        }
        return RowEncoder.EncodeRow(schema, values);
    }

    /// <summary>
    /// Coerces the matched JSON element to the WITH-clause column's
    /// declared SQL type. Strings parse via the existing CAST path; numbers
    /// parse via the standard numeric literal route. NULL JSON values
    /// surface as SQL NULL of the column's type.
    /// </summary>
    private static SqlValue ExtractColumnValue(JsonElement element, OpenJsonColumn column)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return SqlValue.Null(column.Type);

        // Stringify the JSON scalar then route through the existing string→type CAST.
        var asText = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()!,
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => element.GetRawText(),
        };

        var sourceValue = SqlValue.FromNVarchar(asText);
        return sourceValue.CoerceTo(column.Type);
    }

    /// <summary>
    /// Parses an <c>OPENJSON(...) [WITH (...)]</c> source from
    /// <paramref name="context"/>. Enters with <see cref="ParserContext.Token"/>
    /// on the <c>OPENJSON</c> name; on return <see cref="ParserContext.Token"/>
    /// sits on the first token after the closing <c>)</c> of OPENJSON
    /// (no WITH) or after the closing <c>)</c> of the WITH clause —
    /// already advanced one past the source's last token, ready for the
    /// caller's alias / comma / JOIN handling.
    /// </summary>
    public static Selection ParseOpenJson(ParserContext context, Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // First arg: the JSON-bearing expression (typically a column reference).
        context.MoveNextRequired();
        var jsonInput = Expression.Parse(context);

        Expression? docPath = null;
        if (context.Token is Operator { Character: ',' })
        {
            context.MoveNextRequired();
            docPath = Expression.Parse(context);
        }

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        // Advance past the OPENJSON `)`.
        context.MoveNextOptional();

        OpenJsonColumn[]? withColumns = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            withColumns = ParseOpenJsonWithColumns(context, outerTypeResolver);
            // ParseOpenJsonWithColumns leaves Token on the WITH `)`; advance past.
            context.MoveNextOptional();
        }

        return Selection.FromOpenJson(jsonInput, docPath, withColumns);
    }

    /// <summary>
    /// Variant of <see cref="ConsumeOptionalAlias"/> that doesn't pre-
    /// advance: <see cref="ParseOpenJson"/> leaves <see cref="ParserContext.Token"/>
    /// already past the closing <c>)</c>, so the alias check inspects the
    /// current Token directly. Returns null when no alias is present.
    /// </summary>
    private static string? ConsumeOptionalAliasInPlace(ParserContext context)
    {
        if (context.Token is ReservedKeyword { Keyword: Keyword.As })
        {
            var alias = context.GetNextRequired<Name>().Value;
            context.MoveNextOptional();
            return alias;
        }
        if (context.Token is Name aliasName)
        {
            context.MoveNextOptional();
            return aliasName.Value;
        }
        return null;
    }

    /// <summary>
    /// Parses the body of an OPENJSON <c>WITH (col TYPE [path] [AS JSON], ...)</c>
    /// clause. Enters with <see cref="ParserContext.Token"/> on the
    /// <c>WITH</c> keyword; on return Token sits on the closing <c>)</c>.
    /// <c>AS JSON</c> isn't modeled — raises NotSupportedException so users
    /// get a diagnostic when they try owned-many-as-JSON-with-AS-JSON
    /// shapes (EF Core 10 doesn't emit these).
    /// </summary>
    private static OpenJsonColumn[] ParseOpenJsonWithColumns(ParserContext context, Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        _ = outerTypeResolver;
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var columns = new List<OpenJsonColumn>();
        while (true)
        {
            if (context.GetNextRequired() is not Name columnNameToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var columnName = columnNameToken.Value;

            context.MoveNextRequired();
            if (context.Token is not Name typeNameToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();

            int? declaredMaxLength = null;
            int? declaredScale = null;
            if (context.Token is Operator { Character: '(' })
            {
                var lengthToken = context.GetNextRequired();
                declaredMaxLength = lengthToken is Numeric { Value: { IsNull: false } numericValue }
                    ? numericValue.AsInt32
                    : context.MatchContextual(ContextualKeyword.Max)
                        ? SqlType.MaxLengthSentinel
                        : throw SimulatedSqlException.SyntaxErrorNear(context);

                switch (context.GetNextRequired())
                {
                    case Operator { Character: ',' }:
                        if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } scaleValue })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        declaredScale = scaleValue.AsInt32;
                        if (context.GetNextRequired() is not Operator { Character: ')' })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        break;
                    case Operator { Character: ')' }:
                        break;
                    default:
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                }
                context.MoveNextRequired();
            }

            var (resolvedType, _) = SqlType.GetByName(typeNameToken, declaredMaxLength, declaredScale, columns.Count + 1, columnName);

            // Optional per-column path literal; defaults to `$.<column-name>`.
            JsonPath path;
            if (context.Token is Literal literal && SqlType.IsStringCategory(literal.Value.Type))
            {
                path = JsonPath.Parse(literal.Value.AsString);
                context.MoveNextRequired();
            }
            else
            {
                path = JsonPath.Parse("$." + columnName);
            }

            // AS JSON modifier — not modeled; EF Core 10 doesn't emit it.
            if (context.Token is ReservedKeyword { Keyword: Keyword.As })
            {
                throw new NotSupportedException("OPENJSON column-level AS JSON modifier is not modeled.");
            }

            columns.Add(new OpenJsonColumn(columnName, resolvedType, path));

            if (context.Token is Operator { Character: ')' })
                break;
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        return [.. columns];
    }
}

/// <summary>
/// One column from an <c>OPENJSON … WITH (...)</c> clause. Per-column path
/// is parsed once at FROM-source-parse time (not per row) since the WITH
/// clause's path argument is always a string literal.
/// </summary>
internal sealed class OpenJsonColumn(string name, SqlType type, JsonPath path)
{
    public readonly string Name = name;
    public readonly SqlType Type = type;
    public readonly JsonPath Path = path;
}
