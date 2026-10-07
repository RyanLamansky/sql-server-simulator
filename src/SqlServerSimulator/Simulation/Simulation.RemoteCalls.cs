using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The two ways a batch runs code on a linked server: EXEC ('…') AT server and
// a four-part procedure call. Both reach the server only while its `rpc out`
// option is on, run in a fresh session of the server, and hand their result
// sets, messages, counts, output parameters and return code back to the
// caller (probed 2026-09-28 against SQL Server 2025).
partial class Simulation
{
    /// <summary>
    /// <c>EXEC ( 'text' [, argument [OUTPUT]] … ) AT server</c>: each <c>?</c>
    /// in the text outside a string, comment or bracketed name binds the next
    /// argument, a character one sent as <c>nvarchar</c>, and an <c>OUTPUT</c>
    /// variable reads the value the server leaves in it.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> ExecuteAtLinkedServer(BatchContext batch, string serverName, string text, List<ProcArgument> arguments, bool insertExecSource)
    {
        var server = ResolveRpcServer(batch, serverName);
        var enlisting = RequireRemoteCallOutsideTransaction(batch, server, insertExecSource);

        var (remoteText, placeholders) = BindPlaceholders(text);
        // The provider refuses an empty text, and arguments that don't match
        // the placeholders, before the server sees anything (probed
        // 2026-10-05 against SQL Server 2025).
        var providerRefusal = string.IsNullOrWhiteSpace(text) ? "Command text was not set for the command object."
            : arguments.Count > placeholders ? "Multiple-step OLE DB operation generated errors. Check each OLE DB status value, if available. No work was done."
            : arguments.Count < placeholders ? "No value given for one or more required parameters."
            : null;
        if (providerRefusal is not null)
            throw SimulatedSqlException.AfterProviderMessage(batch, server, providerRefusal, SimulatedSqlException.RemoteStatementNotExecuted(server.Name));
        var parameters = new List<SimulatedDbParameter>();
        var outputs = new List<(SimulatedDbParameter Parameter, VariableSlot Slot)>();
        for (var i = 0; i < placeholders; i++)
        {
            var value = i < arguments.Count ? arguments[i].Value : SqlValue.Null(SqlType.Int32);
            if (value.Type.Category == SqlTypeCategory.String && value.Type is not NVarcharSqlType)
                value = value.CoerceTo(SqlType.NVarchar);
            var parameter = RemoteParameter("@P" + (i + 1).ToString(CultureInfo.InvariantCulture), value, i < arguments.Count && arguments[i].OutputSlot is not null);
            // A decimal the call computed, rather than wrote as a literal,
            // arrives as numeric (probed 2026-10-06 against SQL Server 2025).
            parameter.SpelledNumeric = value.Type is DecimalSqlType && i < arguments.Count && !arguments[i].IsNumericLiteral;
            parameters.Add(parameter);
            if (i < arguments.Count && arguments[i].OutputSlot is { } slot)
                outputs.Add((parameter, slot));
        }

        var outcomes = RunRemoteCall(batch, server, remoteText, parameters, database: null, procedure: null, enlisting, out var refusal);
        foreach (var outcome in outcomes)
            yield return outcome;
        if (refusal is not null)
            throw refusal;
        foreach (var (parameter, slot) in outputs)
            slot.Value = (parameter.OutputSqlValue ?? SqlValue.Null(slot.DeclaredType)).CoerceTo(slot.DeclaredType);
    }

