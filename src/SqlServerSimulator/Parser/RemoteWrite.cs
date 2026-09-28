using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>Which statement a <see cref="RemoteWrite"/> replays.</summary>
internal enum RemoteWriteKind : byte
{
    Insert,
    Update,
    Delete,
}

/// <summary>
/// An INSERT, UPDATE or DELETE whose target is a linked server's table — named
/// four-part, or the rowset of an <c>OPENQUERY</c>. The statement runs locally
/// against <see cref="Proxy"/>, a table-variable-shaped stand-in holding the
/// remote rows (none for an INSERT), so every source shape, join, <c>TOP</c>,
/// conversion and truncation check the local engine has applies unchanged; once
/// it succeeds, <see cref="Replay"/> sends what it did to the server as
/// parameterized statements inside one remote transaction, where the server's
/// own constraints and triggers judge them.
/// </summary>
/// <remarks>
/// The replay follows what real sends through its provider (probed 2026-09-28
/// against SQL Server 2025, by counting a remote trigger's firings): an INSERT
/// row by row; an UPDATE or DELETE with no FROM clause as one statement, which
/// fires the server's trigger once even when it matched nothing; a joined one,
/// and every <c>OPENQUERY</c> write, row by row through a cursor. Rows are found
/// again by the key browse mode reports — the primary key, a unique constraint
/// or index, else a <c>rowversion</c> column — and on a table with none by
/// matching every comparable column, one row at a time.
/// </remarks>
internal sealed class RemoteWrite
{
    public readonly LinkedServer Server;

    public readonly RemoteWriteKind Kind;

    /// <summary>The bracketed name the replayed statements write.</summary>
    public readonly string TargetText;

    /// <summary>
    /// The database a four-part target lives in, which the replaying session
    /// starts in so a view binds there; null for an <c>OPENQUERY</c> target,
    /// whose session starts where a fresh one does.
    /// </summary>
    public string? DatabaseName;

    /// <summary>The local stand-in the statement writes.</summary>
    public readonly HeapTable Proxy;

    /// <summary>
    /// Per <see cref="Proxy"/> column, the remote column it writes, or null for
    /// an <c>OPENQUERY</c> column that reads an expression.
    /// </summary>
    public readonly HeapColumn?[] RemoteColumns;

    /// <summary>
    /// The four-part name as written, which a FROM source must spell to be
    /// this target; <see langword="null"/> for an <c>OPENQUERY</c> target.
    /// </summary>
    public readonly MultiPartName? WrittenName;

    /// <summary>An <c>OPENQUERY</c> target's pass-through query.</summary>
    public string? OpenQueryText;

    /// <summary>
    /// The columns an INSERT's column list names, in <see cref="Proxy"/>
    /// ordinals; null when it has none, which writes every column the server
    /// takes a value for.
    /// </summary>
    public List<int>? InsertColumns;

    /// <summary>An INSERT's <c>DEFAULT VALUES</c>, which sends no column.</summary>
    public bool InsertsDefaultValues;

    /// <summary>The columns an UPDATE's SET list assigns, in <see cref="Proxy"/> ordinals.</summary>
    public List<int>? SetColumns;

    /// <summary>
    /// An UPDATE or DELETE real ships as one statement: a four-part target and
    /// no FROM clause.
    /// </summary>
    public bool SingleStatement;

    private readonly Dictionary<(int PageIndex, int SlotIndex), SqlValue[]> originals = [];

    // The fetched rows' full layout — the proxy's columns, then any key or
    // rowversion column browse mode appended — and which of them find a row.
    private SqlType[] fetchedSchema = [];
    private int[] keyOrdinals = [];

    private RemoteWrite(LinkedServer server, RemoteWriteKind kind, string targetText, HeapTable proxy, HeapColumn?[] remoteColumns, MultiPartName? writtenName)
    {
        this.Server = server;
        this.Kind = kind;
        this.TargetText = targetText;
        this.Proxy = proxy;
        this.RemoteColumns = remoteColumns;
        this.WrittenName = writtenName;
    }

