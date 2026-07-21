using SqlServerSimulator.Schemas;
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
/// Type codes today: <c>'U'</c> (user table), <c>'V'</c> (view, including the
/// registered <c>sys.*</c> / <c>INFORMATION_SCHEMA.*</c> catalog views),
/// <c>'P'</c> (stored procedure), <c>'FN'</c> / <c>'IF'</c> (scalar / inline-TVF
/// functions), <c>'TR'</c> (DML trigger). Other documented codes (<c>'TF'</c>,
/// FK / DEFAULT constraint codes, …) return NULL pending those features.
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
            // 'IF' (inline table-valued function), 'V' (view), 'P' (stored
            // procedure), 'TR' (DML trigger). Other documented codes (TF /
            // ...) return NULL pending those features.
            if (!BuiltInToken.EqualsAny(typeFilter, "U", "FN", "IF", "V", "P", "TR"))
                return SqlValue.Null(SqlType.Int32);
        }

        var nameStr = nameValue.CoerceTo(SqlType.NVarchar).AsString;
        if (!TryParseObjectName(nameStr, out var parsed))
            return SqlValue.Null(SqlType.Int32);

        // A restricted principal gets NULL for an object it can't view metadata
        // for (probe-confirmed: OBJECT_ID('dbo.tab_none') = NULL for a user
        // without a grant), while the resolved id passes through for dbo /
        // full-visibility sessions. Trigger visibility follows its parent.
        var restrict = PermissionEnforcement.MetadataVisibilityApplies(runtime.Batch);
        var principalId = runtime.Batch.Connection.Security.Effective.DatabasePrincipalId;
        SqlValue GateAs(int resultId, int governObjectId, int governSchemaId) =>
            !restrict || PermissionChecker.CanViewMetadata(runtime.Batch.CurrentDatabase, principalId, governObjectId, governSchemaId)
                ? SqlValue.FromInt32(resultId)
                : SqlValue.Null(SqlType.Int32);
        SqlValue Gate(int objectId, int schemaId) => GateAs(objectId, objectId, schemaId);

        // 'FN' / 'IF' / 'TF' / no filter: try function resolution. With a
        // specific filter the function must match that kind (scalar vs.
        // inline TVF vs. multi-statement TVF); without a filter, any kind
        // matches.
        if (typeFilter is null || BuiltInToken.EqualsAny(typeFilter, "FN", "IF", "TF"))
        {
            if (runtime.Batch.TryResolveFunction(parsed, out var function))
            {
                var kindMatches = typeFilter switch
                {
                    null => true,
                    _ when BuiltInToken.Equals(typeFilter, "FN") => function is ScalarFunction,
                    _ when BuiltInToken.Equals(typeFilter, "IF") => function is InlineTableValuedFunction,
                    _ when BuiltInToken.Equals(typeFilter, "TF") => function is MultiStatementTableValuedFunction,
                    _ => false,
                };
                if (kindMatches)
                    return Gate(function.ObjectId, function.SchemaId);
            }
            if (typeFilter is not null)
                return SqlValue.Null(SqlType.Int32);
        }

        // 'V' / no filter: try view resolution before falling through to
        // tables. With a specific 'V' filter, a table miss returns NULL.
        if (typeFilter is null || BuiltInToken.Equals(typeFilter, "V"))
        {
            if (runtime.Batch.TryResolveView(parsed, out var view))
                return Gate(view.ObjectId, view.SchemaId);
            // Registered sys.* / INFORMATION_SCHEMA.* catalog views resolve as
            // system views (type 'V'). Their id is process-stable but not
            // byte-identical to real SQL Server's fixed system-view ids; the
            // load-bearing property is non-NULL — SSMS's Query Store probe
            // gates on OBJECT_ID('[sys].[database_query_store_options]').
            if (runtime.Batch.TryResolveCatalogView(parsed, out var catalogView, out _))
                return SqlValue.FromInt32(catalogView.ObjectId);
            if (typeFilter is not null)
                return SqlValue.Null(SqlType.Int32);
        }

        // 'P' / no filter: try procedure resolution. Procs share the
        // object-name namespace with tables / views / functions, so the
        // no-filter form falls through here too.
        if (typeFilter is null || BuiltInToken.Equals(typeFilter, "P"))
        {
            if (runtime.Batch.TryResolveProcedure(parsed, out var procedure))
                return Gate(procedure.ObjectId, procedure.SchemaId);
            if (typeFilter is not null)
                return SqlValue.Null(SqlType.Int32);
        }

        // 'TR' / no filter: try DML trigger resolution. Triggers share the
        // object-name namespace, so the no-filter form falls through here too.
        if (typeFilter is null || BuiltInToken.Equals(typeFilter, "TR"))
        {
            if (runtime.Batch.TryResolveTrigger(parsed, out var trigger))
                return GateAs(trigger.ObjectId, trigger.Parent.ObjectId, trigger.Parent.SchemaId);
            if (typeFilter is not null)
                return SqlValue.Null(SqlType.Int32);
        }

        // 'U' filter or no filter: try table resolution.
        return runtime.Batch.TryResolveTable(parsed, out var table)
            ? Gate(table.ObjectId, table.SchemaId)
            : SqlValue.Null(SqlType.Int32);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    // OBJECT_ID resolves a name against the current database at runtime,
    // independent of any table row — row-independent exactly when its
    // name/type arguments are (the canonical `OBJECT_ID(N'[dbo].[T]')` case).
    internal override bool IsRowIndependent =>
        this.nameArg.IsRowIndependent && (this.typeArg is null || this.typeArg.IsRowIndependent);

    internal override string DebugDisplay() =>
        this.typeArg is null
            ? $"OBJECT_ID({this.nameArg.DebugDisplay()})"
            : $"OBJECT_ID({this.nameArg.DebugDisplay()}, {this.typeArg.DebugDisplay()})";

    /// <summary>
    /// Splits a runtime-string object name into a <see cref="MultiPartName"/>.
    /// Honors bracket quoting (<c>[dbo].[foo]</c>) on a per-segment basis;
    /// trims surrounding whitespace; an empty middle segment substitutes
    /// <see cref="Database.DefaultSchemaName"/> (so <c>'db..table'</c>
    /// resolves identically to <c>'db.dbo.table'</c>, matching the
    /// SQL-grammar <see cref="BatchContext.ParseObjectName"/> rule). 4+
    /// segments, 0 segments, or unterminated brackets in any segment return
    /// false.
    /// </summary>
    internal static bool TryParseObjectName(string input, out MultiPartName result)
    {
        result = default;
        if (string.IsNullOrEmpty(input))
            return false;
        var rawSegments = input.Split('.');
        var segments = new List<string>(rawSegments.Length);
        for (var i = 0; i < rawSegments.Length; i++)
        {
            var segment = rawSegments[i].Trim();
            if (segment.Length == 0)
            {
                // Empty middle segment is the `db..table` shorthand for
                // `db.dbo.table` — substitute the default schema so the
                // first segment routes to a database, not a current-DB
                // schema. Leading / trailing empties (`.foo`, `foo.`) drop
                // through to the count check.
                var isLeadingOrTrailing = i == 0 || i == rawSegments.Length - 1;
                if (isLeadingOrTrailing)
                    continue;
                segments.Add(Database.DefaultSchemaName);
                continue;
            }
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
