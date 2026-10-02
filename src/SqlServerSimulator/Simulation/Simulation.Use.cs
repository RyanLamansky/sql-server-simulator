using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>USE &lt;db&gt;</c> and switches
    /// <see cref="SimulatedDbConnection.CurrentDatabase"/> to the named
    /// database. The name is parsed as a single identifier (bare or
    /// bracketed) — variable forms (<c>USE @v</c>) and parenthesized
    /// expressions raise Msg 102 via <see cref="ParserContext.GetNextRequired{T}"/>'s
    /// type-mismatch check (matches probe-confirmed real-server behavior).
    /// Missing database raises Msg 911 via
    /// <see cref="SimulatedSqlException.DatabaseDoesNotExist(string, bool)"/>;
    /// the dispatch loop's mid-batch error handling aborts subsequent
    /// statements, also matching the real server.
    /// </summary>
    /// <remarks>
    /// USE is not transactional — probe-confirmed (<c>BEGIN TRAN; USE other;
    /// ROLLBACK</c> leaves the connection pointed at <c>other</c>). The
    /// simulator mirrors this by mutating <see cref="SimulatedDbConnection.CurrentDatabase"/>
    /// without adding an undo-log entry. Skip-mode (inside an un-taken
    /// branch) suppresses the switch.
    /// </remarks>
    private static void ParseUseStatement(BatchContext batch)
    {
        var context = batch.Parser;
        var nameToken = context.GetNextRequired<Name>();
        context.MoveNextOptional();

        if (batch.IsSkipping)
        {
            // The walk that compiles a batch before it runs binds what follows
            // a USE in the database the USE names, as real's compile does
            // (probed 2026-09-28 against SQL Server 2025: `USE a; INSERT t …`
            // sent from database b binds a's t); CompileBatch puts the session
            // back afterwards. A database that doesn't exist while the batch
            // compiles — even one the batch creates first, and from an untaken
            // branch — refuses the batch with Msg 911 (probed 2026-10-02
            // against SQL Server 2025).
            if (batch.CompilingForRun)
            {
                context.Connection.CurrentDatabase = context.Connection.Simulation.Databases.TryGetValue(nameToken.Value, out var compileTarget)
                    ? compileTarget
                    : throw SimulatedSqlException.DatabaseDoesNotExist(nameToken.Value, fromUse: true);
            }
            return;
        }

        SwitchDatabase(context.Connection, nameToken.Value, fromUse: true);
        // Sent even when the database doesn't change (probed 2026-09-23), but
        // not from a dynamic batch (probed 2026-10-02 against SQL Server 2025).
        if (batch.ProcFrame is not { IsDynamicSql: true })
            context.Connection.PendingMessages.Enqueue(SimulatedSqlException.DatabaseContextChangedMessage(batch, context.Connection.CurrentDatabase.Name));
    }

    /// <summary>
    /// The database switch behind both <c>USE</c> and
    /// <see cref="SimulatedDbConnection.ChangeDatabase"/>. A missing database
    /// raises Msg 911 first (probe-confirmed — existence is reported even to a
    /// principal that couldn't have opened it); an active application role
    /// raises Msg 505 ahead of everything, since real reports the approle
    /// wording even for a would-be-dbo session. Anything the boundary-aware
    /// bypass doesn't wave through then has to resolve in the target — a
    /// restricted principal, and a database-scoped <c>dbo</c> frame alike: its
    /// login's user there becomes the session's base identity (so
    /// <c>CURRENT_USER</c> follows the switch), and a login with no user there
    /// gets Msg 916 with the session left put.
    /// </summary>
    internal static void SwitchDatabase(SimulatedDbConnection connection, string databaseName, bool fromUse = false)
    {
        var security = connection.Security;
        if (security.HasApplicationRole)
            throw SimulatedSqlException.CannotChangeDatabaseUnderApplicationRole();
        if (!connection.Simulation.Databases.TryGetValue(databaseName, out var target))
            throw SimulatedSqlException.DatabaseDoesNotExist(databaseName, fromUse);
        if (!PermissionEnforcement.Bypasses(connection, target))
        {
            var principal = PermissionEnforcement.ResolveCrossDatabasePrincipal(connection, target);
            security.RebindBaseFrameToDatabaseUser(principal);
        }

        connection.CurrentDatabase = target;
    }
}
