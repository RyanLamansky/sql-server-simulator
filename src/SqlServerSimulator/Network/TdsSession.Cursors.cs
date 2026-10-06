using System.Data;
using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Network;

internal sealed partial class TdsSession
{
    /// <summary>
    /// Open API-server cursors, keyed by the integer handle handed to the
    /// client through <c>sp_cursoropen</c> / <c>sp_cursorprepexec</c> /
    /// <c>sp_cursorexecute</c>. These are wire-protocol state (the in-process
    /// ADO surface never uses API cursors), so they live on the session rather
    /// than the engine. Each wraps an engine <see cref="Cursor"/> registered
    /// under an opaque name in the connection's global cursor map so the
    /// positioned-DML <c>WHERE CURRENT OF</c> machinery resolves it.
    /// </summary>
    private readonly Dictionary<int, ApiCursor> apiCursors = [];

    /// <summary>Prepared cursor statements from sp_cursorprepare / sp_cursorprepexec, keyed by handle.</summary>
    private readonly Dictionary<int, PreparedCursor> preparedCursors = [];

    private int nextApiCursorHandle = 180150001;
    private int nextCursorPrepHandle = 0x40000001;

    /// <summary>One open API-server cursor plus the RIDs of its last fetch buffer.</summary>
    private sealed class ApiCursor(int handle, string internalName, Cursor cursor, List<TdsRpcParameter> boundParameters)
    {
        public readonly int Handle = handle;

        /// <summary>The opaque name the engine cursor is registered under in <see cref="SimulatedDbConnection.Cursors"/>.</summary>
        public readonly string InternalName = internalName;

        public readonly Cursor Cursor = cursor;

        /// <summary>
        /// The parameter values bound at open. A KEYSET / DYNAMIC cursor re-runs
        /// its SELECT on every fetch, so the same bindings must ride each fetch
        /// batch (real cursors freeze the parameter values at open).
        /// </summary>
        public readonly List<TdsRpcParameter> BoundParameters = boundParameters;

        /// <summary>Stable per-source RIDs of the rows delivered by the most recent fetch,
        /// in fetch order (one slot per FROM source, so a join cursor's positioned edit
        /// reaches every participating row). A positioned <c>sp_cursor</c> op indexes into
        /// this (1-based) via its rownum.</summary>
        public readonly List<(int Page, int Slot)?[]> Buffer = [];

        /// <summary>1-based absolute row number of the last row the last fetch landed on (for the INFO fetch's position report).</summary>
        public int CurrentRowNumber;
    }

    private sealed class PreparedCursor(string statement, List<string> parameterNames)
    {
        public readonly string Statement = statement;
        public readonly List<string> ParameterNames = parameterNames;
    }

    private void DispatchCursorRpc(ushort procId, TdsRpcRequest request, TdsTokenWriter writer, bool moreRequests)
    {
        switch (procId)
        {
            case Tds.ProcIdCursor:
                this.CursorOp(request, writer, moreRequests);
                break;
            case Tds.ProcIdCursorOpen:
                this.CursorOpen(request, writer, moreRequests);
                break;
            case Tds.ProcIdCursorPrepare:
                this.CursorPrepare(request, writer, moreRequests);
                break;
            case Tds.ProcIdCursorExecute:
                this.CursorExecute(request, writer, moreRequests);
                break;
            case Tds.ProcIdCursorPrepExec:
                this.CursorPrepExec(request, writer, moreRequests);
                break;
            case Tds.ProcIdCursorUnprepare:
                this.CursorUnprepare(request, writer, moreRequests);
                break;
            case Tds.ProcIdCursorFetch:
                this.CursorFetch(request, writer, moreRequests);
                break;
            case Tds.ProcIdCursorClose:
                this.CursorClose(request, writer, moreRequests);
                break;
            default: // ProcIdCursorOption — accepted and ignored (see docs).
                writer.WriteReturnStatus(0);
                this.CompleteCursorRpc(writer, moreRequests, error: false);
                break;
        }
    }

    // ---- sp_cursoropen ----------------------------------------------------

