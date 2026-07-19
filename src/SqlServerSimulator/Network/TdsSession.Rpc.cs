using System.Data;
using System.Globalization;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Network;

internal sealed partial class TdsSession
{
    /// <summary>Prepared statements created by sp_prepare / sp_prepexec, keyed by handle.</summary>
    private readonly Dictionary<int, PreparedStatement> preparedStatements = [];

    private int nextPreparedHandle;

    private sealed class PreparedStatement(string statement, List<string> parameterNames)
    {
        public readonly string Statement = statement;

        /// <summary>
        /// Names from the preparation-time declaration string, used to name
        /// sp_execute parameters that arrive without one.
        /// </summary>
        public readonly List<string> ParameterNames = parameterNames;
    }

    /// <summary>
    /// Handles an RPC request message: sp_executesql, the prepared-statement
    /// family, and direct stored-procedure invocation. A message may carry
    /// several requests separated by the batch flag.
    /// </summary>
    private async ValueTask ExecuteRpcMessageAsync(TdsMessage message, TdsTokenWriter writer, CancellationToken cancellationToken)
    {
        if ((message.FirstStatus & (Tds.StatusResetConnection | Tds.StatusResetConnectionSkipTran)) != 0)
        {
            this.ResetConnection();
            writer.WriteResetConnectionAck();
        }

        List<TdsRpcRequest> requests;
        try
        {
            requests = TdsRpcRequest.ParseMessage(message.Payload, this.connection!.Database);
        }
        catch (NotSupportedException ex)
        {
            writer.WriteErrorOrInfo(Tds.TokenError, 50000, 1, 16, $"SqlServerSimulator: {ex.Message}", "SIMULATED", "", 1);
            writer.WriteDoneToken(Tds.TokenDoneProc, Tds.DoneError, 0);
            return;
        }
        catch (SimulatedSqlException ex)
        {
            // An RPC parameter whose wire form fails validation up front (an
            // unknown CLR-UDT type name → Msg 8064, invalid spatial bytes →
            // Msg 8023) is raised from the parser; surface it as a real error
            // rather than letting it escape the session's crash boundary.
            WriteErrors(writer, ex);
            writer.WriteDoneToken(Tds.TokenDoneProc, Tds.DoneError, 0);
            return;
        }

        this.databaseAtMessageStart = this.connection!.Database;
        for (var i = 0; i < requests.Count; i++)
        {
            var moreRequests = i < requests.Count - 1;
            try
            {
                await this.DispatchRpcAsync(requests[i], writer, moreRequests, cancellationToken).ConfigureAwait(false);
            }
            catch (SimulatedSqlException ex)
            {
                _ = this.FlushInfoMessages(writer);
                WriteErrors(writer, ex);
                if (!moreRequests)
                    this.WriteDatabaseChangeIfAny(writer);
                writer.WriteDoneToken(Tds.TokenDoneProc, (ushort)(Tds.DoneError | (moreRequests ? Tds.DoneMore : Tds.DoneFinal)), 0);
            }
            catch (NotSupportedException ex)
            {
                _ = this.FlushInfoMessages(writer);
                writer.WriteErrorOrInfo(Tds.TokenError, 50000, 1, 16, $"SqlServerSimulator: {ex.Message}", "SIMULATED", "", 1);
                if (!moreRequests)
                    this.WriteDatabaseChangeIfAny(writer);
                writer.WriteDoneToken(Tds.TokenDoneProc, (ushort)(Tds.DoneError | (moreRequests ? Tds.DoneMore : Tds.DoneFinal)), 0);
            }
        }
    }

