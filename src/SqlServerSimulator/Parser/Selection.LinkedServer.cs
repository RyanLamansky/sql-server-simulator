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
    /// The projection is pushed down: a column the reading query never names
    /// is left out of the remote query (<see cref="UnfetchedRemoteColumns"/>),
    /// as real's provider fetches only what the query names — which is what
    /// lets <c>SELECT id</c> read a table whose <c>vector</c> column real's
    /// provider can't convert. Predicates aren't: the local <c>WHERE</c> /
    /// projection / join run on the returned rowset.
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
        Selection? self = null;
        var plan = new Selection(
            schemaArr,
            columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            rowSource: (batch, _) => StreamRemoteRows(batch, server, databaseName, schemaName, leafName, columns, self!.UnfetchedRemoteColumns))
        {
            ReadsRemoteTable = true,
        };
        self = plan;
        return plan;
    }

    /// <summary>
    /// Opens a fresh remote connection, issues
    /// <c>SELECT … FROM [db].[schema].[leaf]</c> — every column but the
    /// <paramref name="unfetched"/> ones, which read as NULL — materializes the
    /// row bytes into a list (so the connection / command / reader chain
    /// disposes before iteration is consumed by the local plan), and
    /// returns the buffered rows. Disposing the connection drops any
    /// remote locks acquired by the query under the remote's
    /// session-isolation defaults — matching the "fresh remote session
    /// per remote query" semantic of real SQL Server's linked-server
    /// pipeline.
    /// </summary>
    private static IEnumerable<byte[]> StreamRemoteRows(BatchContext batch, LinkedServer server, string databaseName, string schemaName, string leafName, HeapColumn[] columns, bool[]? unfetched)
    {
        if (RemoteWrite.RunRemoteQuery(
            server,
            string.Create(CultureInfo.InvariantCulture, $"SELECT {RemoteWrite.ColumnList(columns, unfetched)} FROM [{EscapeIdent(databaseName)}].[{EscapeIdent(schemaName)}].[{EscapeIdent(leafName)}]"),
            databaseName,
            browse: false,
            caller: batch) is not { } result)
        {
            return [];
        }
        var schema = result.Schema;
        var retyped = unfetched is not null;
        for (var i = 0; i < schema.Length && !retyped; i++)
            retyped = schema[i] != columns[i].Type;
        return retyped ? ExposeVectors(server, schema, columns, result.RowBytes) : result.RowBytes;
    }

    /// <summary>
    /// Whether this plan is a four-part name's read of a linked server's
    /// table (<see cref="ForLinkedServer"/>).
    /// </summary>
    internal bool ReadsRemoteTable;

    /// <summary>
    /// Per column of a <see cref="ReadsRemoteTable"/> plan, whether the remote
    /// query leaves it out: set, once the reading query has bound, for each
    /// column it never names (<see cref="NoteUnreadRemoteColumns"/>); null
    /// fetches every column.
    /// </summary>
    internal bool[]? UnfetchedRemoteColumns;

    /// <summary>
    /// Marks the columns of each four-part read among
    /// <paramref name="sources"/> that the query binding them never names, so
    /// the remote query leaves them out. A query whose reach this walk can't
    /// see all of — a subquery, whose body could correlate to the source, or an
    /// <c>APPLY</c>, whose right side could — fetches every column; a query
    /// naming the column anywhere (a derived table's <c>SELECT *</c> included)
    /// fetches it, where real's optimizer may still find it unneeded.
    /// </summary>
    private static void NoteUnreadRemoteColumns(FromSource[] sources, JoinSpec[] joins, List<Expression> expressions, FromClause fromClause)
    {
        List<(int Source, bool[] Unread)>? reads = null;
        for (var s = 0; s < sources.Length; s++)
        {
            if (sources[s].LateralPlan is { ReadsRemoteTable: true })
                (reads ??= []).Add((s, Array.ConvertAll(sources[s].Columns, static _ => true)));
        }
        if (reads is null)
            return;
        foreach (var join in joins)
        {
            if (join.Kind is JoinKind.CrossApply or JoinKind.OuterApply)
                return;
        }

        var seesAll = true;
        bool Visit(ExpressionNode node, NodeShape shape)
        {
            if (!seesAll)
                return false;
            foreach (var local in shape.Locals)
            {
                if (local is Selection)
                {
                    seesAll = false;
                    return false;
                }
            }
            if (shape.Column is { } name)
            {
                var (source, column) = FindSourceColumnOfAnyKind(sources, name);
                foreach (var (read, unread) in reads!)
                {
                    if (read == source)
                        unread[column] = false;
                }
            }
            return true;
        }

        foreach (var expression in expressions)
            expression.Walk(Visit);
        foreach (var excluder in fromClause.Excluders)
            excluder.Walk(Visit);
        foreach (var set in fromClause.GroupingSets)
        {
            foreach (var expression in set)
                expression.Walk(Visit);
        }
        fromClause.Having?.Walk(Visit);
        foreach (var item in fromClause.OrderBy)
            item.Expr?.Walk(Visit);
        foreach (var join in joins)
            join.OnPredicate?.Walk(Visit);
        if (!seesAll)
            return;
        foreach (var (read, unread) in reads)
        {
            // A read naming no column at all (COUNT(*)) still fetches one, so
            // the remote query has a select list.
            if (Array.TrueForAll(unread, static column => column))
                unread[0] = false;
            sources[read].LateralPlan!.UnfetchedRemoteColumns = Array.Exists(unread, static column => column) ? unread : null;
        }
    }

    /// <summary>
    /// The rows of a read whose columns the provider lists as other types
    /// (<see cref="RemoteWrite.ProviderColumns"/>), each value converted to the
    /// listed type — save a <c>vector</c>, listed as <c>varbinary</c>: a NULL
    /// one reads as NULL, and the first row holding a value is Msg 7346, after
    /// the rows ahead of it (probed 2026-09-28 against SQL Server 2025). A
    /// column the query never names isn't fetched
    /// (<see cref="UnfetchedRemoteColumns"/>) and reads as NULL.
    /// </summary>
    private static IEnumerable<byte[]> ExposeVectors(LinkedServer server, SqlType[] fetched, HeapColumn[] columns, IEnumerable<byte[]> rows)
    {
        var exposed = Array.ConvertAll(columns, static column => column.Type);
        foreach (var bytes in rows)
        {
            var values = RowDecoder.DecodeRow(fetched, bytes);
            for (var i = 0; i < values.Length; i++)
            {
                // An unfetched column arrives as a NULL of the literal's type.
                if (fetched[i] is VectorSqlType)
                    values[i] = values[i].IsNull ? SqlValue.Null(exposed[i]) : throw SimulatedSqlException.RemoteRowDataNotConvertible(server);
                else if (fetched[i] != exposed[i])
                    values[i] = values[i].IsNull ? SqlValue.Null(exposed[i]) : values[i].CoerceTo(exposed[i]);
            }
            yield return RowEncoder.EncodeRow(exposed, values);
        }
    }

    /// <summary>
    /// The FROM source over a <see cref="RemoteWrite"/>'s stand-in, for the
    /// joined form of an UPDATE or DELETE whose target alias names a linked
    /// server's table or an <c>OPENQUERY</c>. Enters past the alias, whose
    /// caller consumed it; consumes any hints.
    /// </summary>
    private static FromSource RemoteTargetSource(ParserContext context, RemoteWrite remoteWrite, string? alias, string writtenName)
    {
        var proxy = remoteWrite.Proxy;
        var columnNames = new string[proxy.Columns.Length];
        for (var i = 0; i < columnNames.Length; i++)
            columnNames[i] = proxy.Columns[i].Name;
        _ = ParseOptionalTableHints(context);
        return new FromSource(
            qualifier: alias ?? proxy.Name,
            columnNames: columnNames,
            columns: proxy.Columns,
            storedSchema: proxy.StoredColumns,
            storageOrdinals: proxy.StorageOrdinals,
            lobStore: proxy.Heap,
            rows: ClusteredScan.Rows(proxy),
            backingTable: proxy,
            writtenObjectName: writtenName);
    }

    /// <summary>
    /// A four-part name whose schema is <c>sys</c> or
    /// <c>INFORMATION_SCHEMA</c> reads the server's catalog view of that name:
    /// its columns are the ones the view answers with there, found by running
    /// it once, as <c>OPENQUERY</c> finds its query's. Null when the name isn't
    /// one or the server has no such view.
    /// </summary>
    private static FromSource? TryRemoteCatalogViewSource(ParserContext context, MultiPartName objectName)
    {
        if (!BuiltInToken.Equals(objectName[2], "sys") && !BuiltInToken.Equals(objectName[2], "INFORMATION_SCHEMA"))
            return null;
        var server = RemoteWrite.ResolveServer(context.Batch, objectName[0]);
        var databaseName = string.IsNullOrEmpty(objectName[1]) ? server.SessionDatabaseName : objectName[1];
        if (!server.Target.Databases.ContainsKey(databaseName))
            return null;
        context.Batch.HasSessionScopedReference = true;
        if (!context.Batch.IsSkipping && context.Connection.CurrentTransaction is { IsDistributed: true })
            RemoteWrite.RequireNoTransaction(context.Batch, server);
        var query = string.Create(CultureInfo.InvariantCulture, $"SELECT * FROM [{EscapeIdent(databaseName)}].[{EscapeIdent(objectName[2])}].[{EscapeIdent(objectName.Leaf)}]");
        SimulatedSqlResultSet? probe;
        try
        {
            probe = RemoteWrite.RunRemoteQuery(server, query, databaseName, browse: false);
        }
        catch (SimulatedSqlException error) when (error.Number == 208)
        {
            return null;
        }
        if (probe is null)
            return null;
        var plan = ForOpenQuery(server, query, probe.Schema, probe.ColumnNames);
        var columns = new HeapColumn[plan.Schema.Length];
        for (var i = 0; i < columns.Length; i++)
            columns[i] = new HeapColumn(plan.ColumnNames[i], plan.Schema[i], maxLength: null, nullable: true);
        var alias = ConsumeOptionalAlias(context);
        _ = ParseOptionalTableHints(context);
        return new FromSource(
            qualifier: alias ?? objectName.Leaf,
            columnNames: plan.ColumnNames,
            columns: columns,
            storedSchema: columns,
            storageOrdinals: null,
            lobStore: null,
            rows: [],
            lateralPlan: plan);
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
        var (serverName, queryText) = ParseOpenQueryArguments(context);
        context.MoveNextOptional();

        // OPENQUERY reads external remote state, so its FROM-less-style
        // baked schema mustn't be cached across executions with a different
        // remote. Disqualify the batch from plan-cache promotion.
        context.Batch.HasSessionScopedReference = true;

        var server = RemoteWrite.ResolveServer(context.Batch, serverName);
        if (!context.Batch.IsSkipping && context.Connection.CurrentTransaction is { IsDistributed: true })
            RemoteWrite.RequireNoTransaction(context.Batch, server);
        RemoteWrite.RequireResumableTransaction(context.Batch, server);

        var (schema, columnNames, nullability) = DiscoverOpenQuerySchema(context.Batch, server, queryText);
        var plan = ForOpenQuery(server, queryText, schema, columnNames);
        plan.ColumnNullability = nullability;
        // The provider lists a decimal as numeric (probed 2026-10-05 against
        // SQL Server 2025).
        if (Array.Exists(schema, static type => type is DecimalSqlType))
            plan.ColumnReportsNumeric = Array.ConvertAll(schema, static type => type is DecimalSqlType);
        return plan;
    }

    /// <summary>
    /// Reads <c>OPENQUERY</c>'s two arguments — a bare server identifier and a
    /// bare string literal — entering on the <c>OPENQUERY</c> keyword and
    /// leaving the cursor on the closing <c>)</c>. Anything else is Msg 102 at
    /// the token that breaks the shape, before the server is looked up.
    /// </summary>
    internal static (string ServerName, string Query) ParseOpenQueryArguments(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not Name serverToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not Literal queryLiteral || queryLiteral.Value.Type.Category != SqlTypeCategory.String)
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        return (serverToken.Value, queryLiteral.Value.AsString);
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
            rowSource: (batch, _) => StreamOpenQueryRows(batch, server, queryText));

    /// <summary>
    /// Runs the pass-through query on the remote and captures the first
    /// result set's schema + column names (OPENQUERY returns only the first
    /// result set), refusing what real's provider refuses describing it
    /// (probed 2026-10-05 against SQL Server 2025): an empty query (Msg 7412,
    /// 7399, 7321); a query that doesn't parse (Msg 11529 from
    /// <c>sp_describe_first_result_set</c>) or names what doesn't bind (Msg
    /// 7412, 8180 and the server's error); one returning no rowset — a
    /// <c>DECLARE</c>, a <c>PRINT</c>, a <c>RAISERROR</c> alone — as an object
    /// with no columns (Msg 7357, quoting the query); and two columns of one
    /// name (Msg 492).
    /// </summary>
    private static (SqlType[] Schema, string[] ColumnNames, bool[]? Nullability) DiscoverOpenQuerySchema(BatchContext batch, LinkedServer server, string queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText))
            throw SimulatedSqlException.OpenQueryNoCommandText(batch, server);
        SimulatedSqlResultSet? result;
        try
        {
            result = RemoteWrite.RunRemoteQuery(server, queryText, database: null, browse: false, describeOnly: true);
        }
        catch (SimulatedSqlException error) when (error.Number is 207 or 208 or 4104)
        {
            throw SimulatedSqlException.OpenQueryNotPrepared(batch, server, error);
        }
        catch (SimulatedSqlException error) when (error.Class == 15 || error.Number == 2812)
        {
            throw SimulatedSqlException.OpenQueryNotDescribed(error);
        }
        catch (SimulatedSqlException error) when (error.Class < 20)
        {
            result = null;
        }
        if (result is null)
            throw SimulatedSqlException.RemoteObjectHasNoColumns(server, queryText);
        var names = result.ColumnNames;
        for (var i = 1; i < names.Length; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (names[i].Length > 0 && Collation.Baseline.Equals(names[i], names[j]))
                    throw SimulatedSqlException.OpenQueryDuplicateColumn(names[i]);
            }
        }
        return (Array.ConvertAll(RemoteWrite.RequireNoXmlColumn(result.Schema, "OPENQUERY"), RemoteWrite.ProviderRowsetType), names, result.ColumnNullability);
    }

    /// <summary>
    /// Re-runs the pass-through query on the remote and materializes the
    /// first result set's encoded rows (buffered before the remote
    /// connection disposes). Only the first result set is returned, matching
    /// OPENQUERY's semantics.
    /// </summary>
    private static IEnumerable<byte[]> StreamOpenQueryRows(BatchContext batch, LinkedServer server, string queryText) =>
        RemoteWrite.RunRemoteQuery(server, queryText, database: null, browse: false, ownTransaction: true, caller: batch) is { } result
            ? RemoteWrite.AsProviderRowset(result.Schema, result.RowBytes).Rows
            : [];
}
