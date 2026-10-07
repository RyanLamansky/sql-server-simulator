using System.Globalization;
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
    /// written segment after the server's in double quotes, and a read names
    /// the server as written. It ends the batch, even one that ran the
    /// statement through dynamic SQL (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException RemoteTableNotFound(LinkedServer server, string quotedName, string? writtenServer = null) =>
        new($"The OLE DB provider \"{server.ProviderInMessages}\" for linked server \"{writtenServer ?? server.Name}\" does not contain the table \"{quotedName}\". The table either does not exist or the current user does not have permissions on that table.", 7314, 16, 1) { TerminatesBatch = true };

    // The linked-server procedures' refusals below were probed 2026-10-05
    // against SQL Server 2025; the lines they report live in
    // Simulation.SystemProcedureErrorSite.

    /// <summary>Msg 15426: <c>sp_addlinkedserver</c> with properties and no provider.</summary>
    internal static SimulatedSqlException LinkedServerNeedsProvider() =>
        new("You must specify a provider name with this set of properties.", 15426, 16, 1);

    /// <summary>Msg 15427: <c>sp_addlinkedserver</c> naming a product other than SQL Server and no provider.</summary>
    internal static SimulatedSqlException LinkedServerUnknownProduct(string product) =>
        new($"You must specify a provider name for unknown product '{product}'.", 15427, 16, 1);

    /// <summary>Msg 15428: <c>sp_addlinkedserver</c> naming the <c>SQL Server</c> product and a provider.</summary>
    internal static SimulatedSqlException LinkedServerSqlServerProductProperties() =>
        new("You cannot specify a provider or any properties for product 'SQL Server'.", 15428, 16, 1);

    /// <summary>Msg 15429: <c>sp_addlinkedserver</c> with a NULL product and a provider.</summary>
    internal static SimulatedSqlException LinkedServerInvalidProduct(string product) =>
        new($"'{product}' is an invalid product name.", 15429, 16, 1);

    /// <summary>Msg 15663: <c>sp_addlinkedserver</c> with <c>@linkedstyle = 0</c>, the old remote-server form.</summary>
    internal static SimulatedSqlException AddServerNoLongerSupported() =>
        new("Feature \"sp_addserver\" is no longer supported. Replace remote servers by using linked servers.", 15663, 16, 1);

    /// <summary>Msg 15028: <c>sp_addlinkedserver</c> naming a server that exists, as written.</summary>
    internal static SimulatedSqlException LinkedServerAlreadyExists(string name) =>
        new($"The server '{name}' already exists.", 15028, 16, 1);

    /// <summary>Msg 15190: <c>sp_dropserver</c> without <c>'droplogins'</c> while the server maps a login.</summary>
    internal static SimulatedSqlException LinkedServerHasLogins(string name) =>
        new($"There are still remote logins or linked logins for the server '{name}'.", 15190, 16, 1);

    /// <summary>
    /// Msg 7215 at class 17: an <c>EXEC … AT</c> the provider refused before
    /// sending — an empty text, or arguments that don't match its <c>?</c>
    /// placeholders — after its own account as Msg 7412; it ends the batch.
    /// </summary>
    internal static SimulatedSqlException RemoteStatementNotExecuted(string serverName) =>
        new($"Could not execute statement on remote server '{serverName}'.", 7215, 17, 1) { TerminatesBatch = true };

    /// <summary>
    /// Msg 3933: work a linked server's enlistment would promote the session's
    /// transaction for, while the transaction holds a savepoint; it aborts as
    /// <see cref="DistributedTransactionUnavailable"/> does.
    /// </summary>
    internal static SimulatedSqlException CannotPromoteWithSavepoint() =>
        new("Cannot promote the transaction to a distributed transaction because there is an active save point in this transaction.", 3933, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Msg 4122: a four-part name called as a table-valued function in FROM.</summary>
    internal static SimulatedSqlException RemoteTableValuedFunctionCall() =>
        new("Remote table-valued function calls are not allowed.", 4122, 16, 1);

    /// <summary>Msg 7416: a linked server reached by a login it maps no login for.</summary>
    internal static SimulatedSqlException NoLoginMapping() =>
        new("Access to the remote server is denied because no login-mapping exists.", 7416, 16, 1);

    /// <summary>Msg 7313: the target of a write through a four-part name omits its schema.</summary>
    internal static SimulatedSqlException RemoteSchemaOrCatalogInvalid(LinkedServer server) =>
        new($"An invalid schema or catalog was specified for the provider \"{server.ProviderInMessages}\" for linked server \"{server.Name}\".", 7313, 16, 1);

    /// <summary>
    /// Msg 7411: the server's <c>rpc out</c> option is off for <c>EXEC … AT</c>
    /// or a four-part procedure call (<paramref name="what"/> <c>RPC</c>), or
    /// its <c>data access</c> option is off for a four-part name or
    /// <c>OPENQUERY</c> (<c>DATA ACCESS</c>). It ends the batch and rolls back
    /// an open transaction (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException ServerNotConfiguredFor(string serverName, string what) =>
        new($"Server '{serverName}' is not configured for {what}.", 7411, 16, 1) { AbortsAsUnderXactAbort = true };

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
    /// Msg 3971: an <c>OPENQUERY</c> or four-part read of a loopback server
    /// a remote call of the caller's transaction ran on, which meets that
    /// call's session — enlisted in the transaction — and can't resume it.
    /// Reported at line 1, ending the batch and rolling the transaction back
    /// as <see cref="TransactionContextInUse"/> does (probed 2026-10-07
    /// against SQL Server 2025). Real's <c>Desc</c> is an opaque transaction
    /// handle; this one is the enlisted session's id and the transaction's,
    /// in hex.
    /// </summary>
    internal static SimulatedSqlException CannotResumeTransaction(int sessionId, long transactionId) =>
        new SimulatedSqlException(string.Create(CultureInfo.InvariantCulture, $"The server failed to resume the transaction. Desc:{sessionId:x}{transactionId:x8}."), 3971, 16, 1) { AbortsAsUnderXactAbort = true }.PinLine(1);

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
    /// Msg 7344's account of a NULL an INSERT through a four-part name gives a
    /// column the server keeps NOT NULL (probed 2026-10-05 against SQL Server
    /// 2025); <paramref name="bracketedName"/> as <see cref="RemoteColumnNotWritable"/>'s.
    /// </summary>
    internal static SimulatedSqlException RemoteColumnValueViolatesIntegrity(LinkedServer server, string bracketedName, string columnName) =>
        new($"The OLE DB provider \"{server.ProviderInMessages}\" for linked server \"{server.Name}\" could not INSERT INTO table \"{bracketedName}\" because of column \"{columnName}\". The data value violated the integrity constraints for the column.", 7344, 16, 1);

    /// <summary>
    /// Msg 7344 state 2: an <c>UPDATE</c> through <c>OPENQUERY</c> setting a
    /// column its query computes, which real names the table of as the
    /// provider in brackets (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException RemoteColumnNotUpdatable(LinkedServer server, string columnName) =>
        new($"The OLE DB provider \"{server.ProviderInMessages}\" for linked server \"{server.Name}\" could not UPDATE table \"[{server.ProviderInMessages}]\" because of column \"{columnName}\". The user did not have permission to write to the column.", 7344, 16, 2);

    /// <summary>
    /// The refusals of an <c>OPENQUERY</c> whose query text is empty: the
    /// provider's Msg 7412, then Msg 7399 and 7321 (probed 2026-10-05 against
    /// SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException OpenQueryNoCommandText(BatchContext batch, LinkedServer server) =>
        AfterProviderMessage(batch, server, "Command text was not set for the command object.", Aggregate(
        [
            new($"The OLE DB provider \"{server.ProviderInMessages}\" for linked server \"{server.Name}\" reported an error. No command text was set.", 7399, 16, 1),
            new($"An error occurred while preparing the query \"\" for execution against OLE DB provider \"{server.ProviderInMessages}\" for linked server \"{server.Name}\". ", 7321, 16, 2),
        ]));

    /// <summary>
    /// The provider giving up on a query its linked server's session couldn't
    /// run — on a loopback server, one waiting on a lock its caller holds, for
    /// instance an <c>OPENQUERY</c> or four-part read of a row the caller's
    /// open transaction wrote, which doesn't enlist in it: the provider's
    /// Msg 7412, then Msg 7399 and Msg 7320 quoting the query, ending the
    /// batch and rolling the transaction back as under <c>XACT_ABORT</c>
    /// (probed 2026-10-07 against SQL Server 2025, after the server's
    /// <c>query timeout</c> elapsed).
    /// </summary>
    internal static SimulatedSqlException ProviderQueryTimeout(BatchContext batch, LinkedServer server, string query)
    {
        batch.Connection.PendingMessages.Enqueue(ProviderMessage(batch, server, "Query timeout expired"));
        List<SimulatedError> entries =
        [
            .. new SimulatedSqlException($"The OLE DB provider \"{server.ProviderInMessages}\" for linked server \"{server.Name}\" reported an error. Execution terminated by the provider because a resource limit was reached.", 7399, 16, 1).Errors,
            .. new SimulatedSqlException($"Cannot execute the query \"{query}\" against OLE DB provider \"{server.ProviderInMessages}\" for linked server \"{server.Name}\". ", 7320, 16, 2).Errors,
        ];
        return new(string.Join(Environment.NewLine, entries.Select(entry => entry.Message)), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(entries))
        {
            AbortsAsUnderXactAbort = true,
        };
    }

    /// <summary>
    /// <paramref name="error"/> carrying the provider's account of it, a
    /// <see cref="ProviderMessage"/>, ahead of its own entries — for a refusal
    /// raised before the statement runs, or one that ends the batch, which a
    /// pending message wouldn't precede.
    /// </summary>
    internal static SimulatedSqlException AfterProviderMessage(BatchContext batch, LinkedServer server, string text, SimulatedSqlException error)
    {
        List<SimulatedError> entries = [ProviderMessage(batch, server, text), .. error.Errors];
        return new(string.Join(Environment.NewLine, entries.Select(entry => entry.Message)), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(entries))
        {
            diagnosticsResolved = error.diagnosticsResolved,
            TerminatesBatch = error.TerminatesBatch,
            AbortsAsUnderXactAbort = error.AbortsAsUnderXactAbort,
        };
    }

    /// <summary>
    /// The error a server raised preparing an <c>OPENQUERY</c>'s query — a
    /// name that doesn't bind — after the provider's Msg 7412 and Msg 8180,
    /// each at line 1, the query's (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException OpenQueryNotPrepared(BatchContext batch, LinkedServer server, SimulatedSqlException compileError) =>
        AfterProviderMessage(batch, server, "Deferred prepare could not be completed.", RemoteStatementNotPrepared(compileError));

    /// <summary>
    /// The error a server raised describing an <c>OPENQUERY</c>'s query that
    /// doesn't parse, after its Msg 11529, both from
    /// <c>sys.sp_describe_first_result_set</c> at line 1 (probed 2026-10-05
    /// against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException OpenQueryNotDescribed(SimulatedSqlException syntaxError)
    {
        const string procedure = "sys.sp_describe_first_result_set";
        var described = new SimulatedError(@class: 16, lineNumber: 1, "The metadata could not be determined because every code path results in an error; see previous errors for some of these.", 11529, procedure, SimulatedDbConnection.DataSourceName, SourceName, state: 1);
        var cause = syntaxError.Errors[0];
        var relayed = new SimulatedError(cause.Class, 1, cause.Message, cause.Number, procedure, cause.Server, cause.Source, cause.State);
        return new SimulatedSqlException(described.Message + Environment.NewLine + relayed.Message, described, relayed) { diagnosticsResolved = true };
    }

    /// <summary>
    /// Msg 492: an <c>OPENQUERY</c> or <c>OPENROWSET</c> rowset with two
    /// columns of one name (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException OpenQueryDuplicateColumn(string columnName) =>
        new($"Duplicate column names are not allowed in result sets obtained through OPENQUERY and OPENROWSET. The column name \"{columnName}\" is a duplicate.", 492, 16, 1);

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
    /// a remote procedure call's own spelling of it — when one is given and
    /// the error is the called procedure's own, a procedure it ran by schema
    /// and name, and ends the batch when <paramref name="endsBatch"/> says so.
    /// </summary>
    internal static (List<SimulatedError> Messages, SimulatedSqlException? Error) RelayedRemoteEntries(SimulatedSqlException remote, string? procedure, bool endsBatch)
    {
        var messages = new List<SimulatedError>();
        var errors = new List<SimulatedError>(remote.Errors.Count);
        var calledLeaf = procedure?[(procedure.LastIndexOf('.') + 1)..];
        for (var i = remote.Errors.Count - 1; i >= 0; i--)
        {
            var entry = remote.Errors[i];
            // A procedure the called one runs is named with its schema
            // (probed 2026-10-05 against SQL Server 2025).
            var procedureName = procedure is null || entry.Number == 3997 ? entry.Procedure
                : string.IsNullOrEmpty(entry.Procedure) || Collation.Baseline.Equals(entry.Procedure, calledLeaf) ? procedure
                : entry.Procedure.Contains('.', StringComparison.Ordinal) ? entry.Procedure
                : "dbo." + entry.Procedure;
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
    /// <c>OPENQUERY</c> reports as <c>OPENQUERY</c> and <c>EXEC … AT</c> or a
    /// remote procedure call as <c>IROWSET</c>, each at its statement's line
    /// (probed 2026-10-05 against SQL Server 2025), ending the batch.
    /// </summary>
    internal static SimulatedSqlException XmlInDistributedQuery(string remoteObject) =>
        new($"Xml data type is not supported in distributed queries. Remote object '{remoteObject}' has xml column(s).", 9514, 16, 1) { TerminatesBatch = true };

    /// <inheritdoc cref="XmlInDistributedQuery"/>
    internal static SimulatedSqlException XmlInRemoteCallRowset() =>
        new("Xml data type is not supported in distributed queries. Remote object 'IROWSET' has xml column(s).", 9514, 16, 1) { TerminatesBatch = true };

    /// <summary>
    /// Msg 7325: a four-part name's table or view has a CLR-typed column —
    /// <c>geography</c>, <c>geometry</c> or <c>hierarchyid</c> — which only a
    /// pass-through query reaches; <paramref name="quotedName"/> is each
    /// written segment after the server's in double quotes. Like the xml
    /// refusal it stops the batch at the first statement meeting it (probed
    /// 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException ClrTypeInDistributedQuery(string quotedName) =>
        new($"Objects exposing columns with CLR types are not allowed in distributed queries. Please use a pass-through query to access remote object '{quotedName}'.", 7325, 16, 1) { TerminatesBatch = true };

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
