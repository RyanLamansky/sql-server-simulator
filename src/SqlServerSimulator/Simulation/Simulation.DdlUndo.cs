using System.Collections.Concurrent;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Logs, in the session's open transaction, how to reverse a DDL change to
    /// a permanent object, so a <c>ROLLBACK</c> — or a savepoint rollback past
    /// it — undoes the DDL as SQL Server does (probed 2026-09-25: a rolled-back
    /// <c>CREATE TABLE</c>, view, procedure, <c>ALTER TABLE … ADD</c> or
    /// <c>CREATE INDEX</c> leaves no trace, and a rolled-back <c>DROP TABLE</c>
    /// restores the table with its rows). Outside a transaction the statement
    /// commits as it runs, so there is nothing to log.
    /// </summary>
    internal static void RecordDdlUndo(BatchContext batch, Action undo)
    {
        if (batch.Connection.CurrentTransaction is { } transaction)
            transaction.UndoLog.RecordSchemaChange(batch.Connection.Simulation, undo);
    }

    internal static void RecordDdlUndo(ParserContext context, Action undo) => RecordDdlUndo(context.Batch, undo);

    /// <summary>
    /// <see cref="RecordDdlUndo(BatchContext, Action)"/> for a catalog slot the statement stored or
    /// removed: the undo puts <paramref name="previous"/> back under
    /// <paramref name="name"/>, or empties the slot when there was none.
    /// </summary>
    internal static void RecordSlotUndo<T>(ParserContext context, ConcurrentDictionary<string, T> owner, string name, T? previous)
        where T : class =>
        RecordDdlUndo(context, () =>
        {
            if (previous is null)
                _ = owner.TryRemove(name, out _);
            else
                owner[name] = previous;
        });

    /// <summary>
    /// <see cref="RecordDdlUndo(BatchContext, Action)"/> for a statement about to change
    /// <paramref name="table"/> in place — the <c>ALTER TABLE</c> family, index
    /// DDL, a column or index rename — capturing it whole first. A table
    /// variable is left alone, since its changes never roll back.
    /// </summary>
    internal static void RecordTableDdlUndo(BatchContext batch, HeapTable table)
    {
        if (batch.Connection.CurrentTransaction is null || table.IsTableVariable)
            return;
        var snapshot = new HeapTableSnapshot(table, table.OwningDatabase ?? batch.CurrentDatabase);
        RecordDdlUndo(batch, snapshot.Restore);
    }

    internal static void RecordTableDdlUndo(ParserContext context, HeapTable table) => RecordTableDdlUndo(context.Batch, table);

    /// <summary>
    /// <see cref="RecordDdlUndo(BatchContext, Action)"/> for a security
    /// statement about to change <paramref name="database"/>'s principals, role
    /// memberships or permissions — <c>CREATE</c> / <c>ALTER</c> / <c>DROP</c>
    /// <c>USER</c> or <c>ROLE</c>, a membership change, <c>GRANT</c> /
    /// <c>REVOKE</c> / <c>DENY</c> — capturing all three first, since real
    /// rolls each back with the transaction (probed 2026-09-25).
    /// </summary>
    internal static void RecordSecurityUndo(BatchContext batch, Database database)
    {
        if (batch.Connection.CurrentTransaction is null)
            return;
        var principals = database.Principals.ToArray();
        var principalState = Array.ConvertAll(principals, entry => (entry.Value.Name, entry.Value.DefaultSchemaName, entry.Value.PasswordHash));
        (int RoleId, int MemberId)[] members;
        lock (database.RoleMembers)
            members = [.. database.RoleMembers];
        DatabasePermission[] permissions = [.. database.Permissions];
        RecordDdlUndo(batch, () =>
        {
            database.Principals.Clear();
            for (var i = 0; i < principals.Length; i++)
            {
                var principal = principals[i].Value;
                (principal.Name, principal.DefaultSchemaName, principal.PasswordHash) = principalState[i];
                database.Principals[principals[i].Key] = principal;
            }
            lock (database.RoleMembers)
            {
                database.RoleMembers.Clear();
                database.RoleMembers.AddRange(members);
            }
            database.Permissions.Clear();
            database.Permissions.AddRange(permissions);
        });
    }

    internal static void RecordSecurityUndo(ParserContext context, Database database) => RecordSecurityUndo(context.Batch, database);

    /// <summary>
    /// <see cref="RecordSecurityUndo(BatchContext, Database)"/> one scope out,
    /// for a statement about to change the server's logins, server roles, their
    /// memberships or server permissions — real rolls a <c>CREATE LOGIN</c>
    /// back with the transaction too (probed 2026-09-25).
    /// </summary>
    internal static void RecordServerSecurityUndo(BatchContext batch)
    {
        if (batch.Connection.CurrentTransaction is null)
            return;
        var simulation = batch.Connection.Simulation;
        var logins = simulation.Logins.ToArray();
        var roles = simulation.ServerRoles.ToArray();
        (int RoleId, int MemberId)[] members;
        lock (simulation.ServerRoleMembers)
            members = [.. simulation.ServerRoleMembers];
        ServerPermission[] permissions;
        lock (simulation.ServerPermissions)
            permissions = [.. simulation.ServerPermissions];
        RecordDdlUndo(batch, () =>
        {
            simulation.Logins.Clear();
            foreach (var (name, login) in logins)
                simulation.Logins[name] = login;
            simulation.ServerRoles.Clear();
            foreach (var (name, role) in roles)
                simulation.ServerRoles[name] = role;
            lock (simulation.ServerRoleMembers)
            {
                simulation.ServerRoleMembers.Clear();
                simulation.ServerRoleMembers.AddRange(members);
            }
            lock (simulation.ServerPermissions)
            {
                simulation.ServerPermissions.Clear();
                simulation.ServerPermissions.AddRange(permissions);
            }
        });
    }
}
