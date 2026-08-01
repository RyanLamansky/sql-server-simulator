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

        // A restricted principal (impersonated non-dbo user, or an
        // authenticated login mapped to a non-dbo user) can't cross databases:
        // Msg 916, session stays in the current database. A dbo / sa session
        // keeps the unrestricted switch.
        var connection = context.Connection;
        // An active application role pins the session to the database that set
        // it — Msg 505, ahead of the ordinary restricted-principal gate, since
        // real reports the approle-specific wording even for a would-be-dbo
        // session (probe-confirmed).
        if (connection.Security.HasApplicationRole)
            throw SimulatedSqlException.CannotChangeDatabaseUnderApplicationRole();
        if (!connection.Security.EffectiveIsDbo)
            throw SimulatedSqlException.CannotAccessDatabaseUnderSecurityContext(connection.Security.Effective.LoginName, nameToken.Value);

        var simulation = connection.Simulation;
        if (!simulation.Databases.TryGetValue(nameToken.Value, out var target))
            throw SimulatedSqlException.DatabaseDoesNotExist(nameToken.Value);

        connection.CurrentDatabase = target;
    }
}