    private void CursorOpen(TdsRpcRequest request, TdsTokenWriter writer, bool moreRequests)
    {
        var parameters = request.Parameters;
        var statement = AsString(parameters, 1);
        var scrollopt = AsInt(parameters, 2);
        var ccopt = AsInt(parameters, 3);
        this.OpenAndAnnounce(request, writer, moreRequests, statement, scrollopt, ccopt, cursorOrdinal: 0, scrollOrdinal: 2, ccOrdinal: 3, rowcountOrdinal: 4, boundStart: 5);
    }

    // ---- sp_cursorprepexec ------------------------------------------------

    private void CursorPrepExec(TdsRpcRequest request, TdsTokenWriter writer, bool moreRequests)
    {
        var parameters = request.Parameters;
        var declaration = AsString(parameters, 2);
        var statement = AsString(parameters, 3);
        var scrollopt = AsInt(parameters, 4);
        var ccopt = AsInt(parameters, 5);

        var prepHandle = this.nextCursorPrepHandle++;
        var prepared = new PreparedCursor(statement, ParseDeclarationNames(declaration));
        this.preparedCursors[prepHandle] = prepared;

        // The value params (boundStart 7+) arrive positional/unnamed from native
        // ODBC / OLE DB drivers; name them from the prepared declaration, the
        // same mapping sp_cursorexecute applies on the re-execute path.
        var extraReturns = new List<(ushort Ordinal, string Name, object? Value)> { (0, parameters[0].Name, prepHandle) };
        this.OpenAndAnnounce(request, writer, moreRequests, statement, scrollopt, ccopt, cursorOrdinal: 1, scrollOrdinal: 4, ccOrdinal: 5, rowcountOrdinal: 6, boundStart: 7, extraReturns, preparedNames: prepared.ParameterNames);
    }

    // ---- sp_cursorexecute -------------------------------------------------

    private void CursorExecute(TdsRpcRequest request, TdsTokenWriter writer, bool moreRequests)
    {
        var parameters = request.Parameters;
        var prepHandle = AsInt(parameters, 0);
        if (!this.preparedCursors.TryGetValue(prepHandle, out var prepared))
        {
            writer.WriteErrorOrInfo(Tds.TokenError, 8179, 8, 16, $"Could not find prepared statement with handle {prepHandle}.", "SIMULATED", "", 1);
            writer.WriteReturnStatus(8179);
            this.CompleteCursorRpc(writer, moreRequests, error: true);
            return;
        }

        var scrollopt = AsInt(parameters, 2);
        var ccopt = AsInt(parameters, 3);
        this.OpenAndAnnounce(request, writer, moreRequests, prepared.Statement, scrollopt, ccopt, cursorOrdinal: 1, scrollOrdinal: 2, ccOrdinal: 3, rowcountOrdinal: 4, boundStart: 5, preparedNames: prepared.ParameterNames);
    }

    // ---- sp_cursorprepare -------------------------------------------------

    private void CursorPrepare(TdsRpcRequest request, TdsTokenWriter writer, bool moreRequests)
    {
        var parameters = request.Parameters;
        var declaration = AsString(parameters, 2);
        var statement = AsString(parameters, 3);
        var prepHandle = this.nextCursorPrepHandle++;
        this.preparedCursors[prepHandle] = new PreparedCursor(statement, ParseDeclarationNames(declaration));

        TdsTypeCodec.WriteReturnValue(writer, 0, parameters[0].Name, DbType.Int32, prepHandle);
        writer.WriteReturnStatus(0);
        this.CompleteCursorRpc(writer, moreRequests, error: false);
    }

    // ---- sp_cursorunprepare -----------------------------------------------

    private void CursorUnprepare(TdsRpcRequest request, TdsTokenWriter writer, bool moreRequests)
    {
        var prepHandle = AsInt(request.Parameters, 0);
        if (!this.preparedCursors.Remove(prepHandle))
        {
            writer.WriteErrorOrInfo(Tds.TokenError, 8179, 8, 16, $"Could not find prepared statement with handle {prepHandle}.", "SIMULATED", "", 1);
            writer.WriteReturnStatus(8179);
            this.CompleteCursorRpc(writer, moreRequests, error: true);
            return;
        }

        writer.WriteReturnStatus(0);
        this.CompleteCursorRpc(writer, moreRequests, error: false);
    }

