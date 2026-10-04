using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

// What a linked server reports through its provider: the writes through a
// four-part name or an OPENQUERY target, EXEC … AT and a four-part procedure
// call, and the distributed transaction a write inside a local one needs.
// Wording probed 2026-09-28 against SQL Server 2025 through a loopback
// MSOLEDBSQL linked server, and against a second SQL Server 2025 instance for
// the distributed-transaction refusal.
partial class SimulatedSqlException
{
    /// <summary>
    /// Msg 7314: a four-part name, or the target of a write through one, names a
    /// table the server doesn't hold; <paramref name="quotedName"/> is each
    /// written segment after the server's in double quotes.
    /// </summary>
    internal static SimulatedSqlException RemoteTableNotFound(LinkedServer server, string quotedName) =>
        new($"The OLE DB provider \"{server.ProviderInMessages}\" for linked server \"{server.Name}\" does not contain the table \"{quotedName}\". The table either does not exist or the current user does not have permissions on that table.", 7314, 16, 1);

    /// <summary>Msg 7313: the target of a write through a four-part name omits its schema.</summary>
    internal static SimulatedSqlException RemoteSchemaOrCatalogInvalid(LinkedServer server) =>
        new($"An invalid schema or catalog was specified for the provider \"{server.ProviderInMessages}\" for linked server \"{server.Name}\".", 7313, 16, 1);

    /// <summary>
    /// Msg 7411: the server's <c>rpc out</c> option is off for <c>EXEC … AT</c>
    /// or a four-part procedure call (<paramref name="what"/> <c>RPC</c>), or
    /// its <c>data access</c> option is off for a four-part name or
    /// <c>OPENQUERY</c> (<c>DATA ACCESS</c>).
    /// </summary>
    internal static SimulatedSqlException ServerNotConfiguredFor(string serverName, string what) =>
        new($"Server '{serverName}' is not configured for {what}.", 7411, 16, 1);

    /// <summary>
    /// Msg 7391: the work needs a distributed transaction the server's
    /// coordinator won't begin — a write inside a local transaction, or a remote
    /// call under one while <c>remote proc transaction promotion</c> is on.
    /// Out of the box real's coordinators refuse network transactions, which
    /// is the refusal <see cref="DistributedTransactionRefusedMessage"/> quotes
    /// ahead of this. It ends the batch and rolls the transaction back as under
    /// <c>XACT_ABORT</c>, dooming it when caught.
    /// </summary>
    internal static SimulatedSqlException DistributedTransactionUnavailable(LinkedServer server) =>
        new($"The operation could not be performed because OLE DB provider \"{server.ProviderInMessages}\" for linked server \"{server.Name}\" was unable to begin a distributed transaction.", 7391, 16, 2) { AbortsAsUnderXactAbort = true };

    /// <summary>
    /// Msg 3910: the distributed transaction a loopback linked server needs,
    /// which real can't enlist the server it runs on in. Reported at line 1,
    /// the loopback session's, and aborts as
    /// <see cref="DistributedTransactionUnavailable"/> does.
    /// </summary>
    internal static SimulatedSqlException TransactionContextInUse() =>
        new SimulatedSqlException("Transaction context in use by another session.", 3910, 16, 2) { AbortsAsUnderXactAbort = true }.PinLine(1);

    /// <summary>
    /// Msg 7412, class 0: a message the provider returned, which real relays
    /// ahead of the error it explains.
    /// </summary>
    internal static SimulatedError ProviderMessage(BatchContext batch, LinkedServer server, string text) =>
        batch.InfoMessage(@class: 0, state: 2, number: 7412, $"OLE DB provider \"{server.ProviderInMessages}\" for linked server \"{server.Name}\" returned message \"{text}\".");

    /// <summary>The coordinator's refusal Msg 7391 follows, as a <see cref="ProviderMessage"/>.</summary>
    internal static SimulatedError DistributedTransactionRefusedMessage(BatchContext batch, LinkedServer server) =>
        ProviderMessage(batch, server, "The partner transaction manager has disabled its support for remote/network transactions.");

    /// <summary>The provider's own account of a write it refused, as a <see cref="ProviderMessage"/>.</summary>
    internal static SimulatedError MultipleStepOperationMessage(BatchContext batch, LinkedServer server) =>
        ProviderMessage(batch, server, "Multiple-step OLE DB operation generated errors. Check each OLE DB status value, if available. No work was done.");