    /// <summary>
    /// Resolves <paramref name="name"/> — a four-part name, or a synonym for
    /// one — as the target of a write, and makes it the statement's
    /// <see cref="StatementContext.RemoteWrite"/>; null for any other name.
    /// Raises what real does resolving it: Msg 7202 for an unknown server,
    /// Msg 7411 when its <c>data access</c> is off, Msg 7313 for an omitted
    /// schema and Msg 7314 for a missing table, and — outside skip mode —
    /// the distributed transaction a write inside a local one needs.
    /// </summary>
    public static RemoteWrite? ForTarget(BatchContext batch, MultiPartName name, RemoteWriteKind kind)
    {
        name = batch.ExpandSynonym(name);
        if (name.Count < 4)
            return null;
        if (batch.CurrentStatement.RemoteWrite is { WrittenName: { } written } existing && written.ToString() == name.ToString())
            return existing;

        var server = ResolveServer(batch, name[0]);
        if (name.SchemaOmitted)
            throw SimulatedSqlException.RemoteSchemaOrCatalogInvalid(server);
        var target = server.Target;
        var databaseName = string.IsNullOrEmpty(name[1]) ? Simulation.DefaultDatabaseName : name[1];
        if (!target.Databases.TryGetValue(databaseName, out var database) || !database.Schemas.TryGetValue(name[2], out var schema))
            throw SimulatedSqlException.RemoteTableNotFound(server, QuotedName(name));

        // A view is written through as its projection reads.
        HeapColumn[] remoteColumns;
        string objectName;
        if (schema.HeapTables.TryGetValue(name.Leaf, out var table))
        {
            objectName = table.Name;
            remoteColumns = Array.FindAll(table.Columns, column => !column.IsHidden);
        }
        else if (schema.Views.TryGetValue(name.Leaf, out var view))
        {
            objectName = view.Name;
            remoteColumns = view.OutputColumns;
        }
        else
        {
            throw SimulatedSqlException.RemoteTableNotFound(server, QuotedName(name));
        }

        batch.HasSessionScopedReference = true;
        if (!batch.IsSkipping)
            RequireNoTransaction(batch, server);

        var write = new RemoteWrite(
            server,
            kind,
            $"{Bracket(database.Name)}.{Bracket(schema.Name)}.{Bracket(objectName)}",
            BuildProxy(objectName, remoteColumns, Array.ConvertAll(remoteColumns, column => ProxyColumn(column, column.Name)), database),
            [.. remoteColumns],
            name)
        {
            DatabaseName = database.Name,
        };
        batch.CurrentStatement.RemoteWrite = write;
        if (kind != RemoteWriteKind.Insert && !batch.IsSkipping)
            write.Load($"SELECT * FROM {write.TargetText}", database.Name);
        return write;
    }

