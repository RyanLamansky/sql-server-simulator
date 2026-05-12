using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>OBJECT_ID(name [, type])</c>: returns the int <c>object_id</c> of a
/// named object, or NULL when not found. The name argument is a runtime
/// string parsed as a 1–3-part dotted identifier with bracket-quoting
/// supported (<c>'[dbo].[foo]'</c>, <c>'dbo.foo'</c>, <c>'claude.dbo.foo'</c>
/// all resolve the same). Probe-confirmed against SQL Server 2025
/// (2026-05-11): single-arg form matches any object type; 2-arg form filters
/// by 2-char type code (case-insensitive, whitespace-sensitive — <c>'U '</c>
/// fails, <c>'U'</c> works); a NULL anywhere propagates NULL; a 4-part name
/// (linked-server form) returns NULL silently. Result type is always
/// <see cref="SqlType.Int32"/>.
/// </summary>
/// <remarks>
/// <para>
/// Type codes today: only <c>'U'</c> (user table) matches. Other codes
/// (<c>V</c>/<c>P</c>/<c>F</c>/<c>FN</c>/...) return NULL since the simulator
/// doesn't yet model views / procs / functions / FK constraints. When those
/// features land their codes get added here.
/// </para>
/// <para>
/// Divergence from real SQL Server on temp tables: <c>OBJECT_ID('#foo')</c>
/// resolves the session's <c>#foo</c> directly because
/// <see cref="BatchContext.TryResolveTable"/> routes <c>#</c>-prefixed leaves
/// to the connection's temp dict regardless of qualifier. Real SQL Server
/// requires the explicit <c>tempdb..#foo</c> three-part form because
/// unqualified resolution targets the current database (typically not
/// tempdb). Matches the simulator's existing temp-routing simplification.
/// </para>
/// </remarks>
internal sealed class ObjectId : Expression
{
    private readonly Expression nameArg;
    private readonly Expression? typeArg;

    public ObjectId(ParserContext context)
    {
        this.nameArg = Parse(context);
        if (context.Token is Tokens.Operator { Character: ',' })
        {
            this.typeArg = Parse(context.MoveNextRequiredReturnSelf());
            if (context.Token is Tokens.Operator { Character: ',' })
                throw SimulatedSqlException.FunctionRequiresNArguments("object_id", 2);
        }
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var nameValue = this.nameArg.Run(runtime);
        if (nameValue.IsNull)
            return SqlValue.Null(SqlType.Int32);

        string? typeFilter = null;
        if (this.typeArg is not null)
        {
            var typeValue = this.typeArg.Run(runtime);
            if (typeValue.IsNull)
                return SqlValue.Null(SqlType.Int32);
            typeFilter = typeValue.CoerceTo(SqlType.NVarchar).AsString;
            // Probe-confirmed: real SQL Server is whitespace-sensitive on the
            // type filter (' U ' returns NULL) but case-insensitive ('u' works).
            // Modeled codes today: 'U' (user table), 'FN' (scalar UDF),
            // 'IF' (inline table-valued function). Other documented codes
            // (V / P / TF / ...) return NULL pending those features.
            if (!Collation.Default.Equals(typeFilter, "U")
                && !Collation.Default.Equals(typeFilter, "FN")
                && !Collation.Default.Equals(typeFilter, "IF"))
            {
                return SqlValue.Null(SqlType.Int32);
            }
        }

        var nameStr = nameValue.CoerceTo(SqlType.NVarchar).AsString;
        if (!TryParseObjectName(nameStr, out var parsed))
            return SqlValue.Null(SqlType.Int32);

        // 'FN' / 'IF' / no filter: try function resolution. With a specific
        // filter the function must match that kind (scalar vs. inline TVF);
        // without a filter, either kind matches.
        if (typeFilter is null || Collation.Default.Equals(typeFilter, "FN") || Collation.Default.Equals(typeFilter, "IF"))
        {
            if (runtime.Batch.TryResolveFunction(parsed, out var function))
            {
                var kindMatches = typeFilter switch
                {
                    null => true,
                    _ when Collation.Default.Equals(typeFilter, "FN") => function is ScalarFunction,
                    _ when Collation.Default.Equals(typeFilter, "IF") => function is InlineTableValuedFunction,
                    _ => false,
                };
                if (kindMatches)
                    return SqlValue.FromInt32(function.ObjectId);
            }
            if (typeFilter is not null)
                return SqlValue.Null(SqlType.Int32);
        }

        // 'U' filter or no filter: try table resolution.
        return runtime.Batch.TryResolveTable(parsed, out var table)
            ? SqlValue.FromInt32(table.ObjectId)
            : SqlValue.Null(SqlType.Int32);
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() =>
        this.typeArg is null
            ? $"OBJECT_ID({this.nameArg.DebugDisplay()})"
            : $"OBJECT_ID({this.nameArg.DebugDisplay()}, {this.typeArg.DebugDisplay()})";

    /// <summary>
    /// Splits a runtime-string object name into a <see cref="MultiPartName"/>.
    /// Honors bracket quoting (<c>[dbo].[foo]</c>) on a per-segment basis;
    /// trims surrounding whitespace; compresses empty middle segments (so
    /// <c>'tempdb..#foo'</c> yields a 2-part name, same rule the SQL-level
    /// <see cref="BatchContext.ParseObjectName"/> applies). 4+ segments,
    /// 0 segments, or unterminated brackets in any segment return false.
    /// </summary>
    private static bool TryParseObjectName(string input, out MultiPartName result)
    {
        result = default;
        if (string.IsNullOrEmpty(input))
            return false;
        var segments = new List<string>();
        foreach (var raw in input.Split('.'))
        {
            var segment = raw.Trim();
            if (segment.Length == 0)
                continue; // empty middle segment (tempdb..#foo)
            if (segment.Length >= 2 && segment[0] == '[' && segment[^1] == ']')
            {
                var inner = segment[1..^1];
                if (inner.AsSpan().Contains('['))
                    return false; // unbalanced bracket inside bracket
                segment = inner.Replace("]]", "]", StringComparison.Ordinal);
            }
            else if (segment.AsSpan().IndexOfAny('[', ']') >= 0)
            {
                return false; // stray bracket
            }
            segments.Add(segment);
        }
        if (segments.Count is 0 or > 4)
            return false;
        result = new MultiPartName(segments[0]);
        for (var i = 1; i < segments.Count; i++)
            result = result.WithAddedPart(segments[i]);
        return true;
    }
}