    /// <summary>
    /// <c>EXEC [@rc =] server.database.schema.procedure arguments</c>: the call
    /// runs on the server as written there, its positional, named and
    /// <c>OUTPUT</c> arguments carried as parameters. An error the procedure
    /// raises names it as the call does, without the server.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeRemoteProcedure(BatchContext batch, MultiPartName procName, List<ProcArgument> arguments, string? returnCodeVar, bool insertExecSource)
    {
        var server = ResolveRpcServer(batch, procName[0]);
        var enlisting = RequireRemoteCallOutsideTransaction(batch, server, insertExecSource);

        // A name leaving the database and schema both empty is the bare
        // procedure name on the server (probed 2026-10-06 against SQL Server
        // 2025: Msg 2812 naming it alone).
        var first = procName.Count == 4 && procName[1].Length == 0 && procName[2].Length == 0 ? 3 : 1;
        var written = new StringBuilder();
        for (var i = first; i < procName.Count; i++)
        {
            if (i > first)
                _ = written.Append('.');
            if (!(i == 2 && procName.SchemaOmitted))
                _ = written.Append(procName[i]);
        }
        var text = new StringBuilder("EXEC @RETURN_VALUE = ");
        for (var i = first; i < procName.Count; i++)
        {
            if (i > first)
                _ = text.Append('.');
            if (!(i == 2 && procName.SchemaOmitted))
                _ = text.Append(RemoteWrite.Bracket(procName[i]));
        }

        var returnValue = RemoteParameter("@RETURN_VALUE", SqlValue.Null(SqlType.Int32), output: true);
        var parameters = new List<SimulatedDbParameter> { returnValue };
        var outputs = new List<(SimulatedDbParameter Parameter, VariableSlot Slot)>();
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            _ = text.Append(i == 0 ? " " : ", ");
            if (argument.Name is { } name)
                _ = text.Append('@').Append(name).Append(" = ");
            if (argument.IsDefault)
            {
                _ = text.Append("DEFAULT");
                continue;
            }
            var parameter = RemoteParameter("@P" + (i + 1).ToString(CultureInfo.InvariantCulture), argument.Value, argument.OutputSlot is not null);
            parameters.Add(parameter);
            _ = text.Append(parameter.ParameterName);
            if (argument.OutputSlot is { } slot)
            {
                _ = text.Append(" OUTPUT");
                outputs.Add((parameter, slot));
            }
        }