    /// <summary>
    /// Shared open path for sp_cursoropen / sp_cursorprepexec / sp_cursorexecute:
    /// builds the engine cursor, opens it, and writes the metadata-only announce
    /// (COLMETADATA + a trailing ROWSTAT column, zero rows) plus the downgraded
    /// scrollopt/ccopt and the rowcount output parameters.
    /// </summary>
    private void OpenAndAnnounce(
        TdsRpcRequest request,
        TdsTokenWriter writer,
        bool moreRequests,
        string statement,
        int scrollopt,
        int ccopt,
        int cursorOrdinal,
        int scrollOrdinal,
        int ccOrdinal,
        int rowcountOrdinal,
        int boundStart,
        List<(ushort Ordinal, string Name, object? Value)>? extraReturns = null,
        List<string>? preparedNames = null)
    {
        var parameters = request.Parameters;
        var boundParameters = BindTail(parameters, boundStart, preparedNames);

        // A STATIC or FAST_FORWARD request takes only READ_ONLY concurrency:
        // anything else is Msg 16966 from sp_cursoropen's line 1, return
        // status 1, no handle and a NULL row count (probed 2026-09-29 against
        // SQL Server 2025).
        if ((scrollopt & 0x1F) is 0x8 or 0x10 && (ccopt & 0xF) is 0x2 or 0x4 or 0x8)
        {
            var refusal = SimulatedSqlException.ApiCursorConcurrencyIncompatible(ccopt & 0xF).Errors[0];
            writer.WriteErrorOrInfo(Tds.TokenError, refusal.Number, refusal.State, refusal.Class, refusal.Message, TdsSession.ServerName, "sp_cursoropen", 1);
            writer.WriteReturnStatus(1);
            var refused = extraReturns is null ? [] : new List<(ushort, string, object?)>(extraReturns);
            refused.Add(((ushort)cursorOrdinal, parameters[cursorOrdinal].Name, 0));
            refused.Add(((ushort)scrollOrdinal, parameters[scrollOrdinal].Name, scrollopt));
            refused.Add(((ushort)ccOrdinal, parameters[ccOrdinal].Name, ccopt));
            refused.Add(((ushort)rowcountOrdinal, parameters[rowcountOrdinal].Name, null));
            foreach (var (ordinal, pname, value) in refused)
                TdsTypeCodec.WriteReturnValue(writer, ordinal, pname, DbType.Int32, value);
            this.CompleteCursorRpc(writer, moreRequests, error: true);
            return;
        }

        // PARAMETERIZED_STMT (0x1000) on an sp_cursoropen carrying no parameter
        // definition is Msg 16902 state 22: no handle, the options echoed and
        // the row count as sent (probed 2026-10-06 against SQL Server 2025).
        if ((scrollopt & 0x1000) != 0 && cursorOrdinal == 0 && parameters.Count <= boundStart)
        {
            writer.WriteErrorOrInfo(Tds.TokenError, 16902, 22, 16, "sp_cursoropen: The value of the parameter 'scrollopt' is invalid.", TdsSession.ServerName, "sp_cursoropen", 1);
            writer.WriteReturnStatus(1);
            TdsTypeCodec.WriteReturnValue(writer, (ushort)cursorOrdinal, parameters[cursorOrdinal].Name, DbType.Int32, 0);
            TdsTypeCodec.WriteReturnValue(writer, (ushort)scrollOrdinal, parameters[scrollOrdinal].Name, DbType.Int32, scrollopt);
            TdsTypeCodec.WriteReturnValue(writer, (ushort)ccOrdinal, parameters[ccOrdinal].Name, DbType.Int32, ccopt);
            TdsTypeCodec.WriteReturnValue(writer, (ushort)rowcountOrdinal, parameters[rowcountOrdinal].Name, DbType.Int32, parameters[rowcountOrdinal].Value);
            this.CompleteCursorRpc(writer, moreRequests, error: true);
            return;
        }

        var connection = this.connection!;
        var name = "sss_apicursor_" + this.nextApiCursorHandle.ToString(CultureInfo.InvariantCulture);
        var declareOpen = $"DECLARE {name} CURSOR {CursorOptionKeywords(scrollopt, ccopt)} FOR {statement};\nOPEN {name};";

        Cursor cursor;
        try
        {
            using var command = connection.CreateCommand();
#pragma warning disable CA2100 // This IS a SQL endpoint: the statement is the client's query by design.
            command.CommandText = declareOpen;
#pragma warning restore CA2100
            // An API server cursor keeps KEYSET over a table with no unique
            // index where a T-SQL one converts to a read-only snapshot
            // (probe-confirmed), so the synthesized DECLARE says which it is.
            command.ApiServerCursor = true;
            foreach (var wire in boundParameters)
                _ = AddParameter(command, wire);
            _ = command.ExecuteNonQuery();
            cursor = connection.Cursors[name];
        }
        catch (SimulatedSqlException ex)
        {
            _ = connection.Cursors.Remove(name);
            foreach (var error in ex.Errors)
                writer.WriteErrorOrInfo(Tds.TokenError, error.Number, error.State, error.Class, error.Message, TdsSession.ServerName, error.Procedure, error.LineNumber);
            writer.WriteErrorOrInfo(Tds.TokenError, 16945, 2, 16, "The cursor was not declared.", "SIMULATED", "", 1);

            // Echo the requested option values; the handle comes back zero.
            var failOut = extraReturns is null ? [] : new List<(ushort, string, object?)>(extraReturns);
            failOut.Add(((ushort)cursorOrdinal, parameters[cursorOrdinal].Name, 0));
            failOut.Add(((ushort)scrollOrdinal, parameters[scrollOrdinal].Name, scrollopt & 0x1F));
            failOut.Add(((ushort)ccOrdinal, parameters[ccOrdinal].Name, ccopt & 0xF));
            failOut.Add(((ushort)rowcountOrdinal, parameters[rowcountOrdinal].Name, 0));
            writer.WriteReturnStatus(ex.Errors[0].Number);
            foreach (var (ordinal, pname, value) in failOut)
                TdsTypeCodec.WriteReturnValue(writer, ordinal, pname, DbType.Int32, value);
            this.CompleteCursorRpc(writer, moreRequests, error: true);
            return;
        }

        var (effScroll, effCc, rowcount) = ResolveEffectiveOptions(cursor, scrollopt, ccopt, connection.LastCursorRows);

        // CHECK_ACCEPTED_TYPES (0x8000): the type the cursor settled on must be
        // one of the *_ACCEPTABLE bits (KEYSET 0x10000 … FAST_FORWARD
        // 0x100000, each its type's bit shifted 16), else the open is Msg 16955
        // and Msg 16945, returning 16955 with no handle, the options echoed and
        // a zero row count (probed 2026-10-06 against SQL Server 2025).
        if ((scrollopt & 0x8000) != 0 && (scrollopt & ((effScroll & 0x1F) << 16)) == 0)
        {
            using (var drop = connection.CreateCommand())
            {
#pragma warning disable CA2100 // The name is the endpoint's own synthesized cursor name.
                drop.CommandText = $"DEALLOCATE {name};";
#pragma warning restore CA2100
                _ = drop.ExecuteNonQuery();
            }
            writer.WriteErrorOrInfo(Tds.TokenError, 16955, 2, 16, "Could not create an acceptable cursor.", TdsSession.ServerName, "sp_cursoropen", 1);
            writer.WriteErrorOrInfo(Tds.TokenError, 16945, 2, 16, "The cursor was not declared.", TdsSession.ServerName, "sp_cursoropen", 1);
            writer.WriteReturnStatus(16955);
            if (extraReturns is not null)
            {
                foreach (var (ordinal, pname, value) in extraReturns)
                    TdsTypeCodec.WriteReturnValue(writer, ordinal, pname, DbType.Int32, value);
            }
            TdsTypeCodec.WriteReturnValue(writer, (ushort)cursorOrdinal, parameters[cursorOrdinal].Name, DbType.Int32, 0);
            TdsTypeCodec.WriteReturnValue(writer, (ushort)scrollOrdinal, parameters[scrollOrdinal].Name, DbType.Int32, scrollopt);
            TdsTypeCodec.WriteReturnValue(writer, (ushort)ccOrdinal, parameters[ccOrdinal].Name, DbType.Int32, ccopt);
            TdsTypeCodec.WriteReturnValue(writer, (ushort)rowcountOrdinal, parameters[rowcountOrdinal].Name, DbType.Int32, 0);
            this.CompleteCursorRpc(writer, moreRequests, error: true);
            return;
        }

        var handle = this.nextApiCursorHandle++;
        var api = new ApiCursor(handle, name, cursor, boundParameters);
        this.apiCursors[handle] = api;

        // AUTO_FETCH (0x2000) fetches the first rows with the open — as many as
        // the row count sent, else 20 — and reports how many it fetched as the
        // row count, whatever the cursor's type (probed 2026-10-06 against SQL
        // Server 2025).
        List<SqlValue[]>? fetched = null;
        if ((scrollopt & 0x2000) != 0)
        {
            var wanted = AsInt(parameters, rowcountOrdinal);
            fetched = this.FetchIntoBuffer(api, FetchDirection.Next, 0, wanted > 0 ? wanted : 20);
            rowcount = fetched.Count;
        }

        WriteCursorMetadata(writer, cursor, fetched);

        writer.WriteReturnStatus(0);
        if (extraReturns is not null)
        {
            foreach (var (ordinal, pname, value) in extraReturns)
                TdsTypeCodec.WriteReturnValue(writer, ordinal, pname, DbType.Int32, value);
        }
        TdsTypeCodec.WriteReturnValue(writer, (ushort)cursorOrdinal, parameters[cursorOrdinal].Name, DbType.Int32, handle);
        TdsTypeCodec.WriteReturnValue(writer, (ushort)scrollOrdinal, parameters[scrollOrdinal].Name, DbType.Int32, effScroll);
        TdsTypeCodec.WriteReturnValue(writer, (ushort)ccOrdinal, parameters[ccOrdinal].Name, DbType.Int32, effCc);
        TdsTypeCodec.WriteReturnValue(writer, (ushort)rowcountOrdinal, parameters[rowcountOrdinal].Name, DbType.Int32, rowcount);
        this.CompleteCursorRpc(writer, moreRequests, error: false);
    }

