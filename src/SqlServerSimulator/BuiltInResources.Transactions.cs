using System.Buffers.Binary;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// Row sources for the transaction DMVs registered in BuiltInResources.ServerAndDatabases.cs.
internal static partial class BuiltInResources
{
    /// <summary>
    /// Rows for <c>sys.dm_tran_active_transactions</c>: every session's user
    /// transaction, then the querying statement's autocommit one when its
    /// session has none — named for the statement, read-only for a SELECT.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysDmTranActiveTransactions(Parser.BatchContext batch, Database database)
    {
        _ = database;
        var nullGuid = SqlValue.Null(SqlType.UniqueIdentifier);
        var nullBinary = SqlValue.Null(SqlType.Varbinary);
        var zero = SqlValue.FromInt32(0);
        var active = SqlValue.FromInt32(2);
        foreach (var connection in batch.Connection.Simulation.SnapshotConnections())
        {
            if (connection.CurrentTransaction is not { } transaction)
                continue;
            yield return [
                SqlValue.FromInt64(transaction.TransactionId),
                SqlValue.FromNVarchar(transaction.Name ?? "user_transaction"),
                SqlValue.FromDateTime(transaction.BeginTimeUtc),
                SqlValue.FromInt32(1),
                nullGuid, active, zero, SqlValue.FromInt32(258), zero, zero, SqlValue.FromInt32(-1), nullBinary,
            ];
        }

        if (batch.Connection.CurrentTransaction is null)
        {
            var verb = batch.CurrentStatement.StatementVerb;
            yield return [
                SqlValue.FromInt64(batch.CurrentTransactionId()),
                SqlValue.FromNVarchar(verb),
                SqlValue.FromDateTime(batch.CurrentStatement.UtcNow),
                SqlValue.FromInt32(verb == "SELECT" ? 2 : 1),
                nullGuid, active, zero, zero, zero, zero, zero, nullBinary,
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sys.dm_tran_session_transactions</c>: one per session with
    /// a user transaction, however deeply nested (real reports
    /// <c>open_transaction_count</c> 1 under a nested BEGIN). The descriptor
    /// is real's shape: 1 then the session id, each a little-endian int.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysDmTranSessionTransactions(Parser.BatchContext batch, Database database)
    {
        _ = database;
        var one = SqlValue.FromInt32(1);
        var bitOn = SqlValue.FromBoolean(true);
        var bitOff = SqlValue.FromBoolean(false);
        foreach (var connection in batch.Connection.Simulation.SnapshotConnections())
        {
            if (connection.CurrentTransaction is not { } transaction)
                continue;
            var descriptor = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(descriptor, 1);
            BinaryPrimitives.WriteInt32LittleEndian(descriptor.AsSpan(4), connection.Spid);
            yield return [
                SqlValue.FromInt32(connection.Spid),
                SqlValue.FromInt64(transaction.TransactionId),
                SqlValue.FromBinary(SqlType.GetBinary(8), descriptor),
                one, bitOn, bitOn, bitOff, bitOff, one,
            ];
        }
    }

    /// <summary>
    /// The one row of <c>sys.dm_tran_current_transaction</c>: the running
    /// statement's transaction, its snapshot stamp under SNAPSHOT isolation,
    /// and the commit counter as the version store's sequence.
    /// </summary>
    private static SqlValue[][] EnumerateSysDmTranCurrentTransaction(Parser.BatchContext batch, Database database)
    {
        _ = database;
        var lastCommit = batch.Connection.Simulation.CurrentTransactionCommitId;
        var snapshot = batch.Connection.CurrentTransaction?.SnapshotXid;
        return [[
            SqlValue.FromInt64(batch.CurrentTransactionId()),
            SqlValue.FromInt64(snapshot ?? 0),
            SqlValue.FromBoolean(snapshot is not null),
            SqlValue.Null(SqlType.BigInt),
            SqlValue.FromInt64(lastCommit),
            SqlValue.FromInt64(lastCommit + 1),
        ]];
    }
}
