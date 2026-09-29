using System.Text;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using SqlServerSimulator.Storage.Bulk;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// The providers the Linux server knows, every one SQL Server's own; any
    /// other name is Msg 7222 (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    private static readonly string[] SqlServerProviders = ["MSOLEDBSQL", "MSOLEDBSQL19", "SQLNCLI", "SQLNCLI10", "SQLNCLI11", "SQLOLEDB"];

    /// <summary>
    /// The server names that mean the instance a session is connected to
    /// when no remote simulation was registered under them.
    /// </summary>
    private static readonly string[] LocalServerNames = ["(local)", ".", "127.0.0.1", "localhost"];

    /// <summary>
    /// An <c>OPENROWSET( … )</c> FROM source, entered on the keyword: the
    /// <c>BULK</c> form over a data file, or the provider form over a server.
    /// Leaves the cursor past the alias.
    /// </summary>
    internal static FromSource ParseOpenRowsetSource(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Bulk })
            return ParseBulkRowset(context);

        var arguments = ParseAdHocArguments(context);
        context.MoveNextOptional();
        // The alias an UPDATE or DELETE named as its target makes this rowset
        // the statement's write target.
        if (context.Batch.CurrentStatement.RemoteWriteAlias is { } targetAlias)
        {
            var aliasCheckpoint = context.SaveCheckpoint();
            if (ConsumeOptionalAliasAtCurrent(context) is { } writtenAlias && context.CurrentDatabase.Collation.Equals(writtenAlias, targetAlias))
            {
                var write = AdHocWrite(context.Batch, arguments, context.Batch.CurrentStatement.RemoteWriteAliasKind);
                return RemoteTargetSource(context, write, writtenAlias, write.Proxy.Name);
            }
            context.RestoreCheckpoint(aliasCheckpoint);
        }

        var server = arguments.Server;
        context.Batch.HasSessionScopedReference = true;
        if (!context.Batch.IsSkipping && context.Connection.CurrentTransaction is { IsDistributed: true })
            RemoteWrite.RequireNoTransaction(context.Batch, server);
        Selection plan;
        HeapColumn[] columns;
        if (arguments.Query is { } query)
        {
            var result = (string.IsNullOrWhiteSpace(query) ? null : RemoteWrite.RunRemoteQuery(server, query, database: null, browse: false))
                ?? throw SimulatedSqlException.RemoteObjectHasNoColumns(server, query);
            // Real's refusal of an xml column names the rowset's alias when it
            // has one (probed 2026-09-29 against SQL Server 2025).
            var aliasCheckpoint = context.SaveCheckpoint();
            var namedAs = ConsumeOptionalAliasAtCurrent(context);
            context.RestoreCheckpoint(aliasCheckpoint);
            plan = ForOpenQuery(server, query, RemoteWrite.RequireNoXmlColumn(result.Schema, namedAs ?? "OPENROWSET"), result.ColumnNames);
            columns = new HeapColumn[plan.Schema.Length];
            for (var i = 0; i < columns.Length; i++)
                columns[i] = new HeapColumn(plan.ColumnNames[i], plan.Schema[i], maxLength: null, nullable: true);
        }
        else
        {
            var name = arguments.Object!.Value;
            if (!BatchContext.TryResolveRemoteTable(server, name, out var remoteName, out var remoteColumns, out var databaseName, out var schemaName))
                throw SimulatedSqlException.RemoteTableNotFound(server, RemoteWrite.QuotedName(name));
            plan = ForLinkedServer(server, databaseName, schemaName, remoteName, remoteColumns);
            columns = remoteColumns;
        }
        var alias = ConsumeOptionalAliasAtCurrent(context);
        // Real refuses a column-alias list on a provider rowset.
        if (context.Token is Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context.GetNextRequired());
        return new FromSource(
            qualifier: alias,
            columnNames: plan.ColumnNames,
            columns: columns,
            storedSchema: columns,
            storageOrdinals: null,
            lobStore: null,
            rows: [],
            lateralPlan: plan);
    }

    /// <summary>What an ad hoc <c>OPENROWSET</c> over a provider names.</summary>
    internal readonly struct AdHocArguments(LinkedServer server, string? query, MultiPartName? objectName)
    {
        public readonly LinkedServer Server = server;

        /// <summary>The pass-through query, or null for the object form.</summary>
        public readonly string? Query = query;

        /// <summary>The object form's name, four-part with the server slot <c>(null)</c>.</summary>
        public readonly MultiPartName? Object = objectName;
    }

    /// <summary>
    /// <c>'provider', 'connection string' | 'server'; 'user'; 'password', 'query' | [db.]schema.object</c>,
    /// entered on the provider literal and leaving the cursor on the closing
    /// <c>)</c>. Real refuses, as the batch compiles, a provider that isn't SQL
    /// Server's (Msg 7222), then any while <c>Ad Hoc Distributed Queries</c> is
    /// off (Msg 15281), then an object named in fewer than three parts
    /// (Msg 7313).
    /// </summary>
    internal static AdHocArguments ParseAdHocArguments(ParserContext context)
    {
        var provider = RequireStringLiteral(context);
        if (context.GetNextRequired() is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var connection = RequireStringLiteral(context);
        string? dataSource = null;
        if (context.GetNextRequired() is Operator { Character: ';' })
        {
            // The three-part form: data source; user; password.
            dataSource = connection;
            context.MoveNextRequired();
            _ = RequireStringLiteral(context);
            if (context.GetNextRequired() is not Operator { Character: ';' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            _ = RequireStringLiteral(context);
            context.MoveNextRequired();
        }
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        string? query = null;
        MultiPartName? objectName = null;
        if (context.Token is Literal { Value.Type.Category: SqlTypeCategory.String } queryLiteral)
        {
            query = queryLiteral.Value.AsString;
        }
        else if (context.Token is Name)
        {
            objectName = BatchContext.ParseObjectName(context);
            if (objectName.Value.Count > 3)
                throw SimulatedSqlException.TooManyNamePrefixes(objectName.Value, 2);
        }
        else
        {
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        if (context.GetNextRequired() is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var server = ResolveAdHocServer(context.Batch, provider, connection, dataSource);
        MultiPartName? fourPart = null;
        if (objectName is { } written)
        {
            if (written.Count < 3 || written.SchemaOmitted)
                throw SimulatedSqlException.RemoteSchemaOrCatalogInvalid(server);
            fourPart = new MultiPartName(server.Name).WithAddedPart(written[written.Count - 3]).WithAddedPart(written[written.Count - 2]).WithAddedPart(written.Leaf);
        }
        return new AdHocArguments(server, query, fourPart);
    }

    /// <summary>
    /// The server an ad hoc rowset connects to, as a transient linked-server
    /// entry named <c>(null)</c>, which is how real's messages name it. The
    /// connection string's <c>Server</c> / <c>Data Source</c> /
    /// <c>Address</c> key names a simulation registered through
    /// <see cref="Simulation.AddRemoteSimulation"/>; none, or a local alias
    /// such as <c>localhost</c> registered as nothing else, is the session's
    /// own instance; any other name reaches nothing, which real reports as
    /// Msg 2, ending the batch.
    /// <c>Database</c> / <c>Initial Catalog</c> is where its sessions start.
    /// </summary>
    private static LinkedServer ResolveAdHocServer(BatchContext batch, string provider, string connection, string? dataSource)
    {
        if (!Array.Exists(SqlServerProviders, p => string.Equals(p, provider, StringComparison.OrdinalIgnoreCase)))
            throw SimulatedSqlException.OnlySqlServerProviderAllowed();
        var simulation = batch.Connection.Simulation;
        if (!simulation.AdHocDistributedQueriesEnabled)
            throw SimulatedSqlException.AdHocDistributedQueriesDisabled();

        // A joined write parses its source twice; the second parse keeps the
        // server the first one opened.
        if (batch.CurrentStatement.RemoteWrite?.Server is { Name: "(null)" } opened
            && string.Equals(opened.ProviderString, connection, StringComparison.Ordinal)
            && string.Equals(opened.Provider, provider, StringComparison.Ordinal))
        {
            return opened;
        }

        var serverName = dataSource;
        string? catalog = null;
        if (dataSource is null)
        {
            foreach (var pair in connection.Split(';'))
            {
                var equals = pair.IndexOf('=', StringComparison.Ordinal);
                if (equals < 0)
                    continue;
                var key = pair[..equals].Trim();
                var value = pair[(equals + 1)..].Trim();
                if (key.Equals("Server", StringComparison.OrdinalIgnoreCase) || key.Equals("Data Source", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("Address", StringComparison.OrdinalIgnoreCase) || key.Equals("Addr", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("Network Address", StringComparison.OrdinalIgnoreCase))
                {
                    serverName = value;
                }
                else if (key.Equals("Database", StringComparison.OrdinalIgnoreCase) || key.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase))
                {
                    catalog = value;
                }
            }
        }

        var target = simulation;
        if (!string.IsNullOrEmpty(serverName))
        {
            var bare = serverName.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) ? serverName[4..] : serverName;
            if (bare.IndexOf(',', StringComparison.Ordinal) is >= 0 and var comma)
                bare = bare[..comma];
            if (simulation.AvailableRemotes.TryGetValue(serverName, out var named) || simulation.AvailableRemotes.TryGetValue(bare, out named))
                target = named;
            else if (!Array.Exists(LocalServerNames, local => string.Equals(local, bare, StringComparison.OrdinalIgnoreCase)))
                target = null;
        }
        return target is null
            ? throw SimulatedSqlException.AdHocServerUnreachable()
            : new LinkedServer("(null)", target, "SQL Server", provider, serverName, location: null, connection, catalog, DateTime.UtcNow);
    }

    private static string RequireStringLiteral(ParserContext context) =>
        context.Token is Literal { Value.Type.Category: SqlTypeCategory.String } literal
            ? literal.Value.AsString
            : throw SimulatedSqlException.SyntaxErrorNear(context);

    /// <summary>
    /// The write an <c>INSERT</c> / <c>UPDATE</c> / <c>DELETE</c> through an
    /// ad hoc rowset makes: its query's base table, or the object it names,
    /// on its server, replayed there as a linked server's write is.
    /// </summary>
    internal static RemoteWrite AdHocWrite(BatchContext batch, AdHocArguments arguments, RemoteWriteKind kind) =>
        arguments.Query is { } query
            ? RemoteWrite.ForServerQuery(batch, arguments.Server, query, kind)
            : RemoteWrite.ForServerTarget(batch, arguments.Server, arguments.Object!.Value, kind);

    /// <summary>
    /// An <c>OPENROWSET</c> as a DML target, entered on the keyword and
    /// leaving the cursor on its closing <c>)</c>. The <c>BULK</c> form is no
    /// target (Msg 156 near <c>BULK</c>).
    /// </summary>
    internal static RemoteWrite ParseAdHocWriteTarget(ParserContext context, RemoteWriteKind kind)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Bulk } bulk)
            throw SimulatedSqlException.SyntaxErrorNearKeyword(bulk);
        return AdHocWrite(context.Batch, ParseAdHocArguments(context), kind);
    }

    /// <summary>
    /// <c>OPENDATASOURCE('provider', 'init string')</c>, entered on the
    /// keyword: real's Linux server has no data-link component to open it
    /// with, so a SQL Server provider is Msg 7302, after the refusals every ad
    /// hoc rowset meets — all as the batch compiles.
    /// </summary>
    internal static SimulatedSqlException ParseOpenDataSource(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var provider = RequireStringLiteral(context);
        if (context.GetNextRequired() is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        _ = RequireStringLiteral(context);
        if (context.GetNextRequired() is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (!Array.Exists(SqlServerProviders, p => string.Equals(p, provider, StringComparison.OrdinalIgnoreCase)))
            return SimulatedSqlException.OnlySqlServerProviderAllowed();
        return context.Connection.Simulation.AdHocDistributedQueriesEnabled
            ? SimulatedSqlException.DataLinkProviderUnavailable()
            : SimulatedSqlException.AdHocDistributedQueriesDisabled();
    }

    /// <summary>
    /// <c>OPENROWSET(BULK 'file', option [, …]) alias [(columns)]</c>,
    /// entered on <c>BULK</c>: <c>SINGLE_BLOB</c> / <c>SINGLE_CLOB</c> /
    /// <c>SINGLE_NCLOB</c> read the whole file as one <c>BulkColumn</c>, and
    /// a format file lays it out as rows. The file is opened when the
    /// statement runs, and read again each time its rows are.
    /// </summary>
    private static FromSource ParseBulkRowset(ParserContext context)
    {
        if (context.GetNextRequired() is not Literal { Value.Type.Category: SqlTypeCategory.String } pathLiteral)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var path = pathLiteral.Value.AsString;
        var options = new BulkOptions();
        context.MoveNextRequired();
        while (context.Token is Operator { Character: ',' })
        {
            context.MoveNextRequired();
            options.ParseOption(context, rowset: true);
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        if (options.CodePageWritten is { } codePage)
            throw SimulatedSqlException.OptionNotSupportedOnPlatform(codePage);
        if (context.Token is ReservedKeyword { Keyword: Keyword.With } && options.Csv)
            throw SimulatedSqlException.BulkCsvWithClauseNotSupported(path);
        if (options.Single == BulkSingleKind.None && options.FormatFile is null)
            throw options.Csv ? SimulatedSqlException.BulkRowsetNeedsFormat() : SimulatedSqlException.BulkSchemaNotDetermined();

        var batch = context.Batch;
        batch.HasSessionScopedReference = true;
        string[] names;
        SqlType[] types;
        BulkFormatFile? format = null;
        if (options.FormatFile is { } formatPath)
        {
            byte[] formatBytes;
            try
            {
                formatBytes = Simulation.ReadBulkFileFor(batch, formatPath, missingState: 3);
            }
            catch (SimulatedSqlException) when (batch.IsSkipping)
            {
                // The batch compiles past a rowset whose format file it can't
                // read; the statement reports that when it runs.
                batch.CurrentStatement.BindsDeferredSource = true;
                throw;
            }
            format = BulkFormatFile.Parse(formatBytes, context.CurrentDatabase.Collation, out var hasDecimal)
                ?? throw new NotSupportedException($"The format file '{formatPath}' is in a form the simulator doesn't read.");
            if (hasDecimal)
                throw SimulatedSqlException.BulkRowsetDecimalNotSupported();
            names = format.ColumnNames;
            types = format.ColumnTypes;
        }
        else
        {
            names = ["BulkColumn"];
            types = [options.Single switch
            {
                BulkSingleKind.Blob => SqlType.VarbinaryMax,
                BulkSingleKind.Clob => VarcharSqlType.Get(SqlType.MaxLengthSentinel, context.CurrentDatabase.Collation.ForVarcharStorage(), Coercibility.CoercibleDefault),
                _ => NVarcharSqlType.Get(SqlType.MaxLengthSentinel, context.CurrentDatabase.Collation, Coercibility.CoercibleDefault),
            }];
        }
        if (!batch.IsSkipping)
            _ = Simulation.ReadBulkFileFor(batch, path, missingState: 4);

        var alias = ConsumeOptionalAliasAtCurrent(context) ?? throw SimulatedSqlException.BulkRowsetNeedsCorrelationName();
        if (context.Token is Operator { Character: '(' })
        {
            names = [.. names];
            for (var i = 0; ; i++)
            {
                if (context.GetNextRequired() is not Name column)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (i < names.Length)
                    names[i] = column.Value;
                if (context.GetNextRequired() is Operator { Character: ')' })
                    break;
                if (context.Token is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextOptional();
        }

        var single = options.Single;
        var plan = new Selection(
            types,
            names,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            rowSource: (runBatch, _) => single != BulkSingleKind.None
                ? [ReadSingleBulkValue(runBatch, path, single, types[0])]
                : ReadFormattedBulkRows(runBatch, path, format!, options, types));
        var columns = new HeapColumn[types.Length];
        for (var i = 0; i < columns.Length; i++)
            columns[i] = new HeapColumn(names[i], types[i], maxLength: null, nullable: true);
        return new FromSource(
            qualifier: alias,
            columnNames: names,
            columns: columns,
            storedSchema: columns,
            storageOrdinals: null,
            lobStore: null,
            rows: [],
            lateralPlan: plan);
    }

    /// <summary>
    /// A <c>SINGLE_*</c> file as its one encoded row: the bytes as they are,
    /// UTF-8 text (a byte-order mark skipped) in the database's code page, or
    /// UTF-16 text after its required byte-order mark. <c>SINGLE_CLOB</c>
    /// over a UTF-16 file is Msg 4806, <c>SINGLE_NCLOB</c> over any other is
    /// Msg 4809, and text that isn't UTF-8 ends the load as a truncation
    /// (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    private static byte[] ReadSingleBulkValue(BatchContext batch, string path, BulkSingleKind kind, SqlType type)
    {
        var data = Simulation.ReadBulkFileFor(batch, path, missingState: 4);
        SqlValue value;
        switch (kind)
        {
            case BulkSingleKind.Blob:
                value = SqlValue.FromVarbinary(data);
                break;
            case BulkSingleKind.Clob:
                {
                    if (data is [0xFF, 0xFE, ..])
                        throw SimulatedSqlException.SingleClobFileIsUnicode();
                    var start = data is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;
                    string text;
                    try
                    {
                        text = StrictUtf8.GetString(data, start, data.Length - start);
                    }
                    catch (DecoderFallbackException)
                    {
                        throw SimulatedSqlException.BulkRowStreamFailed(SimulatedSqlException.BulkConversionTruncation(1, 1, "BulkColumn", state: 4));
                    }
                    value = SqlValue.FromVarchar(text).CoerceTo(type);
                    break;
                }
            default:
                if (data is not [0xFF, 0xFE, ..] || data.Length % 2 != 0)
                    throw SimulatedSqlException.SingleNclobFileIsNotUnicode();
                value = SqlValue.FromNVarchar(Encoding.Unicode.GetString(data, 2, data.Length - 2)).CoerceTo(type);
                break;
        }
        return RowEncoder.EncodeRow([type], [value]);
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// A format file's rows, each field converted to its rowset column's
    /// type, between <c>FIRSTROW</c> and <c>LASTROW</c>. A field that doesn't
    /// convert ends the statement with its row error.
    /// </summary>
    private static List<byte[]> ReadFormattedBulkRows(BatchContext batch, string path, BulkFormatFile format, BulkOptions options, SqlType[] types)
    {
        var data = Simulation.ReadBulkFileFor(batch, path, missingState: 4);
        var fileColumns = new HeapColumn[types.Length];
        for (var i = 0; i < fileColumns.Length; i++)
            fileColumns[i] = new HeapColumn(format.ColumnNames[i], types[i], maxLength: null, nullable: true);
        var source = Simulation.OpenBulkSource(batch, path, data, format, options.DataFileType ?? "char", options, fileColumns);
        var firstRow = Math.Max(options.FirstRow ?? 1, 1);
        var lastRow = options.LastRow is > 0 and var last ? last : long.MaxValue;
        var cells = new object?[source.FieldCount];
        var rows = new List<byte[]>();
        while (source.NextRowNumber <= lastRow && source.Read(cells, skipping: source.NextRowNumber < firstRow))
        {
            var rowNumber = source.NextRowNumber - 1;
            if (rowNumber < firstRow)
                continue;
            var values = new SqlValue[types.Length];
            for (var c = 0; c < values.Length; c++)
                values[c] = SqlValue.Null(types[c]);
            for (var f = 0; f < cells.Length; f++)
            {
                var destination = source.DestinationOf(f);
                if (destination < 0)
                    continue;
                var failure = Simulation.ConvertBulkCell(cells[f], types[destination], out var value);
                if (failure != BulkConversionFailure.None)
                {
                    throw failure switch
                    {
                        BulkConversionFailure.Truncation => SimulatedSqlException.BulkConversionTruncation(rowNumber, f + 1, fileColumns[destination].Name),
                        BulkConversionFailure.Overflow => SimulatedSqlException.BulkConversionOverflow(rowNumber, f + 1, fileColumns[destination].Name),
                        _ => SimulatedSqlException.BulkConversionTypeMismatch(rowNumber, f + 1, fileColumns[destination].Name),
                    };
                }
                values[destination] = value;
            }
            rows.Add(RowEncoder.EncodeRow(types, values));
        }
        return rows;
    }
}
