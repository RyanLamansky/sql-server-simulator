using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// Wraps a four-part-name reference (<c>server.db.schema.t</c>) as a
    /// <see cref="Selection"/> suitable for use as a
    /// <see cref="FromSource.LateralPlan"/>. Each <see cref="Execute"/> call
    /// opens a fresh <see cref="SimulatedDbConnection"/> on
    /// <paramref name="server"/>'s <see cref="LinkedServer.Target"/>,
    /// issues <c>SELECT * FROM [<paramref name="databaseName"/>].[<paramref
    /// name="schemaName"/>].[<paramref name="leafName"/>]</c> through the
    /// remote's full parser / planner / lock-manager pipeline, and streams
    /// the encoded row bytes back. Re-executes on every outer-row
    /// invocation (matching the catalog-view / correlated-derived-table
    /// pattern), so the remote sees a fresh snapshot per row when the
    /// query is correlated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SELECT-projection output bytes from the remote use the type-only
    /// <see cref="RowEncoder.EncodeRow(ReadOnlySpan{SqlType}, ReadOnlySpan{SqlValue})"/>
    /// overload (no LOB store), so the bytes are self-contained — no LOB
    /// pointers reference the remote heap. The local plan reads them via
    /// the same <see cref="RowDecoder"/> path it uses for any other
    /// <see cref="FromSource"/>.
    /// </para>
    /// <para>
    /// No predicate / projection push-down: the simulator always asks the
    /// remote for the full table and applies the local <c>WHERE</c> /
    /// projection / join on the returned rowset. Correct but slow for
    /// large remote tables; matches the agreed initial scope.
    /// </para>
    /// </remarks>
    internal static Selection ForLinkedServer(LinkedServer server, string databaseName, string schemaName, string leafName, HeapColumn[] columns)
    {
        var schemaArr = new SqlType[columns.Length];
        var columnNames = new string[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            schemaArr[i] = columns[i].Type;
            columnNames[i] = columns[i].Name;
        }
        return new Selection(
            schemaArr,
            columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            rowSource: (_, _) => StreamRemoteRows(server, databaseName, schemaName, leafName));
    }

    /// <summary>
    /// Opens a fresh remote connection, issues
    /// <c>SELECT * FROM [db].[schema].[leaf]</c>, materializes the row
    /// bytes into a list (so the connection / command / reader chain
    /// disposes before iteration is consumed by the local plan), and
    /// returns the buffered rows. Disposing the connection drops any
    /// remote locks acquired by the query under the remote's
    /// session-isolation defaults — matching the "fresh remote session
    /// per remote query" semantic of real SQL Server's linked-server
    /// pipeline.
    /// </summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The query string is built from identifiers that already passed parser validation as Name tokens (so they're well-formed SQL identifiers, not user-typed text); the leaf / schema / db segments are bracket-quoted with embedded ] escaping. The remote command runs against a sibling Simulation in the same process — there's no external SQL surface to inject against.")]
    private static List<byte[]> StreamRemoteRows(LinkedServer server, string databaseName, string schemaName, string leafName)
    {
        using var conn = server.Target.CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = string.Create(
            CultureInfo.InvariantCulture,
            $"SELECT * FROM [{EscapeIdent(databaseName)}].[{EscapeIdent(schemaName)}].[{EscapeIdent(leafName)}]");
        var rows = new List<byte[]>();
        foreach (var outcome in server.Target.CreateResultSetsForCommand(cmd))
        {
            if (outcome is SimulatedSqlResultSet rs)
                rows.AddRange(rs.RowBytes);
        }
        return rows;
    }

    private static string EscapeIdent(string ident) => ident.Replace("]", "]]", StringComparison.Ordinal);

    /// <summary>
    /// Parses an <c>OPENQUERY(server, 'query')</c> FROM / JOIN source. Enters
    /// with <see cref="ParserContext.Token"/> on the <c>OPENQUERY</c> name;
    /// on return <see cref="ParserContext.Token"/> sits on the first token
    /// past the closing <c>)</c> (ready for the caller's in-place alias
    /// handling). Exactly two arguments: a bare identifier (plain or
    /// bracketed) naming the linked server, and a bare string literal
    /// carrying the pass-through query. Anything else (a literal / dotted
    /// name in the server slot, a variable / concatenation / extra arg in
    /// the query slot) fails as Msg 102 because the token after each slot
    /// isn't the expected separator. The linked server is resolved eagerly:
    /// an unregistered name raises Msg 7202. The pass-through query then
    /// runs once on the remote to discover its result-set schema (columns
    /// aren't known until the query executes).
    /// </summary>
    internal static Selection ParseOpenQuery(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not Name serverToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var serverName = serverToken.Value;

        if (context.GetNextRequired() is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not Literal queryLiteral || queryLiteral.Value.Type.Category != SqlTypeCategory.String)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var queryText = queryLiteral.Value.AsString;

        if (context.GetNextRequired() is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        // OPENQUERY reads external remote state, so its FROM-less-style
        // baked schema mustn't be cached across executions with a different
        // remote. Disqualify the batch from plan-cache promotion.
        context.Batch.HasSessionScopedReference = true;

        if (!context.Batch.Connection.Simulation.ActiveLinkedServers.TryGetValue(serverName, out var server))
            throw SimulatedSqlException.LinkedServerNotFound(serverName);

        var (schema, columnNames) = DiscoverOpenQuerySchema(server, queryText);
        return ForOpenQuery(server, queryText, schema, columnNames);
    }

    /// <summary>
    /// Wraps an <c>OPENQUERY</c> pass-through query as a
    /// <see cref="Selection"/> usable as a <see cref="FromSource.LateralPlan"/>.
    /// Unlike the four-part-name <see cref="ForLinkedServer"/>, the schema is
    /// discovered once at parse time and passed in here; each
    /// <see cref="Execute"/> RE-RUNS the query on the remote and streams the
    /// first result set's rows (it does not cache the schema-discovery pass's
    /// rows). So a side-effecting pass-through payload runs once for schema
    /// discovery plus once per outer execution — acceptable for the SELECT
    /// payloads OPENQUERY targets.
    /// </summary>
    internal static Selection ForOpenQuery(LinkedServer server, string queryText, SqlType[] schema, string[] columnNames) =>
        new(
            schema,
            columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            rowSource: (_, _) => StreamOpenQueryRows(server, queryText));

    /// <summary>
    /// Runs the pass-through query on the remote and captures the first
    /// result set's schema + column names (OPENQUERY returns only the first
    /// result set). A query that yields no result set (empty string, a
    /// non-SELECT statement) raises <see cref="NotSupportedException"/> — the
    /// exact real-server Msg for this case isn't probed, so the simulator
    /// names the condition rather than fabricating a number.
    /// </summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "OPENQUERY's second argument is a pass-through query string by design — the caller intends it to run verbatim on the remote. The remote command runs against a sibling Simulation in the same process; there's no external SQL surface to inject against.")]
    private static (SqlType[] Schema, string[] ColumnNames) DiscoverOpenQuerySchema(LinkedServer server, string queryText)
    {
        // An all-whitespace / empty pass-through string reaches the remote
        // command as an uninitialized CommandText (which raises its own
        // InvalidOperationException); short-circuit to the uniform no-result
        // message instead so every no-rowset payload surfaces the same way.
        if (string.IsNullOrWhiteSpace(queryText))
            throw OpenQueryNoResultSet(server.Name);

        using var conn = server.Target.CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = queryText;
        foreach (var outcome in server.Target.CreateResultSetsForCommand(cmd))
        {
            if (outcome is SimulatedSqlResultSet rs)
                return (rs.Schema, rs.ColumnNames);
        }
        throw OpenQueryNoResultSet(server.Name);
    }

    private static NotSupportedException OpenQueryNoResultSet(string serverName) =>
        new($"OPENQUERY pass-through query on linked server '{serverName}' returned no result set. Only queries that produce a result set are supported.");

    /// <summary>
    /// Re-runs the pass-through query on the remote and materializes the
    /// first result set's encoded rows (buffered before the remote
    /// connection disposes). Only the first result set is returned, matching
    /// OPENQUERY's semantics.
    /// </summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "OPENQUERY's second argument is a pass-through query string by design — the caller intends it to run verbatim on the remote. The remote command runs against a sibling Simulation in the same process; there's no external SQL surface to inject against.")]
    private static List<byte[]> StreamOpenQueryRows(LinkedServer server, string queryText)
    {
        using var conn = server.Target.CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = queryText;
        foreach (var outcome in server.Target.CreateResultSetsForCommand(cmd))
        {
            if (outcome is SimulatedSqlResultSet rs)
                return [.. rs.RowBytes];
        }
        return [];
    }
}
