using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;

namespace SqlServerSimulator.ExtendedEvents;

/// <summary>
/// <c>object_created</c>, <c>object_altered</c> and <c>object_deleted</c> —
/// and through them the trace's <c>Object:Created</c>, <c>Object:Altered</c>
/// and <c>Object:Deleted</c> — as SQL Server 2025 fires them (probed
/// 2026-10-16): for a table (a temporary one included, in <c>tempdb</c>), an
/// index, statistics, a view, procedure, function, trigger, type, synonym,
/// sequence, schema and database, each twice — its <c>Begin</c> phase as the
/// statement makes the change, then its <c>Commit</c> as the transaction
/// commits, or <c>Rollback</c> as it rolls back, a transaction's commits in
/// the reverse of their changes' order. A user, a role, a table variable,
/// <c>sp_rename</c>, <c>TRUNCATE</c>, <c>UPDATE STATISTICS</c> and a
/// constraint other than a key's index fire none.
/// </summary>
internal static class XeObjectEvents
{
    /// <summary>Whether a running session or trace takes any of the three events — three array reads.</summary>
    public static bool AnyTaken(Simulation simulation) =>
        XeEvents.Takes(simulation, XeEventKind.ObjectCreated) || XeEvents.Takes(simulation, XeEventKind.ObjectAltered)
        || XeEvents.Takes(simulation, XeEventKind.ObjectDeleted);

    /// <summary>The event for a schema-scoped object, its type the two characters <c>sys.objects.type</c> holds.</summary>
    public static void Note(BatchContext batch, XeEventKind kind, Database database, SchemaObject @object) =>
        Note(batch, kind, database, @object.ObjectId, @object.ObjectTypeCode, 0, @object.Name);

    /// <summary>
    /// Ends an autocommit statement's object events: each closing phase, in
    /// the reverse of the order the statement fired them — a key's index
    /// commits after its table (probed 2026-10-16 against SQL Server 2025:
    /// <c>create table t (a int primary key, b int unique)</c> begins the two
    /// indexes and then the table, and commits the table, the unique index
    /// and the primary key).
    /// </summary>
    public static void Close(BatchContext batch, bool succeeded)
    {
        if (batch.CurrentStatement.ObjectEventPhases is not { } phases)
            return;
        batch.CurrentStatement.ObjectEventPhases = null;
        for (var i = phases.Count - 1; i >= 0; i--)
        {
            if (succeeded)
                phases[i].Commit();
            else
                phases[i].Rollback();
        }
    }

    /// <summary>
    /// The events for the indexes of <paramref name="table"/>'s keys whose ids
    /// <paramref name="before"/> doesn't list — every key's for a table just
    /// created — each ahead of its table's own event, as real fires them.
    /// </summary>
    public static void NoteKeyIndexes(BatchContext batch, XeEventKind kind, Storage.HeapTable table, HashSet<int>? before = null)
    {
        if (batch.IsSkipping || !XeEvents.Takes(batch.Connection.Simulation, kind))
            return;
        foreach (var identity in table.IndexIdentities())
        {
            if (identity.Constraint is not null && identity.Name is { } name && before?.Contains(identity.IndexId) != true)
                NoteIndex(batch, kind, DatabaseOf(batch, table), table.ObjectId, identity.IndexId, name, statistics: false);
        }
    }

    /// <summary>The ids of the indexes backing <paramref name="table"/>'s keys, for <see cref="NoteKeyIndexes"/> to compare against after a change.</summary>
    public static HashSet<int> KeyIndexIds(Storage.HeapTable table)
    {
        var ids = new HashSet<int>();
        foreach (var identity in table.IndexIdentities())
        {
            if (identity.Constraint is not null)
                _ = ids.Add(identity.IndexId);
        }
        return ids;
    }

    /// <summary>The event for a table, a temporary one reported in <c>tempdb</c>.</summary>
    public static void NoteTable(BatchContext batch, XeEventKind kind, Storage.HeapTable table)
    {
        if (!batch.IsSkipping && XeEvents.Takes(batch.Connection.Simulation, kind))
            Note(batch, kind, DatabaseOf(batch, table), table);
    }

    /// <summary>The database a table's events name: its own, <c>tempdb</c> for a temporary one.</summary>
    public static Database DatabaseOf(BatchContext batch, Storage.HeapTable table) =>
        table.OwningDatabase
        ?? (table.Name.StartsWith('#') ? batch.Connection.Simulation.Databases[Simulation.TempdbDatabaseName] : batch.CurrentDatabase);

    /// <summary>
    /// The event for an index or statistics, reported under its table's id
    /// with its own id and name: type <c>IX</c> for an index, <c>ST</c> for
    /// statistics.
    /// </summary>
    public static void NoteIndex(BatchContext batch, XeEventKind kind, Database database, int tableId, int indexId, string name, bool statistics, bool independent = false) =>
        Note(batch, kind, database, tableId, statistics ? "ST" : "IX", indexId, name, independent);