    // ---- sp_cursorfetch ---------------------------------------------------

    private void CursorFetch(TdsRpcRequest request, TdsTokenWriter writer, bool moreRequests)
    {
        var parameters = request.Parameters;
        var handle = AsInt(parameters, 0);
        var fetchType = AsInt(parameters, 1);
        var rownum = parameters.Count > 2 ? AsInt(parameters, 2) : 0;
        var nrows = parameters.Count > 3 ? AsInt(parameters, 3) : 1;

        if (!this.apiCursors.TryGetValue(handle, out var api))
        {
            WriteInvalidHandle(writer, "sp_cursorfetch", handle);
            EchoFetchOutputs(writer, parameters, rownum, nrows);
            this.CompleteCursorRpc(writer, moreRequests, error: true);
            return;
        }

        // INFO: no rows; report the current 1-based position and total row count.
        if ((fetchType & 0x100) != 0)
        {
            writer.WriteReturnStatus(0);
            EchoFetchOutputs(writer, parameters, api.CurrentRowNumber, this.connection!.LastCursorRows);
            this.CompleteCursorRpc(writer, moreRequests, error: false);
            return;
        }

        var (firstDirection, offset) = MapFetchType(fetchType, rownum);
        var rows = this.FetchIntoBuffer(api, firstDirection, offset, nrows);

        WriteCursorMetadata(writer, api.Cursor, rows);
        writer.WriteReturnStatus(0);
        this.CompleteCursorRpc(writer, moreRequests, error: false);
    }

