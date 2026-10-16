using System.Data;
using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Network;

// The API server cursor procedures as TDS RPC requests: each decodes its
// parameters, runs the operation the EXEC text form shares
// (Simulation.ApiCursors.cs) over the session's handles on the connection, and
// writes what it reports as tokens.
internal sealed partial class TdsSession
{
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
            default:
                this.CursorOption(request, writer, moreRequests);
                break;
        }
    }

    // ---- sp_cursoropen ----------------------------------------------------

    private void CursorOpen(TdsRpcRequest request, TdsTokenWriter writer, bool moreRequests)
    {
        var parameters = request.Parameters;
        this.OpenAndAnnounce(request, writer, moreRequests, "sp_cursoropen", AsString(parameters, 1), cursorOrdinal: 0, scrollOrdinal: 2, ccOrdinal: 3, rowcountOrdinal: 4, boundStart: 5);
    }

    // ---- sp_cursorprepexec ------------------------------------------------

    private void CursorPrepExec(TdsRpcRequest request, TdsTokenWriter writer, bool moreRequests)
    {
        var parameters = request.Parameters;
        var statement = AsString(parameters, 3);
        var prepHandle = this.connection!.PrepareCursor(statement, AsString(parameters, 2));

        // The value params (boundStart 7+) arrive positional/unnamed from native
        // ODBC / OLE DB drivers; name them from the prepared declaration, the
        // same mapping sp_cursorexecute applies on the re-execute path.
        var extraReturns = new List<(ushort Ordinal, string Name, object? Value)> { (0, parameters[0].Name, prepHandle) };
        this.OpenAndAnnounce(request, writer, moreRequests, "sp_cursorprepexec", statement, cursorOrdinal: 1, scrollOrdinal: 4, ccOrdinal: 5, rowcountOrdinal: 6, boundStart: 7, extraReturns, this.connection.PreparedApiCursors[prepHandle].ParameterNames);
    }

    // ---- sp_cursorexecute -------------------------------------------------

    private void CursorExecute(TdsRpcRequest request, TdsTokenWriter writer, bool moreRequests)
    {
        var parameters = request.Parameters;
        var prepHandle = AsInt(parameters, 0);
        if (!this.connection!.PreparedApiCursors.TryGetValue(prepHandle, out var prepared))
        {
            WriteErrors(writer, Simulation.AtProcedureLine(SimulatedSqlException.CouldNotFindPreparedStatement(prepHandle, state: 4), "sp_cursorexecute", 1));
            writer.WriteReturnStatus(8179);
            this.CompleteCursorRpc(writer, moreRequests, error: true);
            return;
        }

        this.OpenAndAnnounce(request, writer, moreRequests, "sp_cursorexecute", prepared.Statement, cursorOrdinal: 1, scrollOrdinal: 2, ccOrdinal: 3, rowcountOrdinal: 4, boundStart: 5, preparedNames: prepared.ParameterNames);
    }

    // ---- sp_cursorprepare -------------------------------------------------

    // prepared_handle OUTPUT, params, stmt, options [, scrollopt [, ccopt]]:
    // the two options an output parameter echoes as sent (probed 2026-10-07
    // against SQL Server 2025).
    private void CursorPrepare(TdsRpcRequest request, TdsTokenWriter writer, bool moreRequests)
    {
        var parameters = request.Parameters;
        var prepHandle = this.connection!.PrepareCursor(AsString(parameters, 2), AsString(parameters, 1));

        TdsTypeCodec.WriteReturnValue(writer, 0, parameters[0].Name, DbType.Int32, prepHandle);
        for (var ordinal = 4; ordinal < Math.Min(parameters.Count, 6); ordinal++)
        {
            if (parameters[ordinal].IsOutput)
                TdsTypeCodec.WriteReturnValue(writer, (ushort)ordinal, parameters[ordinal].Name, DbType.Int32, parameters[ordinal].Value);
        }
        writer.WriteReturnStatus(0);
        this.CompleteCursorRpc(writer, moreRequests, error: false);
    }

    // ---- sp_cursorunprepare -----------------------------------------------

    private void CursorUnprepare(TdsRpcRequest request, TdsTokenWriter writer, bool moreRequests)
    {
        var prepHandle = AsInt(request.Parameters, 0);
        if (!Simulation.Unprepare(this.connection!, prepHandle))
        {
            WriteErrors(writer, Simulation.AtProcedureLine(SimulatedSqlException.CouldNotFindPreparedStatement(prepHandle, state: 8), "sp_cursorunprepare", 1));
            writer.WriteReturnStatus(8179);
            this.CompleteCursorRpc(writer, moreRequests, error: true);
            return;
        }

        writer.WriteReturnStatus(0);
        this.CompleteCursorRpc(writer, moreRequests, error: false);
    }

    /// <summary>
    /// The open sp_cursoropen, sp_cursorprepexec and sp_cursorexecute share:
    /// opens the cursor and writes its metadata-only announce (COLMETADATA and
    /// a trailing ROWSTAT column, zero rows — or the rows AUTO_FETCH fetched),
    /// the return status, and the handle, the effective options and the row
    /// count as output parameters.
    /// </summary>
    private void OpenAndAnnounce(
        TdsRpcRequest request,
        TdsTokenWriter writer,
        bool moreRequests,
        string procedure,
        string statement,
        int cursorOrdinal,
        int scrollOrdinal,
        int ccOrdinal,
        int rowcountOrdinal,
        int boundStart,
        List<(ushort Ordinal, string Name, object? Value)>? extraReturns = null,
        List<string>? preparedNames = null)
    {
        var parameters = request.Parameters;
        var boundParameters = NameUnnamedParameters(parameters, boundStart, preparedNames ?? []);
        var connection = this.connection!;
        var opening = Simulation.OpenApiCursor(
            connection,
            procedure,
            statement,
            AsInt(parameters, scrollOrdinal),
            AsInt(parameters, ccOrdinal),
            AsInt(parameters, rowcountOrdinal),
            parameterizedWithoutDefinition: cursorOrdinal == 0 && parameters.Count <= boundStart,
            definitionWithoutParameterized: procedure == "sp_cursorprepexec" && parameters[2].Value is string { Length: > 0 },
            (name, declareOpen) =>
            {
                using var command = CreateCommand(connection);
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
                return connection.Cursors[name];
            },
            command =>
            {
                foreach (var wire in boundParameters)
                    _ = AddParameter(command, wire);
                return new BatchContext(command);
            });

        if (opening.Error is { } error)
            WriteErrors(writer, error);
        else
            WriteCursorMetadata(writer, opening.Cursor!.Cursor, opening.Fetched);
        writer.WriteReturnStatus(opening.ReturnStatus);
        if (extraReturns is not null)
        {
            foreach (var (ordinal, pname, value) in extraReturns)
                TdsTypeCodec.WriteReturnValue(writer, ordinal, pname, DbType.Int32, value);
        }
        TdsTypeCodec.WriteReturnValue(writer, (ushort)cursorOrdinal, parameters[cursorOrdinal].Name, DbType.Int32, opening.Handle);
        TdsTypeCodec.WriteReturnValue(writer, (ushort)scrollOrdinal, parameters[scrollOrdinal].Name, DbType.Int32, opening.Scroll);
        TdsTypeCodec.WriteReturnValue(writer, (ushort)ccOrdinal, parameters[ccOrdinal].Name, DbType.Int32, opening.Cc);
        TdsTypeCodec.WriteReturnValue(writer, (ushort)rowcountOrdinal, parameters[rowcountOrdinal].Name, DbType.Int32, opening.RowCount is { } rowcount ? rowcount : parameters[rowcountOrdinal].Value);
        this.CompleteCursorRpc(writer, moreRequests, error: opening.Error is not null);
    }

    // ---- sp_cursorfetch ---------------------------------------------------

    private void CursorFetch(TdsRpcRequest request, TdsTokenWriter writer, bool moreRequests)
    {
        var parameters = request.Parameters;
        var handle = AsInt(parameters, 0);
        var fetchType = AsInt(parameters, 1);
        var rownum = parameters.Count > 2 ? AsInt(parameters, 2) : 0;
        var nrows = parameters.Count > 3 ? AsInt(parameters, 3) : 1;

        // Only INFO reports through rownum and nrows: either one sent for
        // output on another fetch is Msg 16902, rownum's state 5 ahead of
        // nrows' 6 (probed 2026-10-07 against SQL Server 2025).
        if (this.connection!.ApiCursors.ContainsKey(handle) && (fetchType & 0x100) == 0
            && ((parameters.Count > 2 && parameters[2].IsOutput) || (parameters.Count > 3 && parameters[3].IsOutput)))
        {
            var rownumOutput = parameters[2].IsOutput;
            WriteErrors(writer, Simulation.AtProcedureLine(SimulatedSqlException.ApiCursorParameterInvalid("sp_cursorfetch", rownumOutput ? "rownum" : "nrows", rownumOutput ? (byte)5 : (byte)6), "sp_cursorfetch", 1));
            writer.WriteReturnStatus(1);
            EchoFetchOutputs(writer, parameters, rownum, nrows);
            this.CompleteCursorRpc(writer, moreRequests, error: true);
            return;
        }

        var fetching = Simulation.FetchApiCursor(this.connection, handle, fetchType, rownum, nrows);
        if (fetching.Error is { } error)
            WriteErrors(writer, error);
        if (fetching.Rows is { } rows)
            WriteCursorMetadata(writer, fetching.Cursor!.Cursor, rows, request.NoMetadata);
        writer.WriteReturnStatus(fetching.ReturnStatus);
        if (fetching.Cursor is not null && (fetchType & 0x100) != 0)
            EchoFetchOutputs(writer, parameters, fetching.InfoRowNumber, fetching.InfoRowCount);
        else if (fetching.Cursor is null)
            EchoFetchOutputs(writer, parameters, rownum, nrows);
        this.CompleteCursorRpc(writer, moreRequests, error: fetching.Error is not null && fetching.Cursor is null);
    }

    // ---- sp_cursor (positioned UPDATE / DELETE / INSERT / SETPOSITION) ----

    private void CursorOp(TdsRpcRequest request, TdsTokenWriter writer, bool moreRequests)
    {
        var parameters = request.Parameters;
        var values = parameters.Count > 4 ? parameters[4..] : [];
        var operating = Simulation.OperateApiCursor(
            this.connection!,
            AsInt(parameters, 0),
            AsInt(parameters, 1),
            AsInt(parameters, 2),
            parameters.Count > 3 ? AsString(parameters, 3) : "",
            [.. values.Select(static value => value.Name)],
            write =>
            {
                using var command = CreateCommand(this.connection!);
#pragma warning disable CA2100 // Object / column names are the cursor's own; the engine re-validates them.
                command.CommandText = write;
#pragma warning restore CA2100
                foreach (var value in values)
                    _ = AddParameter(command, value);
                return command.ExecuteNonQuery();
            });

        if (operating.Error is { } error)
            WriteErrors(writer, error);

        // A positioned write reports its row twice, in two DONEINPROC tokens,
        // and an INSERT once, which SQL Server's OLE DB provider reads a
        // linked server's write from (probed 2026-10-11 against SQL Server 2025).
        foreach (var count in operating.Counts)
            writer.WriteDoneToken(Tds.TokenDoneInProc, Tds.DoneCount | Tds.DoneMore, count, StatementDoneKind.CursorOperation);
        writer.WriteReturnStatus(operating.ReturnStatus);
        this.CompleteCursorRpc(writer, moreRequests, error: operating.Error is not null);
    }

    // ---- sp_cursorclose ---------------------------------------------------

    private void CursorClose(TdsRpcRequest request, TdsTokenWriter writer, bool moreRequests)
    {
        var error = Simulation.CloseApiCursor(this.connection!, AsInt(request.Parameters, 0));
        if (error is not null)
            WriteErrors(writer, error);
        writer.WriteReturnStatus(error is null ? 0 : 1);
        this.CompleteCursorRpc(writer, moreRequests, error: error is not null);
    }

    // ---- sp_cursoroption --------------------------------------------------

    private void CursorOption(TdsRpcRequest request, TdsTokenWriter writer, bool moreRequests)
    {
        var parameters = request.Parameters;
        var value = parameters.Count > 2 && parameters[2].Value is { } sent and not DBNull
            ? sent switch
            {
                string text => SqlValue.FromNVarchar(text),
                _ => SqlValue.FromInt32(Convert.ToInt32(sent, CultureInfo.InvariantCulture)),
            }
            : SqlValue.Null(SqlType.Int32);
        var error = Simulation.SetApiCursorOption(this.connection!, AsInt(parameters, 0), AsInt(parameters, 1), value);
        if (error is not null)
            WriteErrors(writer, error);
        writer.WriteReturnStatus(error is null ? 0 : 1);
        this.CompleteCursorRpc(writer, moreRequests, error: error is not null);
    }

    // ---- shared helpers ---------------------------------------------------

    /// <summary>
    /// Writes the cursor's COLMETADATA (<see cref="Cursor.FetchResult"/>'s shape,
    /// ending in the hidden <c>ROWSTAT</c> column) and,
    /// when <paramref name="rows"/> is non-null, one ROW per fetched row, each
    /// already ending in its ROWSTAT. A null rows list is the metadata-only
    /// announce (sp_cursoropen); <paramref name="noMetadata"/> sends the rows
    /// under an empty COLMETADATA, for a fetch whose RPC asked for none.
    /// </summary>
    private static void WriteCursorMetadata(TdsTokenWriter writer, Cursor cursor, List<SqlValue[]>? rows, bool noMetadata = false)
    {
        var result = Simulation.ApiCursorResult(cursor, rows ?? []);
        if (noMetadata)
        {
            // A fetch asking for no metadata gets a COLMETADATA of no columns
            // (probed 2026-10-11 against SQL Server 2025, SQL Server's OLE DB
            // provider fetching a linked server's cursor).
            writer.WriteByte(Tds.TokenColMetadata);
            writer.WriteUInt16(0xFFFF);
        }
        else
        {
            TdsTypeCodec.WriteColMetadata(writer, result.Schema, result.ColumnNames, result.ColumnNullability, result.ColumnReportsNumeric, result.HiddenColumnCount, result.ColumnWireFlags, browse: result.Browse);
        }

        if (rows is not null)
        {
            using var cur = result.CreateCursor();
            while (cur.MoveNext())
                TdsTypeCodec.WriteRow(writer, result.Schema, cur, result.ColumnNullability);
        }

        // Real closes the metadata, and each fetch's rows, with a
        // DONEINPROC counting them, which SQL Server's OLE DB provider reads
        // how many rows a fetch brought from (probed 2026-10-11 against SQL
        // Server 2025).
        writer.WriteDoneToken(Tds.TokenDoneInProc, Tds.DoneCount | Tds.DoneMore, rows?.Count ?? 0, StatementDoneKind.Select);
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
        index < parameters.Count && parameters[index].Value is not null and not DBNull
            ? Convert.ToInt32(parameters[index].Value, CultureInfo.InvariantCulture)
            : 0;
}