    /// <summary>
    /// The target of an <c>INSERT</c> / <c>UPDATE</c> / <c>DELETE</c>
    /// <c>OPENQUERY(server, 'query')</c>: the query runs on the server under
    /// browse mode, whose metadata names the one table its columns write. A
    /// query the provider can't open an updatable cursor over — no table, or
    /// more than one — is Msg 16955 after the provider's Msg 7412.
    /// </summary>
    public static RemoteWrite ForOpenQuery(BatchContext batch, string serverName, string query, RemoteWriteKind kind)
    {
        // A joined write's FROM clause parses twice.
        if (batch.CurrentStatement.RemoteWrite is { OpenQueryText: { } text } existing && text == query && existing.Server.Name == serverName)
            return existing;
        var server = ResolveServer(batch, serverName);
        batch.HasSessionScopedReference = true;
        if (!batch.IsSkipping)
            RequireNoTransaction(batch, server);

        var result = RunRemoteQuery(server, query, database: null, browse: true)
            ?? throw new NotSupportedException($"OPENQUERY pass-through query on linked server '{server.Name}' returned no result set. Only queries that produce a result set are supported.");
        var browse = result.Browse;
        if (browse is not { Tables.Length: 1 })
        {
            // Real refuses the cursor when the statement runs.
            if (!batch.IsSkipping)
                throw CursorRefused(batch, server);
            var columns = new HeapColumn[result.Schema.Length];
            for (var i = 0; i < columns.Length; i++)
                columns[i] = new HeapColumn(result.ColumnNames[i], result.Schema[i], maxLength: null, nullable: true);
            var placeholder = new RemoteWrite(server, kind, "", new HeapTable(server.Name, columns, objectId: 0, isTableVariable: true), new HeapColumn?[columns.Length], writtenName: null);
            batch.CurrentStatement.RemoteWrite = placeholder;
            return placeholder;
        }

        var visible = VisibleColumnCount(result);
        var baseTable = ResolveBrowseTable(server.Target, browse.Tables[0]);
        var remoteColumns = new HeapColumn?[visible];
        var proxyColumns = new HeapColumn[visible];
        for (var i = 0; i < visible; i++)
        {
            var (tableNumber, status, baseName) = browse.Columns[i];
            var columnName = baseName ?? result.ColumnNames[i];
            HeapColumn? remoteColumn = null;
            if (tableNumber == 1 && (status & 0x04) == 0 && baseTable is not null)
            {
                foreach (var candidate in baseTable.Columns)
                {
                    if (Collation.Baseline.Equals(candidate.Name, columnName))
                    {
                        remoteColumn = candidate;
                        break;
                    }
                }
            }
            remoteColumns[i] = remoteColumn;
            proxyColumns[i] = remoteColumn is null
                ? new HeapColumn(result.ColumnNames[i], result.Schema[i], maxLength: null, nullable: true)
                : ProxyColumn(remoteColumn, result.ColumnNames[i]);
        }

        var targetParts = new StringBuilder();
        foreach (var part in browse.Tables[0])
        {
            if (targetParts.Length > 0)
                _ = targetParts.Append('.');
            _ = targetParts.Append(Bracket(part));
        }
        var write = new RemoteWrite(
            server,
            kind,
            targetParts.ToString(),
            BuildProxy(baseTable?.Name ?? browse.Tables[0][^1], remoteColumns, proxyColumns, baseTable?.OwningDatabase ?? batch.CurrentDatabase),
            remoteColumns,
            writtenName: null)
        {
            OpenQueryText = query,
        };
        batch.CurrentStatement.RemoteWrite = write;
        if (kind != RemoteWriteKind.Insert && !batch.IsSkipping)
            write.Fill(result);
        return write;
    }

    /// <summary>
    /// The active linked server <paramref name="name"/> names, checked for
    /// <c>data access</c>: Msg 7202 when there is none, Msg 7411 when its
    /// option is off.
    /// </summary>
    public static LinkedServer ResolveServer(BatchContext batch, string name)
    {
        if (!batch.Connection.Simulation.ActiveLinkedServers.TryGetValue(name, out var server))
            throw SimulatedSqlException.LinkedServerNotFound(name);
        if (!server.DataAccess)
            throw SimulatedSqlException.ServerNotConfiguredFor(server.Name, "DATA ACCESS");
        return server;
    }

