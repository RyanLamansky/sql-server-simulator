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

        var handle = this.nextApiCursorHandle++;
        this.apiCursors[handle] = new ApiCursor(handle, name, cursor, boundParameters);

        var (effScroll, effCc, rowcount) = ResolveEffectiveOptions(cursor, scrollopt, ccopt, connection.LastCursorRows);

        WriteCursorMetadata(writer, cursor, rows: null);

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
        api.Buffer.Clear();
        var rows = new List<SqlValue[]>();
        using (var fetchCommand = this.connection!.CreateCommand())
        {
            fetchCommand.CommandText = " ";
            foreach (var wire in api.BoundParameters)
                _ = AddParameter(fetchCommand, wire);
            var batch = new BatchContext(fetchCommand);
            for (var i = 0; i < nrows; i++)
            {
                var direction = i == 0 ? firstDirection : FetchDirection.Next;
                var (status, values) = api.Cursor.Fetch(batch, direction, offset);
                if (status != 0 || values is null)
                    break;
                rows.Add(values);
                if (api.Cursor.CurrentRids is { } rids)
                    api.Buffer.Add(rids);
                api.CurrentRowNumber += 1;
            }
        }

        WriteCursorMetadata(writer, api.Cursor, rows);
        writer.WriteReturnStatus(0);
        this.CompleteCursorRpc(writer, moreRequests, error: false);
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
        using (var closeCommand = connection.CreateCommand())
        {
            closeCommand.CommandText = " ";
            if (api.Cursor.IsOpen)
                api.Cursor.Close(new BatchContext(closeCommand));
        }
        _ = connection.Cursors.Remove(api.InternalName);

        writer.WriteReturnStatus(0);
        this.CompleteCursorRpc(writer, moreRequests, error: false);
    }

    // ---- shared helpers ---------------------------------------------------

    /// <summary>
    /// Writes the cursor's COLMETADATA (its projection schema plus a trailing
    /// <c>ROWSTAT</c> int column, matching real client-cursor result sets) and,
    /// when <paramref name="rows"/> is non-null, one ROW per fetched row with
    /// ROWSTAT = 1. A null rows list is the metadata-only announce (sp_cursoropen).
    /// </summary>
    private static void WriteCursorMetadata(TdsTokenWriter writer, Cursor cursor, List<SqlValue[]>? rows)
    {
        var baseSchema = cursor.Selection.Schema;
        var schema = new SqlType[baseSchema.Length + 1];
        Array.Copy(baseSchema, schema, baseSchema.Length);
        schema[^1] = SqlType.Int32;

        var names = new string[schema.Length];
        Array.Copy(cursor.Selection.ColumnNames, names, baseSchema.Length);
        names[^1] = "ROWSTAT";

        TdsTypeCodec.WriteColMetadata(writer, schema, names, columnNullability: null);

        if (rows is null)
            return;

        foreach (var values in rows)
        {
            var full = new SqlValue[schema.Length];
            Array.Copy(values, full, values.Length);
            full[^1] = SqlValue.FromInt32(1);
            var result = new SimulatedSqlResultSet(schema, names, [full]);
            using var cur = result.CreateCursor();
            while (cur.MoveNext())
                TdsTypeCodec.WriteRow(writer, schema, cur, columnNullability: null);
        }
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
        var requestedCc = ccopt & 0xF;
        if (cursor.BaseTables.Length == 0)
            return (0x8, 0x1, lastCursorRows);

        var rowcount = requestedScroll is 0x2 or 0x4 or 0x10 ? -1 : lastCursorRows;
        var cc = requestedCc == 0 ? 0x1 : requestedCc;
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
            this.WriteDatabaseChangeIfAny(writer);
        var status = (ushort)((error ? Tds.DoneError : 0) | (moreRequests ? Tds.DoneMore : Tds.DoneFinal));
        writer.WriteDoneToken(Tds.TokenDoneProc, status, 0);
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