    /// <summary>
    /// Fires <paramref name="kind"/>'s <c>Begin</c> phase now and the
    /// <c>Commit</c> or <c>Rollback</c> its transaction's end decides — an
    /// autocommit statement's own end (<see cref="Close"/>); an
    /// object with no id of its own (a database) passes 0. A change made
    /// <paramref name="independent"/> of the statement's transaction — a
    /// statistic a query's compile creates — commits at once. The type is the
    /// object's two characters (<c>U </c>, <c>IX</c>, <c>DB</c>), which the
    /// <c>object_type</c> map keys by their codes, the first in the low byte.
    /// </summary>
    public static void Note(BatchContext batch, XeEventKind kind, Database database, int objectId, string typeCode, int indexId, string name, bool independent = false)
    {
        var connection = batch.Connection;
        if ((batch.IsSkipping && !independent) || !XeEvents.Takes(connection.Simulation, kind))
            return;
        var key = (ulong)(typeCode[0] | ((typeCode.Length > 1 ? typeCode[1] : ' ') << 8));
        var pairing = new System.Runtime.CompilerServices.StrongBox<long>();
        // The default trace alone takes it: written as its row, without the
        // Extended Events catalog a firing reads its fields from — nor an
        // autocommit transaction id drawn for it, which would move the ids
        // the session's later transactions take.
        if (!XeEvents.TakesBeyondDefaultTrace(connection.Simulation, kind))
        {
            var drawn = connection.CurrentTransaction?.TransactionId ?? batch.CurrentStatement.AutocommitTransactionId;
            void Write(int phase) => Trace.TraceEvents.WriteDefaultObjectEvent(connection, kind, phase, database, objectId, key, indexId, name, drawn, pairing);
            Write(0);
            if (independent)
                Write(1);
            else if (connection.CurrentTransaction is { } open)
                open.UndoLog.RecordObjectEvent(() => Write(1), () => Write(2));
            else
                (batch.CurrentStatement.ObjectEventPhases ??= []).Add((() => Write(1), () => Write(2)));
            return;
        }
        var transactionId = batch.CurrentTransactionId();
        void Fire(ulong phase, string phaseText) => XeEvents.Fire(connection, kind, @event => XeEvents.Data(@event,
            ("database_id", XeValue.FromUnsigned((ulong)database.Id)),
            ("object_id", XeValue.FromSigned(objectId)),
            ("object_type", XeValue.FromMap(key, null)),
            ("index_id", XeValue.FromUnsigned((ulong)indexId)),
            ("related_object_id", XeValue.FromSigned(0)),
            ("ddl_phase", XeValue.FromMap(phase, phaseText)),
            ("transaction_id", XeValue.FromSigned(transactionId)),
            ("object_name", XeValue.FromUnicode(name)),
            ("database_name", XeValue.FromUnicode(""))),
            tracePairing: pairing);
        Fire(0, "Begin");
        if (independent)
            Fire(1, "Commit");
        else if (connection.CurrentTransaction is { } transaction)
            transaction.UndoLog.RecordObjectEvent(() => Fire(1, "Commit"), () => Fire(2, "Rollback"));
        else
            (batch.CurrentStatement.ObjectEventPhases ??= []).Add((() => Fire(1, "Commit"), () => Fire(2, "Rollback")));
    }

    /// <summary>
    /// The event for an index the table holds by <paramref name="name"/>, its
    /// id read off the table; one the table doesn't list by name (a JSON,
    /// vector, XML or spatial index, a hypothetical one) reports none.
    /// </summary>
    public static void NoteIndexNamed(BatchContext batch, XeEventKind kind, Storage.HeapTable table, string name)
    {
        if (batch.IsSkipping || !XeEvents.Takes(batch.Connection.Simulation, kind))
            return;
        foreach (var identity in table.IndexIdentities())
        {
            if (identity.Name is { } indexName && string.Equals(indexName, name, StringComparison.OrdinalIgnoreCase))
            {
                NoteIndex(batch, kind, DatabaseOf(batch, table), table.ObjectId, identity.IndexId, indexName, statistics: false);
                return;
            }
        }
    }

    /// <summary>The <c>user_type_id</c> of the type <paramref name="schema"/> holds as <paramref name="name"/>, 0 for none.</summary>
    public static int TypeIdOf(Schema schema, string name) =>
        schema.AliasTypes.TryGetValue(name, out var alias) ? alias.UserTypeId
        : schema.TableTypes.TryGetValue(name, out var table) ? table.UserTypeId
        : 0;

    /// <summary>
    /// The events for a database: <c>object_created</c> or
    /// <c>object_deleted</c> under the database's own id, type <c>DB</c> and
    /// object id 0, then <c>database_created</c> or <c>database_dropped</c>
    /// naming it.
    /// </summary>
    public static void NoteDatabase(BatchContext batch, XeEventKind kind, Database database)
    {
        if (batch.IsSkipping)
            return;
        var connection = batch.Connection;
        void Named() => XeEvents.Fire(connection, kind == XeEventKind.ObjectCreated ? XeEventKind.DatabaseCreated : XeEventKind.DatabaseDropped,
            @event => XeEvents.Data(@event, ("database_name", XeValue.FromUnicode(database.Name))));
        // Closed in reverse, so the database's own event follows its commit.
        (batch.CurrentStatement.ObjectEventPhases ??= []).Add((Named, static () => { }));
        Note(batch, kind, database, 0, "DB", 0, database.Name);
    }
}
