using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;

namespace SqlServerSimulator;

// sp_MSforeachdb — run one to three command templates once per accessible
// database, substituting the database's name for each @replacechar occurrence.
// Probe-confirmed against SQL Server 2025 (2026-08-01).
//
// The sibling sp_MSforeachtable takes a deliberate exception to the
// build-rows-in-C# convention because its @whereand parameter is a raw SQL
// fragment that needs a real query to graft onto (see the fence comment on
// Simulation.ForEachTable.cs). sp_MSforeachdb has no @whereand: its parameters
// are all values, so the database list comes from catalog state in C# and only
// the per-command dispatch reuses ExecuteDynamicBatch.
partial class Simulation
{
    /// <summary>
    /// Handles <c>EXEC sp_MSforeachdb @command1 [, @replacechar] [, @command2]
    /// [, @command3] [, @precommand] [, @postcommand]</c>. <c>@precommand</c>
    /// runs once first, <c>@command1</c> / <c>@command2</c> / <c>@command3</c>
    /// run once per database in that order, and <c>@postcommand</c> runs once
    /// last; every one of them is dispatched as its own dynamic batch, so each
    /// yields its own result sets to the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The proc does <strong>not</strong> switch database context on the
    /// caller's behalf (probe-confirmed: a command reading <c>DB_NAME()</c>
    /// reports the session's own database every time). Running against each
    /// database is the caller's job, through the idiomatic
    /// <c>'USE [?]; …'</c> command — and because each command is its own
    /// dynamic batch, that <c>USE</c> binds for that command only and the
    /// session stays where it was.
    /// </para>
    /// <para>
    /// The database list is the one real's cursor selects — every database
    /// <c>HAS_DBACCESS</c> reports 1 for, in <c>database_id</c> order, which
    /// includes the system databases and excludes <c>model</c> (the restricted
    /// template) here for the same reason <c>sp_helpdb</c>'s listing does.
    /// </para>
    /// <para>
    /// Substitution follows real's rules for the character preceding
    /// <c>@replacechar</c>: a <c>'</c> doubles every quote in the name and a
    /// <c>[</c> doubles every closing bracket; anywhere else the worker
    /// <c>QUOTENAME</c>s the name, so a bare <c>?</c> expands to
    /// <c>[<em>database</em>]</c> — unlike <c>sp_MSforeachtable</c>, whose
    /// names are bracketed before they reach the worker.
    /// </para>
    /// </remarks>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpMsForEachDb(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (command1, replaceChar, command2, command3, preCommand, postCommand) =
            ParseSpMsForEachDbArgs(arguments);
        if (command1 is null)
            throw SimulatedSqlException.ProcedureExpectsParameter("sp_MSforeachdb", "command1");

        if (preCommand is not null)
        {
            foreach (var outcome in this.ExecuteDynamicBatch(batch, preCommand, preDeclaredVariables: null))
                yield return outcome;
        }

        string?[] commands = [command1, command2, command3];
        foreach (var databaseName in ForEachDatabaseNames(batch))
        {
            foreach (var command in commands)
            {
                if (command is null)
                    continue;
                var expanded = ForEachSubstitute(command, replaceChar, databaseName,
                    bare: "[" + databaseName.Replace("]", "]]", StringComparison.Ordinal) + "]");
                foreach (var outcome in this.ExecuteDynamicBatch(batch, expanded, preDeclaredVariables: null))
                    yield return outcome;
            }
        }

        if (postCommand is null)
            yield break;
        foreach (var outcome in this.ExecuteDynamicBatch(batch, postCommand, preDeclaredVariables: null))
            yield return outcome;
    }

    // Real's cursor query reduced to the state it reads: every database
    // has_dbaccess reports 1 for, in the database_id order sysdatabases
    // enumerates. The single-user and inaccessible-status filters it also
    // applies have no simulator counterpart — no database is either.
    private static List<string> ForEachDatabaseNames(BatchContext batch)
    {
        var names = new List<string>();
        foreach (var (database, _) in DbId.DatabasesWithIds(batch.Connection.Simulation))
        {
            if (HasDbAccess.IsAccessible(database))
                names.Add(database.Name);
        }

        return names;
    }

    // sp_MSforeachdb's own parameter order: the @whereand slot sp_MSforeachtable
    // carries between @command3 and @precommand isn't declared here, so a
    // positional @precommand sits one place earlier.
    private static (string? Command1, char ReplaceChar, string? Command2, string? Command3,
        string? PreCommand, string? PostCommand) ParseSpMsForEachDbArgs(List<ProcArgument> arguments)
    {
        string? command1 = null, command2 = null, command3 = null, preCommand = null, postCommand = null;
        var replaceChar = '?';
        var positional = 0;
        foreach (var arg in arguments)
        {
            if (arg.Name is null)
            {
                switch (positional++)
                {
                    case 0: command1 = CatalogStringArg(arg); break;
                    case 1: replaceChar = ForEachReplaceChar(arg, replaceChar); break;
                    case 2: command2 = CatalogStringArg(arg); break;
                    case 3: command3 = CatalogStringArg(arg); break;
                    case 4: preCommand = CatalogStringArg(arg); break;
                    case 5: postCommand = CatalogStringArg(arg); break;
                    default: throw SimulatedSqlException.InvalidProcedureParameters("sp_MSforeachdb");
                }

                continue;
            }

            switch (arg.Name)
            {
                case var n when BuiltInToken.Equals(n, "command1"): command1 = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "replacechar"): replaceChar = ForEachReplaceChar(arg, replaceChar); break;
                case var n when BuiltInToken.Equals(n, "command2"): command2 = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "command3"): command3 = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "precommand"): preCommand = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "postcommand"): postCommand = CatalogStringArg(arg); break;
                default: throw SimulatedSqlException.InvalidProcedureParameters("sp_MSforeachdb");
            }
        }

        return (command1, replaceChar, command2, command3, preCommand, postCommand);
    }
}
