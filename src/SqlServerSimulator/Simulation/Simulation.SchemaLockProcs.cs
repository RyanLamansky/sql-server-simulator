using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sp_getschemalock / sp_releaseschemalock, the extended procedures SQL
// Server's OLE DB provider brackets a linked server's statement with: the
// object's schema-stability lock for the statement, and its schema version
// to check the metadata it read against.
partial class Simulation
{
    /// <summary>
    /// <c>sp_getschemalock @handle OUTPUT, @version OUTPUT, @object_name</c>:
    /// takes Sch-S on the object for the transaction and writes back a
    /// handle — the one already given out while the object's lock is still
    /// held — and the object's schema version (<see cref="ObjectSchemaVersion"/>).
    /// Outside a user transaction it is Msg 3918, returning 3918; an object
    /// the name doesn't reach is Msg 208, which ends the batch and the
    /// transaction (probed 2026-10-11 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpGetSchemaLock(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        if (arguments.Count == 0)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.ProcedureExpectsParameter("sp_getschemalock", "handle", 71), 1);
        if (arguments.Count < 3)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.ProcedureExpectsParameter("sp_getschemalock", "object_name", 73), 1);
        if (batch.Connection.CurrentTransaction is not { } transaction)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.MustExecuteInUserTransaction(71), 1, returnCode: 3918);

        var written = arguments[2].Value.IsNull ? "" : arguments[2].Value.CoerceTo(SqlType.NVarchar).AsString;
        if (!Parser.Expressions.ObjectId.TryParseObjectName(written, out var name) || ResolveSchemaLockObject(batch, name) is not { } target)
            throw AtProcedureLine(SimulatedSqlException.SchemaLockObjectNotFound(Parser.Expressions.ObjectId.TryParseObjectName(written, out var parsed) ? parsed.Leaf : written), calledAs, 1);

        var handles = transaction.SchemaLockHandles ??= [];
        var handle = handles.Find(entry => ReferenceEquals(entry.Object, target)).Handle;
        if (handle == 0)
        {
            var connection = batch.Connection;
            var owner = connection.LockOwner;
            connection.Simulation.LockManager.Acquire(target.SchemaLock, LockMode.SchemaStability, owner, connection.LockTimeoutMillis);
            transaction.HeldLocks.Add((target.SchemaLock, LockMode.SchemaStability, owner));
            handle = ++transaction.LastSchemaLockHandle;
            handles.Add((handle, target));
        }
        if (arguments[0].OutputSlot is { } handleSlot)
            handleSlot.Value = SqlValue.FromInt32(handle).CoerceTo(handleSlot.DeclaredType);
        if (arguments[1].OutputSlot is { } versionSlot)
            versionSlot.Value = SqlValue.FromInt64(ObjectSchemaVersion(target.ModifyDate)).CoerceTo(versionSlot.DeclaredType);
    }

    /// <summary>
    /// <c>sp_reset_connection</c> run from T-SQL, which real answers as a
    /// missing object (probed 2026-10-11 against SQL Server 2025): Msg 208
    /// from line 1 of the procedure under the name the call used, whatever
    /// the call's arguments.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpResetConnection(BatchContext batch, string calledAs)
    {
        _ = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        throw AtSystemProcedureLine(calledAs, SimulatedSqlException.ResetConnectionFromBatch(), 1);
    }

    /// <summary>
    /// <c>sp_releaseschemalock @handle</c>: gives back the lock
    /// <c>sp_getschemalock</c> took under the handle. A handle the
    /// transaction doesn't hold is Msg 8174, which ends the batch and the
    /// transaction; outside a user transaction it is Msg 3918.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpReleaseSchemaLock(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        if (arguments.Count == 0)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.ProcedureExpectsParameter("sp_releaseschemalock", "handle", 71), 1);
        if (batch.Connection.CurrentTransaction is not { } transaction)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.MustExecuteInUserTransaction(71), 1, returnCode: 3918);

        var handle = arguments[0].Value.IsNull ? 0 : arguments[0].Value.CoerceTo(SqlType.Int32).AsInt32;
        var handles = transaction.SchemaLockHandles;
        var index = handles?.FindIndex(entry => entry.Handle == handle) ?? -1;
        if (index < 0)
            throw AtProcedureLine(SimulatedSqlException.SchemaLockHandleNotFound(handle), calledAs, 1);

        var target = handles![index].Object;
        handles.RemoveAt(index);
        var owner = batch.Connection.LockOwner;
        var held = transaction.HeldLocks.FindLastIndex(entry => ReferenceEquals(entry.Resource, target.SchemaLock) && entry.Mode == LockMode.SchemaStability && ReferenceEquals(entry.Owner, owner));
        if (held >= 0)
        {
            transaction.HeldLocks.RemoveAt(held);
            batch.Connection.Simulation.LockManager.Release(target.SchemaLock, LockMode.SchemaStability, owner);
        }
    }

    /// <summary>
    /// The table, view, synonym, procedure or function a schema-lock name
    /// reaches — its database part one of the server's, its schema part the
    /// default schema when omitted.
    /// </summary>
    private static SchemaObject? ResolveSchemaLockObject(BatchContext batch, MultiPartName name)
    {
        if (name.Count > 3)
            return null;
        var database = batch.CurrentDatabase;
        if (name.Count == 3 && !batch.Connection.Simulation.Databases.TryGetValue(name[0], out database))
            return null;
        var schemaName = name.Count >= 2 ? name[name.Count - 2] : Database.DefaultSchemaName;
        if (!database.Schemas.TryGetValue(schemaName, out var schema))
            return null;
        var leaf = name.Leaf;
        return schema.HeapTables.TryGetValue(leaf, out var table) ? table
            : schema.Views.TryGetValue(leaf, out var view) ? view
            : schema.Synonyms.TryGetValue(leaf, out var synonym) ? synonym
            : schema.Procedures.TryGetValue(leaf, out var procedure) ? procedure
            : schema.Functions.TryGetValue(leaf, out var function) ? function
            : null;
    }
}