    /// <summary>
    /// Fetches up to <paramref name="nrows"/> rows into <paramref name="api"/>'s
    /// buffer, the first in <paramref name="firstDirection"/> and the rest
    /// NEXT, each carrying its ROWSTAT.
    /// </summary>
    private List<SqlValue[]> FetchIntoBuffer(ApiCursor api, FetchDirection firstDirection, long offset, int nrows)
    {
        api.Buffer.Clear();
        var rows = new List<SqlValue[]>();
        using var fetchCommand = this.connection!.CreateCommand();
        fetchCommand.CommandText = " ";
        foreach (var wire in api.BoundParameters)
            _ = AddParameter(fetchCommand, wire);
        var batch = new BatchContext(fetchCommand);
        var start = -1;
        for (var i = 0; i < nrows; i++)
        {
            var direction = i == 0 ? firstDirection : FetchDirection.Next;
            var (status, values) = api.Cursor.Fetch(batch, direction, offset);
            if (i == 0 && status is 0 or -2)
                start = api.Cursor.FetchBufferStart;
            if (status == -2)
            {
                // A keyset member deleted out from under the cursor still
                // fills its buffer row, marked ROWSTAT 2, and the fetch
                // carries on past it (probed 2026-09-29 against SQL Server
                // 2025).
                rows.Add(Cursor.WithRowStat(api.Cursor.DeletedMemberValues(), 2));
                api.Buffer.Add(new (int Page, int Slot)?[api.Cursor.BaseTables.Length]);
                api.CurrentRowNumber += 1;
                continue;
            }
            if (status != 0 || values is null)
                break;
            rows.Add(Cursor.WithRowStat(values, 1));
            if (api.Cursor.CurrentRids is { } rids)
                api.Buffer.Add(rids);
            api.CurrentRowNumber += 1;
        }
        api.Cursor.ApiFetchBuffer = (rows.Count, rows.Count == 0 ? -1 : start);
        return rows;
    }