    /// <summary>
    /// Refuses work on <paramref name="server"/> that would enlist it in the
    /// session's open transaction: real promotes the transaction to a
    /// distributed one, which out of the box no coordinator accepts — Msg 7391
    /// after the coordinator's refusal (Msg 7412) for a remote server, and
    /// Msg 3910 for a loopback. Real's defaults leave no configuration under
    /// which such a transaction commits, so none is modeled.
    /// </summary>
    public static void RequireNoTransaction(BatchContext batch, LinkedServer server)
    {
        if (batch.Connection.CurrentTransaction is null)
            return;
        if (server.IsLoopback(batch.Connection.Simulation))
            throw SimulatedSqlException.TransactionContextInUse();
        batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.DistributedTransactionRefusedMessage(batch, server));
        throw SimulatedSqlException.DistributedTransactionUnavailable(server);
    }

    /// <summary>
    /// Msg 16955 after the provider's Msg 7412, for an <c>OPENQUERY</c> write
    /// the provider can't open an updatable cursor for.
    /// </summary>
    public static SimulatedSqlException CursorRefused(BatchContext batch, LinkedServer server)
    {
        batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.MultipleStepOperationMessage(batch, server));
        return SimulatedSqlException.CouldNotCreateAcceptableCursor();
    }

    /// <summary>
    /// The <see cref="Proxy"/> ordinal of the column <paramref name="name"/>,
    /// or -1.
    /// </summary>
    public int ProxyOrdinal(string name)
    {
        var columns = this.Proxy.Columns;
        for (var i = 0; i < columns.Length; i++)
        {
            if (Collation.Baseline.Equals(columns[i].Name, name))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Refuses an UPDATE's SET column the server won't take a value for, as its
    /// provider reports the remote statement's compile error: Msg 8180 ahead of
    /// Msg 8102 for an identity column, Msg 271 for a computed one and Msg 272
    /// for a <c>rowversion</c>, and an <c>OPENQUERY</c> column that reads an
    /// expression as Msg 16955; and records the SET list for the replay.
    /// </summary>
    public void CheckSetColumns(BatchContext batch, List<string> names)
    {
        this.SetColumns = [];
        foreach (var name in names)
        {
            var ordinal = this.ProxyOrdinal(name);
            if (ordinal < 0)
                continue;
            this.SetColumns.Add(ordinal);
            if (this.RemoteColumns[ordinal] is not { } column)
            {
                // An OPENQUERY column that reads an expression has no cursor
                // position to write.
                if (!batch.IsSkipping)
                    throw CursorRefused(batch, this.Server);
                continue;
            }
            if (column.Identity is not null)
                throw SimulatedSqlException.RemoteStatementNotPrepared(SimulatedSqlException.CannotUpdateIdentityColumn(column.Name));
            if (column.Computed is not null)
                throw SimulatedSqlException.RemoteStatementNotPrepared(SimulatedSqlException.ColumnCannotBeModified(column.Name));
            if (column.Type == SqlType.RowVersion)
                throw SimulatedSqlException.RemoteStatementNotPrepared(SimulatedSqlException.CannotUpdateTimestampColumn());
        }
    }

    /// <summary>
    /// Records an INSERT's column list, refusing the identity column as the
    /// provider does — Msg 7344 after its Msg 7412 — since it writes rows
    /// through a rowset whose identity column takes no value.
    /// </summary>
    public void CheckInsertColumns(BatchContext batch, List<string> names)
    {
        this.InsertColumns = [];
        foreach (var name in names)
        {
            var ordinal = this.ProxyOrdinal(name);
            if (ordinal < 0)
                continue;
            this.InsertColumns.Add(ordinal);
            if (this.RemoteColumns[ordinal] is { Identity: not null } identity && !batch.IsSkipping)
            {
                batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.MultipleStepOperationMessage(batch, this.Server));
                var written = this.WrittenName is { } four ? $"[{four[0]}].[{four[1]}].[{four[2]}].[{four.Leaf}]" : this.TargetText;
                throw SimulatedSqlException.RemoteColumnNotWritable(this.Server, written, identity.Name);
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="query"/> on a fresh session of the server and
    /// returns its first result set, null when it produces none; an error the
    /// query raises there is thrown as the server raised it. The session
    /// starts in <paramref name="database"/> when one is given, so a view the
    /// query names binds in its own database; <paramref name="browse"/> runs it
    /// under <c>SET NO_BROWSETABLE ON</c>, which reports each column's base
    /// table and appends the key columns a cursor finds rows by.
    /// </summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The query is the caller's own pass-through text or a SELECT over identifiers the parser validated, bracket-escaped; it runs against a sibling in-process Simulation.")]
    internal static SimulatedSqlResultSet? RunRemoteQuery(LinkedServer server, string query, string? database, bool browse)
    {
        using var connection = server.Target.CreateDbConnection();
        connection.Open();
        if (database is not null)
            connection.ChangeDatabase(database);
        connection.NoBrowseTable = browse;
        using var command = connection.CreateCommand();
        command.CommandText = query;
        foreach (var outcome in server.Target.CreateResultSetsForCommand(command))
        {
            switch (outcome)
            {
                case SimulatedSqlResultSet result:
                    return result;
                case SimulatedErrorOutcome error:
                    throw error.Exception;
            }
        }
        return null;
    }

    private void Load(string query, string database)
    {
        if (RunRemoteQuery(this.Server, query, database, browse: true) is { } result)
            this.Fill(result);
        else
            this.Proxy.Heap.TouchedSlots = [];
    }

    /// <summary>
    /// Loads the fetched rows into <see cref="Proxy"/>, remembering each one's
    /// full fetched image by the address it landed at, and picks the columns
    /// that find a row again on the server.
    /// </summary>
    private void Fill(SimulatedSqlResultSet result)
    {
        this.fetchedSchema = result.Schema;
        var keys = new List<int>();
        if (result.Browse is { } browse)
        {
            for (var i = 0; i < browse.Columns.Length; i++)
            {
                if ((browse.Columns[i].Status & 0x08) != 0)
                    keys.Add(i);
            }
        }
        if (keys.Count == 0)
        {
            for (var i = 0; i < this.fetchedSchema.Length; i++)
            {
                if (this.fetchedSchema[i] == SqlType.RowVersion)
                {
                    keys.Add(i);
                    break;
                }
            }
        }
        this.keyOrdinals = [.. keys];

        var proxy = this.Proxy;
        var width = proxy.Columns.Length;
        this.hiddenNames = new string[this.fetchedSchema.Length - width];
        for (var i = 0; i < this.hiddenNames.Length; i++)
            this.hiddenNames[i] = result.Browse?.Columns[width + i].BaseName ?? result.ColumnNames[width + i];
        foreach (var bytes in result.RowBytes)
        {
            var fetched = RowDecoder.DecodeRow(result.Schema, bytes);
            var full = fetched.AsSpan(0, width).ToArray();
            var image = RowEncoder.EncodeRow(proxy.StoredColumns, Simulation.ProjectStoredValues(proxy, full), proxy.Heap);
            var address = proxy.Heap.Insert(image);
            this.originals[address] = fetched;
        }
        proxy.Heap.TouchedSlots = [];
    }

    /// <summary>
    /// Sends the statement's work to the server inside one remote transaction,
    /// committed once every statement succeeds; an error the server raises
    /// rolls it back and is relayed (<see cref="SimulatedSqlException.RelayedRemoteError"/>).
    /// An INSERT leaves <c>SCOPE_IDENTITY()</c> and <c>@@IDENTITY</c> NULL, as
    /// real's does whatever identity the server drew.
    /// </summary>
    public void Replay(BatchContext batch)
    {
        var statements = this.Kind switch
        {
            RemoteWriteKind.Insert => this.InsertStatements(),
            RemoteWriteKind.Update => this.UpdateStatements(),
            _ => this.DeleteStatements(),
        };
        if (this.Kind == RemoteWriteKind.Insert)
            batch.Connection.LastIdentity = null;
        if (statements.Count == 0)
            return;

        using var connection = this.Server.Target.CreateDbConnection();
        connection.Open();
        if (this.DatabaseName is not null)
            connection.ChangeDatabase(this.DatabaseName);
        using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var (text, parameters) in statements)
                Execute(connection, transaction, text, parameters);
            if (transaction.Connection is not null)
                transaction.Commit();
        }
        catch (SimulatedSqlException remote)
        {
            throw SimulatedSqlException.RelayedRemoteError(batch, remote);
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The statement is built from bracket-escaped identifiers the remote table's metadata supplied; every value travels as a parameter.")]
    private static void Execute(SimulatedDbConnection connection, SimulatedDbTransaction transaction, string text, List<SqlValue> parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@P" + i.ToString(CultureInfo.InvariantCulture);
            parameter.Value = parameters[i];
            _ = command.Parameters.Add(parameter);
        }
        _ = command.ExecuteNonQuery();
    }

    private List<(string Text, List<SqlValue> Parameters)> InsertStatements()
    {
        var columns = this.InsertColumns;
        if (columns is null)
        {
            columns = [];
            if (!this.InsertsDefaultValues)
            {
                for (var i = 0; i < this.Proxy.Columns.Length; i++)
                {
                    if (this.RemoteColumns[i] is { } column && column.Identity is null && column.Computed is null && column.Type != SqlType.RowVersion)
                        columns.Add(i);
                }
            }
        }

        var statements = new List<(string, List<SqlValue>)>();
        foreach (var (_, _, bytes) in this.Proxy.Heap.EnumerateRowsWithAddress())
        {
            var row = this.DecodeProxyRow(bytes);
            if (columns.Count == 0)
            {
                statements.Add(($"INSERT INTO {this.TargetText} DEFAULT VALUES", []));
                continue;
            }
            var text = new StringBuilder("INSERT INTO ").Append(this.TargetText).Append(" (");
            var parameters = new List<SqlValue>(columns.Count);
            for (var i = 0; i < columns.Count; i++)
            {
                if (i > 0)
                    _ = text.Append(", ");
                _ = text.Append(Bracket(this.WrittenColumnName(columns[i])));
                parameters.Add(row[columns[i]]);
            }
            _ = text.Append(") VALUES (");
            for (var i = 0; i < columns.Count; i++)
            {
                if (i > 0)
                    _ = text.Append(", ");
                _ = text.Append("@P").Append(i.ToString(CultureInfo.InvariantCulture));
            }
            statements.Add((text.Append(')').ToString(), parameters));
        }
        return statements;
    }

    private List<(string Text, List<SqlValue> Parameters)> UpdateStatements()
    {
        var setColumns = new List<int>();
        foreach (var ordinal in this.SetColumns ?? [])
        {
            if (this.RemoteColumns[ordinal] is not null && !setColumns.Contains(ordinal))
                setColumns.Add(ordinal);
        }
        if (setColumns.Count == 0)
            return [];

        var touched = this.TouchedRows();
        var statements = new List<(string, List<SqlValue>)>();
        if (this.SingleStatement && (this.keyOrdinals.Length > 0 || touched.Count == 0))
        {
            if (touched.Count == 0)
            {
                var name = Bracket(this.WrittenColumnName(setColumns[0]));
                statements.Add(($"UPDATE {this.TargetText} SET {name} = {name} WHERE 1 = 0", []));
                return statements;
            }
            var text = new StringBuilder("UPDATE x SET ");
            for (var i = 0; i < setColumns.Count; i++)
            {
                if (i > 0)
                    _ = text.Append(", ");
                _ = text.Append("x.").Append(Bracket(this.WrittenColumnName(setColumns[i]))).Append(" = v.[n").Append(i.ToString(CultureInfo.InvariantCulture)).Append(']');
            }
            _ = text.Append(" FROM ").Append(this.TargetText).Append(" AS x JOIN (VALUES ");
            var parameters = new List<SqlValue>();
            for (var r = 0; r < touched.Count; r++)
            {
                var (address, original) = touched[r];
                var row = this.DecodeProxyRow(this.Proxy.Heap.ReadSlotBytes(address.PageIndex, address.SlotIndex)!);
                _ = text.Append(r > 0 ? ", (" : "(");
                var values = new List<SqlValue>();
                foreach (var key in this.keyOrdinals)
                    values.Add(original[key]);
                foreach (var column in setColumns)
                    values.Add(row[column]);
                AppendParameters(text, parameters, values);
                _ = text.Append(')');
            }
            _ = text.Append(") AS v(");
            AppendJoinColumns(text, setColumns.Count);
            _ = text.Append(") ON ");
            this.AppendKeyJoin(text);
            statements.Add((text.ToString(), parameters));
            return statements;
        }

        foreach (var (address, original) in touched)
        {
            var row = this.DecodeProxyRow(this.Proxy.Heap.ReadSlotBytes(address.PageIndex, address.SlotIndex)!);
            var parameters = new List<SqlValue>();
            var text = new StringBuilder(this.keyOrdinals.Length > 0 ? "UPDATE " : "UPDATE TOP (1) ").Append(this.TargetText).Append(" SET ");
            for (var i = 0; i < setColumns.Count; i++)
            {
                if (i > 0)
                    _ = text.Append(", ");
                _ = text.Append(Bracket(this.WrittenColumnName(setColumns[i]))).Append(" = ");
                AppendParameter(text, parameters, row[setColumns[i]]);
            }
            _ = text.Append(" WHERE ");
            this.AppendRowMatch(text, parameters, original);
            statements.Add((text.ToString(), parameters));
        }
        return statements;
    }

    private List<(string Text, List<SqlValue> Parameters)> DeleteStatements()
    {
        var touched = this.TouchedRows();
        var statements = new List<(string, List<SqlValue>)>();
        if (this.SingleStatement && (this.keyOrdinals.Length > 0 || touched.Count == 0))
        {
            if (touched.Count == 0)
            {
                statements.Add(($"DELETE FROM {this.TargetText} WHERE 1 = 0", []));
                return statements;
            }
            var text = new StringBuilder("DELETE x FROM ").Append(this.TargetText).Append(" AS x JOIN (VALUES ");
            var parameters = new List<SqlValue>();
            for (var r = 0; r < touched.Count; r++)
            {
                _ = text.Append(r > 0 ? ", (" : "(");
                var values = new List<SqlValue>();
                foreach (var key in this.keyOrdinals)
                    values.Add(touched[r].Original[key]);
                AppendParameters(text, parameters, values);
                _ = text.Append(')');
            }
            _ = text.Append(") AS v(");
            AppendJoinColumns(text, 0);
            _ = text.Append(") ON ");
            this.AppendKeyJoin(text);
            statements.Add((text.ToString(), parameters));
            return statements;
        }

        foreach (var (_, original) in touched)
        {
            var parameters = new List<SqlValue>();
            var text = new StringBuilder(this.keyOrdinals.Length > 0 ? "DELETE FROM " : "DELETE TOP (1) FROM ").Append(this.TargetText).Append(" WHERE ");
            this.AppendRowMatch(text, parameters, original);
            statements.Add((text.ToString(), parameters));
        }
        return statements;
    }

    // The fetched rows the statement reached, in the order the server sent them.
    private List<((int PageIndex, int SlotIndex) Address, SqlValue[] Original)> TouchedRows()
    {
        var touched = this.Proxy.Heap.TouchedSlots ?? [];
        var rows = new List<((int, int), SqlValue[])>();
        foreach (var (address, original) in this.originals)
        {
            if (touched.Contains(address))
                rows.Add((address, original));
        }
        return rows;
    }

    private SqlValue[] DecodeProxyRow(byte[] bytes)
    {
        var proxy = this.Proxy;
        var values = new SqlValue[proxy.Columns.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var ordinal = proxy.StorageOrdinals[i];
            values[i] = ordinal < 0
                ? SqlValue.Null(proxy.Columns[i].Type)
                : RowDecoder.DecodeColumn(proxy.StoredColumns, bytes, ordinal, proxy.Heap);
        }
        return values;
    }

    private string WrittenColumnName(int proxyOrdinal) =>
        this.RemoteColumns[proxyOrdinal]?.Name ?? this.Proxy.Columns[proxyOrdinal].Name;

    // The fetched column a key ordinal or match column names on the server.
    private string FetchedColumnName(int fetchedOrdinal) =>
        fetchedOrdinal < this.Proxy.Columns.Length
            ? this.WrittenColumnName(fetchedOrdinal)
            : this.hiddenNames[fetchedOrdinal - this.Proxy.Columns.Length];

    private string[] hiddenNames = [];

    private void AppendKeyJoin(StringBuilder text)
    {
        for (var k = 0; k < this.keyOrdinals.Length; k++)
        {
            if (k > 0)
                _ = text.Append(" AND ");
            _ = text.Append("x.").Append(Bracket(this.FetchedColumnName(this.keyOrdinals[k]))).Append(" = v.[k").Append(k.ToString(CultureInfo.InvariantCulture)).Append(']');
        }
    }

    private void AppendJoinColumns(StringBuilder text, int setCount)
    {
        for (var k = 0; k < this.keyOrdinals.Length; k++)
            _ = text.Append(k > 0 ? ", [k" : "[k").Append(k.ToString(CultureInfo.InvariantCulture)).Append(']');
        for (var i = 0; i < setCount; i++)
            _ = text.Append(", [n").Append(i.ToString(CultureInfo.InvariantCulture)).Append(']');
    }

    private void AppendRowMatch(StringBuilder text, List<SqlValue> parameters, SqlValue[] original)
    {
        if (this.keyOrdinals.Length > 0)
        {
            for (var k = 0; k < this.keyOrdinals.Length; k++)
            {
                if (k > 0)
                    _ = text.Append(" AND ");
                _ = text.Append(Bracket(this.FetchedColumnName(this.keyOrdinals[k]))).Append(" = ");
                AppendParameter(text, parameters, original[this.keyOrdinals[k]]);
            }
            return;
        }

        var any = false;
        for (var i = 0; i < this.Proxy.Columns.Length; i++)
        {
            if (this.RemoteColumns[i] is not { } column || column.Computed is not null || !IsComparable(this.fetchedSchema[i]))
                continue;
            if (any)
                _ = text.Append(" AND ");
            any = true;
            var name = Bracket(column.Name);
            var parameter = "@P" + parameters.Count.ToString(CultureInfo.InvariantCulture);
            parameters.Add(original[i]);
            _ = this.fetchedSchema[i].PairClass is TypePairClass.AnsiString or TypePairClass.UnicodeString
                ? text.Append("(CONVERT(varbinary(max), ").Append(name).Append(") = CONVERT(varbinary(max), ").Append(parameter).Append(')')
                : text.Append('(').Append(name).Append(" = ").Append(parameter);
            _ = text.Append(" OR (").Append(name).Append(" IS NULL AND ").Append(parameter).Append(" IS NULL))");
        }
        if (!any)
            throw new NotSupportedException($"A write through linked server '{this.Server.Name}' to {this.TargetText}, which has no key and no comparable column to find its rows by, isn't modeled.");
    }

    private static void AppendParameters(StringBuilder text, List<SqlValue> parameters, List<SqlValue> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
                _ = text.Append(", ");
            AppendParameter(text, parameters, values[i]);
        }
    }

    private static void AppendParameter(StringBuilder text, List<SqlValue> parameters, SqlValue value)
    {
        _ = text.Append("@P").Append(parameters.Count.ToString(CultureInfo.InvariantCulture));
        parameters.Add(value);
    }

    // What real's = and IS NULL can match a row by.
    private static bool IsComparable(SqlType type) => type.PairClass is not (
        TypePairClass.Text or TypePairClass.Image or TypePairClass.Xml or TypePairClass.Spatial or TypePairClass.Vector or TypePairClass.Json);

    private static HeapTable BuildProxy(string name, HeapColumn?[] remoteColumns, HeapColumn[] columns, Database remoteDatabase)
    {
        var proxy = new HeapTable(name, columns, objectId: 0, isTableVariable: true);
        // The rowversion values the stand-in draws are thrown away, so they
        // come from a counter of its own rather than a real database's.
        foreach (var column in remoteColumns)
        {
            if (column?.Type == SqlType.RowVersion)
            {
                proxy.OwningDatabase = new Database(remoteDatabase.Name, remoteDatabase.Collation);
                break;
            }
        }
        return proxy;
    }

    // A remote column as the stand-in holds it: nullable and unconstrained,
    // since the server judges NULLs, keys and checks; an identity or
    // rowversion column still draws values locally so an INSERT's column count
    // reads it as real's does, and a computed column stays out of the stored
    // row.
    private static HeapColumn ProxyColumn(HeapColumn remote, string name) =>
        new(
            name,
            remote.Type,
            remote.MaxLength,
            nullable: true,
            identity: remote.Identity is null ? null : new IdentityState(1, 1),
            computedExpression: remote.Computed is null ? null : new Value(SqlValue.Null(remote.Type)),
            collation: remote.Collation);

    private static int VisibleColumnCount(SimulatedSqlResultSet result)
    {
        var count = result.Schema.Length;
        if (result.Browse is { } browse)
        {
            while (count > 0 && (browse.Columns[count - 1].Status & 0x10) != 0)
                count--;
        }
        return count;
    }

    // The table a browse-mode result names, as the server resolves it from a
    // fresh session.
    private static HeapTable? ResolveBrowseTable(Simulation target, string[] parts)
    {
        var databaseName = parts.Length >= 3 && parts[^3].Length > 0 ? parts[^3] : Simulation.DefaultDatabaseName;
        var schemaName = parts.Length >= 2 && parts[^2].Length > 0 ? parts[^2] : Database.DefaultSchemaName;
        return target.Databases.TryGetValue(databaseName, out var database)
            && database.Schemas.TryGetValue(schemaName, out var schema)
            && schema.HeapTables.TryGetValue(parts[^1], out var table)
                ? table
                : null;
    }

    internal static string Bracket(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    // Msg 7314's name: every written segment past the server's, each in double quotes.
    internal static string QuotedName(MultiPartName name)
    {
        var text = new StringBuilder();
        for (var i = 1; i < name.Count; i++)
        {
            if (name[i].Length == 0 || (i == 2 && name.SchemaOmitted))
                continue;
            if (text.Length > 0)
                _ = text.Append('.');
            _ = text.Append('"').Append(name[i]).Append('"');
        }
        return text.ToString();
    }
}