        // The session starts in the procedure's database, where real runs its
        // body whatever database the call arrives in.
        var database = procName[1].Length > 0 ? procName[1] : null;
        var outcomes = RunRemoteCall(batch, server, text.ToString(), parameters, database, written.ToString(), enlisting, out var refusal);
        foreach (var outcome in outcomes)
            yield return outcome;
        if (refusal is not null)
            throw refusal;
        foreach (var (parameter, slot) in outputs)
            slot.Value = (parameter.OutputSqlValue ?? SqlValue.Null(slot.DeclaredType)).CoerceTo(slot.DeclaredType);
        if (returnCodeVar is not null)
        {
            var slot = batch.GetVariableSlot(returnCodeVar);
            slot.Value = (returnValue.OutputSqlValue ?? SqlValue.FromInt32(0)).CoerceTo(slot.DeclaredType);
        }
    }

    /// <summary>
    /// The linked server a remote call names, checked for <c>rpc out</c>: Msg
    /// 7202 when there is none, Msg 7411 when its option is off.
    /// </summary>
    private static LinkedServer ResolveRpcServer(BatchContext batch, string name)
    {
        if (!batch.Connection.Simulation.ActiveLinkedServers.TryGetValue(name, out var server))
            throw SimulatedSqlException.LinkedServerNotFound(name);
        if (!server.RpcOut)
            throw SimulatedSqlException.ServerNotConfiguredFor(server.Name, "RPC");
        batch.HasSessionScopedReference = true;
        return server;
    }

    /// <summary>
    /// A remote call inside a local transaction — or feeding an
    /// <c>INSERT … EXEC</c>, whose statement is one — enlists the server while
    /// its <c>remote proc transaction promotion</c> option is on, which a remote
    /// server's coordinator refuses out of the box (Msg 7391). A loopback's
    /// call inside a transaction runs in the caller's transaction instead, the
    /// transaction this returns: the server's session reads <c>@@TRANCOUNT</c>
    /// 1, reads the caller's uncommitted rows without waiting on their locks,
    /// and its writes commit or roll back with the caller's (probed 2026-10-05
    /// and 2026-10-07 against SQL Server 2025); with the option off every call
    /// runs outside it.
    /// </summary>
    private static SimulatedDbTransaction? RequireRemoteCallOutsideTransaction(BatchContext batch, LinkedServer server, bool insertExecSource)
    {
        if (!server.RemoteProcTransactionPromotion)
            return null;
        // Not even a loopback's call promotes a transaction holding a
        // savepoint (probed 2026-10-05 against SQL Server 2025).
        if (batch.Connection.CurrentTransaction is { HasSavepoint: true })
            throw SimulatedSqlException.CannotPromoteWithSavepoint();
        if (server.IsLoopback(batch.Connection.Simulation))
            return batch.Connection.CurrentTransaction;
        if (batch.Connection.CurrentTransaction is null && !insertExecSource)
            return null;
        batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.DistributedTransactionRefusedMessage(batch, server));
        throw SimulatedSqlException.DistributedTransactionUnavailable(server);
    }

    private static SimulatedDbParameter RemoteParameter(string name, SqlValue value, bool output) =>
        new()
        {
            ParameterName = name,
            Value = value,
            Direction = output ? ParameterDirection.InputOutput : ParameterDirection.Input,
        };

    /// <summary>
    /// Runs <paramref name="text"/> in a fresh session of the server — starting
    /// in <paramref name="database"/> when one is given — and hands back what
    /// it produced, materialized before the session closes; the caller's
    /// <c>@@ROWCOUNT</c> reads the last count the server reported. The server's batch runs
    /// on past an error as a batch of its own does, and the error reaches the
    /// client in its place among the results, relayed and attributed to
    /// <paramref name="procedure"/> when a procedure call raised it — outside
    /// the caller's control flow, which no <c>TRY</c> of the caller's catches
    /// (probed 2026-09-28 against SQL Server 2025) — save an error the call
    /// ends with, which comes back as <paramref name="refusal"/>, as does a
    /// rowset with an <c>xml</c> column, which stops the call; the caller
    /// raises it after the outcomes ahead of it.
    /// </summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "EXEC … AT sends the caller's own text by design; a procedure call's text is built from bracket-escaped identifiers. It runs against a sibling in-process Simulation.")]
    private static List<SimulatedStatementOutcome> RunRemoteCall(BatchContext batch, LinkedServer server, string text, List<SimulatedDbParameter> parameters, string? database, string? procedure, SimulatedDbTransaction? enlisting, out SimulatedSqlException? refusal)
    {
        refusal = null;
        var outcomes = new List<SimulatedStatementOutcome>();
        // The caller's @@ROWCOUNT reads the last count the provider saw: the
        // rows of the last rowset, or the last DML count, NOCOUNT or not and
        // whatever RETURN, DECLARE or SET followed, or 0 after an error; a
        // call that reported none of these leaves it as it was (probed
        // 2026-09-28 against SQL Server 2025).
        int? lastCount = null;
        using var connection = server.OpenSession(database);
        // The provider's session is a MARS one, whose batch rolls back a
        // transaction it leaves open (Msg 3997).
        connection.ScopesTransactionsToBatch = true;
        using var command = connection.CreateCommand();
        command.CommandText = text;
        foreach (var parameter in parameters)
            _ = command.Parameters.Add(parameter);
        // The call runs in the caller's transaction, as a session of its own.
        enlisting?.Attach(connection, TransactionMembership.Enlisted, running: false);
        try
        {
            foreach (var outcome in server.Target.CreateResultSetsForCommand(command))
            {
                if (refusal is not null)
                    break;
                // The parameterized batch a procedure call runs in repeats the
                // procedure's Msg 266, which the call itself sends once.
                if (procedure is not null && outcome is SimulatedErrorOutcome { Exception.Number: 266 } && outcomes is [.., SimulatedErrorOutcome { Exception.Number: 266 }])
                    continue;
                switch (outcome)
                {
                    case SimulatedErrorOutcome failure:
                        lastCount = 0;
                        // The provider hands back no rowset for the statement that
                        // failed.
                        if (outcomes is [.., SimulatedSqlResultSet { RecordsAffected: <= 0 } failed] && !failed.RowBytes.Any())
                            outcomes.RemoveAt(outcomes.Count - 1);
                        var (messages, error) = SimulatedSqlException.RelayedRemoteEntries(failure.Exception, procedure, endsBatch: false);
                        foreach (var message in messages)
                            outcomes.Add(new SimulatedInfoOutcome(message));
                        if (error is not null)
                            outcomes.Add(new SimulatedErrorOutcome(error));
                        break;
                    // The notice the server follows a failed write with arrives
                    // ahead of the error it follows, at state 1, as the rest of
                    // the error's entries do.
                    case SimulatedInfoOutcome { Message.Number: 3621 } notice when outcomes is [.., SimulatedErrorOutcome]:
                        var entry = notice.Message;
                        outcomes.Insert(outcomes.Count - 1, new SimulatedInfoOutcome(
                            new SimulatedError(entry.Class, entry.LineNumber, entry.Message, entry.Number, procedure ?? entry.Procedure, entry.Server, entry.Source, state: 1)));
                        break;
                    // The provider refuses a rowset with an xml column, which ends
                    // the batch after what the server sent ahead of it.
                    case SimulatedSqlResultSet result when Array.Exists(result.Schema, static type => type.PairClass == TypePairClass.Xml):
                        refusal = SimulatedSqlException.XmlInRemoteCallRowset();
                        break;
                    case SimulatedSqlResultSet result:
                        var (providerSchema, providerRows) = RemoteWrite.AsProviderRowset(result.Schema, result.RowBytes);
                        byte[][] rows = [.. providerRows];
                        outcomes.Add(new SimulatedSqlResultSet(providerSchema, result.ColumnNames, rows, result.RecordsAffected)
                        {
                            ColumnNullability = result.ColumnNullability,
                        });
                        lastCount = rows.Length;
                        break;
                    default:
                        // A count the server's NOCOUNT withholds reaches the
                        // provider as none (probed 2026-10-05 against SQL Server 2025).
                        if (outcome is SimulatedNonQuery { RecordsAffected: >= 0 } counted)
                            lastCount = connection.NoCount ? 0 : counted.RecordsAffected;
                        outcomes.Add(outcome);
                        break;
                }
            }
        }
        finally
        {
            // The call returns, and its session leaves the caller's
            // transaction, which goes on.
            if (enlisting is not null && ReferenceEquals(connection.CurrentTransaction, enlisting))
                _ = enlisting.Detach(connection);
        }
        if (lastCount is int count)
            batch.Connection.LastStatementRowCount = count;
        // The errors the call ends with arrive in reverse, as one error's
        // entries do, and the last of them is the call's own failure: a TRY
        // catches it, @@ERROR reads it and XACT_ABORT ends the batch on it,
        // where an error the server's batch ran on past reaches the client
        // outside the caller's control flow (probed 2026-10-05 against SQL
        // Server 2025).
        // The MARS session's own refusal of a transaction left open arrives
        // ahead of the error the call ended with.
        if (outcomes is [.., SimulatedErrorOutcome, SimulatedErrorOutcome { Exception.Number: 3997 } marsEnd])
        {
            outcomes.RemoveAt(outcomes.Count - 1);
            outcomes.Insert(outcomes.Count - 1, marsEnd);
        }
        // An EXEC … AT's final error, or a procedure call's that no procedure
        // ran to raise — the procedure not found, its arguments refused — is
        // the call's own failure: a TRY catches it, @@ERROR reads it and
        // XACT_ABORT ends the batch on it. Any other reaches the client
        // outside the caller's control flow (probed 2026-10-05 against SQL
        // Server 2025).
        if (refusal is null && outcomes is [.., SimulatedErrorOutcome last]
            && (procedure is null || (outcomes.Count == 1 && last.Exception.Number is 201 or 2812 or 8144 or 8145)))
        {
            refusal = last.Exception;
            // Raised by the server's batch, which it ended, not the caller's.
            refusal.EndedCalledBatch = true;
            outcomes.RemoveAt(outcomes.Count - 1);
        }
        return outcomes;
    }

    /// <summary>
    /// Rewrites each <c>?</c> placeholder outside a string literal, comment or
    /// bracketed / quoted name as <c>@P1</c>, <c>@P2</c> …, returning the
    /// rewritten text and how many there were.
    /// </summary>
    private static (string Text, int Count) BindPlaceholders(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        var count = 0;
        var i = 0;
        while (i < text.Length)
        {
            var end = SkippedSpanEnd(text, i);
            if (end > i)
            {
                _ = builder.Append(text, i, end - i);
                i = end;
                continue;
            }
            if (text[i] == '?')
                _ = builder.Append("@P").Append((++count).ToString(CultureInfo.InvariantCulture));
            else
                _ = builder.Append(text[i]);
            i++;
        }
        return (builder.ToString(), count);
    }

    // Where the string literal, quoted or bracketed name, or comment starting
    // at start ends; start itself when none starts there.
    private static int SkippedSpanEnd(string text, int start)
    {
        var c = text[start];
        if (c is '\'' or '"' or '[')
        {
            var close = c == '[' ? ']' : c;
            var end = start + 1;
            while (end < text.Length)
            {
                if (text[end] != close)
                    end++;
                else if (end + 1 < text.Length && text[end + 1] == close)
                    end += 2;
                else
                    return end + 1;
            }
            return text.Length;
        }
        if (c == '-' && start + 1 < text.Length && text[start + 1] == '-')
        {
            var end = text.IndexOf('\n', start);
            return end < 0 ? text.Length : end + 1;
        }
        if (c == '/' && start + 1 < text.Length && text[start + 1] == '*')
        {
            var end = text.IndexOf("*/", start + 2, StringComparison.Ordinal);
            return end < 0 ? text.Length : end + 2;
        }
        return start;
    }
}