    // ---- sp_cursor (positioned UPDATE / DELETE / SETPOSITION) -------------

    private void CursorOp(TdsRpcRequest request, TdsTokenWriter writer, bool moreRequests)
    {
        var parameters = request.Parameters;
        var handle = AsInt(parameters, 0);
        var optype = AsInt(parameters, 1);
        var rownum = AsInt(parameters, 2);

        if (!this.apiCursors.TryGetValue(handle, out var api))
        {
            WriteInvalidHandle(writer, "sp_cursor", handle);
            this.CompleteCursorRpc(writer, moreRequests, error: true);
            return;
        }

        if (api.Buffer.Count == 0)
        {
            WriteFetchBufferError(writer, 16931, "There are no rows in the current fetch buffer.");
            this.CompleteCursorRpc(writer, moreRequests, error: true);
            return;
        }

        if (rownum < 1 || rownum > api.Buffer.Count)
        {
            WriteFetchBufferError(writer, 16930, "The requested row is not in the fetch buffer.");
            this.CompleteCursorRpc(writer, moreRequests, error: true);
            return;
        }

        // Position the engine cursor on the requested buffer row so its
        // WHERE CURRENT OF matching targets it; SETPOSITION (0x20) stops here.
        api.Cursor.CurrentRids = api.Buffer[rownum - 1];
        if ((optype & 0x20) != 0 && (optype & 0x3) == 0)
        {
            writer.WriteReturnStatus(0);
            this.CompleteCursorRpc(writer, moreRequests, error: false);
            return;
        }

        var table = parameters.Count > 3 ? AsString(parameters, 3) : "";
        try
        {
            using var command = this.connection!.CreateCommand();
            if ((optype & 0x2) != 0)
            {
#pragma warning disable CA2100 // Object name is the cursor's registered base table; the engine re-validates it.
                command.CommandText = $"DELETE FROM {table} WHERE CURRENT OF {api.InternalName};";
#pragma warning restore CA2100
            }
            else
            {
                var assignments = new List<string>();
                for (var i = 4; i < parameters.Count; i++)
                {
                    var column = parameters[i].Name.TrimStart('@');
                    assignments.Add($"[{column}] = {parameters[i].Name}");
                    _ = AddParameter(command, parameters[i]);
                }

#pragma warning disable CA2100 // Object / column names are the cursor's own; the engine re-validates them.
                command.CommandText = $"UPDATE {table} SET {string.Join(", ", assignments)} WHERE CURRENT OF {api.InternalName};";
#pragma warning restore CA2100
            }

            _ = command.ExecuteNonQuery();
        }
        catch (SimulatedSqlException ex)
        {
            foreach (var error in ex.Errors)
                writer.WriteErrorOrInfo(Tds.TokenError, error.Number, error.State, error.Class, error.Message, TdsSession.ServerName, error.Procedure, error.LineNumber);
            writer.WriteReturnStatus(ex.Errors[0].Number);
            this.CompleteCursorRpc(writer, moreRequests, error: true);
            return;
        }

        writer.WriteReturnStatus(0);
        this.CompleteCursorRpc(writer, moreRequests, error: false);
    }

