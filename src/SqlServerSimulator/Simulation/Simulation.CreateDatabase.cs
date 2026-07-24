using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE DATABASE &lt;name&gt; [COLLATE &lt;collation&gt;] [&lt;file / option clauses&gt;]</c>.
    /// The database name is a single identifier (bare or bracketed / quoted);
    /// an optional <c>COLLATE</c> clause sets the new database's collation
    /// (defaulting to the server collation, mirroring <c>model.collation</c>),
    /// and every remaining clause (<c>ON (...)</c>, <c>LOG ON (...)</c>,
    /// <c>WITH ...</c>, <c>CONTAINMENT = ...</c>, <c>FOR ATTACH</c>, …) is
    /// parse-and-discarded — the simulator has no physical-file model. The new
    /// database is registered with the smallest free <c>database_id</c> ≥ 5
    /// (see <see cref="RegisterUserDatabase"/>). Cursor on entry is the
    /// <c>DATABASE</c> keyword.
    /// </summary>
    /// <remarks>
    /// Probe-confirmed against SQL Server 2025: a duplicate name raises
    /// <strong>Msg 1801</strong> (Class 16, State 3). <c>COLLATE</c> can legally
    /// precede or follow the file clauses, so it's detected anywhere in the
    /// trailing-clause scan rather than only immediately after the name.
    /// </remarks>
    private bool TryParseCreateDatabase(ParserContext context)
    {
        // Cursor on DATABASE; advance to the database name. A reserved keyword
        // where the name belongs is a syntax error (routes to Msg 102).
        if (context.GetNextRequired() is not Name nameToken)
            return false;
        var databaseName = nameToken.Value;

        // Walk the trailing clauses: capture a COLLATE clause anywhere at the
        // top level, discard everything else (tracking nested parens) until a
        // statement boundary. WITH is a boundary keyword but legitimately
        // continues CREATE DATABASE, so it's consumed rather than stopping the
        // scan.
        string? collationName = null;
        var depth = 0;
        context.MoveNextOptional();
        while (context.Token is { } token)
        {
            if (depth == 0 && token is ReservedKeyword { Keyword: Keyword.Collate })
            {
                if (context.GetNextRequired() is not UnquotedString collationToken)
                    return false;
                collationName = collationToken.Value;
                context.MoveNextOptional();
                continue;
            }
            if (depth == 0 && IsStatementBoundary(token) && token is not ReservedKeyword { Keyword: Keyword.With })
                break;
            switch (token)
            {
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' } when depth > 0:
                    depth--;
                    break;
            }
            context.MoveNextOptional();
        }

        // A skipped (un-taken IF branch) statement validates nothing and has no
        // schema effect — mirror ALTER DATABASE COLLATE, which defers collation
        // resolution past the skip check.
        if (context.Batch.IsSkipping)
            return true;

        var collation = collationName is null
            ? this.ServerCollation
            : Collation.TryGet(collationName)
                ?? throw new NotSupportedException($"CREATE DATABASE COLLATE: collation '{collationName}' isn't on the simulator's recognized list.");

        lock (this.Databases)
        {
            if (this.Databases.ContainsKey(databaseName))
                throw SimulatedSqlException.DatabaseAlreadyExists(databaseName);
            RegisterUserDatabaseLocked(new Database(databaseName, collation));
        }
        return true;
    }
}