    private async ValueTask DispatchRpcAsync(TdsRpcRequest request, TdsTokenWriter writer, bool moreRequests, CancellationToken cancellationToken)
    {
        var procId = request.ProcId;
        if (procId == 0 && request.ProcName.Length != 0)
            procId = WellKnownProcId(request.ProcName);

        switch (procId)
        {
            case 0:
                await this.ExecuteProcedureRpcAsync(request, writer, moreRequests, cancellationToken).ConfigureAwait(false);
                break;
            case Tds.ProcIdCursor:
            case Tds.ProcIdCursorOpen:
            case Tds.ProcIdCursorPrepare:
            case Tds.ProcIdCursorExecute:
            case Tds.ProcIdCursorPrepExec:
            case Tds.ProcIdCursorUnprepare:
            case Tds.ProcIdCursorFetch:
            case Tds.ProcIdCursorOption:
            case Tds.ProcIdCursorClose:
                this.DispatchCursorRpc(procId, request, writer, moreRequests);
                break;
            case Tds.ProcIdExecuteSql:
                {
                    var statement = ParameterText(request.Parameters, 0);
                    var bound = request.Parameters.Skip(request.Parameters.Count >= 2 ? 2 : 1).ToList();
                    await this.ExecuteStatementRpcAsync(statement, bound, handleReturn: null, writer, moreRequests, cancellationToken).ConfigureAwait(false);
                    break;
                }

            case Tds.ProcIdPrepare:
                {
                    var handle = this.StorePreparedStatement(ParameterText(request.Parameters, 2), ParameterText(request.Parameters, 1));
                    TdsTypeCodec.WriteReturnValue(writer, 0, request.Parameters[0].Name, DbType.Int32, handle);
                    writer.WriteReturnStatus(0);
                    writer.WriteDoneToken(Tds.TokenDoneProc, moreRequests ? Tds.DoneMore : Tds.DoneFinal, 0);
                    break;
                }

            case Tds.ProcIdExecute:
                {
                    var handle = Convert.ToInt32(request.Parameters[0].Value, CultureInfo.InvariantCulture);
                    if (!this.preparedStatements.TryGetValue(handle, out var prepared))
                    {
                        writer.WriteErrorOrInfo(Tds.TokenError, 8179, 1, 16, $"Could not find prepared statement with handle {handle}.", "SIMULATED", "", 1);
                        writer.WriteDoneToken(Tds.TokenDoneProc, (ushort)(Tds.DoneError | (moreRequests ? Tds.DoneMore : Tds.DoneFinal)), 0);
                        break;
                    }

                    var bound = new List<TdsRpcParameter>();
                    for (var i = 1; i < request.Parameters.Count; i++)
                    {
                        var parameter = request.Parameters[i];
                        if (parameter.Name.Length == 0 && i - 1 < prepared.ParameterNames.Count)
                            parameter = new TdsRpcParameter(prepared.ParameterNames[i - 1], parameter.IsOutput, parameter.DbType, parameter.Value, parameter.Size, parameter.Precision, parameter.Scale);

                        bound.Add(parameter);
                    }

                    await this.ExecuteStatementRpcAsync(prepared.Statement, bound, handleReturn: null, writer, moreRequests, cancellationToken).ConfigureAwait(false);
                    break;
                }

            case Tds.ProcIdPrepExec:
                {
                    var handleName = request.Parameters[0].Name;
                    var handle = this.StorePreparedStatement(ParameterText(request.Parameters, 2), ParameterText(request.Parameters, 1));
                    var bound = request.Parameters.Skip(3).ToList();
                    await this.ExecuteStatementRpcAsync(this.preparedStatements[handle].Statement, bound, (handleName, handle), writer, moreRequests, cancellationToken).ConfigureAwait(false);
                    break;
                }

            case Tds.ProcIdUnprepare:
                _ = this.preparedStatements.Remove(Convert.ToInt32(request.Parameters[0].Value, CultureInfo.InvariantCulture));
                writer.WriteReturnStatus(0);
                writer.WriteDoneToken(Tds.TokenDoneProc, moreRequests ? Tds.DoneMore : Tds.DoneFinal, 0);
                break;
            default:
                writer.WriteErrorOrInfo(
                    Tds.TokenError, 50000, 1, 16,
                    $"The SqlServerSimulator network listener does not support the well-known RPC procedure id {procId}.",
                    "SIMULATED", "", 1);
                writer.WriteDoneToken(Tds.TokenDoneProc, (ushort)(Tds.DoneError | (moreRequests ? Tds.DoneMore : Tds.DoneFinal)), 0);
                break;
        }
    }