    // ---- sp_cursorclose ---------------------------------------------------

    private void CursorClose(TdsRpcRequest request, TdsTokenWriter writer, bool moreRequests)
    {
        var handle = AsInt(request.Parameters, 0);
        if (!this.apiCursors.Remove(handle, out var api))
        {
            WriteInvalidHandle(writer, "sp_cursorclose", handle);
            this.CompleteCursorRpc(writer, moreRequests, error: true);
            return;
        }

        var connection = this.connection!;
        if (api.Cursor.IsOpen)
            api.Cursor.Close(connection);
        _ = connection.Cursors.Remove(api.InternalName);

        writer.WriteReturnStatus(0);
        this.CompleteCursorRpc(writer, moreRequests, error: false);
    }

    // ---- shared helpers ---------------------------------------------------

    /// <summary>
    /// Writes the cursor's COLMETADATA (<see cref="Cursor.FetchResult"/>'s shape,
    /// ending in the hidden <c>ROWSTAT</c> column) and,
    /// when <paramref name="rows"/> is non-null, one ROW per fetched row, each
    /// already ending in its ROWSTAT. A null rows list is the metadata-only
    /// announce (sp_cursoropen).
    /// </summary>
    private static void WriteCursorMetadata(TdsTokenWriter writer, Cursor cursor, List<SqlValue[]>? rows)
    {
        var result = cursor.FetchResult(rows ?? []);
        TdsTypeCodec.WriteColMetadata(writer, result.Schema, result.ColumnNames, result.ColumnNullability, result.ColumnReportsNumeric, result.HiddenColumnCount, result.ColumnWireFlags);

        if (rows is null)
            return;

        using var cur = result.CreateCursor();
        while (cur.MoveNext())
            TdsTypeCodec.WriteRow(writer, result.Schema, cur, result.ColumnNullability);
    }

    /// <summary>
    /// The engine cursor sensitivity resolves scrollopt; a query forced to STATIC
    /// (not a single updatable base table) reports STATIC (0x8) / READ_ONLY (0x1)
    /// and a materialized row count. Otherwise the requested low bits pass through,
    /// with the rowcount -1 for the non-materialized shapes (dynamic / forward-only
    /// / fast-forward) and the true count for keyset / static — matching probe.
    /// </summary>
    private static (int Scroll, int Cc, int RowCount) ResolveEffectiveOptions(Cursor cursor, int scrollopt, int ccopt, int lastCursorRows)
    {
        var requestedScroll = scrollopt & 0x1F;
        // UPDT_IN_PLACE (0x4000) comes back with whatever concurrency the
        // cursor settles on (probed 2026-09-29 against SQL Server 2025).
        var inPlace = ccopt & 0x4000;
        var requestedCc = ccopt & 0xF;
        // FAST_FORWARD stays FAST_FORWARD over any shape; a DYNAMIC or
        // FORWARD_ONLY request the shape caps at KEYSET (a sort, a row
        // limit) reports KEYSET with its row count (probed 2026-09-29
        // against SQL Server 2025).
        if (requestedScroll == 0x10)
            return (0x10, 0x1 | inPlace, -1);
        if (cursor.BaseTables.Length == 0)
            return (0x8, 0x1 | inPlace, lastCursorRows);
        var cc = (requestedCc == 0 ? 0x1 : requestedCc) | inPlace;
        if (requestedScroll is 0x2 or 0x4 && cursor.Sensitivity == CursorSensitivity.Keyset)
            return (0x1, cc, lastCursorRows);

        var rowcount = requestedScroll is 0x2 or 0x4 ? -1 : lastCursorRows;
        return (requestedScroll, cc, rowcount);
    }