    /// <summary>
    /// Msg 7344: an INSERT through a four-part name lists the remote table's
    /// identity column; <paramref name="bracketedName"/> is the written name
    /// with every segment bracketed.
    /// </summary>
    internal static SimulatedSqlException RemoteColumnNotWritable(LinkedServer server, string bracketedName, string columnName) =>
        new($"The OLE DB provider \"{server.ProviderInMessages}\" for linked server \"{server.Name}\" could not INSERT INTO table \"{bracketedName}\" because of column \"{columnName}\". The user did not have permission to write to the column.", 7344, 16, 1);

    /// <summary>
    /// Msg 405: a write whose target is remote carries an <c>OUTPUT</c> clause,
    /// or reads its rows from a nested DML statement.
    /// </summary>
    internal static SimulatedSqlException RemoteDmlTargetWithOutput() =>
        new("A remote table cannot be used as a DML target in a statement which includes an OUTPUT clause or a nested DML statement.", 405, 16, 1);

    /// <summary>Msg 5315: a <c>MERGE</c> whose target is a four-part name or an <c>OPENQUERY</c>.</summary>
    internal static SimulatedSqlException MergeTargetIsRemote() =>
        new("The target of a MERGE statement cannot be a remote table, a remote view, or a view over remote tables.", 5315, 16, 1);

    /// <summary>
    /// Msg 8180, which the provider puts ahead of an error the server raised
    /// compiling the statement a remote write sends — an identity, computed or
    /// <c>timestamp</c> column in an UPDATE's SET list. Both report line 1, the
    /// remote statement's.
    /// </summary>
    internal static SimulatedSqlException RemoteStatementNotPrepared(SimulatedSqlException compileError)
    {
        var prepare = new SimulatedError(@class: 16, lineNumber: 1, "Statement(s) could not be prepared.", 8180, procedure: "", server: SimulatedDbConnection.DataSourceName, source: SourceName, state: 1);
        var cause = compileError.Errors[0];
        cause.LineNumber = 1;
        return new SimulatedSqlException(prepare.Message + Environment.NewLine + cause.Message, prepare, cause) { diagnosticsResolved = true };
    }

    /// <summary>
    /// Msg 16955: an <c>OPENQUERY</c> write target whose query the provider
    /// can't open an updatable cursor over — an aggregate, no table. Real
    /// reports it at the remote statement's line 1, after a
    /// <see cref="MultipleStepOperationMessage"/>.
    /// </summary>
    internal static SimulatedSqlException CouldNotCreateAcceptableCursor() =>
        new SimulatedSqlException("Could not create an acceptable cursor.", 16955, 16, 2).PinLine(1);

    /// <summary>
    /// The error a linked server raised on a remote write's statement, as the
    /// provider relays it (<see cref="RelayedRemoteEntries"/>): the class-0
    /// entries go ahead as messages, and what's left ends the batch, where a
    /// <c>TRY</c> can still catch it.
    /// </summary>
    internal static SimulatedSqlException RelayedRemoteError(BatchContext batch, SimulatedSqlException remote)
    {
        var (messages, error) = RelayedRemoteEntries(remote, procedure: null, endsBatch: true);
        foreach (var message in messages)
            batch.Connection.PendingMessages.Enqueue(message);
        return error ?? remote;
    }