    /// <summary>Runs a statement with bound wire parameters (sp_executesql / sp_execute / sp_prepexec).</summary>
    private async ValueTask ExecuteStatementRpcAsync(
        string statement,
        List<TdsRpcParameter> boundParameters,
        (string Name, int Handle)? handleReturn,
        TdsTokenWriter writer,
        bool moreRequests,
        CancellationToken cancellationToken)
    {
        using var command = this.connection!.CreateCommand();
#pragma warning disable CA2100 // This IS a SQL endpoint: the statement is the client's query by design.
        command.CommandText = statement;
#pragma warning restore CA2100

        var outputs = new List<(int Ordinal, TdsRpcParameter Wire, SimulatedDbParameter Bound)>();
        for (var i = 0; i < boundParameters.Count; i++)
        {
            var bound = AddParameter(command, boundParameters[i]);
            if (boundParameters[i].IsOutput)
                outputs.Add((i, boundParameters[i], bound));
        }

        // A cancelled RPC skips its trailing RETURNSTATUS / RETURNVALUE /
        // DONEPROC: the batch loop emits the single DONE_ATTN acknowledgment.
        if (await this.StreamOutcomesAsync(command, writer, Tds.TokenDoneInProc, trailingTokensFollow: true, cancellationToken).ConfigureAwait(false))
            return;

        writer.WriteReturnStatus(0);
        if (handleReturn is { } handleValue)
            TdsTypeCodec.WriteReturnValue(writer, 0, handleValue.Name, DbType.Int32, handleValue.Handle);

        WriteOutputReturnValues(writer, outputs);

        if (!moreRequests)
            this.WriteDatabaseChangeIfAny(writer);
        writer.WriteDoneToken(Tds.TokenDoneProc, moreRequests ? Tds.DoneMore : Tds.DoneFinal, 0);
    }

    /// <summary>Direct stored-procedure invocation by name.</summary>
    private async ValueTask ExecuteProcedureRpcAsync(TdsRpcRequest request, TdsTokenWriter writer, bool moreRequests, CancellationToken cancellationToken)
    {
        using var command = this.connection!.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
#pragma warning disable CA2100 // This IS a SQL endpoint: the procedure name is the client's request by design.
        command.CommandText = request.ProcName;
#pragma warning restore CA2100

        var returnParameter = command.CreateParameter();
        returnParameter.ParameterName = "@RETURN_VALUE";
        returnParameter.Direction = ParameterDirection.ReturnValue;
        returnParameter.DbType = DbType.Int32;
        _ = command.Parameters.Add(returnParameter);

        var outputs = new List<(int Ordinal, TdsRpcParameter Wire, SimulatedDbParameter Bound)>();
        for (var i = 0; i < request.Parameters.Count; i++)
        {
            var bound = AddParameter(command, request.Parameters[i]);
            if (request.Parameters[i].IsOutput)
                outputs.Add((i, request.Parameters[i], bound));
        }

        // A cancelled RPC skips its trailing RETURNSTATUS / RETURNVALUE /
        // DONEPROC: the batch loop emits the single DONE_ATTN acknowledgment.
        if (await this.StreamOutcomesAsync(command, writer, Tds.TokenDoneInProc, trailingTokensFollow: true, cancellationToken).ConfigureAwait(false))
            return;

        writer.WriteReturnStatus(returnParameter.Value is int returnCode ? returnCode : 0);
        WriteOutputReturnValues(writer, outputs);

        if (!moreRequests)
            this.WriteDatabaseChangeIfAny(writer);
        writer.WriteDoneToken(Tds.TokenDoneProc, moreRequests ? Tds.DoneMore : Tds.DoneFinal, 0);
    }

    /// <summary>
    /// The well-known RPC procedures SqlClient (and legacy ODBC / OLE DB) may
    /// send by name rather than by numeric ProcID — <c>sp_executesql</c> and the
    /// API-server-cursor family. Returns 0 for any other name (a genuine
    /// stored-procedure call routed to <see cref="ExecuteProcedureRpcAsync"/>).
    /// </summary>
    private static ushort WellKnownProcId(string procName) =>
        WellKnownProcIds.TryGetValue(procName, out var id) ? id : (ushort)0;

