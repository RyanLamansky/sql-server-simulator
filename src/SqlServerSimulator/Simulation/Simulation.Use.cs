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
    /// <see cref="SimulatedSqlException.DatabaseDoesNotExist(string)"/>;
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
            return;

        SwitchDatabase(context.Connection, nameToken.Value);
    }

    /// <summary>
    /// The database switch behind both <c>USE</c> and
    /// <see cref="SimulatedDbConnection.ChangeDatabase"/>. A missing database
    /// raises Msg 911 first (probe-confirmed — existence is reported even to a
    /// principal that couldn't have opened it); an active application role
    /// raises Msg 505 ahead of everything, since real reports the approle
    /// wording even for a would-be-dbo session. A restricted principal then
    /// has to resolve in the target: its login's user there becomes the
    /// session's base identity (so <c>CURRENT_USER</c> follows the switch),
    /// and a login with no user there gets Msg 916 with the session left put.
    /// </summary>
    internal static void SwitchDatabase(SimulatedDbConnection connection, string databaseName)
    {
        var security = connection.Security;
        if (security.HasApplicationRole)
            throw SimulatedSqlException.CannotChangeDatabaseUnderApplicationRole();
        if (!connection.Simulation.Databases.TryGetValue(databaseName, out var target))
            throw SimulatedSqlException.DatabaseDoesNotExist(databaseName);
        if (!security.EffectiveIsDbo)
        {
            var principal = PermissionEnforcement.ResolveCrossDatabasePrincipal(connection, target);
            security.RebindBaseFrameToDatabaseUser(principal);
        }

        connection.CurrentDatabase = target;
    }
}