    /// <summary>
    /// An error a linked server raised, split as the provider relays it: every
    /// entry in reverse order — so the Msg 3621 the server follows a failed
    /// write with, and a trigger's Msg 3609, arrive ahead of the error they
    /// follow — each keeping its own number, line and server, and its state
    /// save 0, which arrives as 1. The class-0 entries come back as messages
    /// and the rest as one error, which names <paramref name="procedure"/> —
    /// a remote procedure call's own spelling of it — when one is given, and
    /// ends the batch when <paramref name="endsBatch"/> says so.
    /// </summary>
    internal static (List<SimulatedError> Messages, SimulatedSqlException? Error) RelayedRemoteEntries(SimulatedSqlException remote, string? procedure, bool endsBatch)
    {
        var messages = new List<SimulatedError>();
        var errors = new List<SimulatedError>(remote.Errors.Count);
        for (var i = remote.Errors.Count - 1; i >= 0; i--)
        {
            var entry = remote.Errors[i];
            var procedureName = procedure ?? entry.Procedure;
            if (entry.Class == 0)
                messages.Add(new SimulatedError(@class: 0, entry.LineNumber, entry.Message, entry.Number, procedureName, entry.Server, entry.Source, state: 1));
            else
                errors.Add(new SimulatedError(entry.Class, entry.LineNumber, entry.Message, entry.Number, procedureName, entry.Server, entry.Source, entry.State == 0 ? (byte)1 : entry.State));
        }
        return errors.Count == 0
            ? (messages, null)
            : (messages, new SimulatedSqlException(string.Join(Environment.NewLine, errors.Select(error => error.Message)), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(errors)) { diagnosticsResolved = true, TerminatesBatch = endsBatch });
    }

    /// <summary>
    /// Msg 9514: a distributed query meets an <c>xml</c> column, which the
    /// provider can't carry — a four-part name's table or view that has one,
    /// named as written (<c>lb.db.dbo.t</c>) and reported at line 12 wherever
    /// the statement sits, or a rowset that returns one, which
    /// <c>OPENQUERY</c> reports as <c>OPENQUERY</c> at its statement's line and
    /// <c>EXEC … AT</c> or a remote procedure call as <c>IROWSET</c> at line 1,
    /// ending the batch.
    /// </summary>
    internal static SimulatedSqlException XmlInDistributedQuery(string remoteObject) =>
        new($"Xml data type is not supported in distributed queries. Remote object '{remoteObject}' has xml column(s).", 9514, 16, 1);

    /// <inheritdoc cref="XmlInDistributedQuery"/>
    internal static SimulatedSqlException XmlInRemoteCallRowset() =>
        new SimulatedSqlException("Xml data type is not supported in distributed queries. Remote object 'IROWSET' has xml column(s).", 9514, 16, 1) { TerminatesBatch = true }.PinLine(1);

    /// <summary>
    /// Msg 7325: a four-part name's table or view has a CLR-typed column —
    /// <c>geography</c>, <c>geometry</c> or <c>hierarchyid</c> — which only a
    /// pass-through query reaches; <paramref name="quotedName"/> is each
    /// written segment after the server's in double quotes.
    /// </summary>
    internal static SimulatedSqlException ClrTypeInDistributedQuery(string quotedName) =>
        new($"Objects exposing columns with CLR types are not allowed in distributed queries. Please use a pass-through query to access remote object '{quotedName}'.", 7325, 16, 1);

    /// <summary>
    /// Msg 7357: a four-part name's table or view exposes no column to the
    /// provider — every one it has is <c>json</c>, which the provider doesn't
    /// list; <paramref name="quotedName"/> as <see cref="ClrTypeInDistributedQuery"/>'s.
    /// </summary>
    internal static SimulatedSqlException RemoteObjectHasNoColumns(LinkedServer server, string quotedName) =>
        new($"Cannot process the object \"{quotedName}\". The OLE DB provider \"{server.ProviderInMessages}\" for linked server \"{server.Name}\" indicates that either the object has no columns or the current user does not have permissions on that object.", 7357, 16, 2);

    /// <summary>
    /// Msg 7346: a row a four-part name reads carries a value the provider
    /// exposes but can't convert — a non-NULL <c>vector</c>, which it lists as
    /// <c>varbinary</c>. Raised as the row is reached, after the rows before
    /// it, and ends the batch.
    /// </summary>
    internal static SimulatedSqlException RemoteRowDataNotConvertible(LinkedServer server) =>
        new($"Cannot get the data of the row from the OLE DB provider \"{server.ProviderInMessages}\" for linked server \"{server.Name}\". Could not convert the data value due to reasons other than sign mismatch or overflow.", 7346, 16, 2) { TerminatesBatch = true };

    /// <summary>
    /// Msg 102 at state 3, near the closing parenthesis: <c>EXEC ( … )</c>
    /// passing arguments without an <c>AT</c> naming the server they're for.
    /// </summary>
    internal static SimulatedSqlException ExecuteArgumentsWithoutServer() =>
        new("Incorrect syntax near ')'.", 102, 15, 3);
}