    private static readonly Dictionary<string, ushort> WellKnownProcIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sp_executesql"] = Tds.ProcIdExecuteSql,
        ["sp_cursor"] = Tds.ProcIdCursor,
        ["sp_cursoropen"] = Tds.ProcIdCursorOpen,
        ["sp_cursorprepare"] = Tds.ProcIdCursorPrepare,
        ["sp_cursorexecute"] = Tds.ProcIdCursorExecute,
        ["sp_cursorprepexec"] = Tds.ProcIdCursorPrepExec,
        ["sp_cursorunprepare"] = Tds.ProcIdCursorUnprepare,
        ["sp_cursorfetch"] = Tds.ProcIdCursorFetch,
        ["sp_cursoroption"] = Tds.ProcIdCursorOption,
        ["sp_cursorclose"] = Tds.ProcIdCursorClose,
    };

    /// <summary>
    /// RETURNVALUE tokens for a request's output-direction parameters.
    /// <see cref="DbType.Object"/> marks a <c>sql_variant</c> / CLR-UDT
    /// parameter (its wire value rode a pre-built <see cref="SqlValue"/>, not
    /// a <see cref="DbType"/>): those write from the engine-typed
    /// <see cref="SimulatedDbParameter.OutputSqlValue"/> stamped at
    /// end-of-batch write-back, falling back to echoing the decoded input
    /// value when the batch never reached write-back.
    /// </summary>
    private static void WriteOutputReturnValues(TdsTokenWriter writer, List<(int Ordinal, TdsRpcParameter Wire, SimulatedDbParameter Bound)> outputs)
    {
        foreach (var (ordinal, wire, bound) in outputs)
        {
            if (wire.DbType == DbType.Object)
                TdsTypeCodec.WriteReturnValue(writer, checked((ushort)ordinal), wire.Name, bound.OutputSqlValue ?? (SqlValue)wire.Value!);
            else
                TdsTypeCodec.WriteReturnValue(writer, checked((ushort)ordinal), wire.Name, wire.DbType, bound.Value);
        }
    }

    private static SimulatedDbParameter AddParameter(SimulatedDbCommand command, TdsRpcParameter wire)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = wire.Name;

        // A table-valued parameter carries its decoded rows; setting the
        // TypeName + structured value routes it through the engine's
        // Structured-parameter binding (into TableVariables), the same path the
        // in-process ADO.NET Structured parameter takes.
        if (wire.Value is TableValuedParameterData tvp)
        {
            parameter.TypeName = tvp.TypeName;
            parameter.Value = tvp;
            parameter.Direction = ParameterDirection.Input;
            _ = command.Parameters.Add(parameter);
            return parameter;
        }

        parameter.DbType = wire.DbType;
        parameter.Value = wire.Value ?? DBNull.Value;
        parameter.Direction = wire.IsOutput ? ParameterDirection.InputOutput : ParameterDirection.Input;
        if (wire.Size != 0)
            parameter.Size = wire.Size;

        _ = command.Parameters.Add(parameter);
        return parameter;
    }

    private int StorePreparedStatement(string statement, string declaration)
    {
        var handle = ++this.nextPreparedHandle;
        this.preparedStatements[handle] = new PreparedStatement(statement, ParseDeclarationNames(declaration));
        return handle;
    }

    private static string ParameterText(List<TdsRpcParameter> parameters, int index) =>
        index < parameters.Count && parameters[index].Value is string text
            ? text
            : throw new InvalidDataException($"RPC parameter {index} was expected to be a statement or declaration string.");

    /// <summary>
    /// Extracts the parameter names from a declaration string like
    /// <c>@a int, @b decimal(10,2) OUTPUT</c>, honoring parenthesized type
    /// arguments when splitting.
    /// </summary>
    private static List<string> ParseDeclarationNames(string declaration)
    {
        var names = new List<string>();
        var depth = 0;
        var segmentStart = 0;
        for (var i = 0; i <= declaration.Length; i++)
        {
            if (i < declaration.Length)
            {
                var c = declaration[i];
                if (c == '(')
                    depth++;
                else if (c == ')')
                    depth--;

                if (c != ',' || depth != 0)
                    continue;
            }

            var segment = declaration.AsSpan(segmentStart, i - segmentStart).Trim();
            segmentStart = i + 1;
            if (segment.Length == 0 || segment[0] != '@')
                continue;

            var end = 0;
            while (end < segment.Length && !char.IsWhiteSpace(segment[end]))
                end++;

            names.Add(segment[..end].ToString());
        }

        return names;
    }
}