    /// <summary>Translates scrollopt/ccopt option bits into DECLARE CURSOR keywords.</summary>
    private static string CursorOptionKeywords(int scrollopt, int ccopt)
    {
        var sensitivity = (scrollopt & 0x1F) switch
        {
            0x2 => "DYNAMIC",
            0x4 => "FORWARD_ONLY",
            0x8 => "STATIC",
            0x10 => "FAST_FORWARD",
            _ => "KEYSET",
        };

        // READ_ONLY (ccopt 0x1) makes the cursor non-updatable; SCROLL_LOCKS /
        // OPTIMISTIC keep it updatable but their concurrency control is not wired
        // for the API path (probe-confirmed API-cursor optimistic conflicts do not
        // surface), so those map to the default updatable cursor.
        return (ccopt & 0xF) == 0x1 ? sensitivity + " READ_ONLY" : sensitivity;
    }

    /// <summary>
    /// Maps a fetchtype bitmask to the engine <see cref="FetchDirection"/> for the
    /// first row of the buffer plus the absolute/relative offset. Subsequent rows
    /// in an nrows &gt; 1 buffer always advance forward (NEXT).
    /// </summary>
    private static (FetchDirection Direction, long Offset) MapFetchType(int fetchType, int rownum) => (fetchType & 0xFF) switch
    {
        0x1 => (FetchDirection.First, 0),
        0x4 => (FetchDirection.Prior, 0),
        0x8 => (FetchDirection.Last, 0),
        0x10 => (FetchDirection.Absolute, rownum),
        0x20 => (FetchDirection.Relative, rownum),
        _ => (FetchDirection.Next, 0),
    };

    private static List<TdsRpcParameter> BindTail(List<TdsRpcParameter> parameters, int start, List<string>? preparedNames) =>
        NameUnnamedParameters(parameters, start, preparedNames ?? []);

    private static void WriteInvalidHandle(TdsTokenWriter writer, string proc, int handle)
    {
        writer.WriteErrorOrInfo(
            Tds.TokenError, 16909, 1, 16,
            $"{proc}: The cursor identifier value provided ({handle.ToString("x", CultureInfo.InvariantCulture)}) is not valid.",
            "SIMULATED", "", 1);
        writer.WriteReturnStatus(1);
    }

    private static void WriteFetchBufferError(TdsTokenWriter writer, int number, string message)
    {
        writer.WriteErrorOrInfo(Tds.TokenError, number, 1, 16, message, "SIMULATED", "", 1);
        writer.WriteErrorOrInfo(Tds.TokenError, 3621, 0, 0, "The statement has been terminated.", "SIMULATED", "", 1);
        writer.WriteReturnStatus(number);
    }

    private static void EchoFetchOutputs(TdsTokenWriter writer, List<TdsRpcParameter> parameters, int rownum, int nrows)
    {
        if (parameters.Count > 2 && parameters[2].IsOutput)
            TdsTypeCodec.WriteReturnValue(writer, 2, parameters[2].Name, DbType.Int32, rownum);
        if (parameters.Count > 3 && parameters[3].IsOutput)
            TdsTypeCodec.WriteReturnValue(writer, 3, parameters[3].Name, DbType.Int32, nrows);
    }

    private void CompleteCursorRpc(TdsTokenWriter writer, bool moreRequests, bool error)
    {
        if (!moreRequests)
            this.WriteSessionEnvChangesIfAny(writer);
        var status = (ushort)((error ? Tds.DoneError : 0) | (moreRequests ? Tds.DoneMore : Tds.DoneFinal));
        writer.WriteDoneToken(Tds.TokenDoneProc, status, 0, StatementDoneKind.Execute);
    }

    private static string AsString(List<TdsRpcParameter> parameters, int index) =>
        index < parameters.Count && parameters[index].Value is string text
            ? text
            : throw new InvalidDataException($"Cursor RPC parameter {index} was expected to be a string.");

    private static int AsInt(List<TdsRpcParameter> parameters, int index) =>
        index < parameters.Count && parameters[index].Value is not null
            ? Convert.ToInt32(parameters[index].Value, CultureInfo.InvariantCulture)
            : 0;
}
